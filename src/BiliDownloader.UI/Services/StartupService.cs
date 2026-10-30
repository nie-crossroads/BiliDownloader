using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace BiliDownloader.UI.Services;

/// <summary>
/// 开机自启（设置项 AutoStart）：写当前用户的 Run 注册表项。
/// 用 HKCU 而不是 HKLM，不需要管理员权限，也不会影响其他账号。
/// </summary>
public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BiliDownloader";

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>切换开关即时生效，不需要重启。</summary>
    public void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

            if (key is null) return;

            if (enabled) key.SetValue(ValueName, CommandLine());
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            // 没权限写注册表不该让设置页崩掉
        }
    }

    /// <summary>启动命令行。路径带引号，避免目录里有空格时被截断。</summary>
    private static string CommandLine()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path))
            path = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;

        return $"\"{path}\"";
    }
}
