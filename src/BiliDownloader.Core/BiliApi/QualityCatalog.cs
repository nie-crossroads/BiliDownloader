using BiliDownloader.Core.Models;

namespace BiliDownloader.Core.BiliApi;

/// <summary>
/// 清晰度 / 音质档位表（方案设计 §8.1、§8.3）与可用性判定。
/// 档位名与权限是配置化常量，接口返回什么流才决定「能不能下」。
/// </summary>
public static class QualityCatalog
{
    /// <summary>qn → (名称, 最低权限)。Permission: 0=免登录 1=登录 2=大会员。</summary>
    private static readonly (int Qn, string Name, int Permission)[] VideoQnTable =
    [
        (127, "8K 超清", 2),
        (126, "杜比视界", 2),
        (125, "HDR 真彩", 2),
        (120, "4K 超清", 2),
        (116, "1080P60 高帧率", 1),
        (112, "1080P+ 高码率", 1),
        (80, "1080P 高清", 0),
        (74, "720P60", 1),
        (64, "720P 高清", 0),
        (32, "480P 清晰", 0),
        (16, "360P 流畅", 0),
        (6, "240P 极速", 0)
    ];

    private static readonly (int Id, string Name, int Permission)[] AudioTable =
    [
        (30251, "Hi-Res 无损", 2),
        (30250, "杜比全景声", 2),
        (30280, "192K", 0),
        (30232, "132K", 0),
        (30216, "64K", 0)
    ];

    public static string CodecName(int codecId) => codecId switch
    {
        7 => "AVC",
        12 => "HEVC",
        13 => "AV1",
        _ => $"codec{codecId}"
    };

    public static string QualityName(int qn) =>
        VideoQnTable.FirstOrDefault(t => t.Qn == qn).Name ?? $"{qn}";

    public static string AudioName(int id) =>
        AudioTable.FirstOrDefault(t => t.Id == id).Name ?? $"{id}";

    /// <summary>全部画质档位（由高到低），供设置页「默认画质」下拉使用。</summary>
    public static IReadOnlyList<int> QualityList => VideoQnTable.Select(t => t.Qn).ToArray();

    /// <summary>全部音质档位（由高到低），供设置页「默认音质」下拉使用。</summary>
    public static IReadOnlyList<int> AudioIdList => AudioTable.Select(t => t.Id).ToArray();

    /// <summary>
    /// 由 playurl 结果 + 账号权限生成下拉项。接口里有的档位才可用，
    /// 其余档位保留但灰显并标注原因（需登录 / 需大会员 / 该视频无此画质）。
    /// </summary>
    public static IReadOnlyList<QualityOption> BuildVideoOptions(
        PlayUrlResult play, bool isLoggedIn, bool isVip)
    {
        var result = new List<QualityOption>();

        foreach (var (qn, name, permission) in VideoQnTable)
        {
            var streams = play.Videos.Where(v => v.Id == qn)
                                     .OrderBy(v => CodecPriority(v.CodecId))
                                     .ToList();

            if (streams.Count > 0)
            {
                // 同一 qn 多条编码：按 AVC > HEVC > AV1 并列展示
                foreach (var s in streams)
                {
                    var codec = CodecName(s.CodecId);
                    result.Add(new QualityOption(
                        qn, s.CodecId, $"{name} · {codec}", name, true, null,
                        s.Width, s.Height, s.Bandwidth));
                }
                continue;
            }

            var reason = permission switch
            {
                2 when !isVip => "需大会员",
                1 when !isLoggedIn => "需登录",
                _ => "该视频无此画质"
            };
            result.Add(new QualityOption(qn, 0, name, name, false, reason, 0, 0, 0));
        }

        return result;
    }

    public static IReadOnlyList<AudioOption> BuildAudioOptions(
        PlayUrlResult play, bool isLoggedIn, bool isVip)
    {
        var result = new List<AudioOption>();

        foreach (var (id, name, permission) in AudioTable)
        {
            var stream = play.Audios.FirstOrDefault(a => a.Id == id);
            if (stream is not null)
            {
                result.Add(new AudioOption(id, name, true, null, stream.Bandwidth));
                continue;
            }

            var reason = permission switch
            {
                2 when !isVip => isLoggedIn ? "需大会员" : "需登录",
                1 when !isLoggedIn => "需登录",
                _ => "无此音质"
            };
            result.Add(new AudioOption(id, name, false, reason, 0));
        }

        return result;
    }

    /// <summary>默认画质：优先设置里的 preferredQn，取不到就取可用的最高档（AVC 优先）。</summary>
    public static QualityOption? PickDefault(IReadOnlyList<QualityOption> options, int preferredQn, string preferredCodec)
    {
        var available = options.Where(o => o.IsAvailable).ToList();
        if (available.Count == 0) return null;

        var codecId = preferredCodec switch
        {
            "HEVC" => 12,
            "AV1" => 13,
            _ => 7
        };

        return available.FirstOrDefault(o => o.Qn == preferredQn && o.CodecId == codecId)
            ?? available.Where(o => o.Qn == preferredQn).OrderBy(o => CodecPriority(o.CodecId)).FirstOrDefault()
            ?? available.OrderByDescending(o => o.Qn).ThenBy(o => CodecPriority(o.CodecId)).First();
    }

    /// <summary>默认音质：普通档里可用的最高（30280 → 30232 → 30216），不自动升到会员档。</summary>
    public static AudioOption? PickDefaultAudio(IReadOnlyList<AudioOption> options, int preferredId)
    {
        var normal = options.Where(o => o.IsAvailable && o.Id <= 30280).ToList();
        return normal.FirstOrDefault(o => o.Id == preferredId)
            ?? normal.OrderByDescending(o => o.Id).FirstOrDefault()
            ?? options.FirstOrDefault(o => o.IsAvailable);
    }

    private static int CodecPriority(int codecId) => codecId switch
    {
        7 => 0,
        12 => 1,
        13 => 2,
        _ => 3
    };
}
