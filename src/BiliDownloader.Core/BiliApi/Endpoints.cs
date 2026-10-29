namespace BiliDownloader.Core.BiliApi;

/// <summary>各接口 URL 与必要默认参数集中管理，避免散落硬编码。</summary>
public static class Endpoints
{
    public const string Nav = "https://api.bilibili.com/x/web-interface/nav";
    public const string View = "https://api.bilibili.com/x/web-interface/view";
    public const string PlayUrlWbi = "https://api.bilibili.com/x/player/wbi/playurl";        // 登录态 + WBI 签名
    public const string PlayUrlNoLogin = "https://api.bilibili.com/x/player/playurl";        // 免登录老接口 + try_look=1
    public const string QrGenerate = "https://passport.bilibili.com/x/passport-login/web/qrcode/generate";
    public const string QrPoll = "https://passport.bilibili.com/x/passport-login/web/qrcode/poll";
}
