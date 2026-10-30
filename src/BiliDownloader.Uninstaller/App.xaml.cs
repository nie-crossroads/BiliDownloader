using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BiliDownloader.Uninstaller.Views;

namespace BiliDownloader.Uninstaller;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var args = e.Args;
        var cmp = StringComparison.OrdinalIgnoreCase;

        var cleanup = args.FirstOrDefault(a => a.TrimStart('-', '/').StartsWith("cleanup=", cmp));
        if (cleanup != null)
        {
            var dir = cleanup.Substring(cleanup.IndexOf('=') + 1).Trim('"');
            TryDeleteDir(dir);
            // 删除自身（含 .config）：进程还在跑时直接 File.Delete 必然失败，
            // 交给一个隐藏的 cmd 等本进程退出后再删。
            DeleteSelfDelayed();
            Environment.Exit(0);
            return;
        }

        // 诊断：真实窗口截图（经过 DWM 合成，能反映 ClearType 与 DPI 缩放）。
        // 注：不能在这里 Exit，要留出消息循环让窗口绘制完成后再抓图。
        if (args.Any(a => string.Equals(a.TrimStart('-', '/'), "shot-real", cmp)))
        {
            var dir = Path.Combine(Path.GetTempPath(), "bili_uninstaller_real");
            Directory.CreateDirectory(dir);
            RenderRealWindow(Path.Combine(dir, "real-uninstall.png"));
            return;
        }

        if (args.Any(a => string.Equals(a.TrimStart('-', '/'), "shot", cmp)))
        {
            var dir = Path.Combine(Path.GetTempPath(), "bili_uninstaller_shot");
            Directory.CreateDirectory(dir);
            RenderDialog(Path.Combine(dir, "uninstall.png"));
            Console.WriteLine("[shot] " + dir);
            Environment.Exit(0);
            return;
        }

        if (args.Any(a => string.Equals(a.TrimStart('-', '/'), "S", cmp)))
        {
            var m = new UninstallModel();
            m.UninstallAsync().GetAwaiter().GetResult();
            Environment.Exit(0);
            return;
        }

        var win = new UninstallWindow();
        win.Show();
    }

    private static void DeleteSelfDelayed()
    {
        var self = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(self) || !File.Exists(self)) return;

        // 用 ping 做延时（timeout 命令在无控制台时不可靠），再删本体与 .config。
        var arguments = "/c ping 127.0.0.1 -n 3 >nul & del /f /q \"" + self + "\" \"" + self + ".config\" >nul 2>&1";
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch { /* 忽略 */ }
    }

    private static void TryDeleteDir(string dir)
    {
        for (var i = 0; i < 5; i++)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                if (!Directory.Exists(dir)) return;
            }
            catch { /* 重试 */ }
            System.Threading.Thread.Sleep(300);
        }
    }

    private static async void RenderRealWindow(string path)
    {
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var window = new UninstallWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 80,
            Top = 80,
            ShowInTaskbar = false
        };

        window.Show();
        window.Topmost = true;
        window.Activate();
        window.UpdateLayout();

        // 留出一次 DWM 合成的时间，否则可能抓到未绘制的窗口。
        await System.Threading.Tasks.Task.Delay(800);

        try
        {
            SetupShared.WindowCapture.CaptureToPng(window, path);
            Console.WriteLine("[shot-real] 已保存 -> " + path);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[shot-real] 失败: " + ex.Message);
        }

        window.Close();
        Application.Current.Shutdown();
    }

    private static void RenderDialog(string path)
    {
        var model = new UninstallModel();
        var dlg = new UninstallDialog { DataContext = model };
        var wrap = new Border { Background = GetBrush(), Padding = new Thickness(40), Child = dlg };
        RenderElement(wrap, path);
    }

    private static void RenderElement(FrameworkElement el, string path)
    {
        el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        el.Arrange(new Rect(0, 0, el.DesiredSize.Width, el.DesiredSize.Height));
        var w = Math.Max(1, (int)Math.Ceiling(el.DesiredSize.Width));
        var h = Math.Max(1, (int)Math.Ceiling(el.DesiredSize.Height));

        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(el);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static Brush GetBrush()
    {
        if (Application.Current?.Resources["Bg"] is Brush b) return b;
        return new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xF9));
    }
}
