using System.Diagnostics;
using System.Text;

namespace BiliDownloader.Core.Utils;

/// <summary>
/// .NET Framework 4.8 缺失的 BCL 成员集中收口。
///
/// 背景：主程序从 net10.0 迁回 net48（运行时由 Windows 自带，不再随包分发 166 MB 的 .NET）。
/// 代码里用到的一批 .NET Core 3.0+ / .NET 5+ API 在 4.8 上不存在，逐点改写成
/// “语义等价 + 名字显眼”的辅助方法，比在几十处散落手写 try/catch 更好维护。
/// 每个方法都注明它等价于哪个 BCL API、以及为什么可以这样等价。
/// </summary>
public static class Net48
{
    // ---------------------------------------------------------------------
    //  Math.Clamp（.NET Core 2.0+）
    // ---------------------------------------------------------------------

    public static int Clamp(int value, int min, int max)
    {
        if (min > max) throw new ArgumentException("min 不能大于 max");

        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    public static long Clamp(long value, long min, long max)
    {
        if (min > max) throw new ArgumentException("min 不能大于 max");

        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    public static double Clamp(double value, double min, double max)
    {
        if (min > max) throw new ArgumentException("min 不能大于 max");

        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    // ---------------------------------------------------------------------
    //  File.ReadAllBytesAsync / WriteAllBytesAsync（.NET Core 2.0+）
    //  net48 的 File 只有同步版本，用 Task.Run 包一层即可：
    //  这两个 API 本身就是“同步 IO 丢到线程池”的实现，语义一致。
    // ---------------------------------------------------------------------

    public static Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct = default)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return File.ReadAllBytes(path);
        }, ct);

    public static Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken ct = default)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            File.WriteAllBytes(path, bytes);
        }, ct);

    // ---------------------------------------------------------------------
    //  File.Move(source, dest, overwrite)（.NET Core 3.0+）
    //  net48 没有 overwrite 参数。用“先删目标再移动”实现，
    //  注意这只在目标已存在时才有额外动作，不存在时行为与原生完全一致。
    // ---------------------------------------------------------------------

    public static void MoveFile(string source, string dest, bool overwrite)
    {
        if (overwrite && File.Exists(dest))
            File.Delete(dest);

        File.Move(source, dest);
    }

    // ---------------------------------------------------------------------
    //  string.Replace(old, new, StringComparison)（.NET Core 2.0+）
    //  net48 的 Replace 只有序数匹配版本。这里用 StringBuilder 手写，
    //  保留调用方指定的比较规则（通常是 OrdinalIgnoreCase）。
    // ---------------------------------------------------------------------

    public static string Replace(string input, string oldValue, string newValue, StringComparison comparison)
    {
        if (string.IsNullOrEmpty(oldValue) || input.Length == 0) return input;

        var index = input.IndexOf(oldValue, comparison);
        if (index < 0) return input;

        var sb = new StringBuilder(input.Length);
        var start = 0;

        while (index >= 0)
        {
            sb.Append(input, start, index - start);
            sb.Append(newValue);
            start = index + oldValue.Length;
            index = start >= input.Length ? -1 : input.IndexOf(oldValue, start, comparison);
        }

        sb.Append(input, start, input.Length - start);
        return sb.ToString();
    }

    // ---------------------------------------------------------------------
    //  Process.WaitForExitAsync / Kill(entireProcessTree)（.NET 5+）
    // ---------------------------------------------------------------------

    /// <summary>等待进程退出；net48 用 TaskCompletionSource 挂 Exited 事件，并在取消时放弃等待。</summary>
    public static Task WaitForExitAsync(Process process, CancellationToken ct = default)
    {
        if (process.HasExited) return Task.CompletedTask;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnExited(object? sender, EventArgs e) => tcs.TrySetResult(true);

        process.EnableRaisingEvents = true;
        process.Exited += OnExited;

        // 订阅与检查之间可能已经退出，补一次检查避免漏事件
        if (process.HasExited) tcs.TrySetResult(true);

        if (!ct.CanBeCanceled)
        {
            return tcs.Task.ContinueWith(t =>
            {
                process.Exited -= OnExited;
            }, TaskScheduler.Default);
        }

        var registration = ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task.ContinueWith(t =>
        {
            registration.Dispose();
            process.Exited -= OnExited;
        }, TaskScheduler.Default);
    }

    /// <summary>杀掉整棵进程树（net48 的 Kill 只管自己，ffmpeg 会派生子进程，必须连根拔）。</summary>
    public static void KillProcessTree(Process process)
    {
        try
        {
            if (process.HasExited) return;

            // 用 taskkill /T 递归终止；cmd 自带，无额外依赖
            using var killer = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/PID {process.Id} /T /F",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            killer?.WaitForExit(3000);
        }
        catch
        {
            // taskkill 不可用就退化成只杀自己，不因为清理失败而抛给上层
            try { process.Kill(); }
            catch { /* 已经退出 */ }
        }
    }

    // ---------------------------------------------------------------------
    //  Parallel.ForEachAsync（.NET 6+）
    //  net48 只有同步的 Parallel.ForEach。这里用 SemaphoreSlim 限流 + WhenAll 实现，
    //  行为对齐：并发上限、取消传播、首个异常向外抛。
    // ---------------------------------------------------------------------

    public static async Task ForEachAsync<T>(
        IEnumerable<T> source,
        int maxDegreeOfParallelism,
        CancellationToken ct,
        Func<T, CancellationToken, Task> body)
    {
        if (maxDegreeOfParallelism < 1) throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism));

        using var gate = new SemaphoreSlim(maxDegreeOfParallelism, maxDegreeOfParallelism);
        var pending = new List<Task>();

        try
        {
            foreach (var item in source)
            {
                ct.ThrowIfCancellationRequested();

                await gate.WaitAsync(ct).ConfigureAwait(false);
                pending.Add(RunOneAsync(item));
            }

            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch
        {
            // 有一个失败就别再启动新的，已在跑的不强行终止（调用方负责取消）
            await Task.WhenAll(pending.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)))
                .ConfigureAwait(false);
            throw;
        }

        async Task RunOneAsync(T item)
        {
            try
            {
                await body(item, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    // ---------------------------------------------------------------------
    //  ProcessStartInfo.ArgumentList（.NET Core 2.1+）
    //  net48 只能给 Arguments 拼一整条命令行，必须自己按 Windows 规则加引号，
    //  否则带空格的路径（"C:\我的 视频\a b.mp4"）会被 ffmpeg 拆成两个参数。
    //  算法与 .NET Core 内部拼接 ArgumentList 时使用的 MSVC 规则一致。
    // ---------------------------------------------------------------------

    public static string BuildCommandLine(IEnumerable<string> args)
    {
        var sb = new StringBuilder();
        var first = true;

        foreach (var arg in args)
        {
            if (!first) sb.Append(' ');
            sb.Append(QuoteArgument(arg ?? string.Empty));
            first = false;
        }

        return sb.ToString();
    }

    private static string QuoteArgument(string arg)
    {
        if (arg.Length == 0) return "\"\"";

        // 不含空白和引号时原样返回，避免无意义加引号
        var needQuote = false;
        foreach (var c in arg)
        {
            if (c is ' ' or '\t' or '\n' or '\v' or '"')
            {
                needQuote = true;
                break;
            }
        }

        if (!needQuote) return arg;

        var sb = new StringBuilder(arg.Length + 8);
        sb.Append('"');
        var backslashes = 0;

        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                // 引号前的反斜杠要翻倍，再加一个转义引号
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
                continue;
            }

            sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(c);
        }

        // 结尾的反斜杠要翻倍，否则会转义掉收尾的引号
        sb.Append('\\', backslashes * 2);
        sb.Append('"');

        return sb.ToString();
    }
}
