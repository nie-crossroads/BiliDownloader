using BiliDownloader.Core.BiliApi;
using BiliDownloader.Core.BiliApi.Models;
using BiliDownloader.Core.Download;
using BiliDownloader.Core.Errors;
using BiliDownloader.Core.FileName;
using BiliDownloader.Core.Merge;
using BiliDownloader.Core.Models;
using BiliDownloader.Core.Orchestration;
using BiliDownloader.Core.Parse;
using BiliDownloader.Core.Storage;

namespace BiliDownloader.Tests;

public sealed class OrchestrationTests
{
    [Theory]
    [InlineData(true, false, ErrorKind.PaidContent)]
    [InlineData(false, true, ErrorKind.VideoUnavailable)]
    public async Task UnsupportedContent_IsRejectedBeforePlayUrl(bool paid, bool season, ErrorKind error)
    {
        var api = new Api { Info = Info with { IsPaidContent = paid, HasUgcSeason = season } };
        using var http = new HttpClient();
        using var service = Create(api, new Downloader(), http);
        var ex = await Assert.ThrowsAsync<BiliException>(() => service.ProbeAsync("BV1xx411c7mD"));
        Assert.Equal(error, ex.Kind);
        var task = service.Enqueue(Request(Path.GetTempPath()));
        await WaitAsync(service, task);
        Assert.Equal(DownloadStatus.Failed, task.Status);
        Assert.Equal(error, task.Error);
        Assert.Equal(0, api.PlayCalls);
    }

    [Fact]
    public async Task DuplicateTasks_HaveIndependentWorkspaces_AndDoNotDownloadEstimatedLengths()
    {
        var root = Path.Combine(Path.GetTempPath(), "BiliDownloaderTests", Guid.NewGuid().ToString("N"));
        var downloader = new Downloader();
        using var http = new HttpClient();
        using var service = Create(new Api(), downloader, http);
        try
        {
            var first = service.Enqueue(Request(root));
            var second = service.Enqueue(Request(root));
            await Task.WhenAll(WaitAsync(service, first), WaitAsync(service, second));
            Assert.Equal(DownloadStatus.Completed, first.Status);
            Assert.Equal(DownloadStatus.Completed, second.Status);
            Assert.NotEqual(first.Pages[0].TempDir, second.Pages[0].TempDir);
            Assert.NotEqual(first.FinalPath, second.FinalPath);
            Assert.All(downloader.Lengths, length => Assert.Equal(0, length));
            Assert.Equal(2, downloader.Lengths.Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExistingFilePolicy_Skip_MarksPageSkippedAndKeepsExistingFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "BiliDownloaderTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var http = new HttpClient();
        using var service = Create(new Api(), new Downloader(), http);

        try
        {
            // 默认命名规则下这一页的文件名是「01 episode」
            var existing = Path.Combine(root, "01 episode.mp4");
            await File.WriteAllTextAsync(existing, "original");

            var task = service.Enqueue(Request(root, ExistingFileAction.Skip));
            await WaitAsync(service, task);

            Assert.Equal(DownloadStatus.Skipped, task.Pages[0].Status);
            Assert.Equal("original", await File.ReadAllTextAsync(existing));
            Assert.Equal(existing, task.Pages[0].FinalPath);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static readonly VideoInfo Info = new()
    {
        Bvid = "BV1xx411c7mD", Title = "test", Pages = [new(1, "episode", 1, TimeSpan.FromSeconds(60))]
    };
    private static DownloadRequest Request(string directory, ExistingFileAction policy = ExistingFileAction.Rename) =>
        new(Info.Bvid, [1, 1], 80, 7, 30280, directory, "test", null, null, TimeSpan.FromSeconds(60), 1,
            null, policy, null);
    private static VideoDownloadService Create(Api api, Downloader downloader, HttpClient http) =>
        new(api, downloader, new Merger(), new DefaultFileNameStrategy(), new UrlParser(), http);
    private static async Task WaitAsync(VideoDownloadService service, VideoTask task)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (service.IsRunning(task.TaskId)) await Task.Delay(10, timeout.Token);
    }
    private sealed class Api : IBiliApiClient
    {
        public VideoInfo Info { get; init; } = OrchestrationTests.Info;
        public int PlayCalls;
        public bool IsLoggedIn => false;
        public Task<NavInfo> GetNavAsync(CancellationToken ct = default) => Task.FromResult(new NavInfo());
        public Task<VideoInfo> GetVideoInfoAsync(string id, CancellationToken ct = default) => Task.FromResult(Info);
        public Task<PlayUrlResult> GetPlayUrlAsync(PlayUrlRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref PlayCalls);
            return Task.FromResult(new PlayUrlResult
            {
                Videos = [new(80, 7, "https://cdn.test/video", [], "avc", 1920, 1080, 8000, null)]
            });
        }
    }
    private sealed class Downloader : IFileDownloader
    {
        public System.Collections.Concurrent.ConcurrentBag<long> Lengths { get; } = [];
        /// <summary>探测返回的指纹；测试可换成「另一条流」来验证恢复时会整段重下。</summary>
        public StreamFingerprint? Fingerprint { get; set; } = new("\"etag-1\"", null, 4);
        public Task<long?> ProbeLengthAsync(RemoteFile file, CancellationToken ct = default) => Task.FromResult<long?>(4);
        public Task<StreamFingerprint?> ProbeFingerprintAsync(RemoteFile file, CancellationToken ct = default) =>
            Task.FromResult(Fingerprint);
        public async Task<DownloadResult> DownloadAsync(RemoteFile file, string targetPath, DownloadOptions options,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            Lengths.Add(file.ExpectedLength);
            await File.WriteAllBytesAsync(targetPath, [1, 2, 3, 4], ct);
            return new(4, false, 1);
        }
    }
    private sealed class Merger : IFfmpegMerger
    {
        public bool IsAvailable => true;
        public Task MergeAsync(MergeRequest request, IProgress<MergeProgress>? progress = null, CancellationToken ct = default)
        {
            File.Copy(request.VideoPath, request.OutputPath);
            return Task.CompletedTask;
        }
    }
}
