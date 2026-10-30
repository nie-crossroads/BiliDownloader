using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BiliDownloader.Core.Download;
using BiliDownloader.Core.FileName;
using BiliDownloader.Core.Storage;
using BiliDownloader.Core.Utils;

namespace BiliDownloader.UI.Services;

/// <summary>
/// 设置（主页面用到的子集）：保存目录 / 默认画质 / 并发 / 主题。
/// 完整 26 项见方案设计 §12，随设置页一起落地。
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public string SaveDirectory { get; set; } = StoragePaths.DefaultSaveDirectory();

    public int MaxConcurrentTasks { get; set; } = 3;

    public int PreferredQn { get; set; } = 80;

    public string PreferredCodec { get; set; } = "AVC";

    public int PreferredAudioId { get; set; } = 30280;

    /// <summary>Light / Dark。</summary>
    public string Theme { get; set; } = "Light";

    /// <summary>任务完成后自动打开所在目录。</summary>
    public bool AutoOpenFolder { get; set; }

    /// <summary>外部 FFmpeg 路径（null 表示用内嵌 / PATH）。</summary>
    public string? FfmpegPath { get; set; }

    // ---------------------------------------------------------------- 下载 · 命名

    /// <summary>IndexTitle（序号+选集标题）/ Title（纯标题）/ TitleUp（标题+UP 主）。</summary>
    public string FileNameTemplate { get; set; } = "IndexTitle";

    /// <summary>序号补零位数，2 或 3。</summary>
    public int IndexPadding { get; set; } = 2;

    // ---------------------------------------------------------------- 下载 · 性能

    /// <summary>单任务分片并发数。</summary>
    public int ConnectionsPerTask { get; set; } = 4;

    /// <summary>全局限速（KB/s），0 表示不限速。</summary>
    public int GlobalSpeedLimitKbps { get; set; }

    // ---------------------------------------------------------------- 下载 · 完成之后

    public bool NotifyOnComplete { get; set; } = true;

    /// <summary>Rename / Skip / Overwrite。</summary>
    public string ExistingFileAction { get; set; } = "Rename";

    // ---------------------------------------------------------------- 基础

    public bool AutoStart { get; set; }

    public bool CheckUpdateOnStartup { get; set; } = true;

    /// <summary>Minimize / Exit。</summary>
    public string CloseAction { get; set; } = "Exit";

    public string Language { get; set; } = "zh-CN";

    // ---------------------------------------------------------------- 网络

    /// <summary>None / System / Manual。</summary>
    public string ProxyMode { get; set; } = "System";

    public string ProxyAddress { get; set; } = string.Empty;

    public int RequestTimeoutSeconds { get; set; } = 30;

    public int MaxRetries { get; set; } = 3;

    public bool AutoFallbackEndpoint { get; set; } = true;

    // ---------------------------------------------------------------- 外观

    public bool ShowThumbnails { get; set; } = true;

    public bool FadeCompletedProgress { get; set; } = true;

    public bool ReduceMotion { get; set; }

    /// <summary>
    /// 主题色 key，必须是 <see cref="AccentPalette.Presets"/> 里的一个 key（视觉规范 §3.2）。
    /// 令牌字典里放的只是默认粉，选中后用 App.Resources 顶层覆盖 Accent* 系列笔刷。
    /// </summary>
    public string AccentColor { get; set; } = AccentPalette.DefaultKey;

    // ---------------------------------------------------------------- 日志

    /// <summary>Debug / Info / Warn / Error。</summary>
    public string LogLevel { get; set; } = "Info";

    // ---------------------------------------------------------------- 映射到 Core

    // 下面几个是「字符串设置 → Core 枚举」的派生属性，必须标 JsonIgnore：
    // 否则 Save() 会把它们一起写进 settings.json，配置文件里混进一堆派生字段。
    // 另外 ExistingFileAction / ProxyMode 的类型名和本类的字符串属性重名，
    // 所以 Core 侧的类型一律写全名，避免编译器把类型解析成同名属性。

    /// <summary>设置里的命名规则字符串 → Core 命名规则枚举。</summary>
    [JsonIgnore]
    public FileNameRule NameRule => FileNameTemplate switch
    {
        "Title" => FileNameRule.PlainTitle,
        "TitleUp" => FileNameRule.WithUpName,
        _ => FileNameRule.IndexAndPart
    };

    /// <summary>设置里的同名处理字符串 → Core 落盘策略枚举。</summary>
    [JsonIgnore]
    public BiliDownloader.Core.Storage.ExistingFileAction ExistingFilePolicy => ExistingFileAction switch
    {
        "Skip" => BiliDownloader.Core.Storage.ExistingFileAction.Skip,
        "Overwrite" => BiliDownloader.Core.Storage.ExistingFileAction.Overwrite,
        _ => BiliDownloader.Core.Storage.ExistingFileAction.Rename
    };

    /// <summary>设置里的代理模式字符串 → Core 代理枚举。</summary>
    [JsonIgnore]
    public BiliDownloader.Core.Utils.ProxyMode ProxySetting => ProxyMode switch
    {
        "None" => BiliDownloader.Core.Utils.ProxyMode.None,
        "Manual" => BiliDownloader.Core.Utils.ProxyMode.Manual,
        _ => BiliDownloader.Core.Utils.ProxyMode.System
    };

    /// <summary>
    /// 下载器参数快照：并发分片数、重试次数、是否回退备用节点都在这里定死，
    /// 入队时随请求一起交给 Core（Core 不反向依赖 AppSettings）。
    /// </summary>
    [JsonIgnore]
    public DownloadOptions DownloadPreferences => new(
        ChunkSizeBytes: 1 << 20,
        MaxRetriesPerUrl: MaxRetries,
        Connections: ConnectionsPerTask,
        AllowFallback: AutoFallbackEndpoint);

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BiliDownloader", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();

            var json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            settings.Normalize();
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            Net48.MoveFile(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 设置写不进去不该阻断下载
        }
    }

    public void Normalize()
    {
        MaxConcurrentTasks = Net48.Clamp(MaxConcurrentTasks, 1, 8);
        ConnectionsPerTask = Net48.Clamp(ConnectionsPerTask, 1, 16);
        GlobalSpeedLimitKbps = Math.Max(0, GlobalSpeedLimitKbps);
        RequestTimeoutSeconds = Net48.Clamp(RequestTimeoutSeconds, 5, 300);
        MaxRetries = Net48.Clamp(MaxRetries, 0, 10);
        IndexPadding = IndexPadding == 3 ? 3 : 2;

        if (string.IsNullOrWhiteSpace(SaveDirectory)) SaveDirectory = StoragePaths.DefaultSaveDirectory();
        if (Theme is not ("Light" or "Dark")) Theme = "Light";
        if (FileNameTemplate is not ("IndexTitle" or "Title" or "TitleUp")) FileNameTemplate = "IndexTitle";
        if (ExistingFileAction is not ("Rename" or "Skip" or "Overwrite")) ExistingFileAction = "Rename";
        if (CloseAction is not ("Minimize" or "Exit")) CloseAction = "Exit";
        if (ProxyMode is not ("None" or "System" or "Manual")) ProxyMode = "System";
        if (LogLevel is not ("Debug" or "Info" or "Warn" or "Error")) LogLevel = "Info";
        if (!AccentPalette.Contains(AccentColor)) AccentColor = AccentPalette.DefaultKey;
        ProxyAddress ??= string.Empty;
    }

    /// <summary>保存目录不可用（不存在且建不出来 / 不可写）时回落默认目录。</summary>
    public static bool EnsureWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".bilitmp-probe");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
