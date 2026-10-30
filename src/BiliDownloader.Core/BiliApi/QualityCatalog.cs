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
        (80, "1080P 高清", 0),
        (64, "720P 高清", 0),
        (32, "480P 清晰", 0),
        (16, "360P 流畅", 0)
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
    /// <para>
    /// 每个清晰度只出一条：同一 qn 常常同时给 AVC / HEVC / AV1 三条流，下拉里并列展示会让用户
    /// 先纠结编码再纠结清晰度，而设置里已经有统一的「默认编码」。这里按 preferredCodec 挑一路，
    /// 挑不到再按 AVC &gt; HEVC &gt; AV1 兜底，所以文案就是纯清晰度名，不带编码后缀。
    /// </para>
    /// </summary>
    public static IReadOnlyList<QualityOption> BuildVideoOptions(
        PlayUrlResult play, bool isLoggedIn, bool isVip, string? preferredCodec = null)
    {
        var wanted = CodecIdFromName(preferredCodec);
        var result = new List<QualityOption>();

        foreach (var (qn, name, permission) in VideoQnTable)
        {
            var stream = play.Videos.Where(v => v.Id == qn)
                                    .OrderBy(v => CodecRank(v.CodecId, wanted))
                                    .FirstOrDefault();

            if (stream is not null)
            {
                result.Add(new QualityOption(
                    qn, stream.CodecId, name, name, true, null,
                    stream.Width, stream.Height, stream.Bandwidth));
                continue;
            }

            var reason = permission switch
            {
                2 when !isVip => "需大会员",
                1 when !isLoggedIn => "需登录",
                _ => "该视频无此画质"
            };
            // 不可用档位没有流可挑，CodecId 记 0；真下单前必然会换成用户实际选中的可用档。
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

    /// <summary>
    /// 默认画质：优先设置里的 preferredQn，取不到就取可用的最高档。
    /// 现在每个 qn 只有一条选项，codecId 只在同 qn 仍有并列项时用来挑 nearer 的那条。
    /// </summary>
    public static QualityOption? PickDefault(IReadOnlyList<QualityOption> options, int preferredQn, string preferredCodec)
    {
        var available = options.Where(o => o.IsAvailable).ToList();
        if (available.Count == 0) return null;

        var codecId = CodecIdFromName(preferredCodec);

        return available.FirstOrDefault(o => o.Qn == preferredQn && o.CodecId == codecId)
            ?? available.Where(o => o.Qn == preferredQn).OrderBy(o => CodecPriority(o.CodecId)).FirstOrDefault()
            ?? available.OrderByDescending(o => o.Qn).ThenBy(o => CodecPriority(o.CodecId)).First();
    }

    /// <summary>
    /// 默认音质：优先用户在设置里指定的档位；指定档不可用时，在「普通档」里取最高
    /// （30280 → 30232 → 30216），不自动升到会员专属档。
    /// <para>
    /// 注意不能写成 <c>Id &lt;= 30280</c>：30251（Hi-Res）和 30250（杜比）在数值上都小于 30280，
    /// 那样会把会员档也算进普通档，默认就白白升到会员音质了。是否会员档只能查权限表。
    /// </para>
    /// </summary>
    public static AudioOption? PickDefaultAudio(IReadOnlyList<AudioOption> options, int preferredId)
    {
        var available = options.Where(o => o.IsAvailable).ToList();
        if (available.Count == 0) return null;

        // 指定档优先——包括会员档，那是用户主动选的，应当尊重
        var exact = available.FirstOrDefault(o => o.Id == preferredId);
        if (exact is not null) return exact;

        // 没指定或指定档拿不到：普通档里取最高
        var normal = available.Where(o => !IsVipAudioTier(o.Id)).ToList();
        return normal.OrderByDescending(o => o.Id).FirstOrDefault()
               // 只有会员档可用时没得挑，退回第一个可用的
               ?? available[0];
    }

    /// <summary>是否会员专属音质档（权限 2）。</summary>
    private static bool IsVipAudioTier(int id) =>
        AudioTable.FirstOrDefault(t => t.Id == id).Permission >= 2;

    /// <summary>设置里存的编码名 → codecid。认不出来一律按 AVC 处理。</summary>
    public static int CodecIdFromName(string? name) => name switch
    {
        "HEVC" => 12,
        "AV1" => 13,
        _ => 7
    };

    private static int CodecPriority(int codecId) => codecId switch
    {
        7 => 0,
        12 => 1,
        13 => 2,
        _ => 3
    };

    /// <summary>同清晰度多条流时的挑选顺序：用户指定的编码第一，其余按 AVC &gt; HEVC &gt; AV1。</summary>
    private static int CodecRank(int codecId, int wanted) =>
        codecId == wanted ? 0 : CodecPriority(codecId) + 1;
}
