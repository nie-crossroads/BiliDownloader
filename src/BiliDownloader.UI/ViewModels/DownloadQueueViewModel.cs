using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using BiliDownloader.Core.FileName;
using BiliDownloader.Core.Models;
using BiliDownloader.Core.Orchestration;
using BiliDownloader.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BiliDownloader.UI.ViewModels;

/// <summary>
/// 下载队列视图模型：任务列表、暂停 / 继续 / 取消 / 重试、批量动作、跨重启恢复。
/// 从 MainViewModel 拆出（架构评审第 4 项）。编排层事件在这里单向转成绑定属性。
/// </summary>
public partial class DownloadQueueViewModel : ObservableObject
{
    private readonly IVideoDownloadService _service;
    private readonly AppSettings _settings;
    private readonly Action<LogLevel, string> _log;
    private readonly Dispatcher _dispatcher;
    private readonly Action<string, string> _notify;
    private readonly IFolderOpener _folderOpener;

    private readonly Dictionary<string, VideoTask> _taskModels = new();
    private readonly Dictionary<string, TaskItemViewModel> _taskItems = new();

    public DownloadQueueViewModel(IVideoDownloadService service, AppSettings settings,
        Action<LogLevel, string> log, Dispatcher dispatcher,
        Action<string, string> notify, IFolderOpener folderOpener)
    {
        _service = service;
        _settings = settings;
        _log = log;
        _dispatcher = dispatcher;
        _notify = notify;
        _folderOpener = folderOpener;

        _service.ProgressChanged += OnProgressChanged;
        _service.StateChanged += OnStateChanged;

        Tasks.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(TaskCount));
            OnPropertyChanged(nameof(HasTasks));
            RefreshTaskSummary();
        };
    }

    public ObservableCollection<TaskItemViewModel> Tasks { get; } = new();
    public int TaskCount => Tasks.Count;
    public bool HasTasks => Tasks.Count > 0;
    public bool HasRunningTasks => Tasks.Any(t => t.CanPause);
    public string PauseAllText => HasRunningTasks ? "全部暂停" : "全部继续";

    /// <summary>弹层是否淡入（设置项 ReduceMotion）。勾上「减少动效」后弹层直接出现，不再淡入。</summary>
    public PopupAnimation PopupAnimation =>
        _settings.ReduceMotion ? PopupAnimation.None : PopupAnimation.Fade;

    /// <summary>设置窗口关闭后调一次。动效、完成后淡化这类纯展示项依赖 AppSettings，
    /// 而它不是可观察对象，改完不会自己发通知，只能在这里补一次刷新。</summary>
    public void RefreshPresentationSettings()
    {
        OnPropertyChanged(nameof(PopupAnimation));
        foreach (var item in Tasks) item.RefreshPresentation();
    }

    public void AddTaskItem(VideoTask task)
    {
        var item = new TaskItemViewModel(task,
            PauseTask, ResumeTask, CancelTask, RetryTask, OpenTaskFolder, _settings);
        _taskItems[task.TaskId] = item;
        Tasks.Insert(0, item);
        RefreshTaskSummary();
    }

    private void RefreshTaskSummary()
    {
        OnPropertyChanged(nameof(HasRunningTasks));
        OnPropertyChanged(nameof(PauseAllText));
        TogglePauseAllCommand.NotifyCanExecuteChanged();
        ClearCompletedCommand.NotifyCanExecuteChanged();
    }

    private void OnProgressChanged(object? sender, TaskProgressEventArgs e) =>
        _dispatcher.BeginInvoke(() =>
        {
            if (_taskItems.TryGetValue(e.Task.TaskId, out var item))
                item.Update(e.Task, e.SpeedBytesPerSecond);

            RefreshTaskSummary();
        });

    private void OnStateChanged(object? sender, TaskStateChangedEventArgs e) =>
        _dispatcher.BeginInvoke(() =>
        {
            if (!_taskModels.ContainsKey(e.Task.TaskId))
                _taskModels[e.Task.TaskId] = e.Task;

            if (_taskItems.TryGetValue(e.Task.TaskId, out var item))
                item.Update(e.Task, 0);
            else
                AddTaskItem(e.Task);

            RefreshTaskSummary();

            if (e.Previous == e.Current) return;

            switch (e.Current)
            {
                case DownloadStatus.Completed:
                    _log(LogLevel.Info, $"完成：{e.Task.Title}");
                    // 最小化到托盘时用户看不到列表变化，给个气泡提示。
                    if (_settings.NotifyOnComplete)
                        _notify("下载完成", e.Task.Title);
                    if (_settings.AutoOpenFolder) OpenTaskFolder(e.Task.TaskId);
                    break;
                case DownloadStatus.Failed:
                    _log(LogLevel.Warn, $"失败：{e.Task.Title} · {e.Task.ErrorMessage}");
                    break;
                case DownloadStatus.Paused:
                    _log(LogLevel.Info, $"已暂停：{e.Task.Title}");
                    break;
                case DownloadStatus.Canceled:
                    _log(LogLevel.Info, $"已取消：{e.Task.Title}");
                    break;
            }
        });

    private void PauseTask(string taskId)
    {
        _service.Pause(taskId);
        _log(LogLevel.Info, "暂停任务（断点已保留）");
    }

    private void ResumeTask(string taskId)
    {
        _service.Resume(taskId);
        _log(LogLevel.Info, "继续任务（自动重取直链并续传）");
    }

    private void CancelTask(string taskId) => _service.Cancel(taskId, keepTemp: false);

    private void RetryTask(string taskId) => _service.RetryFailed(taskId);

    private void OpenTaskFolder(string taskId)
    {
        if (!_taskModels.TryGetValue(taskId, out var task)) return;

        var path = task.FinalPath ?? task.Pages.FirstOrDefault(p => p.FinalPath is not null)?.FinalPath;
        var target = path ?? (_taskModels[taskId].Pages.Count > 0
            ? Path.Combine(task.SaveDirectory, task.Pages.Count > 1 ? SafeFolder(task.Title) : string.Empty)
            : task.SaveDirectory);

        if (!_folderOpener.Open(path, target))
            _log(LogLevel.Warn, "打开目录失败");
    }

    private static string SafeFolder(string title) => DefaultFileNameStrategy.Sanitize(title);

    [RelayCommand]
    private void TogglePauseAll()
    {
        if (HasRunningTasks)
        {
            foreach (var task in Tasks.Where(t => t.CanPause).ToList())
                _service.Pause(task.TaskId);
        }
        else
        {
            foreach (var task in Tasks.Where(t => t.CanResume).ToList())
                _service.Resume(task.TaskId);
        }
    }

    [RelayCommand]
    private void ClearCompleted()
    {
        var completed = Tasks.Where(t => t.StatusKind == TaskStatusKind.Done).ToList();
        foreach (var item in completed)
        {
            // 同时从编排层移除：停掉后台流程、删掉清单条目，否则重启后又冒出来
            _service.Remove(item.TaskId);
            Tasks.Remove(item);
            _taskItems.Remove(item.TaskId);
            _taskModels.Remove(item.TaskId);
        }
    }

    /// <summary>
    /// 启动时把上次没下完的任务从清单里恢复出来，一律置为「已暂停」等用户点继续。
    /// <para>故意不自动开下载：一来用户可能已经不想下了，二来启动即联网容易触发风控。</para>
    /// </summary>
    public void RestoreUnfinishedTasks(bool autoResume)
    {
        try
        {
            var restored = _service.LoadResumable(_settings.SaveDirectory);
            foreach (var task in restored)
            {
                AddTaskItem(task);
                _log(LogLevel.Info,
                    $"恢复未完成的任务：{task.Title}（{task.CompletedCount}/{task.Pages.Count} 页已完成）");
                if (autoResume) ResumeTask(task.TaskId);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清单读不出来只影响恢复，不该让程序起不来
            _log(LogLevel.Warn, $"读取下载清单失败：{ex.Message}");
        }
    }
}
