using System.Text.Json.Serialization;

namespace BiliDownloader.Core.BiliApi.Models;

/// <summary>playurl 响应（DASH；durl 仅兜底判断）。</summary>
public sealed class PlayUrlResponse
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("data")] public PlayUrlData? Data { get; set; }
}

public sealed class PlayUrlData
{
    [JsonPropertyName("dash")] public DashDto? Dash { get; set; }
    [JsonPropertyName("durl")] public List<DurlDto>? Durl { get; set; }
    [JsonPropertyName("quality")] public int Quality { get; set; }
    [JsonPropertyName("accept_quality")] public List<int>? AcceptQuality { get; set; }
    [JsonPropertyName("accept_description")] public List<string>? AcceptDescription { get; set; }
}

public sealed class DashDto
{
    [JsonPropertyName("duration")] public int Duration { get; set; }
    [JsonPropertyName("video")] public List<DashItemDto>? Video { get; set; }
    [JsonPropertyName("audio")] public List<DashItemDto>? Audio { get; set; }
}

public sealed class DashItemDto
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("codecid")] public int CodecId { get; set; }
    [JsonPropertyName("baseUrl")] public string? BaseUrl { get; set; }
    [JsonPropertyName("base_url")] public string? BaseUrlSnake { get; set; }
    [JsonPropertyName("backupUrl")] public List<string>? BackupUrl { get; set; }
    [JsonPropertyName("backup_url")] public List<string>? BackupUrlSnake { get; set; }
    [JsonPropertyName("codecs")] public string? Codecs { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("bandwidth")] public long Bandwidth { get; set; }
    /// <summary>部分返回里没有，靠 Range 探测。</summary>
    [JsonPropertyName("size")] public long? Size { get; set; }

    public string? Url => BaseUrl ?? BaseUrlSnake;
    public List<string>? Backups => BackupUrl ?? BackupUrlSnake;
}

public sealed class DurlDto
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
}
