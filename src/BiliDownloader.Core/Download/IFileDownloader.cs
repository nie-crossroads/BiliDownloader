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

public sealed record DownloadOptions(int ChunkSizeBytes = 1 << 20, int MaxRetriesPerUrl = 2);

public sealed record DownloadResult(long Bytes, bool Resumed, int Attempts);

/// <summary>
/// 单流下载：顺序 Range 分片追加写 + 断点续传 + 主链失败切备用 CDN（方案设计 §7.3 / §8.5）。
/// 分片按顺序写同一个文件，天然没有多线程写空洞的问题；续传靠已落盘字节数。
/// </summary>
public interface IFileDownloader
{
    Task<DownloadResult> DownloadAsync(RemoteFile file, string targetPath, DownloadOptions options,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default);

    /// <summary>探测远端的实际长度（服务端不给 Content-Length 时用）。</summary>
    Task<long?> ProbeLengthAsync(RemoteFile file, CancellationToken ct = default);
}
