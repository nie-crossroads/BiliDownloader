using System.Text.Json.Serialization;

namespace BiliDownloader.Core.BiliApi.Models;

/// <summary>
/// GET /x/web-interface/nav。
/// ⚠️ 未登录时 code = -101，但 data.wbi_img 仍然有值 —— 取 WBI keys 不能判 code（方案设计 §4.7、§10.4）。
/// </summary>
public sealed class NavResponse
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("data")] public NavData? Data { get; set; }
}

public sealed class NavData
{
    [JsonPropertyName("isLogin")] public bool IsLogin { get; set; }
    [JsonPropertyName("mid")] public long Mid { get; set; }
    [JsonPropertyName("uname")] public string? Uname { get; set; }
    [JsonPropertyName("face")] public string? Face { get; set; }
    [JsonPropertyName("vipStatus")] public int VipStatus { get; set; }
    [JsonPropertyName("vipType")] public int VipType { get; set; }
    [JsonPropertyName("wbi_img")] public WbiImgDto? WbiImg { get; set; }
}

public sealed class WbiImgDto
{
    [JsonPropertyName("img_url")] public string? ImgUrl { get; set; }
    [JsonPropertyName("sub_url")] public string? SubUrl { get; set; }
}

/// <summary>登录态快照（UI 用）。</summary>
public sealed class NavInfo
{
    public bool IsLogin { get; init; }
    public long Mid { get; init; }
    public string? Uname { get; init; }
    public string? Face { get; init; }
    public bool IsVip { get; init; }
}
