using System;
using System.Windows;

namespace BiliDownloader.Installer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var args = e.Args;
        bool Has(string name) =>
            args.Any(a => string.Equals(a.TrimStart('-', '/'), name, StringComparison.OrdinalIgnoreCase));

        // 诊断：真实窗口截图（经过 DWM 合成，能反映 ClearType 与 DPI 缩放）。
        // 注：不能在这里 Exit，要留出消息循环让窗口绘制完成后再抓图。
        if (Has("shot-real"))
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bili_installer_real");
            RealWindowShot.RenderAll(dir);
            return;
        }

        if (Has("shot"))
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bili_installer_shots");
            System.IO.Directory.CreateDirectory(dir);
            InstallerShot.RenderAll(dir);
            Console.WriteLine("[shot] 渲染完成 -> " + dir);
            Environment.Exit(0);
            return;
        }

        if (Has("S"))
        {
            // 必须把执行丢到线程池：OnStartup 里直接 .GetResult() 会阻塞 UI 线程，
            // 而 ExtractAsync 的 await 需要把续体投回 UI 线程 → 死锁（只写出第一个缓冲区就卡死）。
            // 模型本身仍在 UI 线程构造（其构造函数会订阅 CommandManager）。
            var silent = new InstallerModel();
            var code = Task.Run(() => silent.SilentInstallAsync()).GetAwaiter().GetResult();
            Environment.Exit(code);
            return;
        }

        // 必须把「关闭窗口」回调传给模型：否则「取消」即使可点也关不掉窗口。
        MainWindow? window = null;
        var model = new InstallerModel(() => window?.Close());
        window = new MainWindow { DataContext = model };
        window.Show();
    }
}
