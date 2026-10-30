using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using BiliDownloader.Core.Auth;
using BiliDownloader.Core.BiliApi.Models;
using BiliDownloader.Core.Errors;
using BiliDownloader.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BiliDownloader.UI.ViewModels;

/// <summary>
/// 账号视图模型：登录态（扫码 / Cookie / 恢复 / 登出）+ 二维码轮询。
/// 从 MainViewModel 拆出，便于独立扩展登录能力（架构评审第 4 项）。
/// </summary>
public partial class AccountViewModel : ObservableObject
{
    private const int QrTimeoutSeconds = 180;

    private readonly IBiliSession _session;
    private readonly AppSettings _settings;
    private readonly Action<LogLevel, string> _log;
    private readonly Dispatcher _dispatcher;

    private DispatcherTimer? _qrTimer;
    private DispatcherTimer? _qrCloseTimer;
    private DateTimeOffset _qrStartedAt;
    private string? _qrKey;
    private bool _qrPolling;

    /// <summary>登录 / 登出成功后触发，由主视图模型决定要不要刷新预览画质。</summary>
    public Action<string>? OnLoginRefreshRequested { get; set; }

    public AccountViewModel(IBiliSession session, AppSettings settings, Action<LogLevel, string> log, Dispatcher dispatcher)
    {
        _session = session;
        _settings = settings;
        _log = log;
        _dispatcher = dispatcher;
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

    /// <summary>启动时尝试恢复本地凭据并校验（导航接口 isLogin）。不会触发预览刷新。</summary>
    public async Task InitializeAsync()
    {
        try
        {
            var nav = await _session.RestoreAsync();
            if (nav is { IsLogin: true })
            {
                ApplyLogin(nav);
                _log(LogLevel.Info, $"恢复登录态：{nav.Uname}");
            }
        }
        catch (BiliException ex)
        {
            _log(LogLevel.Warn, $"恢复登录态失败：{ex.UserMessage}");
        }
    }

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
            _log(LogLevel.Warn, $"获取登录二维码失败：{ex.UserMessage}");
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
                    _log(LogLevel.Info, $"登录成功：{UserName}");
                    OnLoginRefreshRequested?.Invoke("登录成功");
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
            _log(LogLevel.Warn, $"轮询登录状态失败：{ex.UserMessage}");
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
        _log(LogLevel.Info, "已退出登录，本地凭证已清除");
        OnLoginRefreshRequested?.Invoke("已退出登录");
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
            _log(LogLevel.Info, $"Cookie 登录成功：{UserName}");
            OnLoginRefreshRequested?.Invoke("登录成功");
        }
        catch (BiliException ex)
        {
            CookieError = ex.UserMessage;
            _log(LogLevel.Warn, $"Cookie 登录失败：{ex.UserMessage}");
        }
    }
}
