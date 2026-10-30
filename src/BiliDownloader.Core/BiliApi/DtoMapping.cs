using System.Text.Json;
using System.Text.Json.Serialization;
using BiliDownloader.Core.BiliApi.Models;
using BiliDownloader.Core.Errors;
using BiliDownloader.Core.Models;

namespace BiliDownloader.Core.BiliApi;

/// <summary>
/// DTO → 领域模型的纯映射，与 HTTP 完全解耦。
/// <para>
/// B 站的字段名、付费判定字段、以及 baseUrl / base_url 双写都可能悄悄变化，
/// 这是最容易坏又最难在界面上看出来的地方。抽成纯函数后可用固定输入输出锁定，
/// 接口一变测试立刻红，而不是等到用户下出来的文件不对才发现。
/// </para>
/// </summary>
public static class DtoMapping
{
    /// <summary>
    /// 反序列化选项：字段名大小写不敏感（B 站驼峰 / 蛇形混用），数字允许来自字符串
    /// （部分接口把 cid、duration 写成字符串）。生产与测试共用这一份，避免两边配置漂移。
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>view 响应 → 领域视频信息。缺字段一律兜底，不抛空引用。</summary>
    public static VideoInfo ToVideoInfo(VideoViewResponse payload)
    {
        var data = payload.Data ?? throw BiliException.VideoUnavailable();
        if (string.IsNullOrWhiteSpace(data.Bvid))
            throw BiliException.VideoUnavailable();

        // 分 P：part 为空就用 P{序号}，序号非法按 1 处理；单 P 视频常常没有 pages，用 cid 兜一条
        var pages = data.Pages?.Select(p => new PageInfo(
                p.Cid,
                string.IsNullOrWhiteSpace(p.Part) ? $"P{p.PageIndex}" : p.Part,
                p.PageIndex <= 0 ? 1 : p.PageIndex,
                TimeSpan.FromSeconds(Math.Max(0, p.Duration))))
            .ToList() ?? [];

        if (pages.Count == 0 && data.Cid > 0)
            pages.Add(new PageInfo(data.Cid, data.Title ?? "视频", 1, TimeSpan.FromSeconds(data.Duration)));

        var isPaid = (data.Rights?.Pay ?? 0) > 0
                     || data.IsUpowerExclusive
                     || data.IsChargeableSeason;

        return new VideoInfo
        {
            Bvid = data.Bvid,
            Avid = data.Aid,
            Title = data.Title ?? data.Bvid,
            UpName = data.Owner?.Name,
            UpMid = data.Owner?.Mid ?? 0,
            CoverUrl = NormalizeUrl(data.Pic),
            Duration = TimeSpan.FromSeconds(Math.Max(0, data.Duration)),
            Pages = pages,
            IsPaidContent = isPaid,
            HasUgcSeason = data.UgcSeason is not null
        };
    }

    /// <summary>playurl 响应 → 领域流信息。没有 DASH 或没有视频流都属于「这个画质下不了」。</summary>
    public static PlayUrlResult ToPlayUrlResult(PlayUrlResponse payload)
    {
        var data = payload.Data;
        if (data?.Dash is null)
            throw new BiliException(ErrorKind.Unknown, "该视频暂不支持 DASH 流，无法下载");

        var videos = ToStreams(data.Dash.Video);
        var audios = ToStreams(data.Dash.Audio);

        if (videos.Count == 0)
            throw new BiliException(ErrorKind.VipRequired, "没有获取到可下载的视频流（该画质需要大会员）");

        return new PlayUrlResult
        {
            Videos = videos,
            Audios = audios,
            Duration = TimeSpan.FromSeconds(data.Dash.Duration > 0 ? data.Dash.Duration : 0)
        };
    }

    private static List<DashStream> ToStreams(List<DashItemDto>? items) =>
        (items ?? [])
            .Where(v => v.Url is not null)
            .Select(v => new DashStream(
                v.Id, v.CodecId, v.Url!, v.Backups ?? [], v.Codecs,
                v.Width, v.Height, v.Bandwidth, v.Size))
            .ToList();

    /// <summary>协议相对地址补 https，明文 http 一律升 https。</summary>
    public static string? NormalizeUrl(string? url) =>
        string.IsNullOrWhiteSpace(url) ? null
        : url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url
        : url.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);
}
