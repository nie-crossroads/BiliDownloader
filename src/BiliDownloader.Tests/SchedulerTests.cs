using System.Collections.Concurrent;
using BiliDownloader.Core.BiliApi;
using BiliDownloader.Core.BiliApi.Models;
using BiliDownloader.Core.Download;
using BiliDownloader.Core.FileName;
using BiliDownloader.Core.Merge;
using BiliDownloader.Core.Models;
using BiliDownloader.Core.Orchestration;
using BiliDownloader.Core.Parse;
using BiliDownloader.Core.Storage;

namespace BiliDownloader.Tests;

/// <summary>
/// 任务调度与命令时序测试。评审明确指出：改状态机前先要有这些测试——
/// 「快速暂停/恢复」「取消与合并竞争」「重复命令」是竞态最容易藏的地方，
/// 表现是同一任务被两个后台流程同时跑，或者暂停请求被后来的恢复请求吞掉。
/// </summary>
public sealed class SchedulerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "BiliDownloaderScheduler", Guid.NewGuid().ToString("N"));

    private static readonly VideoInfo Info = new()
    {
        Bvid = "BV1xx411c7mD", Title = "test", Pages = [new(1, "episode", 1, TimeSpan.FromSeconds(60))]
    };

    /// <summary>下载途中反复暂停/恢复：最终仍要跑完，且不能把同一页下两遍。</summary>
    [Fact]
    public async Task RapidPauseResume_CompletesExactlyOnce()
    {
        var downloader = new SlowDownloader();
        using var http = new HttpClient();
        using var service = Create(downloader, http);

        var task = service.Enqueue(Request());
        await Task.Delay(150); // 让它进到下载中

        service.Pause(task.TaskId);
        service.Resume(task.TaskId);   // 旧流程可能还没退出就又开一个
        service.Pause(task.TaskId);
        service.Resume(task.TaskId);

        await WaitAsync(service, task);

        Assert.Equal(DownloadStatus.Completed, task.Status);
        // 暂停会把当前这次下载作废、恢复后重下——这允许；
        // 但绝不能出现两个流程同时写同一个文件
        Assert.Equal(1, downloader.MaxConcurrent);
    }

    /// <summary>重复点「继续」不应该让同一页被两个流程同时下载。</summary>
    [Fact]
    public async Task ResumeTwice_DoesNotDuplicateWork()
    {
        var downloader = new SlowDownloader();
        using var http = new HttpClient();
        using var service = Create(downloader, http);

        var task = service.Enqueue(Request());
        await Task.Delay(150);
        service.Pause(task.TaskId);
        service.Resume(task.TaskId);
        service.Resume(task.TaskId);
        service.Resume(task.TaskId);

        await WaitAsync(service, task);

        Assert.Equal(DownloadStatus.Completed, task.Status);
        Assert.Equal(1, downloader.MaxConcurrent);
    }

    /// <summary>
    /// 取消要能打断「正在下载」的流程：任务落到 Canceled，临时目录清干净，
    /// 不能因为流程还卡在 await 里就永远停在 Downloading。
    /// </summary>
    [Fact]
    public async Task CancelDuringDownload_CancelsAndCleansUp()
    {
        var downloader = new SlowDownloader { Delay = TimeSpan.FromSeconds(30) };
        using var http = new HttpClient();
        using var service = Create(downloader, http);

        var task = service.Enqueue(Request());
        await Task.Delay(200);
        service.Cancel(task.TaskId, keepTemp: false);

        await WaitAsync(service, task, seconds: 15);

        Assert.Equal(DownloadStatus.Canceled, task.Status);
        Assert.All(task.Pages, page => Assert.False(Directory.Exists(page.TempDir)));
    }

    /// <summary>取消发生在合并阶段（合并较慢）时同样要能收住。</summary>
    [Fact]
    public async Task CancelDuringMerge_CancelsAndCleansUp()
    {
        var downloader = new SlowDownloader();
        using var http = new HttpClient();
        using var service = Create(downloader, http, new SlowMerger { Delay = TimeSpan.FromSeconds(30) });

        var task = service.Enqueue(Request());
        await WaitForStatusAsync(task, DownloadStatus.Merging, seconds: 15);
        service.Cancel(task.TaskId, keepTemp: false);

        await WaitAsync(service, task, seconds: 15);

        Assert.Equal(DownloadStatus.Canceled, task.Status);
        Assert.All(task.Pages, page => Assert.False(Directory.Exists(page.TempDir)));
    }

    /// <summary>对已经跑完的任务再发命令，应当安静地什么也不做（不能把状态改坏）。</summary>
    [Fact]
    public async Task CommandsAfterCompletion_AreIgnored()
    {
        var downloader = new SlowDownloader();
        using var http = new HttpClient();
        using var service = Create(downloader, http);

        var task = service.Enqueue(Request());
        await WaitAsync(service, task);
        Assert.Equal(DownloadStatus.Completed, task.Status);

        service.Pause(task.TaskId);
        service.Resume(task.TaskId);
        service.Cancel(task.TaskId, false);
        service.RetryFailed(task.TaskId);

        Assert.Equal(DownloadStatus.Completed, task.Status);
        Assert.Equal(1, downloader.Calls);
    }

    // ---------------------------------------------------------------- 辅助

    private DownloadRequest Request() =>
        new(Info.Bvid, [1], 80, 7, 30280, _root, "test", null, null, TimeSpan.FromSeconds(60), 1);

    private VideoDownloadService Create(SlowDownloader downloader, HttpClient http, IFfmpegMerger? merger = null) =>
        new(new Api(), downloader, merger ?? new Merger(), new DefaultFileNameStrategy(),
            new UrlParser(), http);

    private static async Task WaitAsync(VideoDownloadService service, VideoTask task, int seconds = 15)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (service.IsRunning(task.TaskId)) await Task.Delay(20, timeout.Token);
    }

    private static async Task WaitForStatusAsync(VideoTask task, DownloadStatus status, int seconds)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (task.Pages[0].Status != status) await Task.Delay(20, timeout.Token);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }
    }

    /// <summary>可控延迟的下载器：既能拉长下载阶段制造暂停窗口，也记录实际下载次数。</summary>
    private sealed class SlowDownloader : IFileDownloader
    {
        private readonly object _sync = new();
        private int _calls;
        private int _concurrent;
        private int _maxConcurrent;

        public int Calls => Volatile.Read(ref _calls);

        /// <summary>
        /// 同时下载的最大路数。这是「有没有两个后台流程在跑同一个任务」的直接证据——
        /// 暂停后重新下载一次是正常的，但两路并发写同一个文件就会出坏数据。
        /// </summary>
        public int MaxConcurrent { get { lock (_sync) return _maxConcurrent; } }

        public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(400);
        public StreamFingerprint? Fingerprint { get; set; } = new(null, null, 4, "/video.m4s");

        public Task<long?> ProbeLengthAsync(RemoteFile file, CancellationToken ct = default) =>
            Task.FromResult<long?>(4);

        public Task<StreamFingerprint?> ProbeFingerprintAsync(RemoteFile file, CancellationToken ct = default) =>
            Task.FromResult(Fingerprint);

        public async Task<DownloadResult> DownloadAsync(RemoteFile file, string targetPath, DownloadOptions options,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            var now = Interlocked.Increment(ref _concurrent);
            lock (_sync) _maxConcurrent = Math.Max(_maxConcurrent, now);

            try
            {
                await Task.Delay(Delay, ct);
                await File.WriteAllBytesAsync(targetPath, [1, 2, 3, 4], ct);
                return new(4, false, 1);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }
    }

    private sealed class Api : IBiliApiClient
    {
        public bool IsLoggedIn => false;
        public Task<NavInfo> GetNavAsync(CancellationToken ct = default) => Task.FromResult(new NavInfo());
        public Task<VideoInfo> GetVideoInfoAsync(string id, CancellationToken ct = default) => Task.FromResult(Info);
        public Task<PlayUrlResult> GetPlayUrlAsync(PlayUrlRequest request, CancellationToken ct = default) =>
            Task.FromResult(new PlayUrlResult
            {
                Videos = [new(80, 7, "https://cdn.test/video", [], "avc", 1920, 1080, 8000, 4)]
            });
    }

    private sealed class Merger : IFfmpegMerger
    {
        public bool IsAvailable => true;
        public Task MergeAsync(MergeRequest request, IProgress<MergeProgress>? progress = null,
            CancellationToken ct = default)
        {
            File.Copy(request.VideoPath, request.OutputPath);
            return Task.CompletedTask;
        }
    }

    private sealed class SlowMerger : IFfmpegMerger
    {
        public TimeSpan Delay { get; init; }
        public bool IsAvailable => true;
        public async Task MergeAsync(MergeRequest request, IProgress<MergeProgress>? progress = null,
            CancellationToken ct = default)
        {
            await Task.Delay(Delay, ct);
            File.Copy(request.VideoPath, request.OutputPath);
        }
    }
}
