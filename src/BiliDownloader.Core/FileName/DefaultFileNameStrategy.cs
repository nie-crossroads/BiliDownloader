using System.IO;
using System.Text;

namespace BiliDownloader.Core.FileName;

/// <summary>
/// 文件名清洗 + 三种命名规则（借鉴 downkyicore 的 FileName 模块）。
/// 与下载/合并解耦，便于单测与规则切换。
/// </summary>
public sealed class DefaultFileNameStrategy : IFileNameStrategy
{
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();
    private static readonly char[] Reserved = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };
    private const int MaxLength = 80;

    private readonly FileNameRule _rule;

    public DefaultFileNameStrategy(FileNameRule rule = FileNameRule.IndexAndPart)
    {
        _rule = rule;
    }

    public string Build(int? index, string title, string? upName)
    {
        var core = _rule switch
        {
            FileNameRule.IndexAndPart => (index.HasValue ? $"{index.Value:00} " : "") + (title ?? "未命名"),
            FileNameRule.PlainTitle  => title ?? "未命名",
            FileNameRule.WithUpName  => (upName ?? "未知UP") + " - " + (title ?? "未命名"),
            _ => title ?? "未命名"
        };
        return Sanitize(core);
    }

    /// <summary>清洗非法字符、折叠空白/下划线、截断，保证可作为文件名。</summary>
    public static string Sanitize(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "未命名";

        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
            sb.Append(IsInvalid(c) ? '_' : c);

        var cleaned = sb.ToString().Trim().TrimEnd('.');
        while (cleaned.Contains("  "))
            cleaned = cleaned.Replace("  ", " ");
        while (cleaned.Contains("__"))
            cleaned = cleaned.Replace("__", "_");

        if (cleaned.Length > MaxLength) cleaned = cleaned[..MaxLength];
        cleaned = cleaned.TrimEnd(' ', '_', '.');
        if (cleaned.Length == 0) return "未命名";

        var stem = cleaned.Split('.')[0].TrimEnd(' ');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && stem[3] is >= '1' and <= '9' &&
             (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
              stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))))
            cleaned = "_" + cleaned;
        return cleaned;
    }

    private static bool IsInvalid(char c)
    {
        foreach (var ic in InvalidChars) if (ic == c) return true;
        foreach (var r in Reserved) if (r == c) return true;
        return false;
    }
}
