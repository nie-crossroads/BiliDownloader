using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace BiliDownloader.Core.BiliApi;

/// <summary>
/// WBI 签名：B站 Web 接口风控签名。混淆表与步骤为社区公开实现（BBDown/DownKyi），
/// 随风控可能调整，故集中于此并配单测锁定行为（M2 联调时校验）。
/// </summary>
public static class WbiSign
{
    // 固定 64 长混淆表（公开常量，用于从 imgKey+subKey 派生 32 位 mixinKey）
    private static readonly int[] MixinKeyEncTab =
    {
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35,
        27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13,
        37, 48, 7, 16, 24, 55, 40, 61, 26, 17, 0, 1, 60, 51, 30, 4,
        22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36, 20, 34, 44, 52
    };

    /// <summary>
    /// 需要从参数值中剔除的字符——只这 5 个（与社区 BBDown/DownKyi 实现一致）。
    /// 不要再把 '-', '.', '_', '~' 加进来：签名时改写值、请求时按返回值发送，
    /// 参数值会被静默篡改，一旦某参数（如 sessdata/keyword）含这些字符，业务侧就会莫名报错。
    /// </summary>
    private static readonly char[] Filtered = { '!', '\'', '(', ')', '*' };

    /// <summary>由 imgKey + subKey 派生 32 位 mixinKey。</summary>
    public static string MixinKey(string imgKey, string subKey)
    {
        var concat = (imgKey + subKey).ToCharArray();
        var mixed = new char[32];
        for (int i = 0; i < 32; i++)
            mixed[i] = concat[MixinKeyEncTab[i]];
        return new string(mixed);
    }

    /// <summary>对业务参数签名，返回带 wts / w_rid 的查询串。</summary>
    public static string Sign(IDictionary<string, string> parameters, string mixinKey)
    {
        // 1. 过滤 value 中的特殊字符
        var filtered = parameters
            .Where(p => !string.IsNullOrEmpty(p.Value))
            .ToDictionary(p => p.Key,
                p => new string(p.Value!.Where(c => Array.IndexOf(Filtered, c) < 0).ToArray()));

        // 2. 按 key 升序拼接 k=v&...
        var query = string.Join("&",
            filtered.OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => $"{p.Key}={p.Value}"));

        // 3. 追加 wts
        long wts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        query += $"&wts={wts}";

        // 4. MD5(query + mixinKey)
        var rid = Md5Hex(query + mixinKey);
        return $"{query}&w_rid={rid}";
    }

    private static string Md5Hex(string input)
    {
        using var md5 = MD5.Create();
        var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
