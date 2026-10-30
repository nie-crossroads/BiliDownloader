using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using BiliDownloader.SetupShared;

namespace BiliDownloader.Uninstaller;

public sealed class UninstallModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnProp([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    private bool Set<T>(ref T f, T v, [CallerMemberName] string? n = null)
    {
        if (Equals(f, v)) return false;
        f = v; OnProp(n); return true;
    }

    private bool _deleteUserData;
    public bool DeleteUserData { get => _deleteUserData; set => Set(ref _deleteUserData, value); }

    private bool _busy;
    public bool Busy { get => _busy; set => Set(ref _busy, value); }

    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }

    public string DefaultSaveDir => ProductInfo.DefaultSaveDir;
    public string InstallDir { get; } = AppContext.BaseDirectory.TrimEnd('\\');

    public ICommand UninstallCommand { get; }
    public ICommand CancelCommand { get; }

    private readonly Action? _requestClose;

    public UninstallModel(Action? requestClose = null)
    {
        _requestClose = requestClose;
        UninstallCommand = new RelayCommand(() => _ = UninstallAsync(), () => !Busy);
        CancelCommand = new RelayCommand(() => _requestClose?.Invoke());
    }

    internal async Task UninstallAsync()
    {
        Busy = true;
        Status = "正在卸载…";
        var installDir = InstallDir;
        var self = Process.GetCurrentProcess().MainModule?.FileName ?? "";
        try
        {
            foreach (var f in Directory.GetFiles(installDir))
            {
                if (string.Equals(f, self, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(f); } catch { /* 忽略锁定文件 */ }
            }
            foreach (var d in Directory.GetDirectories(installDir))
            {
                try { Directory.Delete(d, true); } catch { /* 忽略 */ }
            }
            try { ShellShortcut.RemoveDesktopShortcut(ProductInfo.AppName); } catch { /* 忽略 */ }
            try { UninstallRegistry.Unregister(); } catch { /* 忽略 */ }
            if (DeleteUserData)
            {
                var appData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductInfo.AppName);
                try { if (Directory.Exists(appData)) Directory.Delete(appData, true); } catch { /* 忽略 */ }
            }
            Status = "已完成";
        }
        catch (Exception ex)
        {
            Status = "卸载出错：" + ex.Message;
        }
        finally
        {
            Busy = false;
        }

        // uninstall.exe 自身位于安装目录内，需借临时副本完成自删除，避免删到自己
        try { SelfDelete(installDir); } catch { /* 忽略 */ }
        _requestClose?.Invoke();
    }

    private static void SelfDelete(string installDir)
    {
        var self = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(self) || !File.Exists(self)) return;
        var tmp = Path.Combine(Path.GetTempPath(), "bili_uninst_" + Guid.NewGuid().ToString("N") + ".exe");
        File.Copy(self, tmp, true);

        // .NET Framework 的 exe 带一个 .config（supportedRuntime），临时副本要一并带上，
        // 否则副本按「无清单」启动，行为与原件不一致。
        var config = self + ".config";
        if (File.Exists(config)) File.Copy(config, tmp + ".config", true);

        Process.Start(new ProcessStartInfo(tmp, "/cleanup=\"" + installDir + "\"") { UseShellExecute = true });
    }
}
