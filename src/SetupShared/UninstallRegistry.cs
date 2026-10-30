using Microsoft.Win32;
using System.IO;

namespace BiliDownloader.SetupShared;

/// <summary>在 HKCU 注册/反注册「应用和功能」卸载项。</summary>
public static class UninstallRegistry
{
    private const string Base =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + ProductInfo.RegistryKeyName;

    public static void Register(string installDir)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Base);
        if (key is null) return;
        var appExe = Path.Combine(installDir, "BiliDownloader.UI.exe");
        var uninstallExe = Path.Combine(installDir, "uninstall.exe");
        key.SetValue("DisplayName", ProductInfo.AppName, RegistryValueKind.String);
        key.SetValue("DisplayVersion", ProductInfo.Version, RegistryValueKind.String);
        key.SetValue("Publisher", ProductInfo.Publisher, RegistryValueKind.String);
        key.SetValue("InstallLocation", installDir, RegistryValueKind.String);
        key.SetValue("DisplayIcon", appExe + ",0", RegistryValueKind.String);
        key.SetValue("UninstallString", $"\"{uninstallExe}\"", RegistryValueKind.String);
        key.SetValue("QuietUninstallString", $"\"{uninstallExe}\" /S", RegistryValueKind.String);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)(260u * 1024), RegistryValueKind.DWord); // 约 260 MB
    }

    public static void Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(Base, false);
        }
        catch
        {
            // 已有清理逻辑在调用方，忽略注册表缺失
        }
    }
}
