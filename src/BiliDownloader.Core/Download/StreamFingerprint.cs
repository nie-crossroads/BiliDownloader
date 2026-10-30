namespace BiliDownloader.Core.Download;

/// <summary>
/// 一路远端流的「身份指纹」，用于跨重启恢复时判断服务端这条流还是不是上次那条。
/// <para>
/// 关键前提：<b>字节数相等不代表内容相同</b>。B 站直链带鉴权参数、会过期，
/// 重启后必须重取直链，新直链可能指向另一个 CDN 甚至另一条流（同清晰度不同编码/码率）。
/// 若只按已落盘字节数续传，拼出来的文件长度刚好、进度条 100%，内容却是坏的。
/// 所以只有服务端给的身份标识（ETag / Last-Modified）才可信。
/// </para>
/// </summary>
public sealed record StreamFingerprint(string? ETag, string? LastModified, long Length, string? ResourcePath = null)
{
    /// <summary>
    /// 能否认定为同一条流。依次看三类证据，都没有就按「变了」处理。
    /// <list type="number">
    /// <item>ETag（最可靠，服务端给的话优先用）；</item>
    /// <item>Last-Modified；</item>
    /// <item>资源路径 + 长度：B 站 CDN 实测常常<b>两个都不给</b>，但直链路径里含 cid / 清晰度 / 编码
    /// 等内容标识，路径和长度都对上，基本可以认定是同一条流。</item>
    /// </list>
    /// 宁可整段重下（慢），也不能把两条不同的流拼成一个坏文件（错）。
    /// </summary>
    public bool Matches(StreamFingerprint? other)
    {
        if (other is null) return false;

        if (!string.IsNullOrEmpty(ETag) && !string.IsNullOrEmpty(other.ETag))
            return string.Equals(ETag, other.ETag, StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(LastModified) && !string.IsNullOrEmpty(other.LastModified))
            return string.Equals(LastModified, other.LastModified, StringComparison.Ordinal);

        // 服务端没给身份标识时的兜底：先看完整路径，再看文件名（跨 CDN 前缀可能不同）
        if (!string.IsNullOrEmpty(ResourcePath) && !string.IsNullOrEmpty(other.ResourcePath))
        {
            if (Length != other.Length) return false;

            return string.Equals(ResourcePath, other.ResourcePath, StringComparison.Ordinal)
                   || string.Equals(NameOf(ResourcePath), NameOf(other.ResourcePath),
                       StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// 路径里的文件名部分。B 站不同 CDN 的路径前缀常常不一样
    /// （/v1/resource/upgcxcode/... 与 /upgcxcode/...），但文件名一致——
    /// 它含 cid、清晰度、编码，能标识内容本身。
    /// </summary>
    private static string NameOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    /// <summary>
    /// 从直链里取出资源路径（去掉 ? 之后的鉴权参数）。
    /// 鉴权参数会每次重取都变，路径才是标识内容本身的部分。
    /// </summary>
    public static string? PathOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            return new Uri(url, UriKind.Absolute).AbsolutePath;
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            return null;
        }
    }
}
