using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using BiliDownloader.UI.ViewModels;

namespace BiliDownloader.UI.Services;

/// <summary>
/// 应用日志：按设置里的 LogLevel 过滤，落盘到「设置 → 关于与诊断 → 打开日志目录」指向的同一个目录。
/// 写日志前统一脱敏，避免 Cookie / 凭据被原样写进文件。
/// </summary>
public interface ILogService
{
    /// <summary>当前生效的日志目录（设置页「打开日志目录」用的就是它）。</summary>
    string LogDirectory { get; }

    void Write(LogLevel level, string message);
}

public sealed class LogService : ILogService
{
    /// <summary>凭据类字段：值一律打码，只保留键名方便排查「是不是没带上」。</summary>
    private static readonly string[] SensitiveKeys =
        ["SESSDATA", "bili_jct", "DedeUserID", "DedeUserID__ckMd5", "sid", "buvid3", "buvid4", "ac_time_value"];

    private readonly AppSettings _settings;
    private readonly object _sync = new();

    public LogService(AppSettings settings)
    {
        _settings = settings;
        LogDirectory = Path.Combine(Path.GetDirectoryName(AppSettings.FilePath)!, "logs");
    }

    public string LogDirectory { get; }

    public void Write(LogLevel level, string message)
    {
        if (!Enabled(level)) return;

        var line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{(int)level}] {Sanitize(message)}");

        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                var path = Path.Combine(LogDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 日志写不进去不能影响下载，静默放弃这一条
            }
        }
    }

    /// <summary>级别阈值：设置里选 Warn 就只记 Warn / Error。</summary>
    private bool Enabled(LogLevel level)
    {
        var threshold = _settings.LogLevel switch
        {
            "Debug" => LogLevel.Debug,
            "Warn" => LogLevel.Warn,
            "Error" => LogLevel.Error,
            _ => LogLevel.Info
        };

        return level >= threshold;
    }

    /// <summary>把 key=value 形式的敏感值换成 ***，键名保留。</summary>
    internal static string Sanitize(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return message;

        var result = message;
        foreach (var key in SensitiveKeys)
        {
            result = Regex.Replace(result, Regex.Escape(key) + @"\s*=\s*[^;\s,""]+",
                key + "=***", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        return result;
    }
}
