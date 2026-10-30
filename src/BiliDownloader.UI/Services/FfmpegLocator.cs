using System.IO;

namespace BiliDownloader.UI.Services;

/// <summary>
/// FFmpeg 定位：优先程序目录 tools/ffmpeg，其次程序目录，最后 PATH。
/// 找不到返回 null（不抛异常）——启动时不该因为缺 FFmpeg 就崩，只有合并那一步报 FfmpegMissing。
/// </summary>
public static class FfmpegLocator
{
    public static string? Find(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return Path.GetFullPath(overridePath);

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "ffmpeg", "ffmpeg.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        return FindOnPath("ffmpeg.exe");
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        foreach (var dir in path.Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), fileName);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch (ArgumentException)
            {
                // PATH 里有非法字符，跳过
            }
        }

        return null;
    }
}
