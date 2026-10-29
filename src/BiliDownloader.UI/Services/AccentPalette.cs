using System.Globalization;
using System.Windows.Media;

namespace BiliDownloader.UI.Services;

/// <summary>
/// 一套预制主题色。每套都给出主色及派生色（悬浮 / 按下 / 弱底 / 表面文字 / 渐变终点 / 之上的文字），
/// 而不是用算法从主色调亮调暗：自动派生在深色和浅色两套主题里容易一边过曝一边看不清，
/// 手工配能保证两套主题下对比度都够（视觉规范 §3.2）。
/// </summary>
public sealed record AccentPreset(
    string Key,
    string Name,
    string Accent,
    string Hover,
    string Press,
    string SoftLight,
    string SoftDark,
    string InkLight,
    string InkDark,
    string Gradient,
    string OnAccent)
{
    /// <summary>#RRGGBB 或 #AARRGGBB（令牌字典里的弱底色是后者）→ Color。静态是为了 ThemeService 也能复用。</summary>
    public static System.Windows.Media.Color Parse(string hex)
    {
        var span = hex.TrimStart('#').AsSpan();
        var value = uint.Parse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        return span.Length == 8
            ? System.Windows.Media.Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value)
            : System.Windows.Media.Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }
}

/// <summary>可选主题色清单。设置页「外观」里用下拉框选择。</summary>
public static class AccentPalette
{
    public const string DefaultKey = "Pink";

    public static IReadOnlyList<AccentPreset> Presets { get; } =
    [
        new AccentPreset("Pink", "樱花粉（默认）", "#FB7299", "#FF8CAD", "#E45D85",
            "#1AFB7299", "#24FB7299", "#D6336C", "#FF8CAD", "#FF9FC0", "#FFFFFF"),

        new AccentPreset("Blue", "天空蓝", "#3B82F6", "#60A5FA", "#2563EB",
            "#1A3B82F6", "#243B82F6", "#1D4ED8", "#7CB0FF", "#7CB0FF", "#FFFFFF"),

        new AccentPreset("Green", "薄荷绿", "#10B981", "#34D399", "#059669",
            "#1A10B981", "#2410B981", "#047857", "#4ADE80", "#6EE7B7", "#052E1F"),

        new AccentPreset("Violet", "紫罗兰", "#8B5CF6", "#A78BFA", "#7C3AED",
            "#1A8B5CF6", "#248B5CF6", "#6D28D9", "#B69CFF", "#C4B5FD", "#FFFFFF"),

        new AccentPreset("Amber", "琥珀橙", "#F59E0B", "#FBBF24", "#D97706",
            "#1AF59E0B", "#24F59E0B", "#B45309", "#FCD34D", "#FCD34D", "#3B2503")
    ];

    public static bool Contains(string? key) =>
        key is not null && Presets.Any(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));

    public static AccentPreset Find(string? key) =>
        Presets.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase))
        ?? Presets[0];
}
