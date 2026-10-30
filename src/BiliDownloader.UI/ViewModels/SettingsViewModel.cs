using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using BiliDownloader.Core.BiliApi;
using BiliDownloader.Core.Download;
using BiliDownloader.Core.Storage;
using BiliDownloader.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BiliDownloader.UI.ViewModels;

    /// <summary>下拉项：值 + 显示文案（ComboBox 用 SelectedValuePath="Value"）。</summary>
    public sealed record OptionItem(string Value, string Text);

    /// <summary>主题色下拉项：下拉框要显示色卡，所以除了 key / 名称还得带一支笔刷。</summary>
    public sealed record AccentSwatch(string Key, string Name, Brush Swatch)
    {
        public static AccentSwatch From(AccentPreset preset)
        {
            var brush = new SolidColorBrush(AccentPreset.Parse(preset.Accent));
            brush.Freeze();
            return new AccentSwatch(preset.Key, preset.Name, brush);
        }
    }

/// <summary>设置窗口视图模型。改动即时写回 <see cref="AppSettings"/> 并落盘，没有「确定」。</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Action? _openCookieDialog;
    private readonly ISpeedLimiter? _limiter;
    private readonly StartupService? _startup;
    private static readonly HttpClient ProbeHttp = new() { Timeout = TimeSpan.FromSeconds(8) };

    public SettingsViewModel(AppSettings settings, Action? openCookieDialog = null,
        ISpeedLimiter? limiter = null, StartupService? startup = null)
    {
        _settings = settings;
        _openCookieDialog = openCookieDialog;
        _limiter = limiter;
        _startup = startup;

        QualityOptions = QualityCatalog.QualityList
            .Select(qn => new QnOption(qn, QualityCatalog.QualityName(qn)))
            .ToList();
        AudioOptions = QualityCatalog.AudioIdList
            .Select(id => new AudioOptionItem(id, QualityCatalog.AudioName(id)))
            .ToList();
        AccentOptions = AccentPalette.Presets.Select(AccentSwatch.From).ToList();
    }

    // ------------------------------------------------------------ 导航

    /// <summary>左侧导航项（Value 对应 SelectedPane）。</summary>
    public IReadOnlyList<OptionItem> NavItems { get; } =
    [
        new("basic", "基础"),
        new("download", "下载"),
        new("network", "网络"),
        new("appearance", "外观"),
        new("about", "关于与诊断"),
    ];

    /// <summary>download / basic / network / appearance / about。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDownloadPane))]
    [NotifyPropertyChangedFor(nameof(IsBasicPane))]
    [NotifyPropertyChangedFor(nameof(IsNetworkPane))]
    [NotifyPropertyChangedFor(nameof(IsAppearancePane))]
    [NotifyPropertyChangedFor(nameof(IsAboutPane))]
    [NotifyPropertyChangedFor(nameof(PaneTitle))]
    [NotifyPropertyChangedFor(nameof(PaneDescription))]
    private string _selectedPane = "basic";

    public bool IsDownloadPane => SelectedPane == "download";
    public bool IsBasicPane => SelectedPane == "basic";
    public bool IsNetworkPane => SelectedPane == "network";
    public bool IsAppearancePane => SelectedPane == "appearance";
    public bool IsAboutPane => SelectedPane == "about";

    public string PaneTitle => SelectedPane switch
    {
        "basic" => "基础",
        "network" => "网络",
        "appearance" => "外观",
        "about" => "关于与诊断",
        _ => "下载"
    };

    public string PaneDescription => SelectedPane switch
    {
        "basic" => "启动行为、关闭行为与数据位置。",
        "network" => "代理与请求策略。调低并发可缓解风控；代理与超时改动需重启程序生效。",
        "appearance" => "主题与列表呈现方式。",
        "about" => "版本信息、依赖组件与日志清理。",
        _ => "改动立即生效。并发与连接数调高会更快，但更容易触发风控。"
    };

    [RelayCommand] private void SelectPane(string? pane) => SelectedPane = pane ?? "basic";

    // ------------------------------------------------------------ 下载

    public string SaveDirectory
    {
        get => _settings.SaveDirectory;
        set
        {
            Edit(() => _settings.SaveDirectory = value);
            // 清单与临时目录跟随保存目录，改了要重算「配置文件与日志」里的路径。
            OnPropertyChanged(nameof(ConfigEntries));
        }
    }

    private readonly List<OptionItem> _nameTemplates =
    [
        new("IndexTitle", "序号 + 选集标题"),
        new("Title", "标题（不含序号）"),
        new("TitleUp", "标题 + UP 主"),
    ];

    public IReadOnlyList<OptionItem> NameTemplateOptions => _nameTemplates;

    public string FileNameTemplate
    {
        get => _settings.FileNameTemplate;
        set
        {
            Edit(() => _settings.FileNameTemplate = value);
            OnPropertyChanged(nameof(NamePreview));
        }
    }

    public IReadOnlyList<int> IndexPaddingOptions { get; } = [2, 3];

    public int IndexPadding
    {
        get => _settings.IndexPadding;
        set
        {
            Edit(() => _settings.IndexPadding = value);
            OnPropertyChanged(nameof(NamePreview));
        }
    }

    public string NamePreview => _settings.FileNameTemplate switch
    {
        "Title" => "第1节：围棋的规则.mp4",
        "TitleUp" => "第1节：围棋的规则 · UP主名.mp4",
        _ => $"{1.ToString().PadLeft(_settings.IndexPadding, '0')} 第1节：围棋的规则.mp4"
    };

    public IReadOnlyList<QnOption> QualityOptions { get; }
    public IReadOnlyList<AudioOptionItem> AudioOptions { get; }
    public IReadOnlyList<string> CodecOptions { get; } = ["AVC", "HEVC", "AV1"];

    public int PreferredQn
    {
        get => _settings.PreferredQn;
        set => Edit(() => _settings.PreferredQn = value);
    }

    public string PreferredCodec
    {
        get => _settings.PreferredCodec;
        set => Edit(() => _settings.PreferredCodec = value);
    }

    public int PreferredAudioId
    {
        get => _settings.PreferredAudioId;
        set => Edit(() => _settings.PreferredAudioId = value);
    }

    public int MaxConcurrentTasks
    {
        get => _settings.MaxConcurrentTasks;
        set => Edit(() => _settings.MaxConcurrentTasks = value);
    }

    public int ConnectionsPerTask
    {
        get => _settings.ConnectionsPerTask;
        set => Edit(() => _settings.ConnectionsPerTask = value);
    }

    public int GlobalSpeedLimitKbps
    {
        get => _settings.GlobalSpeedLimitKbps;
        set
        {
            Edit(() => _settings.GlobalSpeedLimitKbps = value);
            // 限速器是全局共享的，改完对正在下载的任务立刻生效，不用等下一个任务。
            _limiter?.Update(value * 1024L);
        }
    }

    public bool AutoOpenFolder
    {
        get => _settings.AutoOpenFolder;
        set => Edit(() => _settings.AutoOpenFolder = value);
    }

    public bool NotifyOnComplete
    {
        get => _settings.NotifyOnComplete;
        set => Edit(() => _settings.NotifyOnComplete = value);
    }

    private readonly List<OptionItem> _existingFileActions =
    [
        new("Rename", "重命名"),
        new("Skip", "跳过"),
        new("Overwrite", "覆盖"),
    ];

    public IReadOnlyList<OptionItem> ExistingFileActionOptions => _existingFileActions;

    public string ExistingFileAction
    {
        get => _settings.ExistingFileAction;
        set => Edit(() => _settings.ExistingFileAction = value);
    }

    // ------------------------------------------------------------ 基础

    public bool AutoStart
    {
        get => _settings.AutoStart;
        set
        {
            Edit(() => _settings.AutoStart = value);
            // 开机启动写的是当前用户的 Run 注册表项，切换即时生效。
            _startup?.SetEnabled(value);
        }
    }

    public bool CheckUpdateOnStartup
    {
        get => _settings.CheckUpdateOnStartup;
        set => Edit(() => _settings.CheckUpdateOnStartup = value);
    }

    private readonly List<OptionItem> _closeActions =
    [
        new("Exit", "退出程序"),
        new("Minimize", "最小化到任务栏"),
    ];

    public IReadOnlyList<OptionItem> CloseActionOptions => _closeActions;

    public string CloseAction
    {
        get => _settings.CloseAction;
        set => Edit(() => _settings.CloseAction = value);
    }

    private readonly List<OptionItem> _languages = [new("zh-CN", "简体中文")];

    public IReadOnlyList<OptionItem> LanguageOptions => _languages;

    public string Language
    {
        get => _settings.Language;
        set => Edit(() => _settings.Language = value);
    }

    /// <summary>
    /// 设置页「配置文件与日志」列出的全部路径：设置、加密凭据、日志目录、
    /// 下载任务清单（manifest）与临时目录。清单与临时目录跟随「保存目录」，
    /// 改了保存目录要重算，所以在 SaveDirectory 与恢复默认时一并通知。
    /// </summary>
    public IReadOnlyList<ConfigEntry> ConfigEntries => BuildConfigEntries();

    private IReadOnlyList<ConfigEntry> BuildConfigEntries()
    {
        var appDataDir = Path.GetDirectoryName(AppSettings.FilePath)!;
        var localAppDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliDownloader");
        var tempRoot = StoragePaths.TempRoot(_settings.SaveDirectory);

        return
        [
            new ConfigEntry("设置文件", AppSettings.FilePath,
                "全部设置项（保存目录、画质、并发、代理、主题等），改动即时写入；重装程序不会丢。"),
            new ConfigEntry("登录凭据", Path.Combine(localAppDataDir, "credential.dat"),
                "Windows DPAPI 加密的登录态，重启后自动恢复登录；删除该文件即退出登录。"),
            new ConfigEntry("运行与崩溃日志", Path.Combine(appDataDir, "logs"),
                "运行日志按时间滚动，出问题时可在此打包；记录级别可在「关于与诊断」调整。"),
            new ConfigEntry("下载任务清单", Path.Combine(tempRoot, "manifest.json"),
                "未完成任务清单（参数、进度、流指纹），重启后用于续传；无未完成任务时该文件不存在。"),
            new ConfigEntry("下载临时目录", tempRoot,
                "下载中的分片与合并临时文件，完成后自动清理；异常退出可能残留，可手动删除。"),
        ];
    }

    /// <summary>日志目录（「关于与诊断」的「打开目录」按钮也用它）。</summary>
    public string LogDirText => Path.Combine(Path.GetDirectoryName(AppSettings.FilePath)!, "logs");

    /// <summary>表格行：名称 + 路径 + 作用说明。</summary>
    public sealed record ConfigEntry(string Label, string Location, string Description);

    [RelayCommand]
    private void CopyPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            System.Windows.Clipboard.SetText(path);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or UnauthorizedAccessException)
        {
            // 剪贴板被别的程序占用时静默失败，不影响设置页
        }
    }

    // ------------------------------------------------------------ 网络

    private readonly List<OptionItem> _proxyModes =
    [
        new("System", "跟随系统"),
        new("None", "不使用代理"),
        new("Manual", "手动指定"),
    ];

    public IReadOnlyList<OptionItem> ProxyModeOptions => _proxyModes;

    public string ProxyMode
    {
        get => _settings.ProxyMode;
        set
        {
            Edit(() => _settings.ProxyMode = value);
            OnPropertyChanged(nameof(IsProxyManual));
        }
    }

    public bool IsProxyManual => _settings.ProxyMode == "Manual";

    public string ProxyAddress
    {
        get => _settings.ProxyAddress;
        set => Edit(() => _settings.ProxyAddress = value);
    }

    public int RequestTimeoutSeconds
    {
        get => _settings.RequestTimeoutSeconds;
        set => Edit(() => _settings.RequestTimeoutSeconds = value);
    }

    public int MaxRetries
    {
        get => _settings.MaxRetries;
        set => Edit(() => _settings.MaxRetries = value);
    }

    public bool AutoFallbackEndpoint
    {
        get => _settings.AutoFallbackEndpoint;
        set => Edit(() => _settings.AutoFallbackEndpoint = value);
    }

    [ObservableProperty] private string _connectionStatus = "未检测";

    [ObservableProperty] private bool _isProbing;

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        IsProbing = true;
        ConnectionStatus = "检测中…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            // 未登录时 nav 会返回 code=-101，能拿到响应就说明接口可达
            using var response = await ProbeHttp.GetAsync(
                "https://api.bilibili.com/x/web-interface/nav", cts.Token).ConfigureAwait(true);
            ConnectionStatus = $"接口可达 · HTTP {(int)response.StatusCode}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            ConnectionStatus = "接口不可达，请检查网络或代理";
        }
        finally
        {
            IsProbing = false;
        }
    }

    [RelayCommand] private void OpenCookieDialog() => _openCookieDialog?.Invoke();

    // ------------------------------------------------------------ 外观

    [ObservableProperty] private bool _isDarkTheme;

    public string ThemeText => _settings.Theme == "Dark" ? "深色" : "浅色";

    partial void OnIsDarkThemeChanged(bool value)
    {
        _settings.Theme = value ? "Dark" : "Light";
        _settings.Save();
        ThemeService.Apply(_settings.Theme);
        OnPropertyChanged(nameof(ThemeText));
    }

    public void SyncThemeFromSettings() => IsDarkTheme = _settings.Theme == "Dark";

    public bool ShowThumbnails
    {
        get => _settings.ShowThumbnails;
        set => Edit(() => _settings.ShowThumbnails = value);
    }

    public bool FadeCompletedProgress
    {
        get => _settings.FadeCompletedProgress;
        set => Edit(() => _settings.FadeCompletedProgress = value);
    }

    public bool ReduceMotion
    {
        get => _settings.ReduceMotion;
        set => Edit(() => _settings.ReduceMotion = value);
    }

    /// <summary>预制主题色清单。下拉框选中即换色。</summary>
    public IReadOnlyList<AccentSwatch> AccentOptions { get; }

    public string AccentColor
    {
        get => _settings.AccentColor;
        set
        {
            if (string.Equals(_settings.AccentColor, value, StringComparison.OrdinalIgnoreCase)) return;

            Edit(() => _settings.AccentColor = value);
            ThemeService.ApplyAccent(value);
        }
    }

    // ------------------------------------------------------------ 关于与诊断

    public string VersionText
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public string InstallPathText => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    public string FfmpegStatusText
    {
        get
        {
            var path = FfmpegLocator.Find(_settings.FfmpegPath);
            return path is null ? "未找到（合并音视频会失败）" : path;
        }
    }

    public bool IsFfmpegMissing => FfmpegLocator.Find(_settings.FfmpegPath) is null;

    private readonly List<OptionItem> _logLevels =
    [
        new("Debug", "详细（Debug）"),
        new("Info", "常规（Info）"),
        new("Warn", "仅警告（Warn）"),
        new("Error", "仅错误（Error）"),
    ];

    public IReadOnlyList<OptionItem> LogLevelOptions => _logLevels;

    public string LogLevel
    {
        get => _settings.LogLevel;
        set => Edit(() => _settings.LogLevel = value);
    }

    [RelayCommand]
    private void OpenLogFolder() => OpenInExplorer(LogDirText);

    [RelayCommand]
    private void OpenInstallFolder() => OpenInExplorer(InstallPathText);

    [RelayCommand]
    private void PickFfmpeg()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 ffmpeg.exe",
            Filter = "ffmpeg|ffmpeg.exe|可执行文件|*.exe",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true) return;
        _settings.FfmpegPath = dialog.FileName;
        _settings.Save();
        OnPropertyChanged(nameof(FfmpegStatusText));
        OnPropertyChanged(nameof(IsFfmpegMissing));
    }

    // ------------------------------------------------------------ 命令

    [RelayCommand]
    private void BrowseSaveDirectory()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择默认保存目录",
            InitialDirectory = Directory.Exists(_settings.SaveDirectory)
                ? _settings.SaveDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
        };

        if (dialog.ShowDialog() != true) return;
        SaveDirectory = dialog.FolderName;
    }

    [RelayCommand]
    private void OpenSaveDirectory() => OpenInExplorer(_settings.SaveDirectory);

    [RelayCommand]
    private void RestoreDefaults()
    {
        var defaults = new AppSettings();
        _settings.FileNameTemplate = defaults.FileNameTemplate;
        _settings.IndexPadding = defaults.IndexPadding;
        _settings.PreferredQn = defaults.PreferredQn;
        _settings.PreferredCodec = defaults.PreferredCodec;
        _settings.PreferredAudioId = defaults.PreferredAudioId;
        _settings.MaxConcurrentTasks = defaults.MaxConcurrentTasks;
        _settings.ConnectionsPerTask = defaults.ConnectionsPerTask;
        _settings.GlobalSpeedLimitKbps = defaults.GlobalSpeedLimitKbps;
        _settings.AutoOpenFolder = defaults.AutoOpenFolder;
        _settings.NotifyOnComplete = defaults.NotifyOnComplete;
        _settings.ExistingFileAction = defaults.ExistingFileAction;
        _settings.CheckUpdateOnStartup = defaults.CheckUpdateOnStartup;
        _settings.CloseAction = defaults.CloseAction;
        _settings.ProxyMode = defaults.ProxyMode;
        _settings.ProxyAddress = defaults.ProxyAddress;
        _settings.RequestTimeoutSeconds = defaults.RequestTimeoutSeconds;
        _settings.MaxRetries = defaults.MaxRetries;
        _settings.AutoFallbackEndpoint = defaults.AutoFallbackEndpoint;
        _settings.ShowThumbnails = defaults.ShowThumbnails;
        _settings.FadeCompletedProgress = defaults.FadeCompletedProgress;
        _settings.ReduceMotion = defaults.ReduceMotion;
        _settings.LogLevel = defaults.LogLevel;
        _settings.AccentColor = defaults.AccentColor;
        _settings.Save();

        ThemeService.ApplyAccent(defaults.AccentColor);

        RefreshAll();
    }

    /// <summary>恢复默认后把所有绑定过的属性通知一遍。</summary>
    private void RefreshAll()
    {
        SyncThemeFromSettings();
        foreach (var name in new[]
                 {
                     nameof(FileNameTemplate), nameof(IndexPadding), nameof(NamePreview),
                     nameof(PreferredQn), nameof(PreferredCodec), nameof(PreferredAudioId),
                     nameof(MaxConcurrentTasks), nameof(ConnectionsPerTask), nameof(GlobalSpeedLimitKbps),
                     nameof(AutoOpenFolder), nameof(NotifyOnComplete), nameof(ExistingFileAction),
                     nameof(CheckUpdateOnStartup), nameof(CloseAction), nameof(ProxyMode), nameof(ProxyAddress),
                     nameof(RequestTimeoutSeconds), nameof(MaxRetries), nameof(AutoFallbackEndpoint),
                     nameof(ShowThumbnails), nameof(FadeCompletedProgress), nameof(ReduceMotion),
                     nameof(AccentColor),
                     nameof(LogLevel), nameof(IsProxyManual), nameof(ConfigEntries)
                 })
        {
            OnPropertyChanged(name);
        }
    }

    private void Edit(Action apply, [CallerMemberName] string? propertyName = null)
    {
        apply();
        _settings.Save();
        if (propertyName is not null) OnPropertyChanged(propertyName);
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // 打不开目录不阻断设置页
        }
    }
}

public sealed record QnOption(int Qn, string Text);

public sealed record AudioOptionItem(int Id, string Text);
