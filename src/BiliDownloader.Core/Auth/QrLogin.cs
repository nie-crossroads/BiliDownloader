using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using BiliDownloader.Core.BiliApi;
using BiliDownloader.Core.BiliApi.Models;
using BiliDownloader.Core.Errors;
using BiliDownloader.Core.Utils;

namespace BiliDownloader.Core.Auth;

/// <summary>扫码会话（UI 把 <see cref="Url"/> 渲染成二维码）。</summary>
public sealed record QrLoginSession(string Url, string Key);

public enum QrLoginStatus
{
    Waiting,
    Scanned,
    Confirmed,
    Expired,
    Error
}

public sealed class QrLoginResult
{
    public QrLoginStatus Status { get; init; }
    public Credential? Credential { get; init; }
    public string? Message { get; init; }
}

/// <summary>QR 扫码登录（拿 B 站自身 token，最稳；v1 主登录方式）。</summary>
public interface IQrLogin
{
    Task<QrLoginSession> GenerateAsync(CancellationToken ct = default);

    Task<QrLoginResult> PollAsync(string key, CancellationToken ct = default);
}

/// <summary>B 站扫码登录实现。轮询间隔由调用方控制（视觉规范 §6.12：2s 一次，180s 超时）。</summary>
public sealed class QrLogin : IQrLogin
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _http;

    public QrLogin(HttpClient http) => _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<QrLoginSession> GenerateAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync(Endpoints.QrGenerate, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<QrGenerateResponse>(JsonOptions, ct)
                          ?? throw BiliException.Network();

            if (payload.Code != 0 || payload.Data?.Url is null || payload.Data.QrCodeKey is null)
                throw new BiliException(ErrorKind.Network, payload.Message ?? "获取登录二维码失败，请重试");

            return new QrLoginSession(payload.Data.Url, payload.Data.QrCodeKey);
        }
        catch (BiliException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw BiliException.Network(ex);
        }
    }

    public async Task<QrLoginResult> PollAsync(string key, CancellationToken ct = default)
    {
        var url = $"{Endpoints.QrPoll}?qrcode_key={Uri.EscapeDataString(key)}&source=main-feature";

        try
        {
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<QrPollResponse>(JsonOptions, ct)
                          ?? throw BiliException.Network();

            if (payload.Code != 0 || payload.Data is null)
                return new QrLoginResult { Status = QrLoginStatus.Error, Message = payload.Message };

            return payload.Data.Code switch
            {
                0 => new QrLoginResult
                {
                    Status = QrLoginStatus.Confirmed,
                    Credential = new Credential
                    {
                        Cookie = CookieParser.FromSetCookie(response) ?? CookieParser.FromUrl(payload.Data.Url) ?? string.Empty
                    }
                },
                86090 => new QrLoginResult { Status = QrLoginStatus.Scanned },
                86038 => new QrLoginResult { Status = QrLoginStatus.Expired },
                _ => new QrLoginResult { Status = QrLoginStatus.Waiting }
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw BiliException.Network(ex);
        }
    }
}
