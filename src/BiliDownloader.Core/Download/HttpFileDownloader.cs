using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using BiliDownloader.Core.Errors;
using BiliDownloader.Core.Models;

namespace BiliDownloader.Core.Download;

/// <summary>HttpClient 实现的单流下载器。</summary>
public sealed class HttpFileDownloader : IFileDownloader
{
    private readonly HttpClient _http;

    public HttpFileDownloader(HttpClient http) =>
        _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<long?> ProbeLengthAsync(RemoteFile file, CancellationToken ct = default)
    {
        foreach (var url in file.AllUrls())
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(0, 0);

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                                               .ConfigureAwait(false);

                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or
                    HttpStatusCode.RequestedRangeNotSatisfiable)
                    continue;

                var range = response.Content.Headers.ContentRange;
                if (range?.Length is long length && length > 0) return length;

                var contentLength = response.Content.Headers.ContentLength;
                if (response.StatusCode == HttpStatusCode.OK && contentLength > 0) return contentLength;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // 换下一个域名继续探测
            }
        }

        return null;
    }

    public async Task<DownloadResult> DownloadAsync(RemoteFile file, string targetPath, DownloadOptions options,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        var total = file.ExpectedLength > 0 ? file.ExpectedLength : await ProbeLengthAsync(file, ct).ConfigureAwait(false) ?? 0;

        var written = File.Exists(targetPath) ? new FileInfo(targetPath).Length : 0;
        if (total > 0 && written > total)
        {
            // 落盘比远端还大：分片作废重下（§7.5）
            File.Delete(targetPath);
            written = 0;
        }
        if (total > 0 && written == total)
            return new DownloadResult(written, true, 0);

        var resumed = written > 0;
        var attempts = 0;

        await using var stream = new FileStream(targetPath, written > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);

        var urls = file.AllUrls().ToList();
        if (urls.Count == 0) throw BiliException.StreamExpired();

        var clock = Stopwatch.StartNew();
        var lastReportAt = TimeSpan.Zero;
        var lastReportBytes = written;
        var urlIndex = 0;
        var failures = 0;

        while (total <= 0 || written < total)
        {
            ct.ThrowIfCancellationRequested();

            var url = urls[urlIndex];
            try
            {
                var end = total > 0
                    ? Math.Min(written + options.ChunkSizeBytes - 1, total - 1)
                    : written + options.ChunkSizeBytes - 1;

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(written, end);

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                                               .ConfigureAwait(false);
                attempts++;

                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    // 请求起点已经越过真实文件尾（预估长度偏大时常见）：按已落盘字节收尾
                    if (written > 0)
                    {
                        total = written;
                        break;
                    }

                    if (++urlIndex >= urls.Count) throw BiliException.StreamExpired();
                    continue;
                }

                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                {
                    // 直链过期 / 节点失效：切下一个备用域名，不原地指数退避
                    if (++urlIndex >= urls.Count) throw BiliException.StreamExpired();
                    continue;
                }

                if ((int)response.StatusCode >= 500)
                {
                    failures++;
                    if (++urlIndex >= urls.Count)
                    {
                        if (failures > options.MaxRetriesPerUrl) throw BiliException.Network();
                        urlIndex = 0;
                        await Task.Delay(400 * failures, ct).ConfigureAwait(false);
                    }
                    continue;
                }

                response.EnsureSuccessStatusCode();

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    // 服务端忽略了 Range：降级为整段重下，保证不拼出错位文件（§7.3）
                    if (written > 0)
                    {
                        stream.SetLength(0);
                        written = 0;
                    }

                    var serverTotal = response.Content.Headers.ContentLength ?? 0;
                    if (serverTotal > 0) total = serverTotal;
                }
                else
                {
                    var range = response.Content.Headers.ContentRange;
                    if (range?.From is long start && start != written)
                    {
                        // 响应区间与请求不一致：判为异常，重下该流
                        throw BiliException.StreamExpired();
                    }
                    if (total <= 0 && range?.Length is long length) total = length;
                }

                await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var buffer = new byte[64 * 1024];
                var sinceReport = 0L;
                int read;

                while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await stream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    written += read;
                    sinceReport += read;

                    var now = clock.Elapsed;
                    if (now - lastReportAt >= TimeSpan.FromMilliseconds(200))
                    {
                        var seconds = (now - lastReportAt).TotalSeconds;
                        var speed = seconds > 0 ? (written - lastReportBytes) / seconds : 0;
                        progress?.Report(new DownloadProgress(written, total, speed));
                        lastReportAt = now;
                        lastReportBytes = written;
                    }

                    if (total > 0 && written >= total) break;
                }

                await stream.FlushAsync(ct).ConfigureAwait(false);
                failures = 0;
                progress?.Report(new DownloadProgress(written, total, written - lastReportBytes));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (BiliException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                // 网络抖动：先切备用域名（成本最低），用完一轮后再原地退避重试
                failures++;
                if (urlIndex < urls.Count - 1)
                {
                    urlIndex++;
                    continue;
                }

                if (failures > options.MaxRetriesPerUrl * Math.Max(1, urls.Count))
                    throw BiliException.From(ex);

                urlIndex = 0;
                await Task.Delay(300 * failures, ct).ConfigureAwait(false);
            }
        }

        if (total > 0 && written != total)
            throw new BiliException(ErrorKind.StreamExpired, "下载文件长度校验失败，请重试");

        return new DownloadResult(written, resumed, attempts);
    }
}
