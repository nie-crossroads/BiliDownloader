using System.Diagnostics;
using System.IO;
using BiliDownloader.Core.BiliApi;
using BiliDownloader.Core.Models;
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

    public TaskItemViewModel(VideoTask task,
        Action<string> pause, Action<string> resume, Action<string> cancel, Action<string> retry,
        Action<string> openFolder)
    {
        TaskId = task.TaskId;
        _pause = pause;
        _resume = resume;
        _cancel = cancel;
        _retry = retry;
        _openFolder = openFolder;

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

        Progress = Math.Clamp(task.Progress, 0, 1);
        ProgressText = $"{Progress:P0}".Replace(" ", string.Empty);
        SpeedText = task.Status switch
        {
            DownloadStatus.Downloading => Formatters.Speed(speed),
            DownloadStatus.Merging => "合并中",
            DownloadStatus.Completed => "完成",
            DownloadStatus.Paused => "—",
            _ => "—"
        };

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
