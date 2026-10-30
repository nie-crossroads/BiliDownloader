using System.Collections.Concurrent;
using BiliDownloader.Core.Download;
using BiliDownloader.Core.Models;

namespace BiliDownloader.Core.Orchestration;

/// <summary>
/// 任务调度器：只管「任务的生命周期与命令时序」，不管一页具体怎么下载、合并、落盘。
/// <para>
/// 从编排层拆出来的原因：原先 VideoDownloadService 同时管调度、流水线、状态、清理，
/// 命令时序的正确性（会不会起两个流程、暂停中的继续会不会丢）和下载逻辑纠缠在一起，
/// 两边都难测。拆开后调度器只回答三个问题：这个任务在不在、命令按什么顺序执行、
/// 同一时刻允许几个任务真正在跑。
/// </para>
/// </summary>
internal sealed class TaskScheduler
{
    private readonly ConcurrentDictionary<string, TaskRuntime> _runtimes = new();
    private readonly SemaphoreSlim _gate;

    public TaskScheduler(int maxConcurrency = 3) =>
        _gate = new SemaphoreSlim(Math.Clamp(maxConcurrency, 1, 8));

    public IEnumerable<TaskRuntime> All => _runtimes.Values;

    public TaskRuntime Register(VideoTask task, DownloadRequest request)
    {
        var runtime = new TaskRuntime(task, request);
        _runtimes[task.TaskId] = runtime;
        return runtime;
    }

    public bool TryGet(string taskId, out TaskRuntime runtime) =>
        _runtimes.TryGetValue(taskId, out runtime!);

    public bool TryRemove(string taskId, out TaskRuntime runtime) =>
        _runtimes.TryRemove(taskId, out runtime!);

    public bool IsRunning(string taskId) =>
        _runtimes.TryGetValue(taskId, out var runtime) && runtime.Worker is { IsCompleted: false };

    /// <summary>
    /// 命令串行化：同一任务的暂停 / 继续 / 取消 / 重试按顺序执行，不会互相穿插。
    /// 命令本身是同步的，持锁时间很短，不会阻塞流水线。
    /// </summary>
    public void Execute(string taskId, Action<TaskRuntime> command)
    {
        if (!_runtimes.TryGetValue(taskId, out var runtime)) return;
        lock (runtime.CommandLock) command(runtime);
    }

    /// <summary>
    /// 启动（或重启）流水线。旧的取消令牌先释放再换新，
    /// <paramref name="beforeStart"/> 用来在真正起线程之前把状态改好，保持事件顺序一致。
    /// </summary>
    public void Start(TaskRuntime runtime, Func<TaskRuntime, Task> runner,
        Action<TaskRuntime>? beforeStart = null)
    {
        runtime.Cts.Dispose();
        runtime.Cts = new CancellationTokenSource();
        beforeStart?.Invoke(runtime);
        runtime.Worker = Task.Run(() => runner(runtime), CancellationToken.None);
    }

    /// <summary>并发闸门：同一时刻真正占用下载的任务数。</summary>
    public Task EnterGateAsync(CancellationToken ct) => _gate.WaitAsync(ct);

    public void ExitGate() => _gate.Release();

    /// <summary>退出时请求所有流程停下。</summary>
    public void StopAll()
    {
        foreach (var runtime in _runtimes.Values)
        {
            try { runtime.Cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
}

/// <summary>一个任务的运行时状态（调度器与编排层共享）。</summary>
internal sealed class TaskRuntime(VideoTask task, DownloadRequest request)
{
    public VideoTask Task { get; } = task;
    public DownloadRequest Request { get; } = request;
    public CancellationTokenSource Cts { get; set; } = new();

    /// <summary>命令串行化锁。</summary>
    public object CommandLock { get; } = new();

    public bool PauseRequested { get; set; }
    public bool CancelRequested { get; set; }

    /// <summary>
    /// 「请求暂停」与「已经停止」是两件事：暂停命令发出后，后台流程还要一点时间才真正退出。
    /// 这期间点的「继续」记在这里，等流程退干净了自动接上，否则那条命令就被吞掉了。
    /// </summary>
    public bool ResumePending { get; set; }

    public bool KeepTemp { get; set; } = true;
    public Task? Worker { get; set; }
    public long LastProgressTicks { get; set; }
    public StreamFingerprint? VideoFingerprint { get; set; }
    public StreamFingerprint? AudioFingerprint { get; set; }
}
