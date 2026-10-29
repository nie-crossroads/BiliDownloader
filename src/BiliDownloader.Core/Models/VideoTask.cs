using BiliDownloader.Core.Errors;

namespace BiliDownloader.Core.Models;

/// <summary>一个视频（可能含多个分 P）的下载任务聚合（方案设计 §5.1）。</summary>
public sealed class VideoTask
{
    public string TaskId { get; init; } = Guid.NewGuid().ToString("N");
    public string Bvid { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? UpName { get; init; }
    public string? CoverUrl { get; init; }
    public TimeSpan Duration { get; init; }
    public string SaveDirectory { get; init; } = string.Empty;
    /// <summary>视频总分 P 数，用于判断是否建子目录（请求里只带勾选的 Cids）。</summary>
    public int TotalPages { get; init; } = 1;
    public IReadOnlyList<TaskPageItem> Pages { get; init; } = [];

    public DownloadStatus Status { get; set; } = DownloadStatus.Pending;
    /// <summary>0..1，多 P 时按总字节数加权（§5.4）。</summary>
    public double Progress { get; set; }
    public string? FinalPath { get; set; }
    public ErrorKind? Error { get; set; }
    public string? ErrorMessage { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; set; }

    public int CompletedCount => Pages.Count(p => p.Status == DownloadStatus.Completed);
    public int FailedCount => Pages.Count(p => p.Status == DownloadStatus.Failed);
}

/// <summary>单个分 P 的任务状态。</summary>
public sealed class TaskPageItem
{
    public long Cid { get; init; }
    /// <summary>1-based，来自 view.data.pages[].page。</summary>
    public int Index { get; set; }
    public string Part { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public DownloadStatus Status { get; set; } = DownloadStatus.Pending;
    public double Progress { get; set; }
    public int Qn { get; set; }
    public int CodecId { get; set; }
    public int AudioId { get; set; }
    public long TotalBytes { get; set; }
    public long ReceivedBytes { get; set; }
    public string? TempDir { get; set; }
    public string? FinalPath { get; set; }
    public ErrorKind? Error { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>直链重取次数（上限 1，避免死循环）。</summary>
    public int Attempts { get; set; }
}
