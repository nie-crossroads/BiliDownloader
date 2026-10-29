using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BiliDownloader.Core.Parse;

/// <summary>链接解析结果。</summary>
public sealed class ParseResult
{
    public string? Bvid { get; init; }
    public long? Avid { get; init; }
    public int? Page { get; init; }
    public bool IsShortUrl { get; init; }
}

/// <summary>链接解析：提取 BV/av/选集 p，识别 b23.tv 短链。</summary>
public sealed class UrlParser
{
    private static readonly Regex BvRegex = new(@"BV[1-9A-HJ-NP-Za-km-z]{10}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AvRegex = new(@"av(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PageRegex = new(@"[?&]p=(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ShortRegex = new(@"b23\.tv/\w+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ShortUrlRegex = new(@"(?:https?://)?b23\.tv/[A-Za-z0-9]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>从原始链接解析出 BV/av、选集号与是否短链。</summary>
    public ParseResult Parse(string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
            return new ParseResult();

        var url = rawUrl.Trim();
        bool isShort = ShortRegex.IsMatch(url);

        var bv = BvRegex.Match(url);
        if (bv.Success)
            return new ParseResult { Bvid = bv.Value, Page = ParsePage(url), IsShortUrl = isShort };

        var av = AvRegex.Match(url);
        if (av.Success && long.TryParse(av.Groups[1].Value, out var aid))
            return new ParseResult { Avid = aid, Page = ParsePage(url), IsShortUrl = isShort };

        return new ParseResult { IsShortUrl = isShort };
    }

    private static int? ParsePage(string url)
    {
        var m = PageRegex.Match(url);
        return m.Success && int.TryParse(m.Groups[1].Value, out var p) ? p : null;
    }

    /// <summary>
    /// UI 只调这一个：整段分享文案也能解析；b23.tv 短链会自动展开再解析（方案设计 §4.1）。
    /// </summary>
    public async Task<ParseResult> ParseAsync(string rawInput, HttpClient http, CancellationToken ct = default)
    {
        var result = Parse(rawInput);
        if (result.Bvid is not null || result.Avid is not null || !result.IsShortUrl)
            return result;

        var shortMatch = ShortUrlRegex.Match(rawInput);
        if (!shortMatch.Success) return result;

        var expanded = await ExpandShortUrlAsync(shortMatch.Value, http, ct).ConfigureAwait(false);
        return Parse(expanded);
    }

    /// <summary>展开 b23.tv 短链为真实长链（跟随 302 重定向）。</summary>
    public async Task<string> ExpandShortUrlAsync(string shortUrl, HttpClient http, CancellationToken ct = default)
    {
        var url = shortUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? shortUrl
            : "https://" + shortUrl;

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                                       .ConfigureAwait(false);

        // AllowAutoRedirect=true 时最终地址在 RequestMessage.RequestUri 上
        var finalUri = response.RequestMessage?.RequestUri?.ToString();
        if (!string.IsNullOrEmpty(finalUri) && !finalUri.Contains("b23.tv", StringComparison.OrdinalIgnoreCase))
            return finalUri;

        var location = response.Headers.Location;
        if (location is not null)
            return location.IsAbsoluteUri ? location.ToString() : new Uri(new Uri(url), location).ToString();

        return finalUri ?? url;
    }
}
