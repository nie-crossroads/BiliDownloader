using BiliDownloader.Core.BiliApi;
using BiliDownloader.Core.BiliApi.Models;
using BiliDownloader.Core.Errors;
using BiliDownloader.Core.Utils;

namespace BiliDownloader.Core.Auth;

/// <summary>登录态门面：UI 只跟它打交道（扫码、Cookie 兜底、恢复、登出）。</summary>
public interface IBiliSession
{
    Credential? Current { get; }
    bool IsLoggedIn { get; }

    /// <summary>启动时尝试恢复本地凭据并校验（导航接口 isLogin）。</summary>
    Task<NavInfo?> RestoreAsync(CancellationToken ct = default);

    Task<QrLoginSession> BeginQrLoginAsync(CancellationToken ct = default);

    /// <summary>轮询；确认成功后自动持久化凭据并补全账号信息。</summary>
    Task<QrLoginResult> PollQrLoginAsync(string key, CancellationToken ct = default);

    /// <summary>手动粘贴 Cookie 兜底；校验通过后返回账号信息。</summary>
    Task<NavInfo> LoginWithCookieAsync(string cookie, CancellationToken ct = default);

    Task LogoutAsync(CancellationToken ct = default);
}

public sealed class BiliSession : IBiliSession
{
    private readonly ICredentialStore _store;
    private readonly IQrLogin _qrLogin;
    private readonly IBiliApiClient _api;

    public BiliSession(ICredentialStore store, IQrLogin qrLogin, IBiliApiClient api)
    {
        _store = store;
        _qrLogin = qrLogin;
        _api = api;
    }

    public Credential? Current => _store.Current;

    public bool IsLoggedIn => _store.Current?.HasCookie == true;

    public async Task<NavInfo?> RestoreAsync(CancellationToken ct = default)
    {
        var credential = await _store.LoadAsync(ct).ConfigureAwait(false);
        if (credential?.HasCookie != true) return null;

        var nav = await _api.GetNavAsync(ct).ConfigureAwait(false);
        if (!nav.IsLogin)
        {
            // 凭据过期：清掉本地 cookie，让 UI 回到未登录（AuthExpired 由调用方提示）
            _store.Clear();
            return null;
        }

        await _store.SaveAsync(credential with
        {
            Uid = nav.Mid,
            Uname = nav.Uname,
            Face = nav.Face,
            IsVip = nav.IsVip
        }, ct).ConfigureAwait(false);

        return nav;
    }

    public Task<QrLoginSession> BeginQrLoginAsync(CancellationToken ct = default) =>
        _qrLogin.GenerateAsync(ct);

    public async Task<QrLoginResult> PollQrLoginAsync(string key, CancellationToken ct = default)
    {
        var result = await _qrLogin.PollAsync(key, ct).ConfigureAwait(false);
        if (result.Status != QrLoginStatus.Confirmed || result.Credential is null)
            return result;

        var cookie = result.Credential.Cookie;
        if (string.IsNullOrWhiteSpace(cookie))
            return new QrLoginResult { Status = QrLoginStatus.Error, Message = "登录成功但未拿到凭据，请重试" };

        await _store.SaveAsync(new Credential { Cookie = cookie }, ct).ConfigureAwait(false);

        var nav = await _api.GetNavAsync(ct).ConfigureAwait(false);
        var full = new Credential
        {
            Cookie = cookie,
            Uid = nav.Mid,
            Uname = nav.Uname,
            Face = nav.Face,
            IsVip = nav.IsVip
        };
        await _store.SaveAsync(full, ct).ConfigureAwait(false);

        return new QrLoginResult { Status = QrLoginStatus.Confirmed, Credential = full };
    }

    public async Task<NavInfo> LoginWithCookieAsync(string cookie, CancellationToken ct = default)
    {
        var invalid = CookieParser.Validate(cookie);
        if (invalid is not null) throw new BiliException(ErrorKind.AuthExpired, invalid);

        var normalized = cookie.Trim();
        await _store.SaveAsync(new Credential { Cookie = normalized }, ct).ConfigureAwait(false);

        var nav = await _api.GetNavAsync(ct).ConfigureAwait(false);
        if (!nav.IsLogin)
        {
            _store.Clear();
            throw new BiliException(ErrorKind.AuthExpired, "Cookie 无效或已过期，请重新获取");
        }

        await _store.SaveAsync(new Credential
        {
            Cookie = normalized,
            Uid = nav.Mid,
            Uname = nav.Uname,
            Face = nav.Face,
            IsVip = nav.IsVip
        }, ct).ConfigureAwait(false);

        return nav;
    }

    public Task LogoutAsync(CancellationToken ct = default)
    {
        _store.Clear();
        return Task.CompletedTask;
    }
}
