namespace BiliDownloader.UI.ViewModels;

public enum LogLevel
{
    /// <summary>最详细。设置页 LogLevel 的阈值比较依赖这个顺序，不要调整成员次序。</summary>
    Debug,
    Info,
    Warn,
    Error
}

/// <summary>日志抽屉的一行。时间等宽、级别着色，正文保持三级灰（视觉规范 §6.8）。</summary>
public sealed class LogEntry
{
    public required string Time { get; init; }
    public required string LevelText { get; init; }
    public required LogLevel Level { get; init; }
    public required string Text { get; init; }
}

/// <summary>数值格式化：界面上的速度 / 大小 / 时长统一走这里，避免各写一套。</summary>
public static class Formatters
{
    public static string Bytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:F0} {units[unit]}" : $"{value:F1} {units[unit]}";
    }

    public static string Speed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "—";
        return Bytes((long)bytesPerSecond) + "/s";
    }

    public static string Duration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return "00:00";
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes:00}:{duration.Seconds:00}";
    }
}
