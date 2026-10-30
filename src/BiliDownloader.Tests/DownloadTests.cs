using System.Net;
using System.Net.Http.Headers;
using BiliDownloader.Core.Download;
using BiliDownloader.Core.Errors;

namespace BiliDownloader.Tests;

public sealed class DownloadTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "BiliDownloaderTests", Guid.NewGuid().ToString("N"));
    private string Target => Path.Combine(_directory, "video.m4s");

    [Fact]
    public async Task Resume_WhenRangeIgnored_RestartsWithoutAppending()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(Target, [9, 9]);
        using var http = Client(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) });
        var result = await new HttpFileDownloader(http).DownloadAsync(Remote(4), Target, new());
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(Target));
        Assert.False(result.Resumed);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(20)]
    public async Task Range_UsesServerLengthInsteadOfStaleLength(int expected)
    {
        using var http = Client(request =>
        {
            var start = request.Headers.Range!.Ranges.Single().From!.Value;
            var end = Math.Min(request.Headers.Range.Ranges.Single().To!.Value, 5);
            return Partial(start, end, 6);
        });
        var result = await new HttpFileDownloader(http).DownloadAsync(Remote(expected), Target, new(2));
        Assert.Equal(6, result.Bytes);
        Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 5 }, await File.ReadAllBytesAsync(Target));
    }

    [Fact]
    public async Task InvalidRange_DoesNotAcceptCorruptData()
    {
        using var http = Client(_ => Partial(1, 3, 4));
        var ex = await Assert.ThrowsAsync<BiliException>(() =>
            new HttpFileDownloader(http).DownloadAsync(Remote(4), Target, new()));
        Assert.Equal(ErrorKind.StreamExpired, ex.Kind);
        Assert.Equal(0, new FileInfo(Target).Length);
    }

    [Fact]
    public async Task RangeNotSatisfiable_WithoutMatchingLength_IsNotSuccess()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(Target, [1, 2]);
        using var http = Client(_ => new(HttpStatusCode.RequestedRangeNotSatisfiable));
        await Assert.ThrowsAsync<BiliException>(() =>
            new HttpFileDownloader(http).DownloadAsync(Remote(4), Target, new()));
    }

    [Fact]
    public async Task EmptyResponse_FailsWithinRetryBudget()
    {
        var calls = 0;
        using var http = Client(_ =>
        {
            calls++;
            var response = Partial(0, 3, 4);
            response.Content = new ByteArrayContent([]);
            response.Content.Headers.ContentRange = new(0, 3, 4);
            return response;
        });
        await Assert.ThrowsAsync<BiliException>(() =>
            new HttpFileDownloader(http).DownloadAsync(Remote(4), Target, new(MaxRetriesPerUrl: 0)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Probe_PreservesCallerCancellation()
    {
        using var cts = new CancellationTokenSource();
        using var http = Client(_ =>
        {
            cts.Cancel();
            throw new TaskCanceledException();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new HttpFileDownloader(http).ProbeLengthAsync(Remote(0), cts.Token));
    }

    private static RemoteFile Remote(long length) => new("https://cdn.test/video", [], length);

    // ---------------------------------------------------------------- 多连接分片

    /// <summary>按请求里的 Range 返回对应字节，用来模拟支持断点续传的 CDN。</summary>
    private static HttpClient RangeClient(long total, Action? onRequest = null) =>
        Client(request =>
        {
            onRequest?.Invoke();
            var range = request.Headers.Range!.Ranges.Single();
            var from = range.From!.Value;
            var to = Math.Min(range.To!.Value, total - 1);
            return Partial(from, to, total);
        });

    private static byte[] Sequence(int length) => Enumerable.Range(0, length).Select(i => (byte)i).ToArray();

    [Fact]
    public async Task ChunkedDownload_ProducesContiguousFile_AndCleansUp()
    {
        Directory.CreateDirectory(_directory);
        using var http = RangeClient(100);
        var result = await new HttpFileDownloader(http)
            .DownloadAsync(Remote(100), Target, new(ChunkSizeBytes: 25, Connections: 4));

        Assert.Equal(100, result.Bytes);
        // 关键：拼出来的必须是 0..99 的连续序列，分片顺序错了这里就会露馅
        Assert.Equal(Sequence(100), await File.ReadAllBytesAsync(Target));
        Assert.Empty(Directory.GetFiles(_directory, "*.part*"));
        Assert.False(File.Exists(PartialFile.PlanPath(Target)));
    }

    [Fact]
    public async Task ChunkedDownload_ResumesFromExistingParts()
    {
        Directory.CreateDirectory(_directory);
        var requests = 0;

        // 上一次跑到一半的状态：计划还在，part0 下完、part1 只下了一半。
        // 注意 part1 负责的是 25..49，所以它的前半段必须是 25..34，而不是从 0 开始。
        PartialFile.WritePlan(Target, new ChunkPlan(0, 100, 4, 25));
        await File.WriteAllBytesAsync(PartialFile.PartPath(Target, 0), Sequence(25));
        await File.WriteAllBytesAsync(PartialFile.PartPath(Target, 1),
            Enumerable.Range(25, 10).Select(i => (byte)i).ToArray());

        using var http = RangeClient(100, () => Interlocked.Increment(ref requests));
        var result = await new HttpFileDownloader(http)
            .DownloadAsync(Remote(100), Target, new(ChunkSizeBytes: 25, Connections: 4));

        Assert.Equal(100, result.Bytes);
        Assert.Equal(Sequence(100), await File.ReadAllBytesAsync(Target));
        // part0 已完成不该再请求，part1 只补后 15 字节：请求数应少于全新下载的 4 次
        Assert.True(requests <= 3, $"实际请求 {requests} 次，续传没有生效");
    }

    [Fact]
    public async Task ChunkedDownload_ChangingPlan_DiscardsStaleParts()
    {
        Directory.CreateDirectory(_directory);

        // 旧计划是 2 片，现在按 25 字节切成 4 片：边界变了，旧分片必须作废
        PartialFile.WritePlan(Target, new ChunkPlan(0, 100, 2, 50));
        await File.WriteAllBytesAsync(PartialFile.PartPath(Target, 0), Sequence(50));
        await File.WriteAllBytesAsync(PartialFile.PartPath(Target, 1), Sequence(50));

        using var http = RangeClient(100);
        var result = await new HttpFileDownloader(http)
            .DownloadAsync(Remote(100), Target, new(ChunkSizeBytes: 25, Connections: 4));

        Assert.Equal(100, result.Bytes);
        Assert.Equal(Sequence(100), await File.ReadAllBytesAsync(Target));
    }

    [Fact]
    public async Task ChunkedDownload_ProbesLengthWhenUnknown()
    {
        Directory.CreateDirectory(_directory);
        using var http = RangeClient(100);
        // ExpectedLength 为 0 时先探测总长度，拿到真实长度后仍然可以走分片路径
        var result = await new HttpFileDownloader(http)
            .DownloadAsync(Remote(0), Target, new(ChunkSizeBytes: 25, Connections: 4));

        Assert.Equal(100, result.Bytes);
        Assert.Equal(Sequence(100), await File.ReadAllBytesAsync(Target));
    }

    // ---------------------------------------------------------------- 备用节点回退开关

    [Fact]
    public async Task ChunkedDownload_RangeIgnored_FallsBackToWholeFile()
    {
        using var http = Client(_ => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Sequence(100))
        });
        await new HttpFileDownloader(http).DownloadAsync(Remote(100), Target,
            new(ChunkSizeBytes: 25, Connections: 4));
        Assert.Equal(Sequence(100), await File.ReadAllBytesAsync(Target));
        Assert.False(File.Exists(PartialFile.PlanPath(Target)));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(25)]
    public async Task ChunkedDownload_InterruptedConcatenation_ResumesWithoutDuplicateBytes(int prefix)
    {
        Directory.CreateDirectory(_directory);
        PartialFile.WritePlan(Target, new ChunkPlan(0, 100, 4, 25));
        await File.WriteAllBytesAsync(Target, Sequence(prefix));
        for (var i = 0; i < 4; i++)
            await File.WriteAllBytesAsync(PartialFile.PartPath(Target, i),
                Enumerable.Range(i * 25, 25).Select(n => (byte)n).ToArray());

        using var http = RangeClient(100);
        await new HttpFileDownloader(http).DownloadAsync(Remote(100), Target,
            new(ChunkSizeBytes: 25, Connections: 4));
        Assert.Equal(Sequence(100), await File.ReadAllBytesAsync(Target));
    }

    [Fact]
    public async Task ChunkedDownload_WrongResourceLength_DoesNotWriteParts()
    {
        using var http = RangeClient(120);
        await Assert.ThrowsAsync<BiliException>(() => new HttpFileDownloader(http)
            .DownloadAsync(Remote(100), Target, new(ChunkSizeBytes: 25, Connections: 4)));
        Assert.All(Directory.GetFiles(_directory, "*.part*"),
            path => Assert.Equal(0, new FileInfo(path).Length));
    }

    [Fact]
    public async Task AllowFallbackDisabled_DoesNotSwitchToBackupUrl()
    {
        var calls = 0;
        using var http = Client(_ =>
        {
            calls++;
            return new(HttpStatusCode.Forbidden);
        });

        var file = new RemoteFile("https://cdn.test/video", ["https://cdn.test/backup"], 4);
        var ex = await Assert.ThrowsAsync<BiliException>(() =>
            new HttpFileDownloader(http).DownloadAsync(file, Target, new(AllowFallback: false)));

        Assert.Equal(ErrorKind.StreamExpired, ex.Kind);
        Assert.Equal(1, calls); // 关掉回退后只在主链上失败，不再试备用域名
    }

    [Fact]
    public async Task AllowFallbackEnabled_TriesBackupUrl()
    {
        var calls = 0;
        using var http = Client(_ =>
        {
            calls++;
            return calls == 1 ? new(HttpStatusCode.Forbidden) : Partial(0, 3, 4);
        });

        var file = new RemoteFile("https://cdn.test/video", ["https://cdn.test/backup"], 4);
        var result = await new HttpFileDownloader(http).DownloadAsync(file, Target, new(AllowFallback: true));

        Assert.Equal(4, result.Bytes);
        Assert.Equal(2, calls); // 主链 403 后确实切到了备用域名
    }

    [Fact]
    public async Task RequestTimeout_IsNetworkFailure_NotUserPause()
    {
        using var http = Client(_ => throw new TaskCanceledException());
        var ex = await Assert.ThrowsAsync<BiliException>(() =>
            new HttpFileDownloader(http).DownloadAsync(Remote(4), Target, new()));
        Assert.Equal(ErrorKind.Network, ex.Kind);
    }
    private static HttpResponseMessage Partial(long start, long end, long total)
    {
        var content = new ByteArrayContent(Enumerable.Range((int)start, (int)(end - start + 1)).Select(i => (byte)i).ToArray());
        content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, total);
        return new(HttpStatusCode.PartialContent) { Content = content };
    }
    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> send) => new(new Handler(send));
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
