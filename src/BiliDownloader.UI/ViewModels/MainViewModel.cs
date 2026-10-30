using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using BiliDownloader.Core.Auth;
using BiliDownloader.Core.BiliApi.Models;
using BiliDownloader.Core.Errors;
using BiliDownloader.Core.Models;
using BiliDownloader.Core.Orchestration;
using BiliDownloader.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace BiliDownloader.UI.ViewModels;

/// <summary>
/// 主窗口视图模型：三步式主流程（输入 → 解析 → 预览 → 下载）+ 任务列表 + 日志 + 登录。
/// 所有 Core 事件在这里单向转成绑定属性（方案设计 §6.1）。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private const string DefaultHint = "支持 BV 号 / av 号 / b23.tv 短链";
    private const string HintSeparator = "解析不消耗直链，选完再下载";
    private const int QrTimeoutSeconds = 180;

    private readonly IVideoDownloadService _service;
    private readonly IBiliSession _session;
    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;

    private readonly Dictionary<string, VideoTask> _taskModels = new();
    private readonly Dictionary<string, TaskItemViewModel> _taskItems = new();

    private DispatcherTimer? _qrTimer;
    private DispatcherTimer? _qrCloseTimer;
    private DateTimeOffset _qrStartedAt;
    private string? _qrKey;
    private bool _qrPolling;

    public MainViewModel(IVideoDownloadService service, IBiliSession session, AppSettings settings)
    {
        _service = service;
        _session = session;
        _settings = settings;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        _service.ProgressChanged += OnProgressChanged;
        _service.StateChanged += OnStateChanged;

        Tasks.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(TaskCount));
            OnPropertyChanged(nameof(HasTasks));
            RefreshTaskSummary();
        };

        Logs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(LogCount));
    }

    // ------------------------------------------------------------ 输入 / 解析

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ParseCommand))]
    private string _inputUrl = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ParseCommand))]
    private bool _isParsing;

    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorText = string.Empty;

    public string HintText => DefaultHint;
    public string HintSeparatorText => HintSeparator;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    private PreviewViewModel? _preview;

    public bool HasPreview => Preview is not null;

    [ObservableProperty] private string _parseButtonText = "解析";

    private bool CanParse() => !IsParsing && !string.IsNullOrWhiteSpace(InputUrl);

    [RelayCommand(CanExecute = nameof(CanParse))]
    private async Task ParseAsync()
    {
        if (IsParsing) return;

        IsParsing = true;
        ParseButtonText = "解析中…";
        HasError = false;
        ErrorText = string.Empty;
        AppendLog(LogLevel.Info, "开始解析链接");

        try
        {
            var info = await _service.ProbeAsync(InputUrl.Trim(), _settings.PreferredCodec);

            if (info.IsPaidContent)
            {
                Preview = null;
                HasError = true;
                ErrorText = "充电专属 / 付费视频，v1 不支持下载";
                AppendLog(LogLevel.Warn, $"内容受限：{info.Title}");
                return;
            }

            var preview = new PreviewViewModel(info, _settings);
            preview.PropertyChanged += OnPreviewPropertyChanged;
            Preview = preview;
            IsPagePickerOpen = false;
            _ = preview.LoadCoverAsync();

            AppendLog(LogLevel.Info,
                $"解析成功 {info.Bvid} · {info.Pages.Count} P · 可用画质 {(info.Qualities.Count(q => q.IsAvailable))} 档");

            if (info.HasUgcSeason)
                AppendLog(LogLevel.Info, "该视频属于合集，v1 仅下载当前视频");
        }
        catch (BiliException ex)
        {
            Preview = null;
            HasError = true;
            ErrorText = ex.UserMessage;
            AppendLog(LogLevel.Warn, $"{ex.UserMessage}（{ex.Kind}）");
        }
        catch (Exception ex)
        {
            Preview = null;
            HasError = true;
            ErrorText = "解析失败，请稍后重试";
            AppendLog(LogLevel.Error, $"解析异常：{ex.Message}");
        }
        finally
        {
            IsParsing = false;
            ParseButtonText = "解析";
            ParseCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnPreviewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PreviewViewModel.CanDownload) or nameof(PreviewViewModel.SelectedQuality)
            or nameof(PreviewViewModel.SelectedAudio))
        {
            DownloadCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(Preview));
        }
    }

    // ------------------------------------------------------------ 选集弹框

    /// <summary>「选择更多」弹框是否打开。勾选实时写回 Preview.Pages，确认/× 只负责关闭。</summary>
    [ObservableProperty] private bool _isPagePickerOpen;

    [RelayCommand]
    private void OpenPagePicker()
    {
        if (Preview is null) return;
        IsPagePickerOpen = true;
    }

    [RelayCommand] private void ClosePagePicker() => IsPagePickerOpen = false;

    [RelayCommand] private void ConfirmPagePicker() => IsPagePickerOpen = false;

    [RelayCommand] private void SelectAllPages() => Preview?.SelectAllCommand.Execute(null);

    [RelayCommand] private void InvertPageSelection() => Preview?.InvertSelectionCommand.Execute(null);

    // ------------------------------------------------------------ 下载

    private bool CanDownload() => Preview is { CanDownload: true };

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private void Download()
    {
        var preview = Preview;
        if (preview?.SelectedQuality is not { } quality) return;

        var directory = _settings.SaveDirectory;
        if (!AppSettings.EnsureWritable(directory))
        {
            HasError = true;
            ErrorText = "保存目录不可写，请在设置里更换";
            AppendLog(LogLevel.Warn, $"保存目录不可写：{directory}");
            return;
        }

        var audioId = preview.SelectedAudio?.Id ?? 0;
        var request = new DownloadRequest(
            preview.Bvid,
            preview.SelectedCids,
            quality.Qn,
            quality.CodecId,
            audioId,
            directory,
            preview.Title,
            preview.UpNameOrNull,
            preview.CoverUrl,
            preview.Duration,
            preview.TotalPages);

        try
        {
            var task = _service.Enqueue(request);
            _taskModels[task.TaskId] = task;
            AddTaskItem(task);
            AppendLog(LogLevel.Info,
                $"入队 {task.Pages.Count} 个分 P · {quality.Display} · 保存到 {directory}");

            // 入队后保留输入与预览：用户需要继续看到解析出来的信息（标题/画质/选集），
            // 方便核对结果或接着下载其它分 P；不再清空输入框（§6.1 原设计要求清空，按反馈调整）。
            // 入队成功：解析出来的信息框保留（方便核对与接着下其它分 P），
            // 但选集勾选清空——不然连点两次「下载」会把同一批分 P 再下一遍。
            preview.ClearPageSelection();
            HasError = false;
            ErrorText = string.Empty;
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorText = "入队失败，请重试";
            AppendLog(LogLevel.Error, $"入队失败：{ex.Message}");
        }
        finally
        {
            DownloadCommand.NotifyCanExecuteChanged();
        }
    }

    // ------------------------------------------------------------ 任务

    public ObservableCollection<TaskItemViewModel> Tasks { get; } = new();
    public int TaskCount => Tasks.Count;
    public bool HasTasks => Tasks.Count > 0;
    public bool HasRunningTasks => Tasks.Any(t => t.CanPause);
    public string PauseAllText => HasRunningTasks ? "全部暂停" : "全部继续";

    private void AddTaskItem(VideoTask task)
    {
        var item = new TaskItemViewModel(task,
            PauseTask, ResumeTask, CancelTask, RetryTask, OpenTaskFolder);
        _taskItems[task.TaskId] = item;
        Tasks.Insert(0, item);
        RefreshTaskSummary();
    }

    /// <summary>任务集合或任一任务状态变化后，同步头部两个批量动作的可用性。</summary>
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
                    AppendLog(LogLevel.Info, $"完成：{e.Task.Title}");
                    if (_settings.AutoOpenFolder) OpenTaskFolder(e.Task.TaskId);
                    break;
                case DownloadStatus.Failed:
                    AppendLog(LogLevel.Warn, $"失败：{e.Task.Title} · {e.Task.ErrorMessage}");
                    break;
                case DownloadStatus.Paused:
                    AppendLog(LogLevel.Info, $"已暂停：{e.Task.Title}");
                    break;
                case DownloadStatus.Canceled:
                    AppendLog(LogLevel.Info, $"已取消：{e.Task.Title}");
                    break;
            }
        });

    private void PauseTask(string taskId)
    {
        _service.Pause(taskId);
        AppendLog(LogLevel.Info, "暂停任务（断点已保留）");
    }

    private void ResumeTask(string taskId)
    {
        _service.Resume(taskId);
        AppendLog(LogLevel.Info, "继续任务（自动重取直链并续传）");
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

        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            AppendLog(LogLevel.Warn, "打开目录失败");
        }
    }

    private static string SafeFolder(string title) => Core.FileName.DefaultFileNameStrategy.Sanitize(title);

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
            Tasks.Remove(item);
            _taskItems.Remove(item.TaskId);
            _taskModels.Remove(item.TaskId);
        }
    }

    // ------------------------------------------------------------ 日志

    public ObservableCollection<LogEntry> Logs { get; } = new();

    [ObservableProperty] private bool _isLogOpen;
    public int LogCount => Logs.Count;

    [RelayCommand] private void ToggleLog() => IsLogOpen = !IsLogOpen;

    [RelayCommand]
    private void ClearLog() => Logs.Clear();

    public void AppendLog(LogLevel level, string text)
    {
        void Add()
        {
            Logs.Add(new LogEntry
            {
                Time = DateTime.Now.ToString("HH:mm:ss"),
                Level = level,
                LevelText = level switch
                {
                    LogLevel.Warn => "[warn]",
                    LogLevel.Error => "[error]",
                    _ => "[info]"
                },
                Text = text
            });

            while (Logs.Count > 300) Logs.RemoveAt(0);
        }

        if (_dispatcher.CheckAccess()) Add();
        else _dispatcher.BeginInvoke(Add);
    }

    // ------------------------------------------------------------ 登录态

    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private bool _isVip;
    [ObservableProperty] private string _userName = string.Empty;
    [ObservableProperty] private string _userUidText = string.Empty;
    [ObservableProperty] private string _userInitial = "B";
    [ObservableProperty] private string _loginTagText = "未登录 · 最高 1080P";
    [ObservableProperty] private ImageSource? _avatarImage;
    [ObservableProperty] private bool _isAccountMenuOpen;

    [ObservableProperty] private bool _isQrOpen;
    [ObservableProperty] private ImageSource? _qrImage;
    [ObservableProperty] private bool _qrMaskVisible;
    [ObservableProperty] private bool _qrShowRefresh;
    [ObservableProperty] private bool _qrIsSuccess;
    [ObservableProperty] private string _qrMaskText = string.Empty;
    [ObservableProperty] private string _qrHintText = "二维码 180 秒内有效";
    [ObservableProperty] private Geometry? _qrMaskIcon;

    [ObservableProperty] private bool _isCookieOpen;
    [ObservableProperty] private string _cookieText = string.Empty;
    [ObservableProperty] private string _cookieError = string.Empty;

    [RelayCommand] private void ToggleAccountMenu() => IsAccountMenuOpen = !IsAccountMenuOpen;

    [RelayCommand]
    private async Task ShowLoginAsync()
    {
        IsAccountMenuOpen = false;
        IsCookieOpen = false;
        IsQrOpen = true;
        await StartQrLoginAsync();
    }

    [RelayCommand]
    private void CloseQr()
    {
        StopQrTimer();
        IsQrOpen = false;
        QrImage = null;
        QrMaskVisible = false;
    }

    [RelayCommand]
    private async Task RefreshQrAsync() => await StartQrLoginAsync();

    private async Task StartQrLoginAsync()
    {
        StopQrTimer();
        QrImage = null;
        QrMaskVisible = true;
        QrShowRefresh = false;
        QrIsSuccess = false;
        QrMaskIcon = null;
        QrMaskText = "正在获取二维码…";
        QrHintText = "正在获取二维码…";

        try
        {
            var session = await _session.BeginQrLoginAsync();
            _qrKey = session.Key;
            _qrStartedAt = DateTimeOffset.Now;
            QrImage = QrImageRenderer.Render(session.Url);

            if (QrImage is null)
            {
                QrMaskText = "二维码生成失败";
                QrShowRefresh = true;
                return;
            }

            QrMaskVisible = false;
            QrHintText = "二维码 180 秒内有效";
            StartQrTimer();
        }
        catch (BiliException ex)
        {
            QrMaskText = ex.UserMessage;
            QrShowRefresh = true;
            QrHintText = "点击「重新获取」刷新";
            AppendLog(LogLevel.Warn, $"获取登录二维码失败：{ex.UserMessage}");
        }
    }

    private void StartQrTimer()
    {
        StopQrTimer();
        _qrTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _qrTimer.Tick += async (_, _) => await PollQrAsync();
        _qrTimer.Start();
    }

    private void StopQrTimer()
    {
        if (_qrTimer is not null)
        {
            _qrTimer.Stop();
            _qrTimer = null;
        }
        if (_qrCloseTimer is not null)
        {
            _qrCloseTimer.Stop();
            _qrCloseTimer = null;
        }
    }

    private async Task PollQrAsync()
    {
        if (_qrPolling || _qrKey is null) return;
        if (DateTimeOffset.Now - _qrStartedAt > TimeSpan.FromSeconds(QrTimeoutSeconds))
        {
            ShowQrExpired();
            return;
        }

        _qrPolling = true;
        try
        {
            var result = await _session.PollQrLoginAsync(_qrKey);

            switch (result.Status)
            {
                case QrLoginStatus.Waiting:
                    break;

                case QrLoginStatus.Scanned:
                    QrMaskVisible = true;
                    QrIsSuccess = false;
                    QrMaskIcon = FindGeometry("IconScan");
                    QrMaskText = "已扫码，请在手机上确认";
                    QrHintText = "确认后自动登录";
                    break;

                case QrLoginStatus.Expired:
                    ShowQrExpired();
                    break;

                case QrLoginStatus.Confirmed:
                    StopQrTimer();
                    QrMaskVisible = true;
                    QrIsSuccess = true;
                    QrMaskIcon = FindGeometry("IconCheckCircle");
                    QrMaskText = "登录成功";
                    QrHintText = "正在同步账号信息…";
                    ApplyCredential(result.Credential);
                    AppendLog(LogLevel.Info, $"登录成功：{UserName}");
                    // 高阶画质（4K / 8K / 杜比）要登录后才有，这里立刻重解析一遍，
                    // 让画质与音质下拉即时变成可选，而不是等用户手动再点一次解析。
                    _ = RefreshPreviewAsync("登录成功");
                    _qrCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    _qrCloseTimer.Tick += (_, _) =>
                    {
                        StopQrTimer();
                        IsQrOpen = false;
                    };
                    _qrCloseTimer.Start();
                    break;

                case QrLoginStatus.Error:
                    QrMaskVisible = true;
                    QrShowRefresh = true;
                    QrIsSuccess = false;
                    QrMaskIcon = FindGeometry("IconWarn");
                    QrMaskText = result.Message ?? "登录失败，请重试";
                    QrHintText = "点击「重新获取」刷新";
                    break;
            }
        }
        catch (BiliException ex)
        {
            AppendLog(LogLevel.Warn, $"轮询登录状态失败：{ex.UserMessage}");
        }
        finally
        {
            _qrPolling = false;
        }
    }

    private void ShowQrExpired()
    {
        StopQrTimer();
        QrMaskVisible = true;
        QrShowRefresh = true;
        QrIsSuccess = false;
        QrMaskIcon = FindGeometry("IconRefresh");
        QrMaskText = "二维码已过期";
        QrHintText = "点击「重新获取」刷新";
    }

    private static Geometry? FindGeometry(string key) =>
        Application.Current?.TryFindResource(key) as Geometry;

    private void ApplyCredential(Credential? credential)
    {
        if (credential is null) return;
        ApplyLogin(new NavInfo
        {
            IsLogin = true,
            Mid = credential.Uid,
            Uname = credential.Uname,
            Face = credential.Face,
            IsVip = credential.IsVip
        });
    }

    private void ApplyLogin(NavInfo nav)
    {
        IsLoggedIn = true;
        IsVip = nav.IsVip;
        UserName = nav.Uname ?? "已登录";
        UserUidText = nav.Mid > 0 ? $"UID {nav.Mid}" : string.Empty;
        UserInitial = string.IsNullOrEmpty(UserName) ? "B" : UserName[..1].ToUpperInvariant();
        LoginTagText = nav.IsVip ? "已登录 · 可下 4K / 8K" : "已登录 · 可下 4K";
        _ = LoadAvatarAsync(nav.Face);
    }

    /// <summary>
    /// 登录/退出后重新解析当前链接，把画质与音质列表换成新权限下的一档。
    /// 不刷新的话用户看到的还是登录前那套灰显选项，必须手动再点一次「解析」才生效。
    /// 会尽量把用户选好的分 P 和画质搬过去，匹配不上（权限变了 / 没有这一档）才回落默认值。
    /// </summary>
    private async Task RefreshPreviewAsync(string reason)
    {
        var previous = Preview;
        var url = InputUrl.Trim();
        if (previous is null || url.Length == 0) return;

        try
        {
            var info = await _service.ProbeAsync(url, _settings.PreferredCodec);
            if (info.IsPaidContent) return;

            // 封面已经加载过，复用同一张图，避免预览卡闪烁。
            var fresh = new PreviewViewModel(info, _settings) { CoverImage = previous.CoverImage };
            fresh.RestoreSelection(previous.SelectedCids, previous.SelectedQuality?.Qn,
                previous.SelectedQuality?.CodecId, previous.SelectedAudio?.Id);
            fresh.PropertyChanged += OnPreviewPropertyChanged;
            Preview = fresh;

            var available = fresh.Qualities.Count(q => q.IsAvailable);
            AppendLog(LogLevel.Info, $"{reason}，已刷新可选画质：{available} 档可用");
        }
        catch (BiliException ex)
        {
            AppendLog(LogLevel.Warn, $"{reason}后刷新画质失败：{ex.UserMessage}");
        }
        catch (Exception ex)
        {
            AppendLog(LogLevel.Error, $"{reason}后刷新画质异常：{ex.Message}");
        }
    }

    private async Task LoadAvatarAsync(string? face)
    {
        var image = await ImageCache.LoadAsync(face, 48);
        if (image is not null) AvatarImage = image;
    }

    private void ApplyLogout()
    {
        IsLoggedIn = false;
        IsVip = false;
        UserName = string.Empty;
        UserUidText = string.Empty;
        UserInitial = "B";
        AvatarImage = null;
        LoginTagText = "未登录 · 最高 1080P";
        IsAccountMenuOpen = false;
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        await _session.LogoutAsync();
        ApplyLogout();
        AppendLog(LogLevel.Info, "已退出登录，本地凭证已清除");
        // 退出后高档位重新变灰，预览卡要跟着回落。
        _ = RefreshPreviewAsync("已退出登录");
    }

    [RelayCommand]
    private void ShowCookie()
    {
        QrMaskVisible = false;
        IsCookieOpen = true;
        IsQrOpen = false;
        CookieError = string.Empty;
    }

    [RelayCommand] private void CloseCookie() => IsCookieOpen = false;

    [RelayCommand]
    private async Task SubmitCookieAsync()
    {
        CookieError = string.Empty;
        try
        {
            var nav = await _session.LoginWithCookieAsync(CookieText);
            ApplyLogin(nav);
            IsCookieOpen = false;
            CookieText = string.Empty;
            AppendLog(LogLevel.Info, $"Cookie 登录成功：{UserName}");
            _ = RefreshPreviewAsync("登录成功");
        }
        catch (BiliException ex)
        {
            CookieError = ex.UserMessage;
            AppendLog(LogLevel.Warn, $"Cookie 登录失败：{ex.UserMessage}");
        }
    }


    // ------------------------------------------------------------ 启动

    public async Task InitializeAsync()
    {
        AppendLog(LogLevel.Info, "BiliDownloader 已启动 · 未登录时最高 1080P");

        var ffmpeg = FfmpegLocator.Find();
        AppendLog(ffmpeg is null ? LogLevel.Warn : LogLevel.Info,
            ffmpeg is null ? "未找到 FFmpeg，合并会失败（把 ffmpeg.exe 放进 tools/ffmpeg）" : $"FFmpeg：{ffmpeg}");

        try
        {
            var nav = await _session.RestoreAsync();
            if (nav is { IsLogin: true })
            {
                ApplyLogin(nav);
                AppendLog(LogLevel.Info, $"恢复登录态：{nav.Uname}");
            }
        }
        catch (BiliException ex)
        {
            AppendLog(LogLevel.Warn, $"恢复登录态失败：{ex.UserMessage}");
        }
    }
}
