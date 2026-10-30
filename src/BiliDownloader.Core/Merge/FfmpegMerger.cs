using BiliDownloader.Core.Errors;
using BiliDownloader.Core.Utils;

namespace BiliDownloader.Core.Merge;

public sealed record MergeRequest(string VideoPath, string AudioPath, string OutputPath, TimeSpan Duration);

public sealed record MergeProgress(double Percent);

/// <summary>音视频合并（FFmpeg -c copy 封装，带真实进度、可取消）。</summary>
public interface IFfmpegMerger
{
    bool IsAvailable { get; }

    Task MergeAsync(MergeRequest request, IProgress<MergeProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>调用内嵌 / 外部 ffmpeg 进程。</summary>
public sealed class FfmpegMerger : IFfmpegMerger
{
    private readonly Func<string?> _ffmpegPathProvider;

    public FfmpegMerger(Func<string?> ffmpegPathProvider) =>
        _ffmpegPathProvider = ffmpegPathProvider ?? throw new ArgumentNullException(nameof(ffmpegPathProvider));

    public bool IsAvailable
    {
        get
        {
            var path = _ffmpegPathProvider();
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }
    }

    public async Task MergeAsync(MergeRequest request, IProgress<MergeProgress>? progress = null,
        CancellationToken ct = default)
    {
        var ffmpeg = _ffmpegPathProvider();
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
            throw new BiliException(ErrorKind.FfmpegMissing, "未找到 FFmpeg，请在设置里指定路径");

        if (!File.Exists(request.VideoPath))
            throw new BiliException(ErrorKind.MergeFailed, "合并失败：视频流文件不存在");

        Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath)!);
        if (File.Exists(request.OutputPath)) File.Delete(request.OutputPath);

        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y",
            "-i", request.VideoPath
        };
        if (File.Exists(request.AudioPath))
        {
            args.AddRange(["-i", request.AudioPath]);
        }
        args.AddRange(["-c", "copy", "-movflags", "+faststart", "-progress", "pipe:1", "-nostats",
            request.OutputPath]);

        var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // net48 没有 ArgumentList，按 Windows 命令行规则自行加引号（路径含空格时必须）
        psi.Arguments = Net48.BuildCommandLine(args);

        using var process = new System.Diagnostics.Process { StartInfo = psi };
        var errorTail = new Queue<string>();

        process.OutputDataReceived += (_, e) =>
        {
            var line = e.Data;
            if (string.IsNullOrWhiteSpace(line)) return;

            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                long.TryParse(line.Substring("out_time_us=".Length), out var micro) &&
                request.Duration.TotalSeconds > 0)
            {
                var percent = Net48.Clamp(micro / 1_000_000.0 / request.Duration.TotalSeconds, 0, 1);
                progress?.Report(new MergeProgress(percent * 100));
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            errorTail.Enqueue(e.Data);
            if (errorTail.Count > 20) errorTail.Dequeue();
        };

        if (!process.Start())
            throw new BiliException(ErrorKind.MergeFailed, "无法启动 FFmpeg");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await Net48.WaitForExitAsync(process, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            var tail = string.Join(Environment.NewLine, errorTail);
            throw new BiliException(ErrorKind.MergeFailed, "合并失败，原始文件已保留", new Exception(tail));
        }

        if (!File.Exists(request.OutputPath))
            throw new BiliException(ErrorKind.MergeFailed, "合并失败：没有生成输出文件");

        progress?.Report(new MergeProgress(100));
    }

    private static void TryKill(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited) Net48.KillProcessTree(process);
        }
        catch (InvalidOperationException)
        {
            // 已退出
        }
    }
}
