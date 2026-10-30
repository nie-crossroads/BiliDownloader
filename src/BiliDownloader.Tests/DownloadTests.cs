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
