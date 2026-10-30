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
    private readonly SemaphoreSlim _gate;
    private readonly ConcurrentDictionary<string, TaskRuntime> _runtimes = new();
    private bool _disposed;

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
        _gate = new SemaphoreSlim(Math.Clamp(maxConcurrency, 1, 8));
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
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
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

        var runtime = new TaskRuntime(task, request);
        _runtimes[task.TaskId] = runtime;
        runtime.Worker = Task.Run(() => RunAsync(runtime), CancellationToken.None);
        return task;
    }

    public void Pause(string taskId)
    {
        if (!_runtimes.TryGetValue(taskId, out var rt)) return;
        if (rt.Worker is null || rt.Worker.IsCompleted) return;

        rt.PauseRequested = true;
        rt.Cts.Cancel();

        foreach (var page in rt.Task.Pages.Where(p => p.Status is DownloadStatus.Downloading
                     or DownloadStatus.FetchingUrl or DownloadStatus.FetchingInfo or DownloadStatus.Merging))
        {
            page.Status = DownloadStatus.Paused;
            page.ErrorMessage = null;
        }

        SetTaskStatus(rt, DownloadStatus.Paused);
    }

    public void Resume(string taskId)
    {
        if (!_runtimes.TryGetValue(taskId, out var rt)) return;
        if (rt.Worker is { IsCompleted: false }) return;

        rt.PauseRequested = false;
        rt.CancelRequested = false;

        foreach (var page in rt.Task.Pages.Where(p => p.Status is DownloadStatus.Paused))
            page.Status = DownloadStatus.Pending;

        Restart(rt, DownloadStatus.Pending);
    }

    public void Cancel(string taskId, bool keepTemp)
    {
        if (!_runtimes.TryGetValue(taskId, out var rt)) return;

        rt.CancelRequested = true;
        rt.KeepTemp = keepTemp;

        var running = rt.Worker is { IsCompleted: false };
        rt.Cts.Cancel();

        if (!running) FinalizeCancel(rt);
    }

    public void RetryFailed(string taskId)
    {
        if (!_runtimes.TryGetValue(taskId, out var rt)) return;
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
        Restart(rt, DownloadStatus.Pending);
    }

    public bool IsRunning(string taskId) =>
        _runtimes.TryGetValue(taskId, out var rt) && rt.Worker is { IsCompleted: false };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var rt in _runtimes.Values)
        {
            try { rt.Cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    // ---------------------------------------------------------------- 内部

    private void Restart(TaskRuntime rt, DownloadStatus initial)
    {
        rt.Cts.Dispose();
        rt.Cts = new CancellationTokenSource();
        SetTaskStatus(rt, initial);
        rt.Worker = Task.Run(() => RunAsync(rt), CancellationToken.None);
    }

    private async Task RunAsync(TaskRuntime rt)
    {
        var task = rt.Task;
        var ct = rt.Cts.Token;
        var acquired = false;

        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;

            SetTaskStatus(rt, DownloadStatus.FetchingInfo);
            var info = await _api.GetVideoInfoAsync(task.Bvid, ct).ConfigureAwait(false);
            EnsureSupported(info);

            foreach (var page in task.Pages)
            {
                ct.ThrowIfCancellationRequested();
                if (page.Status == DownloadStatus.Completed) continue;

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
            if (acquired) _gate.Release();
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

        var videoPath = Path.Combine(tempDir, "video.m4s");
        var audioPath = Path.Combine(tempDir, "audio.m4s");

        var videoLen = video.ContentLength ?? EstimateBytes(video.Bandwidth, duration);
        var audioLen = audio is null ? 0 : audio.ContentLength ?? EstimateBytes(audio.Bandwidth, duration);
        page.TotalBytes = videoLen + audioLen;
        page.ReceivedBytes = SpeedMeasured(File.Exists(videoPath), videoPath, videoLen);

        SetPageStatus(rt, page, DownloadStatus.Downloading);

        var videoProgress = new DirectProgress<DownloadProgress>(p =>
        {
            page.ReceivedBytes = p.ReceivedBytes;
            page.Progress = page.TotalBytes > 0
                ? DownloadWeight * Math.Clamp((double)p.ReceivedBytes / page.TotalBytes, 0, 1)
                : 0;
            ReportPageProgress(rt, page, p.SpeedBytesPerSecond);
        });

        var videoResult = await _downloader.DownloadAsync(
            new RemoteFile(video.BaseUrl, video.BackupUrls, video.ContentLength ?? 0),
            videoPath, new DownloadOptions(), videoProgress, ct).ConfigureAwait(false);
        videoLen = videoResult.Bytes;

        if (audio is not null)
        {
            var audioProgress = new DirectProgress<DownloadProgress>(p =>
            {
                page.ReceivedBytes = videoLen + p.ReceivedBytes;
                page.Progress = page.TotalBytes > 0
                    ? DownloadWeight * Math.Clamp((double)page.ReceivedBytes / page.TotalBytes, 0, 1)
                    : 0;
                ReportPageProgress(rt, page, p.SpeedBytesPerSecond);
            });

            var audioResult = await _downloader.DownloadAsync(
                new RemoteFile(audio.BaseUrl, audio.BackupUrls, audio.ContentLength ?? 0),
                audioPath, new DownloadOptions(), audioProgress, ct).ConfigureAwait(false);
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
            page.Progress = DownloadWeight + (1 - DownloadWeight) * Math.Clamp(p.Percent / 100, 0, 1);
            ReportPageProgress(rt, page, 0);
        });

        await _merger.MergeAsync(new MergeRequest(videoPath, audioPath, outputPath, duration), mergeProgress, ct)
                     .ConfigureAwait(false);

        var subDirectory = task.TotalPages > 1 || task.Pages.Count > 1
            ? Path.Combine(task.SaveDirectory, DefaultFileNameStrategy.Sanitize(task.Title))
            : task.SaveDirectory;

        var fileName = _fileName.Build(page.Index, string.IsNullOrWhiteSpace(page.Part) ? task.Title : page.Part,
            task.UpName);

        page.FinalPath = StoragePaths.Commit(outputPath, subDirectory, fileName);
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
        var done = task.Pages.Zip(weights).Sum(t => t.First.Progress * t.Second);
        task.Progress = Math.Clamp(done / totalWeight, 0, 1);

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

    private void RaiseStateChanged(TaskRuntime rt, DownloadStatus previous, DownloadStatus current) =>
        StateChanged?.Invoke(this, new TaskStateChangedEventArgs
        {
            Task = rt.Task,
            Previous = previous,
            Current = current
        });

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

    private static long SpeedMeasured(bool exists, string path, long length) =>
        exists ? Math.Min(new FileInfo(path).Length, Math.Max(length, 0)) : 0;

    private sealed class TaskRuntime(VideoTask task, DownloadRequest request)
    {
        public VideoTask Task { get; } = task;
        public DownloadRequest Request { get; } = request;
        public CancellationTokenSource Cts { get; set; } = new();
        public bool PauseRequested { get; set; }
        public bool CancelRequested { get; set; }
        public bool KeepTemp { get; set; } = true;
        public Task? Worker { get; set; }
        public long LastProgressTicks { get; set; }
    }

    /// <summary>同步转发的 Progress：避免 Progress&lt;T&gt; 默认投递到线程池造成进度乱序。</summary>
    private sealed class DirectProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
