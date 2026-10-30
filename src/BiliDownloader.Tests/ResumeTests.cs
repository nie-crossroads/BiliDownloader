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
/// 跨重启恢复测试。核心命题不是「能不能接着下」，而是
/// <b>「不能确认是同一条流时，绝不能把两段拼起来」</b>——
/// 拼错的文件长度刚好、进度条 100%，只有解码或播放时才暴露，是最坏的一类 bug。
/// </summary>
public sealed class ResumeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "BiliDownloaderResume", Guid.NewGuid().ToString("N"));

    private static readonly VideoInfo Info = new()
    {
        Bvid = "BV1xx411c7mD", Title = "test", Pages = [new(1, "episode", 1, TimeSpan.FromSeconds(60))]
    };

    // ---------------------------------------------------------------- 流身份判定

    [Fact]
    public void SameETag_Matches()
    {
        var a = new StreamFingerprint("\"abc\"", null, 100);
        var b = new StreamFingerprint("\"ABC\"", "ignored", 100);
        Assert.True(a.Matches(b));
    }

    [Fact]
    public void DifferentETag_DoesNotMatch()
    {
        var a = new StreamFingerprint("\"abc\"", null, 100);
        var b = new StreamFingerprint("\"xyz\"", null, 100);
        Assert.False(a.Matches(b));
    }

    [Fact]
    public void SameLastModified_MatchesWhenNoETag()
    {
        var a = new StreamFingerprint(null, "Wed, 21 Oct 2015 07:28:00 GMT", 100);
        var b = new StreamFingerprint(null, "Wed, 21 Oct 2015 07:28:00 GMT", 100);
        Assert.True(a.Matches(b));
    }

    /// <summary>
    /// 长度一样、但服务端没给任何身份标识：无法确认，必须按「变了」处理。
    /// 这条是整套恢复机制的安全底线。
    /// </summary>
    [Fact]
    public void NoIdentifierAtAll_DoesNotMatch()
    {
        var a = new StreamFingerprint(null, null, 100);
        var b = new StreamFingerprint(null, null, 100);
        Assert.False(a.Matches(b));
    }

    [Fact]
    public void NullOther_DoesNotMatch()
    {
        Assert.False(new StreamFingerprint("\"abc\"", null, 1).Matches(null));
    }

    // ---------------------------------------------------------------- 恢复行为

    [Fact]
    public void SamePathAndLength_MatchesWhenNoHeaders()
    {
        // B 站 CDN 实测常常既不返回 ETag 也不返回 Last-Modified，
        // 这时靠直链路径（含 cid / 清晰度 / 编码）+ 长度来认定是不是同一条流
        var a = new StreamFingerprint(null, null, 100, "/upgcx/137649199-1-80.m4s");
        var b = new StreamFingerprint(null, null, 100, "/upgcx/137649199-1-80.m4s");
        Assert.True(a.Matches(b));
    }

    /// <summary>不同 CDN 的路径前缀不一样，但文件名（含 cid / 清晰度 / 编码）一致——仍算同一条流。</summary>
    [Fact]
    public void SameFileNameAcrossCdnHosts_Matches()
    {
        var a = new StreamFingerprint(null, null, 100, "/v1/resource/upgcxcode/99/91/137649199/137649199_da2-1-30080.m4s");
        var b = new StreamFingerprint(null, null, 100, "/upgcxcode/99/91/137649199/137649199_da2-1-30080.m4s");
        Assert.True(a.Matches(b));
    }

    [Fact]
    public void DifferentFileName_DoesNotMatch()
    {
        var a = new StreamFingerprint(null, null, 100, "/upgcx/137649199-1-80.m4s");
        var b = new StreamFingerprint(null, null, 100, "/upgcx/137649199-1-64.m4s");
        Assert.False(a.Matches(b));
    }

    [Fact]
    public void SamePathButDifferentLength_DoesNotMatch()
    {
        var a = new StreamFingerprint(null, null, 100, "/upgcx/1.m4s");
        var b = new StreamFingerprint(null, null, 200, "/upgcx/1.m4s");
        Assert.False(a.Matches(b));
    }

    [Fact]
    public void PathOf_IgnoresAuthQueryString()
    {
        // 鉴权参数每次重取直链都会变，不能拿来当身份；路径才是内容标识
        Assert.Equal("/upgcx/1.m4s",
            StreamFingerprint.PathOf("https://cdn1.test/upgcx/1.m4s?auth_key=abc&e=123"));
        Assert.Null(StreamFingerprint.PathOf(string.Empty));
    }

    /// <summary>
    /// 指纹一致（B 站真实场景：无 ETag，靠路径 + 长度认定）：上次下的字节保留，从断点继续。
    /// </summary>
    [Fact]
    public async Task SameStream_ResumesFromExistingBytes()
    {
        Seed("task-same", new StreamFingerprint(null, null, 4, "/video.m4s"), bytesOnDisk: 2);
        var downloader = new ProbeDownloader { Fingerprint = new StreamFingerprint(null, null, 4, "/video.m4s") };

        using var http = new HttpClient();
        using var service = Create(downloader, http);

        var restored = service.LoadResumable(_root);
        var task = Assert.Single(restored);
        Assert.Equal(DownloadStatus.Paused, task.Status);
        Assert.Equal(2, task.Pages[0].ReceivedBytes);

        Assert.True(Resumed(service, task));
        await WaitAsync(service, task);

        Assert.Equal(DownloadStatus.Completed, task.Status);
        // 保留了已落盘的 2 字节：续传
        Assert.Equal([2L], downloader.StartLengths);
    }

    /// <summary>
    /// 指纹不一致（重启后重取的直链指向了另一条流）：已落盘字节全部作废，整段重下。
    /// 若这里拼起来，产出的文件长度正确、进度 100%，内容却是坏的。
    /// </summary>
    [Fact]
    public async Task ChangedStream_DiscardsExistingBytes()
    {
        Seed("task-changed", new StreamFingerprint("\"etag-1\"", null, 4), bytesOnDisk: 2);
        var downloader = new ProbeDownloader { Fingerprint = new StreamFingerprint("\"etag-2\"", null, 4) };

        using var http = new HttpClient();
        using var service = Create(downloader, http);

        var task = Assert.Single(service.LoadResumable(_root));
        Assert.True(Resumed(service, task));
        await WaitAsync(service, task);

        Assert.Equal(DownloadStatus.Completed, task.Status);
        // 旧的 2 字节被丢弃，从 0 开始：绝不做无校验的拼接
        Assert.Equal([0L], downloader.StartLengths);
    }

    /// <summary>路径变了（重取到的直链指向别的资源）：同样作废重下。</summary>
    [Fact]
    public async Task ChangedPath_DiscardsExistingBytes()
    {
        Seed("task-path", new StreamFingerprint(null, null, 4, "/video.m4s"), bytesOnDisk: 2);
        var downloader = new ProbeDownloader { Fingerprint = new StreamFingerprint(null, null, 4, "/other.m4s") };

        using var http = new HttpClient();
        using var service = Create(downloader, http);

        var task = Assert.Single(service.LoadResumable(_root));
        Assert.True(Resumed(service, task));
        await WaitAsync(service, task);

        Assert.Equal([0L], downloader.StartLengths);
    }

    /// <summary>服务端什么都不给（连路径都拿不到）：无法确认是同一条流，保守整段重下。</summary>
    [Fact]
    public async Task UnidentifiableStream_DiscardsExistingBytes()
    {
        Seed("task-unknown", new StreamFingerprint(null, null, 4), bytesOnDisk: 2);
        var downloader = new ProbeDownloader { Fingerprint = new StreamFingerprint(null, null, 4) };

        using var http = new HttpClient();
        using var service = Create(downloader, http);

        var task = Assert.Single(service.LoadResumable(_root));
        Assert.True(Resumed(service, task));
        await WaitAsync(service, task);

        Assert.Equal([0L], downloader.StartLengths);
    }

    /// <summary>清单生命周期：入队就落盘，跑完就清掉（没恢复价值的条目不该留着）。</summary>
    [Fact]
    public async Task Manifest_AppearsOnEnqueue_AndClearsOnCompletion()
    {
        var downloader = new ProbeDownloader { Fingerprint = new StreamFingerprint("\"etag-1\"", null, 4) };
        using var http = new HttpClient();
        using var service = Create(downloader, http);

        var task = service.Enqueue(Request());
        Assert.True(File.Exists(ManifestStore.PathOf(_root)));

        await WaitAsync(service, task);
        Assert.Equal(DownloadStatus.Completed, task.Status);
        Assert.Empty(ManifestStore.Load(_root).Tasks);
    }

    [Fact]
    public void LoadResumable_ReturnsEmptyWhenNoManifest()
    {
        using var http = new HttpClient();
        using var service = Create(new ProbeDownloader(), http);
        Assert.Empty(service.LoadResumable(_root));
    }

    /// <summary>清单损坏（半截 JSON）不能拖垮启动。</summary>
    [Fact]
    public void LoadResumable_ToleratesCorruptedManifest()
    {
        Directory.CreateDirectory(StoragePaths.TempRoot(_root));
        File.WriteAllText(ManifestStore.PathOf(_root), "{ this is not json");

        using var http = new HttpClient();
        using var service = Create(new ProbeDownloader(), http);
        Assert.Empty(service.LoadResumable(_root));
    }

    /// <summary>已经全部下完的任务没有恢复价值。</summary>
    [Fact]
    public void LoadResumable_SkipsFullyCompletedTasks()
    {
        Seed("task-done", new StreamFingerprint("\"etag-1\"", null, 4), bytesOnDisk: 4,
            status: nameof(DownloadStatus.Completed));

        using var http = new HttpClient();
        using var service = Create(new ProbeDownloader(), http);
        Assert.Empty(service.LoadResumable(_root));
    }

    // ---------------------------------------------------------------- 辅助

    /// <summary>伪造「上次程序关掉时」的状态：临时目录里有半个文件，清单里有记录。</summary>
    private void Seed(string taskId, StreamFingerprint? videoFingerprint, int bytesOnDisk,
        string status = nameof(DownloadStatus.Paused))
    {
        var tempDir = StoragePaths.PageTempDir(_root, taskId, 1);
        Directory.CreateDirectory(tempDir);
        File.WriteAllBytes(Path.Combine(tempDir, "video.m4s"), new byte[bytesOnDisk]);

        var manifest = new DownloadManifest
        {
            Tasks =
            [
                new ManifestTaskEntry(taskId, Info.Bvid, "test", null, null,
                    TimeSpan.FromSeconds(60).Ticks, 1, 1, 2, true,
                    FileNameRule.IndexAndPart, 2, ExistingFileAction.Rename, DateTimeOffset.Now,
                    [new ManifestPageEntry(1, 1, "episode", 80, 7, 30280, status, bytesOnDisk, 4,
                        videoFingerprint, null)])
            ]
        };

        ManifestStore.Save(_root, manifest);
    }

    private DownloadRequest Request() =>
        new(Info.Bvid, [1], 80, 7, 30280, _root, "test", null, null, TimeSpan.FromSeconds(60), 1);

    private VideoDownloadService Create(ProbeDownloader downloader, HttpClient http) =>
        new(new Api(), downloader, new Merger(), new DefaultFileNameStrategy(), new UrlParser(), http);

    private static async Task WaitAsync(VideoDownloadService service, VideoTask task)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (service.IsRunning(task.TaskId)) await Task.Delay(10, timeout.Token);
    }

    /// <summary>
    /// 继续一个恢复出来的任务。Resume 没有返回值，这里用「暂停的页是否已重新排队」表达接上了——
    /// 比查 IsRunning 稳定（小任务可能瞬间跑完，查在不在跑会有竞态）。
    /// </summary>
    private static bool Resumed(VideoDownloadService service, VideoTask task)
    {
        service.Resume(task.TaskId);
        return task.Pages.All(p => p.Status != DownloadStatus.Paused);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// 可观测的下载器替身：除了返回可控指纹，还记下每次下载开始时目标文件已有多少字节——
    /// 这是判断「续传」还是「作废重下」的直接证据。
    /// </summary>
    private sealed class ProbeDownloader : IFileDownloader
    {
        public ConcurrentBag<long> StartLengths { get; } = [];
        public StreamFingerprint? Fingerprint { get; set; }

        public Task<long?> ProbeLengthAsync(RemoteFile file, CancellationToken ct = default) =>
            Task.FromResult<long?>(Fingerprint?.Length ?? 4);

        public Task<StreamFingerprint?> ProbeFingerprintAsync(RemoteFile file, CancellationToken ct = default) =>
            Task.FromResult(Fingerprint);

        public async Task<DownloadResult> DownloadAsync(RemoteFile file, string targetPath, DownloadOptions options,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            StartLengths.Add(File.Exists(targetPath) ? new FileInfo(targetPath).Length : 0);
            await File.WriteAllBytesAsync(targetPath, [1, 2, 3, 4], ct);
            return new(4, false, 1);
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
}
