using System.Text.Json;
using System.Text.RegularExpressions;
using BiliDownloader.Core.BiliApi;
using BiliDownloader.Core.BiliApi.Models;
using BiliDownloader.Core.Errors;
using BiliDownloader.Core.Models;

namespace BiliDownloader.Tests;

/// <summary>
/// B 站接口契约测试：锁定 WBI 签名、DTO 映射与清晰度策略的现有行为。
/// <para>
/// 这些地方 B 站一改接口就会悄悄坏掉——界面上看不出来，要等下出来的文件不对才发现。
/// 这里全部用固定输入输出锁住：签名输出串、字段兜底规则、权限标注、默认档位挑选。
/// 真的需要跟着 B 站改的时候，改代码的同时也要改这里的值，让变更是显式的。
/// </para>
/// </summary>
public class BiliApiContractTests
{
    private const string ImgKey = "7cd084941338484aae1ad9425b84077c";
    private const string SubKey = "4932cff0de5d4d7e9c1c5b0f6e5b8d0f";
    private const long FixedWts = 1700000000L;

    // ------------------------------------------------------------------ WBI 签名

    [Fact]
    public void MixinKey_IsAlways32Chars()
    {
        Assert.Equal(32, WbiSign.MixinKey(ImgKey, SubKey).Length);
    }

    /// <summary>
    /// 混入结果的每个字符都必须来自 imgKey+subKey：表下标抄错、取到范围外的值会立刻暴露。
    /// </summary>
    [Fact]
    public void MixinKey_UsesOnlyCharsFromSourceKeys()
    {
        var mixed = WbiSign.MixinKey(ImgKey, SubKey);
        var allowed = (ImgKey + SubKey).ToHashSet();

        Assert.All(mixed, c => Assert.Contains(c, allowed));
    }

    [Fact]
    public void MixinKey_DiffersWhenKeysDiffer()
    {
        Assert.NotEqual(WbiSign.MixinKey(ImgKey, SubKey), WbiSign.MixinKey(SubKey, ImgKey));
    }

    /// <summary>锁定输出：表或取字符逻辑变了这里就会红。</summary>
    [Fact]
    public void MixinKey_MatchesLockedValue()
    {
        Assert.Equal("7e1db124a13c50d24d4c935a70404fe8", WbiSign.MixinKey(ImgKey, SubKey));
    }

    /// <summary>Ordinal 升序：大写字母排在小写前面（'C' 0x43 &lt; 'a' 0x61）。</summary>
    [Fact]
    public void Sign_OrdersKeysByOrdinal()
    {
        var query = WbiSign.Sign(new Dictionary<string, string>
        {
            ["b"] = "2", ["a"] = "1", ["C"] = "3"
        }, "mixin", FixedWts);

        Assert.StartsWith("C=3&a=1&b=2&wts=1700000000", query, StringComparison.Ordinal);
    }

    /// <summary>
    /// 只剔除 ! ' ( ) * 这五个。'-' '.' '_' '~' 必须原样保留——
    /// 历史上把它们也过滤掉，导致带这些字符的参数值被静默篡改，业务侧莫名报错。
    /// </summary>
    [Fact]
    public void Sign_FiltersOnlyFiveCharacters()
    {
        var query = WbiSign.Sign(new Dictionary<string, string>
        {
            ["k"] = "a!b'c(d)e*f-g.h_i~j"
        }, "mixin", FixedWts);

        Assert.StartsWith("k=abcdef-g.h_i~j&wts=", query, StringComparison.Ordinal);
    }

    [Fact]
    public void Sign_DropsEmptyAndNullValues()
    {
        var query = WbiSign.Sign(new Dictionary<string, string>
        {
            ["keep"] = "1", ["empty"] = "", ["missing"] = null!
        }, "mixin", FixedWts);

        Assert.StartsWith("keep=1&wts=", query, StringComparison.Ordinal);
    }

    [Fact]
    public void Sign_AppendsWtsAnd32CharRid()
    {
        var query = WbiSign.Sign(new Dictionary<string, string> { ["a"] = "1" }, "mixin", FixedWts);

        Assert.Matches(new Regex(@"&wts=1700000000&w_rid=[0-9a-f]{32}$"), query);
    }

    /// <summary>
    /// 完整输出锁定。期望值由 openssl 独立计算（md5("bvid=...&wts=1700000000" + 32 个 m)），
    /// 不是拿实现跑出来的，所以能真正校验排序、过滤和 MD5 三件事。
    /// </summary>
    [Fact]
    public void Sign_MatchesLockedValue()
    {
        const string mixin = "mmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmm";

        var query = WbiSign.Sign(new Dictionary<string, string>
        {
            ["bvid"] = "BV1xx411c7mD",
            ["cid"] = "12345",
            ["qn"] = "80"
        }, mixin, FixedWts);

        Assert.Equal("bvid=BV1xx411c7mD&cid=12345&qn=80&wts=1700000000" +
                     "&w_rid=5c7e6e453f63a2f50bc696cfaf332528", query);
    }

    // ------------------------------------------------------------------ view DTO 映射

    [Fact]
    public void ToVideoInfo_MapsAllFields()
    {
        var info = DtoMapping.ToVideoInfo(new VideoViewResponse
        {
            Code = 0,
            Data = new VideoViewData
            {
                Bvid = "BV1xx411c7mD",
                Aid = 170001,
                Title = "测试标题",
                Pic = "https://i0.hdslb.com/cover.jpg",
                Duration = 212,
                Cid = 12345,
                Owner = new OwnerDto { Mid = 672328094, Name = "某UP" },
                Pages =
                [
                    new PageDto { Cid = 12345, PageIndex = 1, Part = "第一集", Duration = 100 },
                    new PageDto { Cid = 12346, PageIndex = 2, Part = "第二集", Duration = 112 }
                ]
            }
        });

        Assert.Equal("BV1xx411c7mD", info.Bvid);
        Assert.Equal(170001, info.Avid);
        Assert.Equal("某UP", info.UpName);
        Assert.Equal(672328094, info.UpMid);
        Assert.Equal(TimeSpan.FromSeconds(212), info.Duration);
        Assert.Equal(2, info.Pages.Count);
        Assert.True(info.IsMultiPage);
        Assert.False(info.IsPaidContent);
    }

    /// <summary>封面常返回 // 开头；也必须把 http:// 升到 https，否则会被混合内容拦截。</summary>
    [Fact]
    public void ToVideoInfo_NormalizesCoverUrl()
    {
        var info = DtoMapping.ToVideoInfo(ViewWith(pic: "//i0.hdslb.com/cover.jpg"));
        Assert.Equal("https://i0.hdslb.com/cover.jpg", info.CoverUrl);

        var http = DtoMapping.ToVideoInfo(ViewWith(pic: "http://i0.hdslb.com/cover.jpg"));
        Assert.Equal("https://i0.hdslb.com/cover.jpg", http.CoverUrl);
    }

    /// <summary>单 P 视频经常没有 pages，用顶层 cid 兜一条，否则预览页一个分 P 都选不了。</summary>
    [Fact]
    public void ToVideoInfo_FallsBackToCidWhenPagesMissing()
    {
        var info = DtoMapping.ToVideoInfo(new VideoViewResponse
        {
            Data = new VideoViewData { Bvid = "BV1xx411c7mD", Cid = 12345, Duration = 90, Title = "单P" }
        });

        Assert.Single(info.Pages);
        Assert.Equal(12345, info.Pages[0].Cid);
        Assert.False(info.IsMultiPage);
    }

    [Fact]
    public void ToVideoInfo_DefaultsPartNameAndFixesBadPageIndex()
    {
        var info = DtoMapping.ToVideoInfo(new VideoViewResponse
        {
            Data = new VideoViewData
            {
                Bvid = "BV1xx411c7mD",
                Pages =
                [
                    new PageDto { Cid = 1, PageIndex = 0, Part = "  " },
                    new PageDto { Cid = 2, PageIndex = -3, Part = null }
                ]
            }
        });

        Assert.Equal("P0", info.Pages[0].Part);
        Assert.Equal(1, info.Pages[0].Index);
        Assert.Equal("P-3", info.Pages[1].Part);
        Assert.Equal(1, info.Pages[1].Index);
    }

    /// <summary>三个来源任一为真都算付费内容；rights.ugc_pay 不参与判定（B 站用它表示别的含义）。</summary>
    [Theory]
    [InlineData(1, false, false, true)]
    [InlineData(0, true, false, true)]
    [InlineData(0, false, true, true)]
    [InlineData(0, false, false, false)]
    public void ToVideoInfo_DetectsPaidFromAnyFlag(int pay, bool upowerExclusive, bool chargeable, bool expected)
    {
        var data = new VideoViewData
        {
            Bvid = "BV1xx411c7mD",
            Rights = new RightsDto { Pay = pay, UgcPay = 1 },
            IsUpowerExclusive = upowerExclusive,
            IsChargeableSeason = chargeable
        };

        Assert.Equal(expected, DtoMapping.ToVideoInfo(new VideoViewResponse { Data = data }).IsPaidContent);
    }

    [Fact]
    public void ToVideoInfo_FlagsUgcSeason()
    {
        var data = new VideoViewData { Bvid = "BV1xx411c7mD", UgcSeason = new UgcSeasonDto { Id = 9 } };
        Assert.True(DtoMapping.ToVideoInfo(new VideoViewResponse { Data = data }).HasUgcSeason);
    }

    [Fact]
    public void ToVideoInfo_ThrowsWhenDataOrBvidMissing()
    {
        Assert.Throws<BiliException>(() => DtoMapping.ToVideoInfo(new VideoViewResponse { Code = 0 }));
        Assert.Throws<BiliException>(() =>
            DtoMapping.ToVideoInfo(new VideoViewResponse { Data = new VideoViewData { Bvid = "  " } }));
    }

    /// <summary>B 站部分接口把数字写成字符串，且会加我们不认识的字段。两者都不能让解析崩掉。</summary>
    [Fact]
    public void Deserialize_ToleratesStringNumbersAndUnknownFields()
    {
        const string json = """
        {
          "code": 0,
          "message": "0",
          "data": {
            "bvid": "BV1xx411c7mD",
            "aid": "170001",
            "cid": "12345",
            "duration": "212",
            "title": "字符串数字",
            "some_new_field_bilibili_added": { "nested": true },
            "owner": { "mid": "672328094", "name": "某UP" }
          }
        }
        """;

        var payload = JsonSerializer.Deserialize<VideoViewResponse>(json, DtoMapping.JsonOptions)!;
        var info = DtoMapping.ToVideoInfo(payload);

        Assert.Equal(170001, info.Avid);
        Assert.Equal(TimeSpan.FromSeconds(212), info.Duration);
        Assert.Equal(672328094, info.UpMid);
        Assert.Single(info.Pages);
    }

    // ------------------------------------------------------------------ playurl DTO 映射

    /// <summary>返回里 baseUrl / base_url、backupUrl / backup_url 都见过，两种都要能用。</summary>
    [Fact]
    public void ToPlayUrlResult_AcceptsCamelCaseAndSnakeCaseFields()
    {
        const string json = """
        {
          "code": 0,
          "data": {
            "dash": {
              "duration": 212,
              "video": [
                { "id": 80, "codecid": 7, "base_url": "https://cdn/a.m4s",
                  "backup_url": ["https://cdn/a2.m4s"], "width": 1920, "height": 1080, "bandwidth": 3000000 }
              ],
              "audio": [
                { "id": 30280, "codecid": 0, "baseUrl": "https://cdn/audio.m4s", "bandwidth": 192000 }
              ]
            }
          }
        }
        """;

        var payload = JsonSerializer.Deserialize<PlayUrlResponse>(json, DtoMapping.JsonOptions)!;
        var result = DtoMapping.ToPlayUrlResult(payload);

        Assert.Equal("https://cdn/a.m4s", result.Videos[0].BaseUrl);
        Assert.Equal(["https://cdn/a2.m4s"], result.Videos[0].BackupUrls);
        Assert.Equal("https://cdn/audio.m4s", result.Audios[0].BaseUrl);
        Assert.Equal(TimeSpan.FromSeconds(212), result.Duration);
    }

    [Fact]
    public void ToPlayUrlResult_SkipsItemsWithoutUrl()
    {
        var result = DtoMapping.ToPlayUrlResult(new PlayUrlResponse
        {
            Data = new PlayUrlData
            {
                Dash = new DashDto
                {
                    Video =
                    [
                        new DashItemDto { Id = 80, CodecId = 7 },
                        new DashItemDto { Id = 64, CodecId = 7, BaseUrl = "https://cdn/b.m4s" }
                    ]
                }
            }
        });

        Assert.Single(result.Videos);
        Assert.Equal(64, result.Videos[0].Id);
    }

    [Fact]
    public void ToPlayUrlResult_ThrowsWhenDashMissing()
    {
        var ex = Assert.Throws<BiliException>(() =>
            DtoMapping.ToPlayUrlResult(new PlayUrlResponse { Data = new PlayUrlData() }));
        Assert.Contains("DASH", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>一个视频流都没有 = 这个画质要大会员，要能给出正确分类而不是空列表。</summary>
    [Fact]
    public void ToPlayUrlResult_ThrowsVipRequiredWhenNoVideoStream()
    {
        var ex = Assert.Throws<BiliException>(() => DtoMapping.ToPlayUrlResult(new PlayUrlResponse
        {
            Data = new PlayUrlData { Dash = new DashDto { Audio = [new DashItemDto { BaseUrl = "https://a" }] } }
        }));

        Assert.Equal(ErrorKind.VipRequired, ex.Kind);
    }

    [Theory]
    [InlineData("//cdn/a.m4s", "https://cdn/a.m4s")]
    [InlineData("http://cdn/a.m4s", "https://cdn/a.m4s")]
    [InlineData("https://cdn/a.m4s", "https://cdn/a.m4s")]
    [InlineData("", null)]
    public void NormalizeUrl_HandlesAllShapes(string? input, string? expected)
    {
        Assert.Equal(expected, DtoMapping.NormalizeUrl(input));
    }

    // ------------------------------------------------------------------ 清晰度 / 音质策略

    [Theory]
    [InlineData(7, "AVC")]
    [InlineData(12, "HEVC")]
    [InlineData(13, "AV1")]
    [InlineData(999, "codec999")]
    public void CodecName_MapsKnownIds(int codecId, string expected)
    {
        Assert.Equal(expected, QualityCatalog.CodecName(codecId));
    }

    [Fact]
    public void QualityName_FallsBackToRawNumber()
    {
        Assert.Equal("1080P 高清", QualityCatalog.QualityName(80));
        Assert.Equal("999", QualityCatalog.QualityName(999));
    }

    /// <summary>
    /// 同一清晰度常常同时给 AVC / HEVC / AV1 三条流，下拉里必须只出一条，
    /// 并按设置里的默认编码挑，否则用户得先纠结编码再纠结清晰度。
    /// </summary>
    [Fact]
    public void BuildVideoOptions_OneEntryPerQuality_PicksPreferredCodec()
    {
        var play = new PlayUrlResult
        {
            Videos = [Video(80, 7), Video(80, 12), Video(80, 13), Video(64, 7)]
        };

        var options = QualityCatalog.BuildVideoOptions(play, false, false, "AV1");

        Assert.Single(options, o => o.Qn == 80);
        Assert.Equal(13, options.Single(o => o.Qn == 80).CodecId);
        Assert.Equal(9, options.Count);
    }

    [Fact]
    public void BuildVideoOptions_FallsBackToAvcWhenPreferredCodecAbsent()
    {
        var play = new PlayUrlResult { Videos = [Video(80, 12), Video(80, 13)] };
        var options = QualityCatalog.BuildVideoOptions(play, false, false, "AVC");

        Assert.Equal(12, options.Single(o => o.Qn == 80).CodecId);
    }

    [Fact]
    public void BuildVideoOptions_MarksLoginAndVipRequirements()
    {
        var play = new PlayUrlResult { Videos = [Video(80, 7)] };
        var options = QualityCatalog.BuildVideoOptions(play, isLoggedIn: false, isVip: false);

        Assert.Equal("需登录", options.Single(o => o.Qn == 116).UnavailableReason);
        Assert.Equal("需大会员", options.Single(o => o.Qn == 120).UnavailableReason);
        Assert.Equal("该视频无此画质", options.Single(o => o.Qn == 64).UnavailableReason);
        Assert.Null(options.Single(o => o.Qn == 80).UnavailableReason);
    }

    [Fact]
    public void PickDefault_PrefersSettingThenHighestAvailable()
    {
        var play = new PlayUrlResult { Videos = [Video(80, 7), Video(64, 7)] };
        var options = QualityCatalog.BuildVideoOptions(play, false, false);

        Assert.Equal(80, QualityCatalog.PickDefault(options, 80, "AVC")!.Qn);
        Assert.Equal(80, QualityCatalog.PickDefault(options, 125, "AVC")!.Qn);
        Assert.Equal(64, QualityCatalog.PickDefault(options, 64, "AVC")!.Qn);
    }

    [Fact]
    public void PickDefault_ReturnsNullWhenNothingAvailable()
    {
        var options = QualityCatalog.BuildVideoOptions(new PlayUrlResult(), false, false);
        Assert.Null(QualityCatalog.PickDefault(options, 80, "AVC"));
    }

    /// <summary>
    /// 默认音质不自动升到会员专属档（30251 / 30250）：指定档拿不到时应在普通档里取最高。
    /// 30251 / 30250 的数值都比 30280 小，用「Id &lt;= 30280」筛普通档是错的，这条专门盯住它。
    /// </summary>
    [Fact]
    public void PickDefaultAudio_DoesNotAutoUpgradeToVipTier()
    {
        var play = new PlayUrlResult
        {
            Audios = [Audio(30251), Audio(30250), Audio(30280), Audio(30216)]
        };

        var options = QualityCatalog.BuildAudioOptions(play, isLoggedIn: true, isVip: true);

        // 30232 没有流：落到普通档最高，而不是挑上 Hi-Res
        Assert.Equal(30280, QualityCatalog.PickDefaultAudio(options, 30232)!.Id);
        Assert.Equal(30216, QualityCatalog.PickDefaultAudio(options, 30216)!.Id);

        // 用户主动选会员档：尊重，不擅自降回普通档
        Assert.Equal(30251, QualityCatalog.PickDefaultAudio(options, 30251)!.Id);
    }

    [Fact]
    public void PickDefaultAudio_FallsBackToVipTierWhenNothingElseAvailable()
    {
        var play = new PlayUrlResult { Audios = [Audio(30251)] };
        var options = QualityCatalog.BuildAudioOptions(play, isLoggedIn: true, isVip: true);

        // 普通档一条都没有，没得挑时才给会员档
        Assert.Equal(30251, QualityCatalog.PickDefaultAudio(options, 30280)!.Id);
    }

    // ------------------------------------------------------------------ 辅助

    private static VideoViewResponse ViewWith(string? pic) =>
        new() { Data = new VideoViewData { Bvid = "BV1xx411c7mD", Pic = pic } };

    private static DashStream Video(int qn, int codecId, long bandwidth = 1000) =>
        new(qn, codecId, $"https://cdn/{qn}-{codecId}.m4s", [], null, 1920, 1080, bandwidth, null);

    private static DashStream Audio(int id) =>
        new(id, 0, $"https://cdn/audio-{id}.m4s", [], null, 0, 0, 192000, null);
}
