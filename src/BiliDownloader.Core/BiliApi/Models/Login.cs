using System.Text.Json.Serialization;

namespace BiliDownloader.Core.BiliApi.Models;

public sealed class QrGenerateResponse
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("data")] public QrGenerateData? Data { get; set; }
}

public sealed class QrGenerateData
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("qrcode_key")] public string? QrCodeKey { get; set; }
}

public sealed class QrPollResponse
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("data")] public QrPollData? Data { get; set; }
}

public sealed class QrPollData
{
    /// <summary>0 成功 / 86101 未扫码 / 86090 已扫码未确认 / 86038 已过期。</summary>
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    /// <summary>成功时带回 SESSDATA 等参数（同时 Set-Cookie 里也有）。</summary>
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
}
