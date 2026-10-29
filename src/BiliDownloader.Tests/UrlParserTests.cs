using BiliDownloader.Core.Parse;
using Xunit;

namespace BiliDownloader.Tests;

public class UrlParserTests
{
    [Theory]
    [InlineData("https://www.bilibili.com/video/BV1xx411c7mD", "BV1xx411c7mD")]
    [InlineData("https://www.bilibili.com/video/BV1xx411c7mD?p=3", "BV1xx411c7mD")]
    [InlineData("https://b23.tv/abcd123", null)]
    public void Parse_ExtractsBvid(string url, string? expectedBvid)
    {
        var result = new UrlParser().Parse(url);
        Assert.Equal(expectedBvid, result.Bvid);
    }
}
