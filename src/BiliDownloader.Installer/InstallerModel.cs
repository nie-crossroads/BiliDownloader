using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using BiliDownloader.Installer.Infra;
using BiliDownloader.SetupShared;
using Microsoft.Win32;

namespace BiliDownloader.Installer;

public enum InstallStep { Welcome, Installing, Done }
public enum SubStepState { Pending, Running, Done }

public sealed class InstallerModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnProp([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    private bool Set<T>(ref T f, T v, [CallerMemberName] string? n = null)
    {
        if (Equals(f, v)) return false;
        f = v; OnProp(n); return true;
    }

    private InstallStep _step = InstallStep.Welcome;
    public InstallStep Step
    {
        get => _step;
        set
        {
            if (!Set(ref _step, value)) return;
            HeaderRightText = value switch
            {
                InstallStep.Installing => "正在安装",
                InstallStep.Done => "安装完成",
                _ => "仅当前用户安装，无需管理员权限"
            };
        }
    }

    private string _headerRight = "仅当前用户安装，无需管理员权限";
    public string HeaderRightText { get => _headerRight; set => Set(ref _headerRight, value); }

    private string _targetDir = ProductInfo.DefaultInstallDir;
    public string TargetDir
    {
        get => _targetDir;
        set
        {
            if (!Set(ref _targetDir, value)) return;
            UpdateFreeSpace();
        }
    }

    private string _freeSpaceText = "";
    public string FreeSpaceText { get => _freeSpaceText; set => Set(ref _freeSpaceText, value); }

    private void UpdateFreeSpace()
    {
        try
        {
            var root = Path.GetPathRoot(TargetDir.Trim());
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                FreeSpaceText = $"{drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0):F1} GB 可用";
                return;
            }
        }
        catch { /* 忽略，显示空 */ }
        FreeSpaceText = "";
    }

    private bool _createShortcut = true;
    public bool CreateShortcut { get => _createShortcut; set => Set(ref _createShortcut, value); }

    private bool _launchAfter = true;
    public bool LaunchAfter { get => _launchAfter; set => Set(ref _launchAfter, value); }

    private bool _busy;
    public bool Busy
    {
        get => _busy;
        set
        {
            if (!Set(ref _busy, value)) return;
            // Busy 决定「安装」/「取消」的可用性，必须让 WPF 重查，
            // 否则状态变了按钮还停在原来的启用/禁用外观。
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private long _bytesWritten;
    public long BytesWritten
    {
        get => _bytesWritten;
        set { if (Set(ref _bytesWritten, value)) OnProp(nameof(WrittenInfo)); }
    }

    private long _bytesTotal;
    public long BytesTotal
    {
        get => _bytesTotal;
        set { if (Set(ref _bytesTotal, value)) OnProp(nameof(WrittenInfo)); }
    }

    private int _percent;
    public int Percent { get => _percent; set => Set(ref _percent, value); }

    private SubStepState _sCopy = SubStepState.Pending;
    public SubStepState StepCopy { get => _sCopy; set => Set(ref _sCopy, value); }
    private SubStepState _sFfmpeg = SubStepState.Pending;
    public SubStepState StepFfmpeg { get => _sFfmpeg; set => Set(ref _sFfmpeg, value); }
    private SubStepState _sShortcut = SubStepState.Pending;
    public SubStepState StepShortcut { get => _sShortcut; set => Set(ref _sShortcut, value); }
    private SubStepState _sRegister = SubStepState.Pending;
    public SubStepState StepRegister { get => _sRegister; set => Set(ref _sRegister, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private string _errorText = "";
    public string ErrorText { get => _errorText; set => Set(ref _errorText, value); }

    public string WrittenInfo => $"已写入 {ToMb(BytesWritten)} / {ToMb(BytesTotal)} MB";

    /// <summary>按载荷实际大小计算，避免改完载荷后这里的数字留在旧值上。</summary>
    public string RequireInfo => $"需要约 {Payload.TotalBytes() / (1024 * 1024)} MB 磁盘空间";

    private static string ToMb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("F0");

    public ICommand InstallCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand BrowseCommand { get; }
    public ICommand FinishCommand { get; }
    public ICommand OpenFolderCommand { get; }

    private CancellationTokenSource? _cts;
    private readonly Action? _requestClose;

    public InstallerModel(Action? requestClose = null)
    {
        _requestClose = requestClose;
        InstallCommand = new RelayCommand(() => _ = InstallAsync(), () => !Busy && TargetDir.Trim().Length > 0);
        // 「取消」任何时候都要能点：安装中=中止并回滚，否则=关闭窗口。
        // 之前用 () => Busy 作条件，初始 Busy=false，按钮一上来就是禁用态，点了没反应。
        CancelCommand = new RelayCommand(Cancel);
        BrowseCommand = new RelayCommand(Browse);
        FinishCommand = new RelayCommand(Finish);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        UpdateFreeSpace();
    }

    private async Task InstallAsync()
    {
        Busy = true;
        ErrorText = "";
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var written = new List<string>();
        var target = TargetDir.Trim();
        var existedBefore = Directory.Exists(target);
        try
        {
            Step = InstallStep.Installing;
            StepCopy = StepFfmpeg = StepShortcut = StepRegister = SubStepState.Pending;
            Directory.CreateDirectory(target);

            BytesTotal = Payload.TotalBytes();
            BytesWritten = 0;
            Percent = 0;
            var progress = new Progress<long>(delta =>
            {
                BytesWritten += delta;
                var p = BytesTotal > 0 ? (int)(BytesWritten * 100 / BytesTotal) : 0;
                if (p != Percent) Percent = p;
                OnProp(nameof(WrittenInfo));
            });

            StepCopy = SubStepState.Running;
            written.AddRange(await Payload.ExtractAsync(target,
                Payload.Files.Where(f => f.ResName == "payload.app" || f.ResName == "payload.license"), progress, ct));
            StepCopy = SubStepState.Done;

            StepFfmpeg = SubStepState.Running;
            written.AddRange(await Payload.ExtractAsync(target,
                Payload.Files.Where(f => f.ResName == "payload.ffmpeg"), progress, ct));
            StepFfmpeg = SubStepState.Done;

            StepShortcut = SubStepState.Running;
            if (CreateShortcut)
                ShellShortcut.CreateDesktopShortcut(ProductInfo.AppName,
                    Path.Combine(target, "BiliDownloader.UI.exe"));
            StepShortcut = SubStepState.Done;

            StepRegister = SubStepState.Running;
            UninstallRegistry.Register(target);
            written.AddRange(await Payload.ExtractAsync(target,
                Payload.Files.Where(f => f.ResName.StartsWith("payload.uninstall")), progress, ct));
            StepRegister = SubStepState.Done;

            StatusText = $"已安装到 {target}";
            Step = InstallStep.Done;
            // 安装结束不自动拉起主程序：是否启动交给完成页的「立即启动」勾选，
            // 由用户点「完成」时才执行（见 Finish）。
        }
        catch (OperationCanceledException)
        {
            Rollback(written, existedBefore, target);
            Step = InstallStep.Welcome;
            StatusText = "已取消，安装已回滚";
            // 用户主动取消：回滚干净后直接关窗口，不要在欢迎页留一个空壳。
            _requestClose?.Invoke();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            Rollback(written, existedBefore, target);
            Step = InstallStep.Welcome;
        }
        finally
        {
            Busy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Rollback(List<string> written, bool existedBefore, string target)
    {
        // 删除本次写入的文件（存在既有目录时不整体删目录，避免误删用户文件）
        foreach (var f in written.Where(File.Exists))
        {
            try { File.Delete(f); } catch { /* 忽略 */ }
        }
        if (!existedBefore && Directory.Exists(target))
        {
            try { if (Directory.GetFileSystemEntries(target).Length == 0) Directory.Delete(target); } catch { /* 忽略 */ }
        }
        try { if (CreateShortcut) ShellShortcut.RemoveDesktopShortcut(ProductInfo.AppName); } catch { /* 忽略 */ }
        try { UninstallRegistry.Unregister(); } catch { /* 忽略 */ }
    }

    private void Cancel()
    {
        if (Busy)
        {
            // 回滚在 InstallAsync 的 OperationCanceled 分支完成，回滚结束后关闭窗口。
            _cts?.Cancel();
            return;
        }

        _requestClose?.Invoke();
    }

    private void Browse()
    {
        var d = new OpenFileDialog
        {
            Title = "选择安装位置",
            ValidateNames = false,
            CheckFileExists = false,
            CheckPathExists = true,
            FileName = "选择此文件夹后点击打开",
        };
        if (d.ShowDialog() == true)
        {
            var dir = Path.GetDirectoryName(d.FileName);
            if (!string.IsNullOrEmpty(dir)) TargetDir = dir;
        }
    }

    private void Finish()
    {
        if (LaunchAfter && Step == InstallStep.Done)
            LaunchApp(TargetDir.Trim());

        _requestClose?.Invoke();
    }

    private void OpenFolder()
    {
        var target = TargetDir.Trim();
        if (Directory.Exists(target))
            Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
    }

    private static void LaunchApp(string target)
    {
        var exe = Path.Combine(target, "BiliDownloader.UI.exe");
        if (File.Exists(exe))
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = target });
    }

    // ---- 静默安装（/S）：无窗口，输出到控制台 ----
    public async Task<int> SilentInstallAsync()
    {
        var written = new List<string>();
        var target = TargetDir.Trim();
        var existedBefore = Directory.Exists(target);
        try
        {
            Directory.CreateDirectory(target);
            Console.WriteLine($"[BiliDownloader] 安装到 {target}");
            var progress = new Progress<long>(_ => { });
            written.AddRange(await Payload.ExtractAsync(target,
                Payload.Files.Where(f => f.ResName == "payload.app" || f.ResName == "payload.license"), progress, CancellationToken.None));
            written.AddRange(await Payload.ExtractAsync(target,
                Payload.Files.Where(f => f.ResName == "payload.ffmpeg"), progress, CancellationToken.None));
            if (CreateShortcut)
                ShellShortcut.CreateDesktopShortcut(ProductInfo.AppName, Path.Combine(target, "BiliDownloader.UI.exe"));
            UninstallRegistry.Register(target);
            written.AddRange(await Payload.ExtractAsync(target,
                Payload.Files.Where(f => f.ResName.StartsWith("payload.uninstall")), progress, CancellationToken.None));
            Console.WriteLine("[BiliDownloader] 安装完成");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[BiliDownloader] 安装失败：" + ex.Message);
            foreach (var f in written.Where(File.Exists)) { try { File.Delete(f); } catch { } }
            if (!existedBefore && Directory.Exists(target)) { try { Directory.Delete(target, true); } catch { } }
            try { UninstallRegistry.Unregister(); } catch { }
            return 1;
        }
    }
}
