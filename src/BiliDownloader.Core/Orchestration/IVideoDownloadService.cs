using BiliDownloader.Core.Download;
using BiliDownloader.Core.FileName;
using BiliDownloader.Core.Models;
using BiliDownloader.Core.Storage;

namespace BiliDownloader.Core.Orchestration;

/// <summary>
/// 入队参数（方案设计 §4.4）：只带用户勾选的分 P。
/// 后半段是「入队时固定」的执行配置快照：并发数、重试、限速开关、命名规则、
/// 同名处理策略都在这里定死，下载途中改设置不会影响已经在跑的任务，
/// Core 也不需要反向依赖 UI 的 AppSettings。
/// </summary>
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
    int TotalPages,
    FileNameOptions? NameOptions = null,
    ExistingFileAction ExistingFileAction = ExistingFileAction.Rename,
    DownloadOptions? Download = null);

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
    /// <summary>
    /// 预览：解析链接 + 取元数据 + 探测可用画质（不保留任何直链）。
    /// <paramref name="preferredCodec"/> 来自设置里的默认编码（AVC / HEVC / AV1），
    /// 决定同一清晰度有多路流时下拉里保留哪一路。
    /// </summary>
    Task<VideoInfo> ProbeAsync(string rawInput, string? preferredCodec = null, CancellationToken ct = default);

    VideoTask Enqueue(DownloadRequest request);
    void Pause(string taskId);
    void Resume(string taskId);
    void Cancel(string taskId, bool keepTemp);
    /// <summary>只重排 Failed 的分 P，已完成的不重下。</summary>
    void RetryFailed(string taskId);

    /// <summary>
    /// 把任务从列表里移除：停掉它的后台流程并删掉清单条目（否则重启后又冒出来）。
    /// 用于「清除已完成」这类操作。
    /// </summary>
    void Remove(string taskId);

    bool IsRunning(string taskId);

    /// <summary>
    /// 从磁盘清单里恢复上次没下完的任务（跨重启恢复）。
    /// 只重建任务对象并置为「已暂停」，<b>不自动开始下载</b>，
    /// 之后用 <see cref="Resume"/> 继续即可（恢复任务与暂停任务走同一个入口）。
    /// 继续时每一页都会重新校验流指纹：对得上就续传，对不上整段重下。
    /// </summary>
    IReadOnlyList<VideoTask> LoadResumable(string saveDirectory);

    event EventHandler<TaskProgressEventArgs>? ProgressChanged;
    event EventHandler<TaskStateChangedEventArgs>? StateChanged;
}
