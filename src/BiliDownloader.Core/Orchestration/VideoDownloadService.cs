using System.Collections.Concurrent;
using System.Diagnostics;
using BiliDownloader.Core.BiliApi;
using BiliDownloader.Core.Download;
using BiliDownloader.Core.Errors;
using BiliDownloader.Core.FileName;
using BiliDownloader.Core.Merge;
using BiliDownloader.Core.Models;
using BiliDownloader.Core.Parse;
using BiliDownloader.Core.Storage;
using BiliDownloader.Core.Utils;

namespace BiliDownloader.Core.Orchestration;

/// <summary>
/// 下载编排实现。一页流水固定顺序：
/// FetchingInfo(view) → FetchingUrl(playurl) → Downloading(视频流+音频流) → Merging → Move → Completed。
/// 单个分 P 失败不中断整批，已成功文件保留（方案设计 §5.2）。
/// </summary>
public sealed class VideoDownloadService : IVideoDownloadService, IDisposable
{
    private const double DownloadWeight = 0.90; // 下载阶段占总进度 90%，合并 10%（§5.4）

    private readonly IBiliApiClient _api;
    private readonly IFileDownloader _downloader;
    private readonly IFfmpegMerger _merger;
    private readonly IFileNameStrategy _fileName;
    private readonly UrlParser _parser;
    private readonly HttpClient _http;
    private readonly TaskScheduler _scheduler;
    private readonly object _manifestSync = new();
    private DateTimeOffset _lastManifestWrite;
    private bool _disposed;

    /// <summary>进度类清单写入的最小间隔（状态变化不受此限制，立即写）。</summary>
    private static readonly TimeSpan ManifestWriteInterval = TimeSpan.FromSeconds(3);

    public VideoDownloadService(
        IBiliApiClient api,
        IFileDownloader downloader,
        IFfmpegMerger merger,
        IFileNameStrategy fileName,
        UrlParser parser,
        HttpClient http,
        int maxConcurrency = 3)
    {
        _api = api;
        _downloader = downloader;
        _merger = merger;
        _fileName = fileName;
        _parser = parser;
        _http = http;
        _scheduler = new TaskScheduler(maxConcurrency);
    }

    public event EventHandler<TaskProgressEventArgs>? ProgressChanged;
    public event EventHandler<TaskStateChangedEventArgs>? StateChanged;

    public async Task<VideoInfo> ProbeAsync(string rawInput, string? preferredCodec = null,
        CancellationToken ct = default)
    {
        var parsed = await _parser.ParseAsync(rawInput, _http, ct).ConfigureAwait(false);
        var id = parsed.Bvid ?? (parsed.Avid is { } avid ? $"av{avid}" : null);
        if (id is null)
            throw new BiliException(ErrorKind.Unknown, "未能识别 B 站链接，请检查粘贴内容");

        var info = await _api.GetVideoInfoAsync(id, ct).ConfigureAwait(false);
        EnsureSupported(info);

        if (info.Pages.Count == 0)
            return info with { SelectedPage = parsed.Page };

        // 探测可用画质：只用接口返回的流做「可用性」判断，不保存任何直链（§7.5）
        var page = info.Pages.FirstOrDefault(p => p.Index == parsed.Page) ?? info.Pages[0];
        var play = await _api.GetPlayUrlAsync(new PlayUrlRequest(info.Bvid, page.Cid, 127), ct).ConfigureAwait(false);
        var isVip = false;
        if (_api.IsLoggedIn)
        {
            var nav = await _api.GetNavAsync(ct).ConfigureAwait(false);
            isVip = nav.IsVip;
        }

        return info with
        {
            SelectedPage = parsed.Page,
            Qualities = QualityCatalog.BuildVideoOptions(play, _api.IsLoggedIn, isVip, preferredCodec),
            Audios = QualityCatalog.BuildAudioOptions(play, _api.IsLoggedIn, isVip)
        };
    }

    public VideoTask Enqueue(DownloadRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (_disposed) throw new ObjectDisposedException(nameof(VideoDownloadService));
        if (request.Cids.Count == 0)
            throw new ArgumentException("至少选择一个分 P", nameof(request));

        var task = new VideoTask
        {
            Bvid = request.Bvid,
            Title = request.Title,
            UpName = request.UpName,
            CoverUrl = request.CoverUrl,
            Duration = request.Duration,
            SaveDirectory = request.SaveDirectory,
            TotalPages = Math.Max(1, request.TotalPages),
            Pages = request.Cids.Distinct().Select((cid, i) => new TaskPageItem
            {
                Cid = cid,
                Index = i + 1,
                Part = string.Empty,
                Qn = request.Qn,
                CodecId = request.CodecId,
                AudioId = request.AudioId
            }).ToList()
        };

        var runtime = _scheduler.Register(task, request);
        Persist(runtime, force: true);
        _scheduler.Start(runtime, rt => RunAsync(rt));
        return task;
    }

    public void Pause(string taskId) => Execute(taskId, rt =>
    {
        // 已经结束的任务不该再被改成暂停
        if (IgnoresCommands(rt.Task.Status)) return;
        if (rt.Worker is null || rt.Worker.IsCompleted) return;

        rt.PauseRequested = true;
        rt.ResumePending = false;
        RequestStop(rt);

        foreach (var page in rt.Task.Pages.Where(p => p.Status is DownloadStatus.Downloading
                     or DownloadStatus.FetchingUrl or DownloadStatus.FetchingInfo or DownloadStatus.Merging))
        {
            page.Status = DownloadStatus.Paused;
            page.ErrorMessage = null;
        }

        SetTaskStatus(rt, DownloadStatus.Paused);
    });

    public void Resume(string taskId) => Execute(taskId, rt =>
    {
        if (IgnoresCommands(rt.Task.Status)) return;
        rt.CancelRequested = false;

        if (rt.Worker is { IsCompleted: false })
        {
            // 流程还在跑：若它是在响应刚才的暂停（正在退出），等它退干净后自动接上，
            // 否则就是本来就在下载，这条继续命令直接作废。
            rt.ResumePending = rt.PauseRequested;
            return;
        }

        rt.PauseRequested = false;
        rt.ResumePending = false;

        foreach (var page in rt.Task.Pages.Where(p => p.Status is DownloadStatus.Paused))
            page.Status = DownloadStatus.Pending;

        Restart(rt, DownloadStatus.Pending);
    });

    public void Cancel(string taskId, bool keepTemp) => Execute(taskId, rt =>
    {
        // 已经下完的任务不该被取消命令改状态（UI 上「取消」对已完成任务通常已隐藏，这里是兜底）
        if (rt.Task.Status is DownloadStatus.Completed or DownloadStatus.Skipped) return;

        rt.CancelRequested = true;
        rt.ResumePending = false;
        rt.KeepTemp = keepTemp;

        var running = rt.Worker is { IsCompleted: false };
        RequestStop(rt);

        if (!running) FinalizeCancel(rt);
    });

    public void RetryFailed(string taskId) => Execute(taskId, rt =>
    {
        if (rt.Task.Status is DownloadStatus.Completed or DownloadStatus.Skipped or DownloadStatus.Canceled) return;
        if (rt.Worker is { IsCompleted: false }) return;

        var failed = rt.Task.Pages.Where(p => p.Status == DownloadStatus.Failed).ToList();
        if (failed.Count == 0) return;

        foreach (var page in failed)
        {
            page.Status = DownloadStatus.Pending;
            page.Error = null;
            page.ErrorMessage = null;
        }

        rt.Task.Error = null;
        rt.Task.ErrorMessage = null;
        rt.Task.Attempts++;

        rt.PauseRequested = false;
        rt.CancelRequested = false;
        rt.ResumePending = false;
        Restart(rt, DownloadStatus.Pending);
    });

    public bool IsRunning(string taskId) => _scheduler.IsRunning(taskId);

    /// <summary>
    /// 命令串行化入口：同一任务的暂停 / 继续 / 取消 / 重试排队执行，
    /// 避免「旧流程还没退出、新命令已经把状态改了」这类穿插。
    /// </summary>
    private void Execute(string taskId, Action<TaskRuntime> command) =>
        _scheduler.Execute(taskId, command);

    /// <summary>已经结束的任务不再接受暂停 / 继续命令（失败态除外，它可以重试）。</summary>
    private static bool IgnoresCommands(DownloadStatus status) =>
        status is DownloadStatus.Completed or DownloadStatus.Canceled or DownloadStatus.Skipped;

    /// <summary>
    /// 移除任务（列表里不再显示）：停掉后台流程并删掉清单条目，免得重启后又冒出来。
    /// 已经在跑的任务先请求取消，流程退干净后自然不再更新它。
    /// </summary>
    public void Remove(string taskId)
    {
        if (!_scheduler.TryRemove(taskId, out var rt)) return;

        lock (rt.CommandLock)
        {
            rt.CancelRequested = true;
            rt.ResumePending = false;
            RequestStop(rt);
        }

        lock (_manifestSync) RemoveManifestEntry(rt.Task);
    }

    /// <summary>请求停止。CTS 可能已被 Restart 换掉并释放，这里不能让它冒出去。</summary>
    private static void RequestStop(TaskRuntime rt)
    {
        try { rt.Cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    public IReadOnlyList<VideoTask> LoadResumable(string saveDirectory)
    {
        if (string.IsNullOrWhiteSpace(saveDirectory)) return [];

        var manifest = ManifestStore.Load(saveDirectory);
        var restored = new List<VideoTask>();

        foreach (var entry in manifest.Tasks)
        {
            if (entry.Pages.Count == 0) continue;

            var pages = entry.Pages.Select(p => new TaskPageItem
            {
                Cid = p.Cid,
                Index = p.Index,
                Part = p.Part,
                Qn = p.Qn,
                CodecId = p.CodecId,
                AudioId = p.AudioId,
                // 只有真正下完的页保持原状，其余一律置为「已暂停」等用户点继续；
                // 下载中途的状态（Downloading / Merging）在重启后没有意义。
                Status = p.Status is nameof(DownloadStatus.Completed) or nameof(DownloadStatus.Skipped)
                    ? (DownloadStatus)Enum.Parse(typeof(DownloadStatus), p.Status)
                    : DownloadStatus.Paused,
                ReceivedBytes = p.ReceivedBytes,
                TotalBytes = p.TotalBytes,
                Progress = p.TotalBytes > 0 ? Net48.Clamp((double)p.ReceivedBytes / p.TotalBytes, 0, 1) : 0,
                TempDir = StoragePaths.PageTempDir(saveDirectory, entry.TaskId, p.Cid),
                VideoFingerprint = p.Video,
                AudioFingerprint = p.Audio
            }).ToList();

            // 全部下完的任务没有恢复价值
            if (pages.All(p => p.Status is DownloadStatus.Completed or DownloadStatus.Skipped)) continue;

            var task = new VideoTask
            {
                TaskId = entry.TaskId,
                Bvid = entry.Bvid,
                Title = entry.Title,
                UpName = entry.UpName,
                CoverUrl = entry.CoverUrl,
                Duration = TimeSpan.FromTicks(entry.DurationTicks),
                SaveDirectory = saveDirectory,
                TotalPages = entry.TotalPages,
                Pages = pages,
                Status = DownloadStatus.Paused
            };

            var first = pages[0];
            var request = new DownloadRequest(
                entry.Bvid,
                pages.Select(p => p.Cid).ToList(),
                first.Qn, first.CodecId, first.AudioId,
                saveDirectory, entry.Title, entry.UpName, entry.CoverUrl, task.Duration, entry.TotalPages,
                new FileNameOptions(entry.NameRule, entry.IndexPadding),
                entry.Policy,
                new DownloadOptions(MaxRetriesPerUrl: entry.MaxRetriesPerUrl,
                    Connections: entry.Connections, AllowFallback: entry.AllowFallback));

            _scheduler.Register(task, request);
            restored.Add(task);
        }

        return restored;
    }

    // 恢复出来的任务沿用 Resume(taskId) 继续：runtime 已经建好，
    // 与「用户手动暂停后再继续」走同一条路径，不需要第二个入口。

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scheduler.StopAll();
    }

    // ---------------------------------------------------------------- 内部

    private void Restart(TaskRuntime rt, DownloadStatus initial) =>
        _scheduler.Start(rt, x => RunAsync(x), x => SetTaskStatus(x, initial));

    private async Task RunAsync(TaskRuntime rt)
    {
        var task = rt.Task;
        var ct = rt.Cts.Token;
        var acquired = false;

        try
        {
            await _scheduler.EnterGateAsync(ct).ConfigureAwait(false);
            acquired = true;

            SetTaskStatus(rt, DownloadStatus.FetchingInfo);
            var info = await _api.GetVideoInfoAsync(task.Bvid, ct).ConfigureAwait(false);
            EnsureSupported(info);

            foreach (var page in task.Pages)
            {
                ct.ThrowIfCancellationRequested();
                if (page.Status is DownloadStatus.Completed or DownloadStatus.Skipped) continue;

                var pageInfo = info.Pages.FirstOrDefault(p => p.Cid == page.Cid);
                try
                {
                    await ProcessPageWithRetryAsync(rt, page, info, pageInfo, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (BiliException ex)
                {
                    // 单个 P 失败不中断整批（§5.2）
                    FailPage(rt, page, ex);
                }
                catch (Exception ex)
                {
                    FailPage(rt, page, BiliException.From(ex));
                }
            }

            RecomputeTaskStatus(rt);
            if (task.Status == DownloadStatus.Completed)
            {
                task.FinishedAt = DateTimeOffset.Now;
                if (task.Pages.Count == 1) task.FinalPath = task.Pages[0].FinalPath;
                else task.FinalPath = Path.Combine(task.SaveDirectory, DefaultFileNameStrategy.Sanitize(task.Title));
            }
        }
        catch (OperationCanceledException)
        {
            if (rt.CancelRequested) FinalizeCancel(rt);
            else SetTaskStatus(rt, DownloadStatus.Paused);
        }
        catch (BiliException ex)
        {
            FailTask(rt, ex);
        }
        catch (Exception ex)
        {
            FailTask(rt, BiliException.From(ex));
        }
        finally
        {
            if (acquired) _scheduler.ExitGate();

            // 暂停命令发出后，后台流程还要一点时间才真正退出。这期间用户点的「继续」
            // 记在 ResumePending 上，等这里退干净了自动接上——否则那条命令会被吞掉，
            // 任务就永远停在「已暂停」了。
            if (rt.ResumePending && !rt.CancelRequested)
            {
                rt.ResumePending = false;
                rt.PauseRequested = false;

                foreach (var page in rt.Task.Pages.Where(p => p.Status == DownloadStatus.Paused))
                    page.Status = DownloadStatus.Pending;

                Restart(rt, DownloadStatus.Pending);
            }
        }
    }

    /// <summary>
    /// 单页流水；遇到直链过期（403/404 且备用 CDN 也失效）重取一次直链，
    /// 依靠已落盘字节续传，不从 0 重来（方案设计 §7.5）。
    /// </summary>
    private async Task ProcessPageWithRetryAsync(TaskRuntime rt, TaskPageItem page, VideoInfo info,
        PageInfo? pageInfo, CancellationToken ct)
    {
        try
        {
            await ProcessPageAsync(rt, page, info, pageInfo, ct).ConfigureAwait(false);
        }
        catch (BiliException ex) when (ex.Kind == ErrorKind.StreamExpired && page.Attempts == 0)
        {
            page.Attempts = 1;
            await ProcessPageAsync(rt, page, info, pageInfo, ct).ConfigureAwait(false);
        }
    }

    private async Task ProcessPageAsync(TaskRuntime rt, TaskPageItem page, VideoInfo info,
        PageInfo? pageInfo, CancellationToken ct)
    {
        var task = rt.Task;
        page.Part = pageInfo?.Part ?? page.Part;
        page.Index = pageInfo?.Index ?? page.Index;
        page.Duration = pageInfo?.Duration ?? TimeSpan.Zero;
        page.Error = null;
        page.ErrorMessage = null;

        SetPageStatus(rt, page, DownloadStatus.FetchingUrl);

        var play = await _api.GetPlayUrlAsync(new PlayUrlRequest(info.Bvid, page.Cid, page.Qn), ct)
                             .ConfigureAwait(false);
        var video = play.FindVideo(page.Qn, page.CodecId)
                    ?? throw new BiliException(ErrorKind.VipRequired, "该画质不可用，请换一个画质重试");
        var audio = play.FindAudio(page.AudioId);

        page.Qn = video.Id;
        page.CodecId = video.CodecId;
        page.AudioId = audio?.Id ?? 0;

        var duration = page.Duration > TimeSpan.Zero ? page.Duration : play.Duration;
        if (page.Duration <= TimeSpan.Zero) page.Duration = duration;

        var tempDir = StoragePaths.PageTempDir(task.SaveDirectory, task.TaskId, page.Cid);
        page.TempDir = tempDir;
        Directory.CreateDirectory(tempDir);
        StoragePaths.TryHideTempRoot(task.SaveDirectory);

        // 跨重启恢复的正确性闸门：先确认「这条流还是上次那条」，再决定能不能续传
        await EnsureStreamIdentityAsync(page, video, audio, ct).ConfigureAwait(false);

        var videoPath = Path.Combine(tempDir, "video.m4s");
        var audioPath = Path.Combine(tempDir, "audio.m4s");

        var options = rt.Request.Download ?? new DownloadOptions();
        var videoLen = video.ContentLength ?? EstimateBytes(video.Bandwidth, duration);
        var audioLen = audio is null ? 0 : audio.ContentLength ?? EstimateBytes(audio.Bandwidth, duration);
        page.TotalBytes = videoLen + audioLen;
        // 分片下载时已落盘字节分散在 .partN 里，只看主文件长度会低估进度、续传后进度条回退。
        page.ReceivedBytes = ResumedBytes(videoPath, videoLen)
                             + (audio is null ? 0 : ResumedBytes(audioPath, audioLen));

        SetPageStatus(rt, page, DownloadStatus.Downloading);

        var videoProgress = new DirectProgress<DownloadProgress>(p =>
        {
            page.ReceivedBytes = p.ReceivedBytes;
            page.Progress = page.TotalBytes > 0
                ? DownloadWeight * Net48.Clamp((double)p.ReceivedBytes / page.TotalBytes, 0, 1)
                : 0;
            ReportPageProgress(rt, page, p.SpeedBytesPerSecond);
        });

        var videoResult = await _downloader.DownloadAsync(
            new RemoteFile(video.BaseUrl, video.BackupUrls, video.ContentLength ?? 0),
            videoPath, options, videoProgress, ct).ConfigureAwait(false);
        videoLen = videoResult.Bytes;

        if (audio is not null)
        {
            var audioProgress = new DirectProgress<DownloadProgress>(p =>
            {
                page.ReceivedBytes = videoLen + p.ReceivedBytes;
                page.Progress = page.TotalBytes > 0
                    ? DownloadWeight * Net48.Clamp((double)page.ReceivedBytes / page.TotalBytes, 0, 1)
                    : 0;
                ReportPageProgress(rt, page, p.SpeedBytesPerSecond);
            });

            var audioResult = await _downloader.DownloadAsync(
                new RemoteFile(audio.BaseUrl, audio.BackupUrls, audio.ContentLength ?? 0),
                audioPath, options, audioProgress, ct).ConfigureAwait(false);
            audioLen = audioResult.Bytes;
        }

        // 预估长度可能与真实长度有偏差，用真实字节数修正总长，进度才能到 100%
        page.TotalBytes = videoLen + audioLen;
        page.ReceivedBytes = page.TotalBytes;
        page.Progress = DownloadWeight;
        SetPageStatus(rt, page, DownloadStatus.Merging);
        ReportPageProgress(rt, page, 0);

        var outputPath = Path.Combine(tempDir, "out.mp4");
        var mergeProgress = new DirectProgress<MergeProgress>(p =>
        {
            page.Progress = DownloadWeight + (1 - DownloadWeight) * Net48.Clamp(p.Percent / 100, 0, 1);
            ReportPageProgress(rt, page, 0);
        });

        await _merger.MergeAsync(new MergeRequest(videoPath, audioPath, outputPath, duration), mergeProgress, ct)
                     .ConfigureAwait(false);

        var subDirectory = task.TotalPages > 1 || task.Pages.Count > 1
            ? Path.Combine(task.SaveDirectory, DefaultFileNameStrategy.Sanitize(task.Title))
            : task.SaveDirectory;

        var fileName = _fileName.Build(page.Index, string.IsNullOrWhiteSpace(page.Part) ? task.Title : page.Part,
            task.UpName, rt.Request.NameOptions);

        var committed = StoragePaths.Commit(outputPath, subDirectory, fileName, ".mp4", rt.Request.ExistingFileAction);
        if (committed is null)
        {
            // 同名策略选了「跳过」：成品已经在目标位置，这一页记为 Skipped，
            // 既不覆盖旧文件也不白白重下一次。FinalPath 指向已存在的那个文件，方便「打开目录」。
            page.FinalPath = Path.Combine(subDirectory, fileName + ".mp4");
            page.Progress = 1;
            page.ReceivedBytes = page.TotalBytes;
            SetPageStatus(rt, page, DownloadStatus.Skipped);
            ReportPageProgress(rt, page, 0);
            StoragePaths.TryDeleteDirectory(tempDir);
            return;
        }

        page.FinalPath = committed;
        page.Progress = 1;
        page.ReceivedBytes = page.TotalBytes;
        SetPageStatus(rt, page, DownloadStatus.Completed);
        ReportPageProgress(rt, page, 0);
        StoragePaths.TryDeleteDirectory(tempDir);
    }

    private void FailPage(TaskRuntime rt, TaskPageItem page, BiliException ex)
    {
        page.Status = DownloadStatus.Failed;
        page.Error = ex.Kind;
        page.ErrorMessage = ex.UserMessage;
        RecomputeTaskStatus(rt);
        RaiseStateChanged(rt, rt.Task.Status, rt.Task.Status);
    }

    private void FailTask(TaskRuntime rt, BiliException ex)
    {
        foreach (var page in rt.Task.Pages.Where(p => !p.Status.IsTerminal()))
        {
            page.Status = DownloadStatus.Failed;
            page.Error ??= ex.Kind;
            page.ErrorMessage ??= ex.UserMessage;
        }

        rt.Task.Error = ex.Kind;
        rt.Task.ErrorMessage = ex.UserMessage;
        rt.Task.FinishedAt = DateTimeOffset.Now;
        SetTaskStatus(rt, DownloadStatus.Failed);
    }

    private void FinalizeCancel(TaskRuntime rt)
    {
        foreach (var page in rt.Task.Pages.Where(p => !p.Status.IsTerminal()))
        {
            page.Status = DownloadStatus.Canceled;
        }
        if (!rt.KeepTemp)
            foreach (var page in rt.Task.Pages)
                StoragePaths.TryDeleteDirectory(page.TempDir);

        rt.Task.FinishedAt = DateTimeOffset.Now;
        SetTaskStatus(rt, DownloadStatus.Canceled);
    }

    private void ReportPageProgress(TaskRuntime rt, TaskPageItem page, double speed)
    {
        var task = rt.Task;
        RecomputeTaskStatus(rt);

        var weights = task.Pages.Select(p => p.TotalBytes > 0 ? p.TotalBytes : Math.Max(1, (long)p.Duration.TotalSeconds));
        var totalWeight = Math.Max(1, weights.Sum());
        // net48 的 Enumerable.Zip 没有「返回元组」的单参数重载，用带投影的双序列版本
        var done = task.Pages.Zip(weights, (page, weight) => page.Progress * weight).Sum();
        task.Progress = Net48.Clamp(done / totalWeight, 0, 1);

        // 事件节流 ≤10 次/秒（§5.4）
        var now = Stopwatch.GetTimestamp();
        var elapsedMs = (now - rt.LastProgressTicks) * 1000.0 / Stopwatch.Frequency;
        if (elapsedMs < 100 && task.Progress < 1) return;

        rt.LastProgressTicks = now;
        ProgressChanged?.Invoke(this, new TaskProgressEventArgs
        {
            Task = task,
            Progress = task.Progress,
            ReceivedBytes = page.ReceivedBytes,
            TotalBytes = page.TotalBytes,
            SpeedBytesPerSecond = speed,
            Cid = page.Cid
        });

        // 字节进度按间隔节流写入：掉电/崩溃时最多丢最近几秒的进度，之后靠已落盘字节续传
        Persist(rt);
    }

    private void SetPageStatus(TaskRuntime rt, TaskPageItem page, DownloadStatus status)
    {
        if (page.Status == status) return;
        page.Status = status;
        RecomputeTaskStatus(rt);
        RaiseStateChanged(rt, rt.Task.Status, rt.Task.Status);
    }

    private void SetTaskStatus(TaskRuntime rt, DownloadStatus status)
    {
        var previous = rt.Task.Status;
        if (previous == status) return;
        rt.Task.Status = status;
        RaiseStateChanged(rt, previous, status);
    }

    /// <summary>聚合状态：有活跃分 P 时按最靠前的活跃态；全部结束才落 Failed / Completed（§5.2）。</summary>
    private void RecomputeTaskStatus(TaskRuntime rt)
    {
        var pages = rt.Task.Pages;
        var previous = rt.Task.Status;

        DownloadStatus next;
        if (pages.Any(p => p.Status == DownloadStatus.Merging)) next = DownloadStatus.Merging;
        else if (pages.Any(p => p.Status == DownloadStatus.Downloading)) next = DownloadStatus.Downloading;
        else if (pages.Any(p => p.Status == DownloadStatus.FetchingUrl)) next = DownloadStatus.FetchingUrl;
        else if (pages.Any(p => p.Status == DownloadStatus.FetchingInfo)) next = DownloadStatus.FetchingInfo;
        else if (pages.Any(p => p.Status == DownloadStatus.Pending)) next = DownloadStatus.Pending;
        else if (pages.Any(p => p.Status == DownloadStatus.Paused)) next = DownloadStatus.Paused;
        else if (pages.Any(p => p.Status == DownloadStatus.Failed))
        {
            next = DownloadStatus.Failed;
            rt.Task.Error = pages.First(p => p.Status == DownloadStatus.Failed).Error;
            rt.Task.ErrorMessage = pages.First(p => p.Status == DownloadStatus.Failed).ErrorMessage;
        }
        else if (pages.All(p => p.Status is DownloadStatus.Completed or DownloadStatus.Skipped))
        {
            next = pages.Any(p => p.Status == DownloadStatus.Completed)
                ? DownloadStatus.Completed
                : DownloadStatus.Skipped;
        }
        else next = DownloadStatus.Canceled;

        if (previous == next) return;
        rt.Task.Status = next;
        RaiseStateChanged(rt, previous, next);
    }

    private void RaiseStateChanged(TaskRuntime rt, DownloadStatus previous, DownloadStatus current)
    {
        // 状态变化是恢复时最关键的依据（哪些页下完了、哪一页失败了），立即落盘
        Persist(rt, force: true);

        StateChanged?.Invoke(this, new TaskStateChangedEventArgs
        {
            Task = rt.Task,
            Previous = previous,
            Current = current
        });
    }

    private static long EstimateBytes(long bandwidthBps, TimeSpan duration)
    {
        if (bandwidthBps <= 0 || duration <= TimeSpan.Zero) return 0;
        return (long)(bandwidthBps * duration.TotalSeconds / 8);
    }

    private static void EnsureSupported(VideoInfo info)
    {
        if (info.IsPaidContent)
            throw new BiliException(ErrorKind.PaidContent, "付费 / 充电专属内容暂不支持下载");
        if (info.HasUgcSeason)
            throw new BiliException(ErrorKind.VideoUnavailable, "合集内容暂不支持下载");
    }

    /// <summary>
    /// 每次取到直链都探测并记下流指纹；若这一页之前下过而指纹对不上，说明重取到的直链
    /// 已经不是同一条流——已落盘字节不可信，清空整段重下。
    /// 宁可慢，也不能把两条不同的流拼成一个「长度刚好、进度 100%」的坏文件。
    /// </summary>
    private async Task EnsureStreamIdentityAsync(TaskPageItem page, DashStream video, DashStream? audio,
        CancellationToken ct)
    {
        var videoCurrent = await _downloader.ProbeFingerprintAsync(RemoteOf(video), ct).ConfigureAwait(false);
        var audioCurrent = audio is null
            ? null
            : await _downloader.ProbeFingerprintAsync(RemoteOf(audio), ct).ConfigureAwait(false);

        var changed = page.VideoFingerprint?.Matches(videoCurrent) != true
            || (audio is not null && page.AudioFingerprint?.Matches(audioCurrent) != true)
            || (audio is null && page.AudioFingerprint is not null);

        if (changed && !string.IsNullOrEmpty(page.TempDir))
        {
            page.ReceivedBytes = 0;
            page.Progress = 0;
            StoragePaths.TryDeleteDirectory(page.TempDir);
            Directory.CreateDirectory(page.TempDir);
        }

        page.VideoFingerprint = videoCurrent;
        page.AudioFingerprint = audioCurrent;
    }

    private static RemoteFile RemoteOf(DashStream stream) =>
        new(stream.BaseUrl, stream.BackupUrls, stream.ContentLength ?? 0);

    // ---------------------------------------------------------------- 清单持久化

    /// <summary>
    /// 写清单：状态变化立即写，纯进度变化按间隔节流。
    /// 已完成 / 已取消 / 全部跳过的任务没有恢复价值，直接删条目。
    /// </summary>
    private void Persist(TaskRuntime rt, bool force = false)
    {
        lock (_manifestSync)
        {
            // 移除后，旧流程迟到的进度或错误不能重新创建清单条目。
            if (!_scheduler.TryGet(rt.Task.TaskId, out var registered) || !ReferenceEquals(rt, registered))
                return;

            if (rt.Task.Status is DownloadStatus.Completed or DownloadStatus.Canceled or DownloadStatus.Skipped)
            {
                RemoveManifestEntry(rt.Task);
                _lastManifestWrite = DateTimeOffset.Now;
                return;
            }

            var now = DateTimeOffset.Now;
            if (!force && now - _lastManifestWrite < ManifestWriteInterval) return;
            _lastManifestWrite = now;

            WriteManifestEntry(rt.Task, rt.Request);
        }
    }

    private static void WriteManifestEntry(VideoTask task, DownloadRequest request)
    {
        var manifest = ManifestStore.Load(task.SaveDirectory);
        var options = request.Download ?? new DownloadOptions();
        var name = request.NameOptions ?? new FileNameOptions();

        var entry = new ManifestTaskEntry(
            task.TaskId, task.Bvid, task.Title, task.UpName, task.CoverUrl,
            task.Duration.Ticks, task.TotalPages,
            options.Connections, options.MaxRetriesPerUrl, options.AllowFallback,
            name.Rule, name.IndexPadding, request.ExistingFileAction,
            DateTimeOffset.Now,
            task.Pages.Select(p => new ManifestPageEntry(
                p.Cid, p.Index, p.Part, p.Qn, p.CodecId, p.AudioId,
                p.Status.ToString(), p.ReceivedBytes, p.TotalBytes,
                p.VideoFingerprint, p.AudioFingerprint)).ToList());

        var index = manifest.Tasks.FindIndex(t => t.TaskId == task.TaskId);
        if (index >= 0) manifest.Tasks[index] = entry;
        else manifest.Tasks.Add(entry);

        ManifestStore.Save(task.SaveDirectory, manifest);
    }

    private static void RemoveManifestEntry(VideoTask task)
    {
        var manifest = ManifestStore.Load(task.SaveDirectory);
        if (manifest.Tasks.RemoveAll(t => t.TaskId == task.TaskId) == 0) return;

        if (manifest.Tasks.Count == 0) ManifestStore.Delete(task.SaveDirectory);
        else ManifestStore.Save(task.SaveDirectory, manifest);
    }

    /// <summary>已落盘字节数（分片模式下含 .partN），用于续传时的进度起点。</summary>
    private static long ResumedBytes(string path, long expected) =>
        File.Exists(path) ? Math.Min(PartialFile.BytesOnDisk(path), Math.Max(expected, 0)) : 0;

    /// <summary>同步转发的 Progress：避免 Progress&lt;T&gt; 默认投递到线程池造成进度乱序。</summary>
    private sealed class DirectProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
