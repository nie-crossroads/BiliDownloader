using System.IO;

namespace BiliDownloader.Core.Storage;

/// <summary>
/// 磁盘布局与原子提交（方案设计 §7）。
/// 目标文件永远由 temp 里合并好再 Move 进来，不留半成品。
/// </summary>
public static class StoragePaths
{
    /// <summary>默认保存目录：用户「视频」库\BiliDownloader。</summary>
    public static string DefaultSaveDirectory()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrEmpty(videos))
            videos = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return Path.Combine(videos, "BiliDownloader");
    }

    public static string TempRoot(string saveDirectory) => Path.Combine(saveDirectory, ".bilitmp");

    public static string PageTempDir(string saveDirectory, string bvid, long cid) =>
        Path.Combine(TempRoot(saveDirectory), $"{bvid}-{cid}");

    /// <summary>把 .bilitmp 标记为隐藏（失败不影响下载）。</summary>
    public static void TryHideTempRoot(string saveDirectory)
    {
        try
        {
            var root = TempRoot(saveDirectory);
            Directory.CreateDirectory(root);
            var info = new DirectoryInfo(root);
            if (!info.Attributes.HasFlag(FileAttributes.Hidden))
                info.Attributes |= FileAttributes.Hidden;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 忽略：仅为界面整洁
        }
    }

    /// <summary>
    /// 原子落盘：同卷 Move；跨卷先复制成 .incomplete 再改名。
    /// 返回最终路径。目标已存在时按 Rename 策略加 (1)(2)。
    /// </summary>
    public static string Commit(string tempFile, string targetDirectory, string fileNameWithoutExtension,
        string extension = ".mp4")
    {
        Directory.CreateDirectory(targetDirectory);

        var finalPath = Path.Combine(targetDirectory, fileNameWithoutExtension + extension);
        finalPath = EnsureUnique(finalPath);

        try
        {
            File.Move(tempFile, finalPath);
            return finalPath;
        }
        catch (IOException)
        {
            // 跨卷或目标被占用：先复制到临时名再替换
            var staging = finalPath + ".incomplete";
            File.Copy(tempFile, staging, overwrite: true);
            File.Move(staging, finalPath, overwrite: true);
            TryDelete(tempFile);
            return finalPath;
        }
    }

    public static string EnsureUnique(string path)
    {
        if (!File.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }

        return Path.Combine(dir, $"{name} ({DateTime.Now:HHmmss}){ext}");
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
