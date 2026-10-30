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

        // 代理与请求超时只在创建 HttpClient 时生效，而客户端是单例，所以这两项改完要重启程序。
        var http = HttpClientFactory.CreateBiliClient(
            new HttpClientOptions(settings.ProxySetting, settings.ProxyAddress, settings.RequestTimeoutSeconds));

        var services = new ServiceCollection();
        services.AddSingleton(settings);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(http);

        // 全局限速是共享预算：所有任务、所有分片都从这一个桶里取配额，设置页改动即时生效。
        services.AddSingleton<ISpeedLimiter>(_ => new TokenBucketSpeedLimiter(settings.GlobalSpeedLimitKbps * 1024L));
        services.AddSingleton<ILogService>(_ => new LogService(settings));
        services.AddSingleton<StartupService>();
        services.AddSingleton<UpdateService>();

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
        services.AddTransient(sp => new SettingsViewModel(
            sp.GetRequiredService<AppSettings>(),
            null,
            sp.GetRequiredService<ISpeedLimiter>(),
            sp.GetRequiredService<StartupService>()));

        Services = services.BuildServiceProvider();

        var args = Environment.GetCommandLineArgs();
        var vm = Services.GetRequiredService<MainViewModel>();

        // --resume：启动后自动继续上次没下完的任务，用于端到端验证跨重启恢复
        if (args.Any(a => string.Equals(a, "--resume", StringComparison.OrdinalIgnoreCase)))
            vm.AutoResumeRestored = true;

        var window = new MainWindow(vm, Services);
        MainWindow = window;
        window.Show();


        // 开发期界面取证（--shot=<dir>），发行版不会走到这里
        if (DiagnosticsShot.IsRequested(args))
        {
            DiagnosticsShot.Trace($"startup args=[{string.Join(' ', args)}]");
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

    /// <summary>
    /// 退出时统一释放 DI 容器：编排层会在 Dispose 里停掉所有后台流程，
    /// 避免窗口关了但下载线程还在跑（表现为进程退不干净、临时文件被占着）。
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        if (Services is IDisposable disposable) disposable.Dispose();
        base.OnExit(e);
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
            // 与 LogService / 设置页「打开日志目录」指向同一个目录，
            // 否则用户点开目录只会看到一个空文件夹，崩溃日志其实躺在另一个目录下。
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
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
