using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using BiliDownloader.Core.Errors;
using BiliDownloader.Core.Models;
using BiliDownloader.Core.Utils;

namespace BiliDownloader.Core.Download;

/// <summary>HttpClient 实现的下载器：顺序 Range 分片 + 可选多连接分片（方案设计 §7.3 / §8.5）。</summary>
public sealed class HttpFileDownloader : IFileDownloader
{
    private readonly HttpClient _http;
    private readonly ISpeedLimiter _limiter;

    public HttpFileDownloader(HttpClient http, ISpeedLimiter? limiter = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _limiter = limiter ?? new TokenBucketSpeedLimiter(0);
    }

    public async Task<long?> ProbeLengthAsync(RemoteFile file, CancellationToken ct = default)
    {
        var fingerprint = await ProbeFingerprintAsync(file, ct).ConfigureAwait(false);
        return fingerprint?.Length;
    }

    public Task<StreamFingerprint?> ProbeFingerprintAsync(RemoteFile file, CancellationToken ct = default) =>
        ProbeFingerprintAsync(file.AllUrls().ToList(), ct);

    public async Task<DownloadResult> DownloadAsync(RemoteFile file, string targetPath, DownloadOptions options,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        if (file is null) throw new ArgumentNullException(nameof(file));
        if (string.IsNullOrWhiteSpace(targetPath))
            throw new ArgumentException("目标路径不能为空", nameof(targetPath));
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (options.ChunkSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "分片大小必须大于 0");
        if (options.MaxRetriesPerUrl < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "重试次数不能为负");
        ct.ThrowIfCancellationRequested();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);

        var urls = EffectiveUrls(file, options);
        var probed = file.ExpectedLength > 0 ? null : await ProbeFingerprintAsync(urls, ct).ConfigureAwait(false);
        var total = file.ExpectedLength > 0 ? file.ExpectedLength : probed?.Length ?? 0;

        // 分片必须知道总长度才能切边界；探测不到就退回顺序路径。
        return options.Connections > 1 && total > 0
            ? await DownloadChunkedAsync(urls, targetPath, options, total, progress, ct).ConfigureAwait(false)
            : await DownloadSequentialAsync(urls, targetPath, options, total, progress, ct).ConfigureAwait(false);
    }

    /// <summary>关闭「自动回退备用节点」后只保留主链，403/404 直接判为直链失效。</summary>
    private static List<string> EffectiveUrls(RemoteFile file, DownloadOptions options)
    {
        var all = file.AllUrls().Where(u => !string.IsNullOrEmpty(u)).ToList();
        if (all.Count == 0) throw BiliException.StreamExpired();
        return options.AllowFallback ? all : [all[0]];
    }

    /// <summary>
    /// 探测流的身份：优先 Content-Range 里的总长，其次 200 响应的 Content-Length，
    /// 同时记下 ETag / Last-Modified 作为「是不是同一条流」的依据。
    /// </summary>
    private async Task<StreamFingerprint?> ProbeFingerprintAsync(IReadOnlyList<string> urls, CancellationToken ct)
    {
        foreach (var url in urls)
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
                if (range?.Length is long length && length > 0)
                    return new StreamFingerprint(response.Headers.ETag?.Tag,
                        response.Content.Headers.LastModified?.ToString("R"), length,
                        StreamFingerprint.PathOf(url));

                var contentLength = response.Content.Headers.ContentLength;
                if (response.StatusCode == HttpStatusCode.OK && contentLength > 0)
                    return new StreamFingerprint(response.Headers.ETag?.Tag,
                        response.Content.Headers.LastModified?.ToString("R"), contentLength.Value,
                        StreamFingerprint.PathOf(url));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
            {
                // 换下一个域名继续探测
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- 顺序路径

    private async Task<DownloadResult> DownloadSequentialAsync(IReadOnlyList<string> urls, string targetPath,
        DownloadOptions options, long total, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
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

        // net48 的 FileStream 不实现 IAsyncDisposable，只能用同步 using；
        // Dispose 会同步 flush 到磁盘，对下载来说正是想要的收尾动作。
        using var stream = new FileStream(targetPath, FileMode.OpenOrCreate,
            FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        stream.Position = written;

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
                    // 只有服务端确认文件尾与断点一致，才能认定已经下完。
                    if (response.Content.Headers.ContentRange?.Length == written && written > 0)
                    {
                        total = written;
                        break;
                    }

                    if (options.AllowFallback && ++urlIndex < urls.Count) continue;
                    throw BiliException.StreamExpired();
                }

                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                {
                    // 直链过期 / 节点失效：切下一个备用域名，不原地指数退避
                    if (options.AllowFallback && ++urlIndex < urls.Count) continue;
                    throw BiliException.StreamExpired();
                }

                if ((int)response.StatusCode >= 500)
                {
                    failures++;
                    if (options.AllowFallback && urlIndex < urls.Count - 1)
                    {
                        urlIndex++;
                        continue;
                    }

                    urlIndex = 0;
                    if (failures > options.MaxRetriesPerUrl) throw BiliException.Network();
                    await Task.Delay(400 * failures, ct).ConfigureAwait(false);
                    continue;
                }

                response.EnsureSuccessStatusCode();

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    // 服务端忽略了 Range：降级为整段重下，保证不拼出错位文件（§7.3）
                    if (written > 0)
                    {
                        stream.SetLength(0);
                        stream.Position = 0;
                        written = 0;
                        resumed = false;
                        lastReportBytes = 0;
                    }

                    var serverTotal = response.Content.Headers.ContentLength ?? 0;
                    total = serverTotal;
                }
                else
                {
                    var range = response.Content.Headers.ContentRange;
                    if (response.StatusCode != HttpStatusCode.PartialContent ||
                        range?.From != written || range.To is null || range.To < written ||
                        !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase))
                    {
                        // 响应区间与请求不一致：判为异常，重下该流
                        throw BiliException.StreamExpired();
                    }
                    if (range.Length is long length) total = length;
                }

                using var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                var buffer = new byte[64 * 1024];
                var sinceReport = 0L;
                int read;

                while ((read = await body.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                {
                    await stream.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                    written += read;
                    sinceReport += read;

                    await _limiter.ConsumeAsync(read, ct).ConfigureAwait(false);

                    var now = clock.Elapsed;
                    if (now - lastReportAt >= TimeSpan.FromMilliseconds(200))
                    {
                        var seconds = (now - lastReportAt).TotalSeconds;
                        var speed = seconds > 0 ? (written - lastReportBytes) / seconds : 0;
                        progress?.Report(new DownloadProgress(written, total, speed));
                        lastReportAt = now;
                        lastReportBytes = written;
                    }
                }

                await stream.FlushAsync(ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK && total == 0)
                {
                    total = written;
                    break;
                }
                if (sinceReport == 0)
                    throw new HttpRequestException("响应未返回数据");
                if (response.StatusCode == HttpStatusCode.PartialContent &&
                    written != response.Content.Headers.ContentRange!.To + 1)
                    throw BiliException.StreamExpired();
                failures = 0;
                progress?.Report(new DownloadProgress(written, total, written - lastReportBytes));
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw BiliException.Network(ex);
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
                if (options.AllowFallback && urlIndex < urls.Count - 1)
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

    // ---------------------------------------------------------------- 多连接分片

    /// <summary>
    /// 多连接分片：每一片写独立的 .partN 文件，全部完成后按顺序拼回主文件。
    /// 主文件里始终保持「从 0 开始的连续前缀」，所以中途把并发数改回 1 也不会损坏文件。
    /// </summary>
    private async Task<DownloadResult> DownloadChunkedAsync(IReadOnlyList<string> urls, string targetPath,
        DownloadOptions options, long total, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var connections = Net48.Clamp(options.Connections, 1, 64);
        var chunkSize = options.ChunkSizeBytes;

        var prefix = File.Exists(targetPath) ? new FileInfo(targetPath).Length : 0;
        if (prefix > total)
        {
            File.Delete(targetPath);
            prefix = 0;
        }
        if (prefix == total)
        {
            PartialFile.Cleanup(targetPath);
            return new DownloadResult(total, true, 0);
        }

        var span = total - prefix;
        var count = (int)Math.Min(connections, Math.Max(1, (span + chunkSize - 1) / chunkSize));

        // 主文件已经增长说明拼接曾被打断；以连续前缀重新规划，避免重复追加旧分片。
        var existing = PartialFile.TryReadPlan(targetPath);
        var plan = existing is not null
                   && existing.Total == total
                   && existing.Count == count
                   && existing.ChunkSize == chunkSize
                   && existing.Prefix == prefix
            ? existing
            : CreatePlan(targetPath, prefix, total, count, chunkSize);

        var received = new long[plan.Count];
        for (var i = 0; i < plan.Count; i++)
        {
            var expected = plan.LengthOf(i);
            var part = PartialFile.PartPath(targetPath, i);
            var have = File.Exists(part) ? new FileInfo(part).Length : 0;

            if (have > expected)
            {
                // 分片比计划长：边界对不上，整片重下，绝不让多余字节被拼进主文件
                PartialFile.TryDelete(part);
                have = 0;
            }

            received[i] = have;
        }

        long completed = prefix + received.Sum();
        long attempts = 0;
        var rangeIgnored = 0;
        var startedAt = Stopwatch.GetTimestamp();

        double AverageSpeed()
        {
            var elapsed = (Stopwatch.GetTimestamp() - startedAt) * 1.0 / Stopwatch.Frequency;
            return elapsed > 0 ? Interlocked.Read(ref completed) / elapsed : 0;
        }

        async Task DownloadPartAsync(int index, CancellationToken token)
        {
            var part = PartialFile.PartPath(targetPath, index);
            var length = plan.LengthOf(index);
            var start = plan.StartOf(index);

            using var stream = new FileStream(part, FileMode.OpenOrCreate,
                FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
            stream.Position = received[index];

            var urlIndex = 0;
            var failures = 0;

            // 允许切到下一个备用域名；关掉自动回退后只在主链上重试。
            bool TryAdvance()
            {
                if (options.AllowFallback && urlIndex < urls.Count - 1)
                {
                    urlIndex++;
                    return true;
                }
                return false;
            }

            while (received[index] < length)
            {
                token.ThrowIfCancellationRequested();

                var offset = received[index];
                var from = start + offset;
                var to = start + length - 1;

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, urls[urlIndex]);
                    request.Headers.Range = new RangeHeaderValue(from, to);

                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                                                   .ConfigureAwait(false);
                    Interlocked.Increment(ref attempts);

                    if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                    {
                        if (TryAdvance()) continue;
                        throw BiliException.StreamExpired();
                    }

                    if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                    {
                        if (TryAdvance()) continue;
                        throw BiliException.StreamExpired();
                    }

                    if ((int)response.StatusCode >= 500)
                    {
                        failures++;
                        urlIndex = 0;
                        if (failures > options.MaxRetriesPerUrl) throw BiliException.Network();
                        await Task.Delay(400 * failures, token).ConfigureAwait(false);
                        continue;
                    }

                    response.EnsureSuccessStatusCode();

                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        // 整个资源不能当作某一片写入，等其他连接退出后改走顺序下载。
                        Interlocked.Exchange(ref rangeIgnored, 1);
                        return;
                    }
                    else
                    {
                        var range = response.Content.Headers.ContentRange;
                        if (response.StatusCode != HttpStatusCode.PartialContent ||
                            range?.From != from || range.To != to || range.Length != total ||
                            !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase))
                            throw BiliException.StreamExpired();
                    }

                    using var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    var buffer = new byte[64 * 1024];
                    var writtenHere = 0L;
                    int read;

                    while ((read = await body.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                    {
                        if (read > length - received[index])
                            throw BiliException.StreamExpired();
                        await stream.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
                        received[index] += read;
                        writtenHere += read;
                        Interlocked.Add(ref completed, read);

                        await _limiter.ConsumeAsync(read, token).ConfigureAwait(false);
                        progress?.Report(new DownloadProgress(Interlocked.Read(ref completed), total, AverageSpeed()));
                    }

                    await stream.FlushAsync(token).ConfigureAwait(false);
                    if (writtenHere == 0) throw new HttpRequestException("响应未返回数据");
                    failures = 0;
                }
                catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
                {
                    throw BiliException.Network(ex);
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
                    failures++;
                    if (TryAdvance()) continue;

                    if (failures > options.MaxRetriesPerUrl * Math.Max(1, urls.Count))
                        throw BiliException.From(ex);

                    urlIndex = 0;
                    await Task.Delay(300 * failures, token).ConfigureAwait(false);
                }
            }
        }

        var pending = Enumerable.Range(0, plan.Count).Where(i => received[i] < plan.LengthOf(i)).ToArray();
        if (pending.Length > 0)
        {
            // net48 没有 Parallel.ForEachAsync，用等价实现（限流 + 取消传播 + 异常外抛）
            await Net48.ForEachAsync(pending, Math.Min(connections, pending.Length), ct,
                (index, token) => DownloadPartAsync(index, token)).ConfigureAwait(false);
        }

        if (rangeIgnored != 0)
        {
            PartialFile.Cleanup(targetPath);
            return await DownloadSequentialAsync(urls, targetPath, options, total, progress, ct)
                .ConfigureAwait(false);
        }

        for (var i = 0; i < plan.Count; i++)
        {
            if (received[i] != plan.LengthOf(i))
                throw new BiliException(ErrorKind.StreamExpired, "分片下载未完成，请重试");
        }

        await ConcatenateAsync(targetPath, plan, ct).ConfigureAwait(false);
        PartialFile.Cleanup(targetPath);

        var finalLength = new FileInfo(targetPath).Length;
        if (finalLength != total)
            throw new BiliException(ErrorKind.StreamExpired, "分片合并后长度校验失败，请重试");

        progress?.Report(new DownloadProgress(total, total, AverageSpeed()));
        return new DownloadResult(total, prefix > 0, (int)Interlocked.Read(ref attempts));
    }

    private static ChunkPlan CreatePlan(string targetPath, long prefix, long total, int count, long chunkSize)
    {
        PartialFile.DeleteParts(targetPath);
        var plan = new ChunkPlan(prefix, total, count, chunkSize);
        PartialFile.WritePlan(targetPath, plan);
        return plan;
    }

    /// <summary>
    /// 把分片按序追加回主文件，拼完一片删一片。
    /// 中途被打断时剩下的分片还在，下次继续拼即可，不会重复追加。
    /// </summary>
    private static async Task ConcatenateAsync(string targetPath, ChunkPlan plan, CancellationToken ct)
    {
        var position = File.Exists(targetPath) ? new FileInfo(targetPath).Length : 0;

        // net48 的 FileStream 不实现 IAsyncDisposable，只能同步释放
        using var output = new FileStream(targetPath, FileMode.OpenOrCreate,
            FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        output.Position = position;

        for (var i = 0; i < plan.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var part = PartialFile.PartPath(targetPath, i);
            if (!File.Exists(part)) continue; // 这一片上次已经拼进去了

            using (var input = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.None, 1 << 16, useAsync: true))
            {
                // net48 的 CopyToAsync 只有 (stream, bufferSize, ct) 这个带取消的重载
                await input.CopyToAsync(output, 1 << 16, ct).ConfigureAwait(false);
            }

            PartialFile.TryDelete(part);
        }

        await output.FlushAsync().ConfigureAwait(false);
    }
}
