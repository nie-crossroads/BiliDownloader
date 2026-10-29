using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using BiliDownloader.Core.Auth;
using BiliDownloader.Core.BiliApi;
using BiliDownloader.Core.Download;
using BiliDownloader.Core.Errors;
using BiliDownloader.Core.FileName;
using BiliDownloader.Core.Merge;
using BiliDownloader.Core.Orchestration;
using BiliDownloader.Core.Parse;
using BiliDownloader.Core.Utils;
using BiliDownloader.UI.Services;
using BiliDownloader.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace BiliDownloader.UI;

/// <summary>
/// 应用入口：DI 装配 Core 服务 + 三个全局异常钩子（方案设计 §13）。
/// 不引入完整 Generic Host，WPF 下直接建容器。
/// </summary>
public partial class App : Application
{
    public IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);


        HookGlobalExceptions();

        var settings = AppSettings.Load();
        ThemeService.Apply(settings.Theme);
        ThemeService.ApplyAccent(settings.AccentColor);

        var http = HttpClientFactory.CreateBiliClient();

        var services = new ServiceCollection();
        services.AddSingleton(settings);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(http);

        // Core
        services.AddSingleton<ICredentialStore>(_ => new DpapiCredentialStore());
        services.AddSingleton<IQrLogin, QrLogin>();
        services.AddSingleton<IBiliApiClient, BiliApiClient>();
        services.AddSingleton<IBiliSession, BiliSession>();
        services.AddSingleton<UrlParser>();
        services.AddSingleton<IFileDownloader, HttpFileDownloader>();
        services.AddSingleton<IFfmpegMerger>(_ => new FfmpegMerger(() => FfmpegLocator.Find(settings.FfmpegPath)));
        services.AddSingleton<IFileNameStrategy>(_ => new DefaultFileNameStrategy(FileNameRule.IndexAndPart));
        services.AddSingleton<IVideoDownloadService>(sp => new VideoDownloadService(
            sp.GetRequiredService<IBiliApiClient>(),
            sp.GetRequiredService<IFileDownloader>(),
            sp.GetRequiredService<IFfmpegMerger>(),
            sp.GetRequiredService<IFileNameStrategy>(),
            sp.GetRequiredService<UrlParser>(),
            sp.GetRequiredService<HttpClient>(),
            settings.MaxConcurrentTasks));

        // ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddTransient(sp => new SettingsViewModel(sp.GetRequiredService<AppSettings>()));

        Services = services.BuildServiceProvider();

        var window = new MainWindow(Services.GetRequiredService<MainViewModel>(), Services);
        MainWindow = window;
        window.Show();

        var args = Environment.GetCommandLineArgs();

        // 开发期界面取证（--shot=<dir>），发行版不会走到这里
        if (DiagnosticsShot.IsRequested(args))
        {
            DiagnosticsShot.Trace($"startup args=[{string.Join(' ', args)}]");
            var vm = Services.GetRequiredService<MainViewModel>();
            window.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await DiagnosticsShot.RunAsync(window, vm, args);
                }
                catch (Exception ex)
                {
                    DiagnosticsShot.Trace("shot failed: " + ex);
                    window.Close();
                    Shutdown(1);
                }
            }, DispatcherPriority.ApplicationIdle);
        }
    }

    private void HookGlobalExceptions()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog("dispatcher", args.Exception);
            MessageBox.Show(args.Exception is BiliException bili ? bili.UserMessage : "程序遇到一个未处理的错误，已记录日志。",
                "BiliDownloader", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) WriteCrashLog("appdomain", ex);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog("task", args.Exception);
            args.SetObserved();
        };
    }

    /// <summary>崩溃日志单独落盘，方便下次启动排查（方案设计 §13）。</summary>
    private static void WriteCrashLog(string source, Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BiliDownloader", "logs");
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, $"[{source}] {DateTime.Now:O}{Environment.NewLine}{ex}");
        }
        catch (Exception logEx) when (logEx is IOException or UnauthorizedAccessException)
        {
            // 日志写不进去也不能再抛
        }
    }
}
