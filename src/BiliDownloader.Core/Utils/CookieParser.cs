using System.Net.Http;

namespace BiliDownloader.Core.Utils;

/// <summary>Cookie 解析：登录成功时从 Set-Cookie 或回跳 URL 里取关键项。</summary>
public static class CookieParser
{
    /// <summary>下载/接口只需要这几个键，其余无关 Cookie 一律丢弃。</summary>
    private static readonly string[] Wanted =
    [
        "SESSDATA", "bili_jct", "DedeUserID", "DedeUserID__ckMd5", "sid", "buvid3", "buvid4"
    ];

    public static string? FromSetCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return null;

        var found = new List<string>();
        foreach (var raw in values)
        {
            var pair = raw.Split(';')[0];
            var idx = pair.IndexOf('=');
            if (idx <= 0) continue;

            var name = pair[..idx].Trim();
            var value = pair[(idx + 1)..].Trim();
            if (value.Length == 0) continue;

            if (Wanted.Contains(name, StringComparer.OrdinalIgnoreCase))
                found.Add($"{name}={value}");
        }

        return found.Count == 0 ? null : string.Join("; ", found);
    }

    /// <summary>passport 的 poll 接口成功时 data.url 里带 SESSDATA 等查询参数。</summary>
    public static string? FromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var queryIndex = url.IndexOf('?');
        if (queryIndex < 0) return null;

        var found = new List<string>();
        foreach (var part in url[(queryIndex + 1)..].Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=');
            if (idx <= 0) continue;
            var name = part[..idx];
            if (!Wanted.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            found.Add($"{name}={part[(idx + 1)..]}");
        }

        return found.Count == 0 ? null : string.Join("; ", found);
    }

    /// <summary>校验用户手动粘贴的 Cookie 是否含必备项，返回缺失说明。</summary>
    public static string? Validate(string? cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie)) return "Cookie 不能为空";

        var parts = cookie.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                          .Select(p => p.Trim())
                          .ToList();

        bool Has(string name) => parts.Any(p =>
            p.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase) && p.Length > name.Length + 1);

        if (!Has("SESSDATA")) return "缺少 SESSDATA，请从浏览器开发者工具完整复制";

        var sess = parts.First(p => p.StartsWith("SESSDATA=", StringComparison.OrdinalIgnoreCase));
        if (!sess.Contains("%2C", StringComparison.OrdinalIgnoreCase) && !sess.Contains(','))
            return "SESSDATA 不完整（缺少 %2C 分隔段），请重新复制";

        return null;
    }
}
