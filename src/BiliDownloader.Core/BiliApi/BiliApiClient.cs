using System.Net;
using System.Net.Http;
using System.Text.Json;
using BiliDownloader.Core.Auth;
using BiliDownloader.Core.BiliApi.Models;
using BiliDownloader.Core.Errors;
using BiliDownloader.Core.Models;

namespace BiliDownloader.Core.BiliApi;

/// <summary>
/// B 站 API 客户端（HttpClient 单例注入）。
/// 登录态通过 <see cref="ICredentialStore.Current"/> 实时读取，登录/登出立即生效，不重建请求管道。
/// </summary>
public sealed class BiliApiClient : IBiliApiClient
{
    internal static readonly JsonSerializerOptions JsonOptions = DtoMapping.JsonOptions;

    private readonly HttpClient _http;
    private readonly ICredentialStore _credentials;
    private readonly TimeProvider _time;

    private string? _mixinKey;
    private DateOnly _wbiKeyDate;

    public BiliApiClient(HttpClient http, ICredentialStore credentials, TimeProvider? time = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _time = time ?? TimeProvider.System;
    }

    public bool IsLoggedIn => _credentials.Current?.HasCookie == true;

    public async Task<NavInfo> GetNavAsync(CancellationToken ct = default)
    {
        var data = await FetchNavDataAsync(ct).ConfigureAwait(false);
        if (data is null || !data.IsLogin)
            return new NavInfo { IsLogin = false };

        return new NavInfo
        {
            IsLogin = true,
            Mid = data.Mid,
            Uname = data.Uname,
            Face = data.Face,
            IsVip = data.VipStatus == 1
        };
    }

    public async Task<VideoInfo> GetVideoInfoAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var query = id.StartsWith("av", StringComparison.OrdinalIgnoreCase)
            ? $"aid={Uri.EscapeDataString(id[2..])}"
            : $"bvid={Uri.EscapeDataString(id)}";

        var payload = await GetJsonAsync<VideoViewResponse>($"{Endpoints.View}?{query}", ct).ConfigureAwait(false);
        ThrowIfFailed(payload.Code, payload.Message);

        return DtoMapping.ToVideoInfo(payload);
    }

    public async Task<PlayUrlResult> GetPlayUrlAsync(PlayUrlRequest request, CancellationToken ct = default)
    {
        var loggedIn = IsLoggedIn;
        var fnval = request.Fnval != 0 ? request.Fnval : BuildFnval(loggedIn);

        var parameters = new Dictionary<string, string>
        {
            ["bvid"] = request.Bvid,
            ["cid"] = request.Cid.ToString(),
            ["qn"] = request.Qn.ToString(),
            ["fnver"] = "0",
            ["fnval"] = fnval.ToString(),
            ["fourk"] = "1"
        };
        if (!loggedIn) parameters["try_look"] = "1";

        string url;
        if (loggedIn)
        {
            var mixinKey = await GetMixinKeyAsync(ct).ConfigureAwait(false);
            url = $"{Endpoints.PlayUrlWbi}?{WbiSign.Sign(parameters, mixinKey)}";
        }
        else
        {
            var query = string.Join("&", parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
            url = $"{Endpoints.PlayUrlNoLogin}?{query}";
        }

        var payload = await GetJsonAsync<PlayUrlResponse>(url, ct).ConfigureAwait(false);
        ThrowIfFailed(payload.Code, payload.Message);

        return DtoMapping.ToPlayUrlResult(payload);
    }

    /// <summary>按需组装 fnval，不写 4048 魔数（方案设计 §8.4）。</summary>
    private static int BuildFnval(bool loggedIn)
    {
        // 16 = DASH，1024 = AV1；免登录只请求这两项，避免请求了拿不到还触发风控
        const int dash = 16;
        const int av1 = 1024;
        if (!loggedIn) return dash | av1;

        const int fourK = 128;
        const int hdr = 64;
        const int eightK = 512;
        const int dolby = 256 | 2048;
        return dash | av1 | fourK | hdr | eightK | dolby;
    }

    /// <summary>WBI keys 按天缓存（nav 每日轮换，不要每次业务请求都先调 nav）。</summary>
    private async Task<string> GetMixinKeyAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        if (_mixinKey is not null && _wbiKeyDate == today) return _mixinKey;

        var data = await FetchNavDataAsync(ct).ConfigureAwait(false);
        var imgUrl = data?.WbiImg?.ImgUrl;
        var subUrl = data?.WbiImg?.SubUrl;
        if (string.IsNullOrEmpty(imgUrl) || string.IsNullOrEmpty(subUrl))
            throw new BiliException(ErrorKind.Network, "获取签名密钥失败，请稍后重试");

        _mixinKey = WbiSign.MixinKey(KeyOf(imgUrl), KeyOf(subUrl));
        _wbiKeyDate = today;
        return _mixinKey;
    }

    private static string KeyOf(string url)
    {
        var name = url[(url.LastIndexOf('/') + 1)..];
        var dot = name.IndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    /// <summary>取 nav 的 data（未登录 code=-101 也要能拿到 wbi_img，所以不判 code）。</summary>
    private async Task<NavData?> FetchNavDataAsync(CancellationToken ct)
    {
        var payload = await GetJsonAsync<NavResponse>(Endpoints.Nav, ct).ConfigureAwait(false);
        return payload.Data;
    }

    private async Task<T> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var cookie = _credentials.Current?.Cookie;
            if (!string.IsNullOrEmpty(cookie))
                request.Headers.TryAddWithoutValidation("Cookie", cookie);

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                                           .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.PreconditionFailed)
                throw new BiliException(ErrorKind.RiskControl, "访问被风控拦截，请稍后再试");

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<T>(json, JsonOptions);
            return result ?? throw BiliException.Network();
        }
        catch (BiliException) { throw; }
        catch (JsonException ex)
        {
            // B 站改字段 / 返回 HTML 拦截页
            throw new BiliException(ErrorKind.Network, "接口返回异常，请稍后重试", ex);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
        {
            throw BiliException.Network(ex);
        }
    }

    private static void ThrowIfFailed(int code, string? message)
    {
        if (code == 0) return;

        throw code switch
        {
            -101 => BiliException.AuthExpired(),
            -404 or 62002 => BiliException.VideoUnavailable(),
            -352 or -412 => new BiliException(ErrorKind.RiskControl, "访问被风控拦截，请稍后再试"),
            87008 => new BiliException(ErrorKind.PaidContent, "充电专属视频，v1 不支持"),
            _ => new BiliException(ErrorKind.VideoUnavailable, message ?? "接口返回错误，请稍后重试")
        };
    }
}
