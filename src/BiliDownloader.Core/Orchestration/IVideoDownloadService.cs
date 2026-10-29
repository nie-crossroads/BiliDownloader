using BiliDownloader.Core.Models;

namespace BiliDownloader.Core.Orchestration;

/// <summary>入队参数（方案设计 §4.4）：只带用户勾选的分 P。</summary>
public sealed record DownloadRequest(
    string Bvid,
    IReadOnlyList<long> Cids,
    int Qn,
    int CodecId,
    int AudioId,
    string SaveDirectory,
    string Title,
    string? UpName,
    string? CoverUrl,
    TimeSpan Duration,
    int TotalPages);

public sealed class TaskProgressEventArgs : EventArgs
{
    public required VideoTask Task { get; init; }
    public double Progress { get; init; }
    public long ReceivedBytes { get; init; }
    public long TotalBytes { get; init; }
    public double SpeedBytesPerSecond { get; init; }
    public long Cid { get; init; }
}

public sealed class TaskStateChangedEventArgs : EventArgs
{
    public required VideoTask Task { get; init; }
    public DownloadStatus Previous { get; init; }
    public DownloadStatus Current { get; init; }
}

/// <summary>
/// 编排层：解析 → 取信息 → 取直链 → 下双流 → 合并 → 原子落盘 → 清理（方案设计 §4.4）。
/// UI 只通过它拿数据、发指令、订阅事件，不直接碰下载细节。
/// </summary>
public interface IVideoDownloadService
{
    /// <summary>预览：解析链接 + 取元数据 + 探测可用画质（不保留任何直链）。</summary>
    Task<VideoInfo> ProbeAsync(string rawInput, CancellationToken ct = default);

    VideoTask Enqueue(DownloadRequest request);
    void Pause(string taskId);
    void Resume(string taskId);
    void Cancel(string taskId, bool keepTemp);
    /// <summary>只重排 Failed 的分 P，已完成的不重下。</summary>
    void RetryFailed(string taskId);

    bool IsRunning(string taskId);

    event EventHandler<TaskProgressEventArgs>? ProgressChanged;
    event EventHandler<TaskStateChangedEventArgs>? StateChanged;
}
