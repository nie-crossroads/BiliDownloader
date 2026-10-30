using BiliDownloader.Core.Errors;
using BiliDownloader.Core.Models;

namespace BiliDownloader.Core.Download;

/// <summary>一路远程文件（主链 + 备用 CDN）。ExpectedLength 只接受已知真实长度，未知传 0，禁止传码率估算值。</summary>
public sealed record RemoteFile(string Url, IReadOnlyList<string> BackupUrls, long ExpectedLength)
{
    public IEnumerable<string> AllUrls()
    {
        if (!string.IsNullOrEmpty(Url)) yield return Url;
        foreach (var backup in BackupUrls)
            if (!string.IsNullOrEmpty(backup)) yield return backup;
    }
}

/// <summary>
/// 单流下载参数。
/// <paramref name="Connections"/> = 1 走顺序 Range 分片；&gt; 1 走多连接分片（需要已知总长度，
/// 探测不到会自动回落顺序路径）。<paramref name="AllowFallback"/> 关掉后只用主链，
/// 403/404 直接判为直链失效，不再切备用 CDN。
/// </summary>
public sealed record DownloadOptions(
    int ChunkSizeBytes = 1 << 20,
    int MaxRetriesPerUrl = 2,
    int Connections = 1,
    bool AllowFallback = true);

public sealed record DownloadResult(long Bytes, bool Resumed, int Attempts);

/// <summary>
/// 单个远程文件的下载器（方案设计 §7.3 / §8.5）。两种模式：
/// 顺序 Range 分片追加写（Connections = 1，续传靠已落盘字节数，天然没有多线程写空洞的问题）；
/// 多连接分片（Connections &gt; 1，各片写独立的 .pN 文件，全部完成后顺序拼回主文件）。
/// 两种模式下主文件里的内容都始终是「从 0 开始的连续前缀」，切换并发数不会损坏文件。
/// 限速由注入的 <see cref="ISpeedLimiter"/> 统一控制，不随单次调用传入。
/// </summary>
public interface IFileDownloader
{
    Task<DownloadResult> DownloadAsync(RemoteFile file, string targetPath, DownloadOptions options,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default);

    /// <summary>探测远端的实际长度（服务端不给 Content-Length 时用）。</summary>
    Task<long?> ProbeLengthAsync(RemoteFile file, CancellationToken ct = default);

    /// <summary>
    /// 探测流的身份指纹（ETag / Last-Modified / 长度），跨重启恢复时用于校验「流没变」。
    /// 探测不到（全部 URL 失效或服务端不给标识）返回 null。
    /// </summary>
    Task<StreamFingerprint?> ProbeFingerprintAsync(RemoteFile file, CancellationToken ct = default);
}
