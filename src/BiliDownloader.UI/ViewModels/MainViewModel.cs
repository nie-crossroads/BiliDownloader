using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using BiliDownloader.Core.Auth;
using BiliDownloader.Core.Errors;
using BiliDownloader.Core.FileName;
using BiliDownloader.Core.Models;
using BiliDownloader.Core.Orchestration;
using BiliDownloader.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BiliDownloader.UI.ViewModels;

/// <summary>
/// 主窗口视图模型：三步式主流程（输入 → 解析 → 预览 → 下载）+ 日志，并聚合账号与任务队列。
/// 登录态与任务队列已分别拆到 AccountViewModel / DownloadQueueViewModel（架构评审第 4 项），
/// 这里只管解析、预览、选集、下载与日志，并协调登录后刷新预览画质。
/// 所有 Core 事件在子视图模型或这里单向转成绑定属性（方案设计 §6.1）。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private const string DefaultHint = "支持 BV 号 / av 号 / b23.tv 短链";
    private const string HintSeparator = "解析不消耗直链，选完再下载";

    private readonly IVideoDownloadService _service;
    private readonly IBiliSession _session;
    private readonly AppSettings _settings;
    private readonly ILogService _log;
    private readonly UpdateService _updates;
    private readonly Dispatcher _dispatcher;

    /// <summary>需要通知用户时抛出（标题 + 正文），由主窗口转成托盘气泡。</summary>
    public event Action<string, string>? NotificationRequested;

    public MainViewModel(IVideoDownloadService service, IBiliSession session, AppSettings settings,
        ILogService log, UpdateService updates)
    {
        _service = service;
        _session = session;
        _settings = settings;
        _log = log;
        _updates = updates;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        Account = new AccountViewModel(_session, _settings, AppendLog, _dispatcher);
        Queue = new DownloadQueueViewModel(_service, _settings, AppendLog, _dispatcher,
            (title, text) => NotificationRequested?.Invoke(title, text), new ExplorerFolderOpener());
        // 登录 / 登出成功后刷新预览画质，让灰显档位即时变为可选。
        Account.OnLoginRefreshRequested = reason => _ = RefreshPreviewAsync(reason);

        Logs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(LogCount));
    }

    /// <summary>账号视图模型（登录态 / 扫码 / Cookie）。</summary>
    public AccountViewModel Account { get; }

    /// <summary>下载队列视图模型（任务列表 / 暂停 / 继续 / 取消 / 重试 / 恢复）。</summary>
    public DownloadQueueViewModel Queue { get; }

    /// <summary>
    /// 启动后自动继续恢复出来的任务。只在 <c>--resume</c> 下打开，用于端到端验证跨重启恢复；
    /// 正常启动一律不自动开下载（用户可能不想下了，且启动即联网容易触发风控）。
    /// </summary>
    public bool AutoResumeRestored { get; set; }

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
            preview.TotalPages,
            // 入队时把命名规则 / 序号位数 / 同名策略 / 并发与重试固定下来，
            // 之后在设置页改动不会影响这个已经在跑的任务。
            new FileNameOptions(_settings.NameRule, _settings.IndexPadding),
            _settings.ExistingFilePolicy,
            _settings.DownloadPreferences);

        try
        {
            var task = _service.Enqueue(request);
            Queue.AddTaskItem(task);
            AppendLog(LogLevel.Info,
                $"入队 {task.Pages.Count} 个分 P · {quality.Display} · 保存到 {directory}");

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

    // ------------------------------------------------------------ 日志

    public ObservableCollection<LogEntry> Logs { get; } = new();

    [ObservableProperty] private bool _isLogOpen;
    public int LogCount => Logs.Count;

    [RelayCommand] private void ToggleLog() => IsLogOpen = !IsLogOpen;

    [RelayCommand]
    private void ClearLog() => Logs.Clear();

    public void AppendLog(LogLevel level, string text)
    {
        // 落盘与界面列表各写一份：界面那份会滚动丢弃，日志目录里的留着排查用。
        _log.Write(level, text);

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

    // ------------------------------------------------------------ 启动

    public async Task InitializeAsync()
    {
        AppendLog(LogLevel.Info, "BiliDownloader 已启动 · 未登录时最高 1080P");

        var ffmpeg = FfmpegLocator.Find();
        AppendLog(ffmpeg is null ? LogLevel.Warn : LogLevel.Info,
            ffmpeg is null ? "未找到 FFmpeg，合并会失败（把 ffmpeg.exe 放进 tools/ffmpeg）" : $"FFmpeg：{ffmpeg}");

        try
        {
            await Account.InitializeAsync();
        }
        catch (BiliException ex)
        {
            AppendLog(LogLevel.Warn, $"恢复登录态失败：{ex.UserMessage}");
        }

        // 放在登录恢复之后：画质档位的可用性判断依赖登录态
        Queue.RestoreUnfinishedTasks(AutoResumeRestored);

        if (_settings.CheckUpdateOnStartup) _ = CheckUpdateAsync();
    }

    /// <summary>设置窗口关闭后调一次。动效、完成后淡化这类纯展示项依赖 AppSettings，
    /// 而它不是可观察对象，改完不会自己发通知，只能在这里补一次刷新。</summary>
    public void RefreshPresentationSettings()
    {
        Queue.RefreshPresentationSettings();
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

    /// <summary>启动检查更新。查不到或失败都静默，不该用弹窗打断用户。</summary>
    private async Task CheckUpdateAsync()
    {
        var info = await _updates.CheckAsync().ConfigureAwait(false);
        if (info is null) return;

        AppendLog(LogLevel.Info, $"发现新版本 {info.Version}（当前 {_updates.CurrentVersion}）");
        NotificationRequested?.Invoke("有新版本可用", $"{info.Version} 已发布，可前往 Releases 下载");
    }
}
