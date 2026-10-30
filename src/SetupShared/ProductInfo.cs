using System;
using System.IO;

namespace BiliDownloader.SetupShared;

/// <summary>安装包/卸载包共享的产品常量，确保两处口径一致。</summary>
public static class ProductInfo
{
    public const string AppName = "BiliDownloader";
    public const string Version = "1.0.0";
    public const string Publisher = "Still Flow";
    public const string RegistryKeyName = "BiliDownloader";

    /// <summary>卸载项在注册表的位置（当前用户）。</summary>
    public const string UninstallRegistryPath =
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\" + RegistryKeyName;

    /// <summary>默认安装目录：%LOCALAPPDATA%\Programs\BiliDownloader（无需管理员）。</summary>
    public static string DefaultInstallDir
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "Programs", AppName);
        }
    }

    /// <summary>应用默认保存目录（用于卸载时提示「已下载的视频不会被删除」）。</summary>
    public static string DefaultSaveDir
    {
        get
        {
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            return Path.Combine(videos, AppName);
        }
    }
}
