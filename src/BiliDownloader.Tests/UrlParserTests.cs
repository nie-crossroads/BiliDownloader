using BiliDownloader.Core.Parse;
using Xunit;

namespace BiliDownloader.Tests;

public class UrlParserTests
{
    private static UrlParser Parser => new();

    // —— BV 号（原用例保留）——
    [Theory]
    [InlineData("https://www.bilibili.com/video/BV1xx411c7mD", "BV1xx411c7mD", null)]
    [InlineData("https://www.bilibili.com/video/BV1xx411c7mD?p=3", "BV1xx411c7mD", 3)]
    public void Parse_ExtractsBvid(string url, string? expectedBvid, int? expectedPage)
    {
        var result = Parser.Parse(url);
        Assert.Equal(expectedBvid, result.Bvid);
        Assert.Equal(expectedPage, result.Page);
        Assert.False(result.IsShortUrl);
    }

    // —— av 号（此前未覆盖）——
    [Theory]
    [InlineData("https://www.bilibili.com/video/av170001", 170001L, null)]
    [InlineData("https://www.bilibili.com/video/av170001?p=2", 170001L, 2)]
    [InlineData("http://bilibili.com/video/av9999999999", 9999999999L, null)]
    public void Parse_ExtractsAvid(string url, long expectedAvid, int? expectedPage)
    {
        var result = Parser.Parse(url);
        Assert.Equal(expectedAvid, result.Avid);
        Assert.Equal(expectedPage, result.Page);
        Assert.Null(result.Bvid);
        Assert.False(result.IsShortUrl);
    }

    // —— 整段分享文案：正文里夹带 BV / av，正则全局匹配仍能提取（此前未覆盖）——
    [Theory]
    [InlineData("【4K修复】 BV1xx411c7mD 大家来看看", "BV1xx411c7mD", null)]
    [InlineData("推荐个视频 https://www.bilibili.com/video/BV1xx411c7mD?p=2 速看", "BV1xx411c7mD", 2)]
    [InlineData("转自 @up av170001 这期真不错", "170001", null)]
    public void Parse_FromShareText(string text, string? expectedBvid, int? expectedPage)
    {
        var result = Parser.Parse(text);
        Assert.Equal(expectedBvid, result.Bvid ?? result.Avid?.ToString());
        Assert.Equal(expectedPage, result.Page);
    }

    // —— 无 scheme 的短码片段：仍能识别为短链（此前未覆盖）——
    [Theory]
    [InlineData("b23.tv/abcd123", true)]
    [InlineData("b23.tv/abcd123?p=1", true)]
    public void Parse_ShortCodeWithoutScheme(string text, bool expectedShort)
    {
        var result = Parser.Parse(text);
        Assert.Equal(expectedShort, result.IsShortUrl);
        Assert.Null(result.Bvid);
        Assert.Null(result.Avid);
    }

    // —— b23.tv 完整链接：同步 Parse 只标记短链、不解析出 BV/av（展开在 ParseAsync 里做）——
    [Fact]
    public void Parse_FullShortUrl_OnlyMarksShort()
    {
        var result = Parser.Parse("https://b23.tv/abcd123");
        Assert.True(result.IsShortUrl);
        Assert.Null(result.Bvid);
        Assert.Null(result.Avid);
    }

    // —— 边界：空 / 空白 / 无关文本 ——
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("这是一段和B站无关的普通文字")]
    public void Parse_NonVideoInput_ReturnsEmpty(string text)
    {
        var result = Parser.Parse(text);
        Assert.Null(result.Bvid);
        Assert.Null(result.Avid);
        Assert.Null(result.Page);
        Assert.False(result.IsShortUrl);
    }

    // —— ParseAsync 非短链分支：已含 BV 时直接返回，不触发网络展开 ——
    [Fact]
    public async Task ParseAsync_WithBvid_SkipsExpansion()
    {
        var result = await Parser.ParseAsync("https://www.bilibili.com/video/BV1xx411c7mD?p=2",
                                             new HttpClient(), default);
        Assert.Equal("BV1xx411c7mD", result.Bvid);
        Assert.Equal(2, result.Page);
        Assert.False(result.IsShortUrl);
    }

    // —— ParseAsync 非短链且非视频文本：直接透传，不请求网络 ——
    [Fact]
    public async Task ParseAsync_NonShortText_SkipsExpansion()
    {
        var result = await Parser.ParseAsync("随便写点什么", new HttpClient(), default);
        Assert.Null(result.Bvid);
        Assert.Null(result.Avid);
        Assert.False(result.IsShortUrl);
    }
}
