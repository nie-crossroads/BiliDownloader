namespace BiliDownloader.Core.Models;

/// <summary>任务 / 分 P 状态机（方案设计 §5.2）。</summary>
public enum DownloadStatus
{
    Pending,
    FetchingInfo,
    FetchingUrl,
    Downloading,
    Paused,
    Merging,
    Completed,
    Failed,
    Canceled,
    Skipped
}

public static class DownloadStatusExtensions
{
    /// <summary>终态：只能经 Retry 回到 Pending。</summary>
    public static bool IsTerminal(this DownloadStatus status) =>
        status is DownloadStatus.Completed or DownloadStatus.Failed
            or DownloadStatus.Canceled or DownloadStatus.Skipped;
}
