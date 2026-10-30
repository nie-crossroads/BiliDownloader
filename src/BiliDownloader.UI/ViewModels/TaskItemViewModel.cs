using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using BiliDownloader.Core.BiliApi;
using BiliDownloader.Core.Models;
using BiliDownloader.Core.Utils;
using BiliDownloader.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BiliDownloader.UI.ViewModels;

/// <summary>任务行状态语义（决定 pill 颜色与操作按钮）。</summary>
public enum TaskStatusKind
{
    Running,
    Paused,
    Done,
    Failed,
    Canceled,
    Waiting
}

/// <summary>
/// 任务行视图模型。只做「Core 事件 → 可绑定属性」的单向转换，
/// 不参与业务状态机（避免两处状态漂移，方案设计 §5.1）。
/// </summary>
public partial class TaskItemViewModel : ObservableObject
{
    private readonly Action<string> _pause;
    private readonly Action<string> _resume;
    private readonly Action<string> _cancel;
    private readonly Action<string> _retry;
    private readonly Action<string> _openFolder;
    private readonly AppSettings? _settings;

    public TaskItemViewModel(VideoTask task,
        Action<string> pause, Action<string> resume, Action<string> cancel, Action<string> retry,
        Action<string> openFolder, AppSettings? settings = null)
    {
        TaskId = task.TaskId;
        _pause = pause;
        _resume = resume;
        _cancel = cancel;
        _retry = retry;
        _openFolder = openFolder;
        _settings = settings;

        Title = task.Title;
        DurationText = Formatters.Duration(task.Duration);
        Update(task, 0);
    }

    public string TaskId { get; }
    public string Title { get; }
    public string DurationText { get; }

    [ObservableProperty] private string _pageLabel = string.Empty;
    [ObservableProperty] private string _statusText = "等待中";
    [ObservableProperty] private TaskStatusKind _statusKind = TaskStatusKind.Waiting;
    [ObservableProperty] private string _metaText = string.Empty;
    [ObservableProperty] private bool _metaIsError;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "0%";
    [ObservableProperty] private string _speedText = "—";
    [ObservableProperty] private bool _canPause;
    [ObservableProperty] private bool _canResume;
    [ObservableProperty] private bool _canCancel;
    [ObservableProperty] private bool _canRetry;
    [ObservableProperty] private bool _canCancelOrRemove;
    [ObservableProperty] private bool _canOpenFolder;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _qualityText = string.Empty;

    /// <summary>
    /// 完成后进度条的淡化程度（设置项 FadeCompletedProgress）。
    /// 值在 ViewModel 里算好，XAML 只绑定结果——DataTemplate 里做动画容易踩 namescope 的坑。
    /// </summary>
    public double CompletedProgressOpacity =>
        _settings is { FadeCompletedProgress: true } && StatusKind == TaskStatusKind.Done ? 0.5 : 1.0;

    /// <summary>设置窗口关闭后重新取一次纯展示用的值（设置对象本身不会发通知）。</summary>
    public void RefreshPresentation() => OnPropertyChanged(nameof(CompletedProgressOpacity));

    // ----------------------------------------------------- 多 P 层叠卡背
    // 批量任务不是一行走完就算完：后面还排着几集要用「层叠的卡」看出来。
    // 规则：2P → 主卡 + 1 层卡背；3P 及以上 → 主卡 + 2 层卡背；单 P 不叠。
    // 每完成一集，卡背按顺序往主卡方向滚一次（GhostPeek 由 ViewModel 推，见 StartRollForward）。

    private const double GhostRestNear = 6;
    private const double GhostRestFar = 12;
    private const double GhostRollExtra = 6;
    private const int RollDurationMs = 520;

    private DispatcherTimer? _rollTimer;
    private DateTime _rollStartedAt;
    private int _completedSeen;

    /// <summary>层叠总数（主卡 + 卡背），1 表示不叠。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCardStack))]
    [NotifyPropertyChangedFor(nameof(HasSecondCard))]
    [NotifyPropertyChangedFor(nameof(HasThirdCard))]
    private int _stackDepth = 1;

    public bool HasCardStack => StackDepth >= 2;
    public bool HasSecondCard => StackDepth >= 2;
    public bool HasThirdCard => StackDepth >= 3;

    /// <summary>近层卡背相对主卡的下移量（px）。静止 5，滚动时会被推到负值（藏到主卡后面）。</summary>
    [ObservableProperty] private double _ghostPeekNear = GhostRestNear;

    /// <summary>远层卡背相对主卡的下移量（px）。静止 10。</summary>
    [ObservableProperty] private double _ghostPeekFar = GhostRestFar;

    private void UpdateStack(VideoTask task)
    {
        // 终态（完成 / 失败 / 取消）不再排队，层叠收起，避免残留一堆没意义的卡背。
        StackDepth = task.Status.IsTerminal()
            ? 1
            : Net48.Clamp(task.Pages.Count, 1, 3);

        var completed = task.CompletedCount;
        if (completed > _completedSeen && !task.Status.IsTerminal())
            StartRollForward();
        _completedSeen = completed;
    }

    /// <summary>
    /// 卡背向前滚一次：正弦脉冲把两层卡背推到主卡背后（看不见）再弹回原位，
    /// 观感就是「后面排队的往前提了一格」。用 DispatcherTimer 推进而不是 Storyboard，
    /// 是因为 DataTemplate 里的 Storyboard 拿不到稳定的 namescope，容易在应用时炸。
    /// </summary>
    private void StartRollForward()
    {
        if (_rollTimer is null)
        {
            _rollTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render,
                OnRollTick, Dispatcher.CurrentDispatcher);
        }

        _rollStartedAt = DateTime.Now;
        _rollTimer.Start();
    }

    private void OnRollTick(object? sender, EventArgs e)
    {
        if (_rollTimer is null) return;

        var elapsed = (DateTime.Now - _rollStartedAt).TotalMilliseconds;
        if (elapsed >= RollDurationMs)
        {
            _rollTimer.Stop();
            GhostPeekNear = GhostRestNear;
            GhostPeekFar = GhostRestFar;
            return;
        }

        // 0 → 1 → 0 的半周正弦：一次推到底，回程也是顺势减速，不会在终点顿一下。
        var pulse = Math.Sin(elapsed / RollDurationMs * Math.PI);
        GhostPeekNear = GhostRestNear - pulse * (GhostRestNear + GhostRollExtra);
        GhostPeekFar = GhostRestFar - pulse * (GhostRestFar + GhostRollExtra);
    }

    public void Update(VideoTask task, double speed)
    {
        var page = task.Pages.FirstOrDefault(p => p.Status == DownloadStatus.Downloading)
                   ?? task.Pages.FirstOrDefault(p => p.Status == DownloadStatus.Merging)
                   ?? task.Pages.FirstOrDefault();

        StatusKind = task.Status switch
        {
            DownloadStatus.Downloading or DownloadStatus.FetchingInfo or DownloadStatus.FetchingUrl
                or DownloadStatus.Merging => TaskStatusKind.Running,
            DownloadStatus.Paused => TaskStatusKind.Paused,
            DownloadStatus.Completed => TaskStatusKind.Done,
            DownloadStatus.Failed => TaskStatusKind.Failed,
            DownloadStatus.Canceled or DownloadStatus.Skipped => TaskStatusKind.Canceled,
            _ => TaskStatusKind.Waiting
        };

        StatusText = task.Status switch
        {
            DownloadStatus.Pending => "等待中",
            DownloadStatus.FetchingInfo => "解析中",
            DownloadStatus.FetchingUrl => "取链接",
            DownloadStatus.Downloading => "下载中",
            DownloadStatus.Paused => "已暂停",
            DownloadStatus.Merging => "合并中",
            DownloadStatus.Completed => "已完成",
            DownloadStatus.Failed => "失败",
            DownloadStatus.Canceled => "已取消",
            DownloadStatus.Skipped => "已跳过",
            _ => "等待中"
        };

        Progress = Net48.Clamp(task.Progress, 0, 1);
        ProgressText = $"{Progress:P0}".Replace(" ", string.Empty);
        SpeedText = task.Status switch
        {
            DownloadStatus.Downloading => Formatters.Speed(speed),
            DownloadStatus.Merging => "合并中",
            DownloadStatus.Completed => "完成",
            DownloadStatus.Paused => "—",
            _ => "—"
        };

        // StatusKind 变了，派生的淡化值也要跟着通知一次
        OnPropertyChanged(nameof(CompletedProgressOpacity));

        IsRunning = StatusKind == TaskStatusKind.Running;
        CanPause = task.Status is DownloadStatus.Downloading or DownloadStatus.FetchingUrl
            or DownloadStatus.FetchingInfo or DownloadStatus.Merging or DownloadStatus.Pending;
        CanResume = task.Status is DownloadStatus.Paused;
        CanCancel = task.Status is not (DownloadStatus.Completed or DownloadStatus.Canceled or DownloadStatus.Failed);
        CanCancelOrRemove = CanCancel || task.Status == DownloadStatus.Failed;
        CanRetry = task.Status == DownloadStatus.Failed || task.Pages.Any(p => p.Status == DownloadStatus.Failed);
        CanOpenFolder = task.Status == DownloadStatus.Completed;

        PageLabel = task.Pages.Count > 1 && page is not null ? $"P{page.Index}" : string.Empty;
        QualityText = page is { Qn: > 0 } ? $"{QualityCatalog.QualityName(page.Qn)} · {QualityCatalog.CodecName(page.CodecId)}" : string.Empty;

        UpdateStack(task);
        MetaText = BuildMeta(task, page);
        MetaIsError = task.Status == DownloadStatus.Failed;
    }

    private string BuildMeta(VideoTask task, TaskPageItem? page)
    {
        var total = task.Pages.Count;

        switch (task.Status)
        {
            case DownloadStatus.Failed:
                return task.ErrorMessage
                       ?? task.Pages.FirstOrDefault(p => p.ErrorMessage is not null)?.ErrorMessage
                       ?? "下载失败";

            case DownloadStatus.Completed:
            {
                var size = task.Pages.Sum(p => p.TotalBytes);
                var quality = string.IsNullOrEmpty(QualityText) ? string.Empty : QualityText + " · ";
                var pages = total > 1 ? $"{task.CompletedCount}/{total} 完成 · " : string.Empty;
                return $"{pages}{quality}{Formatters.Bytes(size)}";
            }

            case DownloadStatus.Paused:
                return "已暂停 · 断点已保存";

            case DownloadStatus.Canceled:
                return "已取消";

            case DownloadStatus.Merging:
                return string.IsNullOrEmpty(QualityText) ? "正在封装音视频" : $"{QualityText} · 正在封装音视频";

            case DownloadStatus.Downloading:
            {
                if (page is null) return "下载中";
                var received = Formatters.Bytes(page.ReceivedBytes);
                var totalBytes = page.TotalBytes > 0 ? Formatters.Bytes(page.TotalBytes) : "未知";
                var pages = total > 1 ? $"{task.CompletedCount}/{total} 完成 · " : string.Empty;
                var quality = string.IsNullOrEmpty(QualityText) ? string.Empty : QualityText + " · ";
                return $"{pages}{quality}{received} / {totalBytes}";
            }

            default:
                return string.IsNullOrEmpty(QualityText) ? "等待开始" : QualityText;
        }
    }

    [RelayCommand] private void Pause() => _pause(TaskId);
    [RelayCommand] private void Resume() => _resume(TaskId);
    [RelayCommand] private void Cancel() => _cancel(TaskId);
    [RelayCommand] private void Retry() => _retry(TaskId);

    [RelayCommand]
    private void OpenFolder()
    {
        _openFolder(TaskId);
    }

    /// <summary>完成任务打开所在目录（先选中文件，找不到文件则打开目录）。</summary>
    public void OpenInExplorer(string? path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            // 打开资源管理器失败不打断界面
        }
    }
}
