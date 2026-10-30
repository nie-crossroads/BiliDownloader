using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace BiliDownloader.UI.Services;

/// <summary>把「打开文件所在文件夹 / 打开目录」这类 Shell 操作隔离出来，便于替换与测试（架构评审第 4 项）。</summary>
public interface IFolderOpener
{
    /// <summary>若 filePath 存在则选中它，否则打开 fallbackFolder。返回是否成功。</summary>
    bool Open(string? filePath, string fallbackFolder);
}

/// <summary>基于 explorer.exe 的实现：选中文件用 /select，否则直接打开文件夹。</summary>
public sealed class ExplorerFolderOpener : IFolderOpener
{
    public bool Open(string? filePath, string fallbackFolder)
    {
        try
        {
            var argument = File.Exists(filePath)
                ? $"/select,\"{filePath}\""
                : $"\"{fallbackFolder}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            return false;
        }
    }
}
