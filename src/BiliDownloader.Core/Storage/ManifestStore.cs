using System.Text.Json;
using BiliDownloader.Core.Download;
using BiliDownloader.Core.FileName;
using BiliDownloader.Core.Utils;

namespace BiliDownloader.Core.Storage;

/// <summary>清单里的一页：除了进度，最重要的是两路流的指纹，恢复时靠它判断「流有没有变」。</summary>
public sealed record ManifestPageEntry(
    long Cid,
    int Index,
    string Part,
    int Qn,
    int CodecId,
    int AudioId,
    string Status,
    long ReceivedBytes,
    long TotalBytes,
    StreamFingerprint? Video,
    StreamFingerprint? Audio);

/// <summary>清单里的一个任务：含重建任务所需的全部参数与入队时固定的下载选项快照。</summary>
public sealed record ManifestTaskEntry(
    string TaskId,
    string Bvid,
    string Title,
    string? UpName,
    string? CoverUrl,
    long DurationTicks,
    int TotalPages,
    int Connections,
    int MaxRetriesPerUrl,
    bool AllowFallback,
    // 入队时固定的命名与同名策略：恢复后文件名、同名处理必须和中断前一致，
    // 否则同一批分 P 会一半叫「01 标题」一半叫「UP - 标题」。
    FileNameRule NameRule,
    int IndexPadding,
    ExistingFileAction Policy,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ManifestPageEntry> Pages);

public sealed class DownloadManifest
{
    public List<ManifestTaskEntry> Tasks { get; set; } = [];
}

/// <summary>
/// 跨重启恢复的任务清单，落在「保存目录/.bilitmp/manifest.json」，与临时文件同生命周期。
/// <para>
/// 没有它，断点只存在于进程内存里：程序一关，任务卡片就没了，剩下的临时文件也不知道属于谁。
/// 有了它，重启后能重建任务并从上次进度继续——但必须先过流指纹校验（见
/// <see cref="StreamFingerprint.Matches"/>），指纹对不上就整段重下，绝不做无校验的拼接。
/// </para>
/// </summary>
public static class ManifestStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string PathOf(string saveDirectory) =>
        Path.Combine(StoragePaths.TempRoot(saveDirectory), "manifest.json");

    /// <summary>读清单；不存在或损坏都返回空清单——清单坏了只影响恢复，不能拖垮启动。</summary>
    public static DownloadManifest Load(string saveDirectory)
    {
        try
        {
            var path = PathOf(saveDirectory);
            if (!File.Exists(path)) return new DownloadManifest();

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<DownloadManifest>(json, Options) ?? new DownloadManifest();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new DownloadManifest();
        }
    }

    /// <summary>原子写：先写临时文件再替换，避免写到一半崩溃留下半截 JSON。</summary>
    public static void Save(string saveDirectory, DownloadManifest manifest)
    {
        try
        {
            var directory = StoragePaths.TempRoot(saveDirectory);
            Directory.CreateDirectory(directory);
            StoragePaths.TryHideTempRoot(saveDirectory);

            var target = PathOf(saveDirectory);
            var staging = target + ".tmp";
            File.WriteAllText(staging, JsonSerializer.Serialize(manifest, Options));
            Net48.MoveFile(staging, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清单写不进去只影响恢复能力，不能让正在进行的下载失败
        }
    }

    /// <summary>整份清单都没用了（比如用户清空任务）时删除文件。</summary>
    public static void Delete(string saveDirectory)
    {
        try
        {
            var path = PathOf(saveDirectory);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
