using System.IO;
using System.Runtime.InteropServices;

namespace BiliDownloader.SetupShared;

/// <summary>通过 WScript.Shell COM 创建/删除 .lnk 快捷方式（无需引用 interop 程序集）。</summary>
public static class ShellShortcut
{
    public static void CreateDesktopShortcut(string linkName, string targetPath, string? description = null)
    {
        var linkPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), linkName + ".lnk");
        Create(linkPath, targetPath, description);
    }

    public static void Create(string linkPath, string targetPath, string? description = null)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
                        ?? throw new InvalidOperationException("找不到 WScript.Shell 组件，无法创建快捷方式。");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(linkPath);
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath);
            shortcut.Description = description ?? "BiliDownloader";
            shortcut.IconLocation = targetPath + ",0";
            shortcut.Save();
        }
        finally
        {
            Marshal.ReleaseComObject(shell);
        }
    }

    public static void RemoveDesktopShortcut(string linkName)
    {
        var linkPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), linkName + ".lnk");
        if (File.Exists(linkPath)) File.Delete(linkPath);
    }
}
