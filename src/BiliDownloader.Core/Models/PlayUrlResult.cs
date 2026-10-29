namespace BiliDownloader.Core.Models;

/// <summary>一路 DASH 流（视频或音频），同一 qn 可能有多条（不同 codecid）。</summary>
public sealed record DashStream(
    int Id,
    int CodecId,
    string BaseUrl,
    IReadOnlyList<string> BackupUrls,
    string? Codecs,
    int Width,
    int Height,
    long Bandwidth,
    long? ContentLength)
{
    /// <summary>主链 + 备用链（方案设计 §8.5：主链 403/超时立刻切下一个域名）。</summary>
    public IEnumerable<string> AllUrls()
    {
        if (!string.IsNullOrEmpty(BaseUrl)) yield return BaseUrl;
        foreach (var url in BackupUrls)
            if (!string.IsNullOrEmpty(url)) yield return url;
    }
}

/// <summary>playurl 结果（领域模型）：已按 qn 归并的视频流 + 音频流。</summary>
public sealed class PlayUrlResult
{
    public IReadOnlyList<DashStream> Videos { get; init; } = [];
    public IReadOnlyList<DashStream> Audios { get; init; } = [];
    public TimeSpan Duration { get; init; }

    /// <summary>按 (qn, codecid) 精确取流，取不到则按 qn → 任意编码回退（§8.2）。</summary>
    public DashStream? FindVideo(int qn, int codecId) =>
        Videos.FirstOrDefault(v => v.Id == qn && v.CodecId == codecId)
        ?? Videos.FirstOrDefault(v => v.Id == qn)
        ?? Videos.OrderByDescending(v => v.Id).FirstOrDefault();

    /// <summary>按 audio id 取流，取不到则回退到可用最高档。</summary>
    public DashStream? FindAudio(int audioId) =>
        Audios.FirstOrDefault(a => a.Id == audioId)
        ?? Audios.OrderByDescending(a => a.Id).FirstOrDefault();
}
