using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BiliDownloader.Core.Models;
using BiliDownloader.UI.Services;
using BiliDownloader.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace BiliDownloader.UI;

/// <summary>
/// 仅供开发期做界面取证：把主窗口渲染成 PNG 并输出像素统计，便于在无法肉眼观察
/// 的环境（远程 / 无桌面）验证布局与配色是否真的生效。发行版不使用。
/// 用法：BiliDownloader.UI.exe --shot=&lt;输出目录&gt; [--parse=&lt;B站链接&gt;] [--download]
/// </summary>
internal static class DiagnosticsShot
{
    public static bool IsRequested(string[] args) =>
        args.Any(a => a.StartsWith("--shot=", StringComparison.OrdinalIgnoreCase));

    internal static void Trace(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BiliDownloader", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "diag.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string? ArgValue(string[] args, string name) =>
        args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            ?.Split('=', 2)[1].Trim('"');

    public static async Task RunAsync(MainWindow window, MainViewModel vm, string[] args)
    {
        Trace("RunAsync enter");
        var dir = ArgValue(args, "--shot")!;
        Trace($"dir=[{dir}] args=[{string.Join(' ', args)}]");
        Directory.CreateDirectory(dir);
        var url = ArgValue(args, "--parse");
        var wantDownload = args.Contains("--download");

        var report = new StringBuilder();
        report.AppendLine($"window state      : {window.WindowState}");
        report.AppendLine($"actual size       : {window.ActualWidth:0.#} x {window.ActualHeight:0.#}");
        report.AppendLine($"min size          : {window.MinWidth:0.#} x {window.MinHeight:0.#}");

        await Task.Delay(900);

        await Shot(window, dir, "01-empty-light.png", report);
        report.AppendLine($"tasks={vm.TaskCount} hasPreview={vm.HasPreview} log={vm.LogCount} loggedIn={vm.IsLoggedIn}");

        ThemeService.Apply("Dark");
        await Task.Delay(600);
        await Shot(window, dir, "02-empty-dark.png", report);
        ThemeService.Apply("Light");
        await Task.Delay(400);

        if (!string.IsNullOrWhiteSpace(url))
        {
            vm.InputUrl = url;
            report.AppendLine($"--- parse: {url}");
            await vm.ParseCommand.ExecuteAsync(null);

            var waited = 0;
            while (!vm.HasPreview && waited < 15000)
            {
                await Task.Delay(200);
                waited += 200;
            }

            report.AppendLine($"parse finished in ~{waited}ms  hasPreview={vm.HasPreview}  error={vm.HasError} {vm.ErrorText}");
            await Task.Delay(700); // 等封面解码
            await Shot(window, dir, "03-preview.png", report);

            if (vm.Preview is { } pv)
            {
                report.AppendLine($"preview title     : {pv.Title}");
                report.AppendLine($"preview up        : {pv.UpNameOrNull}");
                report.AppendLine($"preview pages     : {pv.TotalPages}  selected={pv.SelectedCids.Count()}");
                report.AppendLine($"qualities         : {string.Join(" | ", pv.Qualities.Select(q => q.Display))}");
                report.AppendLine($"audios            : {string.Join(" | ", pv.Audios.Select(a => a.Display))}");
                report.AppendLine($"selection note    : {pv.SelectionNote}");
                report.AppendLine($"download enabled  : {vm.DownloadCommand.CanExecute(null)}");
            }

            if (wantDownload && vm.DownloadCommand.CanExecute(null))
            {
                var started = DateTime.UtcNow;
                vm.DownloadCommand.Execute(null);
                await Task.Delay(1200);
                await Shot(window, dir, "04-downloading.png", report);

                if (args.Contains("--cancel"))
                {
                    // 等进度真正跑起来，才能看清填充条两端的形状
                    await Task.Delay(3000);
                    await Shot(window, dir, "04b-downloading-progress.png", report);

                    var item = vm.Tasks.FirstOrDefault();
                    report.AppendLine($"before cancel     : [{item?.StatusText}] {item?.ProgressText}");
                    item?.CancelCommand.Execute(null);
                    await Task.Delay(1500);
                    await Shot(window, dir, "05-canceled.png", report);
                    report.AppendLine($"after cancel      : [{item?.StatusText}] {item?.ProgressText}");
                }
                else
                {
                    while (DateTime.UtcNow - started < TimeSpan.FromSeconds(75))
                    {
                        var first = vm.Tasks.FirstOrDefault();
                        if (first is { CanPause: false, CanResume: false } && first.StatusKind is TaskStatusKind.Done or TaskStatusKind.Failed)
                            break;
                        await Task.Delay(500);
                    }

                    await Shot(window, dir, "05-finished.png", report);
                    foreach (var t in vm.Tasks)
                        report.AppendLine($"task              : [{t.StatusText}] {t.ProgressText}  {t.MetaText}  speed={t.SpeedText}  metaErr={t.MetaIsError}  {t.Title}");
                }
            }
        }

        if (int.TryParse(ArgValue(args, "--fake"), out var fakePages) && fakePages > 0)
        {
            // 合成一份多 P 元数据：不联网也能验证「选集只显示一个 chip + 选择更多」的布局。
            var services = ((App)Application.Current).Services;
            var settings = services.GetRequiredService<AppSettings>();
            var selectedPage = int.TryParse(ArgValue(args, "--fakep"), out var sp) ? sp : (int?)null;

            vm.InputUrl = "BV1xx411c7mD";
            vm.Preview = new PreviewViewModel(BuildFakeVideo(fakePages, selectedPage), settings);
            await Task.Delay(500);
            await Shot(window, dir, "03-preview.png", report);

            var pv = vm.Preview!;
            report.AppendLine($"fake pages        : {pv.TotalPages}  selected={pv.SelectedCids.Count()}");
            report.AppendLine($"current page chip : [{pv.CurrentPageText}] tip=[{pv.CurrentPageTip?.Replace("\n", "\\n")}]");
            report.AppendLine($"qualities         : {string.Join(" | ", pv.Qualities.Select(q => q.Display))}");
            report.AppendLine($"audios            : {string.Join(" | ", pv.Audios.Select(a => a.Display))}");
            report.AppendLine($"selection note    : {pv.SelectionNote}");
            report.AppendLine($"download enabled  : {vm.DownloadCommand.CanExecute(null)}");
        }

        if (args.Contains("--pages"))
        {
            vm.OpenPagePickerCommand.Execute(null);
            await Task.Delay(500);
            await Shot(window, dir, "10-pagepicker.png", report);

            // 多选之后收起弹框，预览卡上的单个 chip 要变成「首集 + N」。
            vm.SelectAllPagesCommand.Execute(null);
            await Task.Delay(300);
            await Shot(window, dir, "10b-pagepicker-all.png", report);
            vm.ConfirmPagePickerCommand.Execute(null);
            await Task.Delay(300);
            await Shot(window, dir, "10c-preview-multi.png", report);
            if (vm.Preview is { } pp)
                report.AppendLine($"after select all  : note=[{pp.SelectionNote}] chip=[{pp.CurrentPageText}]");
            vm.ClosePagePickerCommand.Execute(null);
            await Task.Delay(200);
        }

        if (args.Contains("--modals"))
        {
            vm.ShowCookieCommand.Execute(null);
            await Task.Delay(500);
            await Shot(window, dir, "09-cookie.png", report);
            vm.CloseCookieCommand.Execute(null);
            await Task.Delay(300);
        }

        if (args.Contains("--settings"))
        {
            var services = ((App)Application.Current).Services;
            // 让设置页的主题跟随主窗当前主题（settings.json 里的值可能是旧的，截图会误导）
            var appSettings = services.GetRequiredService<AppSettings>();
            appSettings.Theme = ThemeService.Current;

            var settingsVm = services.GetRequiredService<SettingsViewModel>();
            var settingsWindow = new SettingsWindow(settingsVm) { Owner = window };
            settingsWindow.Show();
            await Task.Delay(700);

            foreach (var pane in new[] { "download", "basic", "network", "appearance", "about" })
            {
                settingsVm.SelectPaneCommand.Execute(pane);
                await Task.Delay(350);
                await Shot(settingsWindow, dir, $"11-settings-{pane}.png", report);
            }

            // 深色下再看一遍：导航未选中项以前是默认黑字，在深色主题里直接看不见。
            settingsVm.IsDarkTheme = true;
            settingsVm.SelectPaneCommand.Execute("appearance");
            await Task.Delay(400);
            await Shot(settingsWindow, dir, "12-settings-dark-appearance.png", report);

            // 换主题色后按钮 / 进度条 / 选中项都要跟着变。
            var accentKey = ArgValue(args, "--accent");
            if (!string.IsNullOrWhiteSpace(accentKey))
            {
                settingsVm.SelectPaneCommand.Execute("appearance");
                settingsVm.AccentColor = accentKey!;
                await Task.Delay(400);
                await Shot(settingsWindow, dir, $"13-settings-dark-accent-{accentKey}.png", report);

                settingsVm.IsDarkTheme = false;
                await Task.Delay(400);
                await Shot(settingsWindow, dir, $"14-settings-light-accent-{accentKey}.png", report);

                var applied = Application.Current.Resources["AccentBrush"] as SolidColorBrush;
                report.AppendLine($"accent={ThemeService.AccentKey} saved={appSettings.AccentColor} brush={applied?.Color}");
                await Shot(window, dir, $"15-main-accent-{accentKey}.png", report);
            }

            settingsVm.IsDarkTheme = false;
            settingsWindow.Close();
            await Task.Delay(200);
        }

        if (args.Contains("--dark"))
        {
            ThemeService.Apply("Dark");
            await Task.Delay(500);
            await Shot(window, dir, "07-dark.png", report);
            ThemeService.Apply("Light");
        }

        report.AppendLine("--- log tail");
        foreach (var entry in vm.Logs.TakeLast(40))
            report.AppendLine($"[{entry.Time}] {entry.LevelText,-5} {entry.Text}");

        File.WriteAllText(Path.Combine(dir, "report.txt"), report.ToString());
        Console.WriteLine(report.ToString());

        window.Close();
        Application.Current.Shutdown(0);
    }

    /// <summary>
    /// 合成多 P 视频元数据，供 <c>--fake</c> 取证用：真实链接要联网，
    /// 而选集 chip / 画质灰显这些布局问题离线就能看清。
    /// </summary>
    private static VideoInfo BuildFakeVideo(int pages, int? selectedPage)
    {
        var list = Enumerable.Range(1, pages)
            .Select(i => new PageInfo(90000 + i,
                $"第 {i} 集 · 这里是一段刻意写长的分 P 标题，用来验证截断", i,
                TimeSpan.FromMinutes(11)))
            .ToList();

        return new VideoInfo
        {
            Bvid = "BV1xx411c7mD",
            Avid = 170001,
            Title = "合成数据：多 P 视频标题（界面取证用）",
            UpName = "测试 UP 主",
            Duration = TimeSpan.FromMinutes(11 * pages),
            Pages = list,
            SelectedPage = selectedPage,
            Qualities =
            [
                new QualityOption(16, 7, "360P 流畅", "360P", true, null, 640, 360, 320_000),
                new QualityOption(64, 7, "720P 高清", "720P", true, null, 1280, 720, 1_800_000),
                new QualityOption(80, 7, "1080P 高清", "1080P", true, null, 1920, 1080, 3_200_000),
                new QualityOption(80, 12, "1080P 高清 HEVC", "1080P HEVC", true, null, 1920, 1080, 2_400_000),
                new QualityOption(116, 7, "4K 超清", "4K", false, "需登录", 3840, 2160, 12_000_000),
                new QualityOption(127, 12, "8K 超高清", "8K", false, "大会员", 7680, 4320, 40_000_000)
            ],
            Audios =
            [
                new AudioOption(30216, "64K", true, null, 64_000 * 8),
                new AudioOption(30232, "132K", true, null, 132_000 * 8),
                new AudioOption(30280, "192K", true, null, 192_000 * 8),
                new AudioOption(30250, "杜比全景声", false, "需登录", 320_000 * 8)
            ]
        };
    }

    private static async Task Shot(Window window, string dir, string name, StringBuilder log)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

        var w = (int)Math.Ceiling(window.ActualWidth);
        var h = (int)Math.Ceiling(window.ActualHeight);
        if (w <= 0 || h <= 0) return;

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(window);
        rtb.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        var path = Path.Combine(dir, name);
        using (var fs = File.Create(path))
            encoder.Save(fs);

        log.AppendLine($"shot {name,-22} {w}x{h}  {Analyze(rtb, w, h)}");
    }

    /// <summary>像素统计：能一眼看出「一片空白」「粉色超标」「深色主题没生效」。</summary>
    private static string Analyze(BitmapSource bmp, int w, int h)
    {
        var stride = w * 4;
        var buffer = new byte[stride * h];
        bmp.CopyPixels(buffer, stride, 0);

        var histogram = new Dictionary<int, int>();
        long accent = 0, dark = 0, light = 0;
        double lumSum = 0;
        var total = w * h;

        for (var i = 0; i < buffer.Length; i += 4)
        {
            var b = buffer[i];
            var g = buffer[i + 1];
            var r = buffer[i + 2];

            var key = (r >> 3) << 10 | (g >> 3) << 5 | (b >> 3);
            histogram[key] = histogram.TryGetValue(key, out var c) ? c + 1 : 1;

            lumSum += 0.2126 * r + 0.7152 * g + 0.0722 * b;
            if (Math.Abs(r - 0xFB) < 42 && Math.Abs(g - 0x72) < 42 && Math.Abs(b - 0x99) < 42) accent++;
            if (r < 60 && g < 60 && b < 60) dark++;
            if (r > 235 && g > 235 && b > 235) light++;
        }

        var top = histogram.OrderByDescending(kv => kv.Value).Take(3)
            .Select(kv => $"#{(kv.Key >> 10 & 31) * 8:X2}{(kv.Key >> 5 & 31) * 8:X2}{(kv.Key & 31) * 8:X2} {kv.Value * 100.0 / total:0.0}%");

        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"colors={histogram.Count,5} accent={accent * 100.0 / total,5:0.00}% dark={dark * 100.0 / total,5:0.0}% light={light * 100.0 / total,5:0.0}% lum={lumSum / total,5:0.0} top=[{string.Join(", ", top)}]");
    }
}
