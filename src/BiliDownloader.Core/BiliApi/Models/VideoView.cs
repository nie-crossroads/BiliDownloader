using System.Text.Json.Serialization;

namespace BiliDownloader.Core.BiliApi.Models;

/// <summary>GET /x/web-interface/view 响应（容错反序列化：字段全可空）。</summary>
public sealed class VideoViewResponse
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("data")] public VideoViewData? Data { get; set; }
}

public sealed class VideoViewData
{
    [JsonPropertyName("bvid")] public string? Bvid { get; set; }
    [JsonPropertyName("aid")] public long Aid { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("pic")] public string? Pic { get; set; }
    /// <summary>秒。</summary>
    [JsonPropertyName("duration")] public int Duration { get; set; }
    [JsonPropertyName("cid")] public long Cid { get; set; }
    [JsonPropertyName("owner")] public OwnerDto? Owner { get; set; }
    [JsonPropertyName("pages")] public List<PageDto>? Pages { get; set; }
    [JsonPropertyName("rights")] public RightsDto? Rights { get; set; }
    [JsonPropertyName("is_upower_exclusive")] public bool IsUpowerExclusive { get; set; }
    [JsonPropertyName("is_chargeable_season")] public bool IsChargeableSeason { get; set; }
    [JsonPropertyName("ugc_season")] public UgcSeasonDto? UgcSeason { get; set; }
}

public sealed class OwnerDto
{
    [JsonPropertyName("mid")] public long Mid { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("face")] public string? Face { get; set; }
}

public sealed class PageDto
{
    [JsonPropertyName("cid")] public long Cid { get; set; }
    [JsonPropertyName("page")] public int PageIndex { get; set; }
    [JsonPropertyName("part")] public string? Part { get; set; }
    [JsonPropertyName("duration")] public int Duration { get; set; }
}

public sealed class RightsDto
{
    [JsonPropertyName("pay")] public int Pay { get; set; }
    [JsonPropertyName("ugc_pay")] public int UgcPay { get; set; }
}

public sealed class UgcSeasonDto
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
}
