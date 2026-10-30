using System.IO;
using BiliDownloader.Core.Utils;

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

    public static string PageTempDir(string saveDirectory, string taskId, long cid) =>
        Path.Combine(TempRoot(saveDirectory), $"{taskId}-{cid}");

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
    /// 原子落盘：先复制到目标目录的独占 .incomplete 文件，再改名。
    /// 返回最终路径；<see cref="ExistingFileAction.Skip"/> 且目标已存在时返回 null（调用方应记为跳过）。
    /// 默认 Rename：目标已存在就加 (1)(2)，不覆盖任何已有文件。
    /// </summary>
    public static string? Commit(string tempFile, string targetDirectory, string fileNameWithoutExtension,
        string extension = ".mp4", ExistingFileAction action = ExistingFileAction.Rename)
    {
        Directory.CreateDirectory(targetDirectory);

        var desiredPath = Path.Combine(targetDirectory, fileNameWithoutExtension + extension);

        if (action == ExistingFileAction.Skip && File.Exists(desiredPath))
        {
            // 用户选了「跳过」：成品已经在那儿了，临时文件留着没意义。
            TryDelete(tempFile);
            return null;
        }

        // 每次提交独占暂存文件，最终重命名禁止覆盖；并发同名任务也不会丢文件。
        var staging = Path.Combine(targetDirectory, $".{Guid.NewGuid():N}.incomplete");
        try
        {
            // 保留源文件直到最终提交成功，失败时仍可重试。
            File.Copy(tempFile, staging, overwrite: false);

            if (action == ExistingFileAction.Overwrite)
            {
                try
                {
                    Net48.MoveFile(staging, desiredPath, overwrite: true);
                    TryDelete(tempFile);
                    return desiredPath;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 覆盖失败（目标被占用等）不能把成品弄丢，退回改名策略继续尝试。
                }
            }

            while (true)
            {
                var finalPath = EnsureUnique(desiredPath);
                try
                {
                    Net48.MoveFile(staging, finalPath, overwrite: false);
                    TryDelete(tempFile);
                    return finalPath;
                }
                catch (IOException) when (File.Exists(finalPath))
                {
                    // 另一个任务抢先提交了这个名称，重新选名。
                }
            }
        }
        finally
        {
            TryDelete(staging);
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

        return Path.Combine(dir, $"{name} ({Guid.NewGuid():N}){ext}");
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
