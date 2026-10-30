using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using BiliDownloader.Core.Download;
using BiliDownloader.UI.Services;
using BiliDownloader.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace BiliDownloader.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IServiceProvider _services;
    private readonly AppSettings _settings;
    private readonly TrayIconService _tray;

    /// <summary>置 true 后 Closing 不再拦截，用于托盘菜单里的真退出。</summary>
    private bool _forceExit;

    public MainWindow(MainViewModel viewModel, IServiceProvider services)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _services = services;
        _settings = services.GetRequiredService<AppSettings>();
        DataContext = viewModel;

        _tray = new TrayIconService("BiliDownloader");
        _tray.OpenRequested += RestoreFromTray;
        _tray.ExitRequested += ExitFromTray;

        // 完成通知 / 新版本提示：视图模型只管喊，气泡由托盘负责弹。
        _viewModel.NotificationRequested += (title, text) => _tray.Notify(title, text);

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnWindowClosing;
        Closed += (_, _) => _tray.Dispose();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var settings = _services.GetRequiredService<AppSettings>();
        var viewModel = new SettingsViewModel(settings, OpenCookieFromSettings,
            _services.GetRequiredService<ISpeedLimiter>(), _services.GetRequiredService<StartupService>());
        var window = new SettingsWindow(viewModel)
        {
            Owner = this
        };
        window.ShowDialog();

        // 动效、完成后淡化这类展示项不会自己发通知，回到主窗口补刷新一次。
        _viewModel.RefreshPresentationSettings();
    }

    /// <summary>设置页里的「手动粘贴 Cookie」：先关设置窗口，再回到主窗打开弹层。</summary>
    private void OpenCookieFromSettings()
    {
        foreach (Window window in Application.Current.Windows)
        {
            if (window is not SettingsWindow settings) continue;
            settings.Close();
            break;
        }

        _viewModel.Account.ShowCookieCommand.Execute(null);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureOnScreen();
        await _viewModel.InitializeAsync();
    }

    /// <summary>
    /// 兜底把窗口拉回主屏。
    /// WindowStyle=None + WindowStartupLocation=CenterScreen 在某些机器上会算错位置，
    /// 窗口被丢到屏幕外（实测 Left=-1151，1040 宽的窗口右边界还在 x=-111），启动后
    /// 用户只看到任务栏图标、点一下窗口才「跳出来」，观感就像它总压在最前面。
    /// 这里不复用 WPF 的 Left/Top：窗口完全在屏幕外时 WPF 侧算出的值不可靠，
    /// 直接用 Win32 问系统「这个窗口还落在任何显示器上吗」，不在就挪回主屏居中。
    /// </summary>
    private void EnsureOnScreen()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        // MONITOR_DEFAULTTONULL：与任何显示器都不相交时返回 NULL。
        if (MonitorFromWindow(handle, MonitorDefaultToNull) != IntPtr.Zero) return;
        if (!GetWindowRect(handle, out var rect)) return;

        // 挪回主显示器工作区（不含任务栏）居中。
        var monitor = MonitorFromWindow(handle, MonitorDefaultToPrimary);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var x = info.Work.Left + (info.Work.Right - info.Work.Left - width) / 2;
        var y = info.Work.Top + (info.Work.Bottom - info.Work.Top - height) / 2;

        SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;

        // Win11 圆角（12px）。系统不支持时静默忽略（视觉规范 §10.5）。
        try
        {
            var preference = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(handle, 33, ref preference, sizeof(int));
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        // 防御：清掉任何外部因素留下的置顶状态。窗口一旦被设成 WS_EX_TOPMOST，
        // 就会永远压在别的程序上面，点后面的应用也不会被遮挡 —— 这与设计预期相反。
        SetWindowPos(handle, HwndNotTopmost, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 所有关闭路径（标题栏 ×、Alt+F4、系统菜单）都在这里收口。
    /// 设置里选了「最小化到任务栏」就取消关闭、改成隐藏窗口 + 亮托盘图标，下载继续跑；
    /// 选「退出程序」才真关。以前这里压根没读 CloseAction，所以设置怎么选都是直接退出。
    /// </summary>
    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_forceExit) return;

        // 取证模式必须能真的关掉窗口，否则脚本收尾时退不出去
        if (DiagnosticsShot.IsRequested(Environment.GetCommandLineArgs())) return;

        if (_settings.CloseAction is not "Minimize") return;

        e.Cancel = true;
        Hide();
        _tray.Show();
    }

    private void RestoreFromTray()
    {
        _tray.Hide();
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        _forceExit = true;
        _tray.Dispose();
        Application.Current.Shutdown();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static readonly IntPtr HwndNotTopmost = new(-2);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint MonitorDefaultToNull = 0x00000000;
    private const uint MonitorDefaultToPrimary = 0x00000002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }
}
