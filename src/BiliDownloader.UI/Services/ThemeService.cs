using System.Windows;
using System.Windows.Media;

namespace BiliDownloader.UI.Services;

/// <summary>
/// 主题与主题色：主题替换 App.Resources.MergedDictionaries 里的 Tokens.*.xaml；
/// 主题色则以 App.Resources 顶层条目覆盖 Accent* 系列笔刷（顶层条目优先于合并字典）。
/// 模板里全部 DynamicResource，切换即时生效（视觉规范 §10.2 / §3.2）。
/// </summary>
public static class ThemeService
{
    private const string LightSource = "Themes/Tokens.Light.xaml";
    private const string DarkSource = "Themes/Tokens.Dark.xaml";

    public static string Current { get; private set; } = "Light";

    public static string AccentKey { get; private set; } = AccentPalette.DefaultKey;

    public static void Apply(string theme)
    {
        var app = Application.Current;
        if (app is null) return;

        var source = theme == "Dark" ? DarkSource : LightSource;
        var dictionaries = app.Resources.MergedDictionaries;

        var index = -1;
        for (var i = 0; i < dictionaries.Count; i++)
        {
            var original = dictionaries[i].Source?.OriginalString;
            if (original is not null && original.Contains("Tokens.", StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        var replacement = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };
        if (index >= 0) dictionaries[index] = replacement;
        else dictionaries.Insert(0, replacement);

        Current = theme == "Dark" ? "Dark" : "Light";

        // 令牌字典里只有默认粉，换完之后会被（已重置的）字典覆盖回去，这里重新刷一遍主题色。
        ApplyAccent(AccentKey);
    }

    public static void ApplyAccent(string key)
    {
        var app = Application.Current;
        if (app is null) return;

        var preset = AccentPalette.Find(key);
        AccentKey = preset.Key;

        var dark = Current == "Dark";
        var res = app.Resources;

        SetBrush(res, "AccentBrush", preset.Accent);
        SetBrush(res, "AccentHoverBrush", preset.Hover);
        SetBrush(res, "AccentPressBrush", preset.Press);
        SetBrush(res, "AccentSoftBrush", dark ? preset.SoftDark : preset.SoftLight);
        SetBrush(res, "AccentInkBrush", dark ? preset.InkDark : preset.InkLight);
        SetBrush(res, "FgOnAccentBrush", preset.OnAccent);

        // 进度条 / 头像渐变跟着主题色走，否则换完主题色还留着一条粉条。
        Put(res, "ProgressGradientBrush", MakeGradient2(preset.Accent, preset.Gradient));
        Put(res, "AvatarGradientBrush", MakeGradient3(preset.Accent, preset.Hover, preset.Gradient));
    }

    private static void SetBrush(ResourceDictionary res, string key, string hex)
    {
        var brush = new SolidColorBrush(AccentPreset.Parse(hex));
        brush.Freeze();
        res[key] = brush;
    }

    private static void Put(ResourceDictionary res, string key, Freezable brush)
    {
        brush.Freeze();
        res[key] = brush;
    }

    private static LinearGradientBrush MakeGradient2(string from, string to) => new()
    {
        StartPoint = new Point(0, 0),
        EndPoint = new Point(1, 0),
        GradientStops =
        [
            new GradientStop(AccentPreset.Parse(from), 0),
            new GradientStop(AccentPreset.Parse(to), 1)
        ]
    };

    private static LinearGradientBrush MakeGradient3(string from, string mid, string to) => new()
    {
        StartPoint = new Point(0, 0),
        EndPoint = new Point(1, 1),
        GradientStops =
        [
            new GradientStop(AccentPreset.Parse(from), 0),
            new GradientStop(AccentPreset.Parse(mid), 0.5),
            new GradientStop(AccentPreset.Parse(to), 1)
        ]
    };
}
