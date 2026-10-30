using System.Diagnostics;

namespace BiliDownloader.Core.Download;

/// <summary>
/// 全局限速器（设置项 GlobalSpeedLimitKbps）。所有下载流共享同一个预算，
/// 所以并发分片 + 多任务同时跑也不会超过用户设的上限。
/// </summary>
public interface ISpeedLimiter
{
    /// <summary>当前限速（字节/秒），0 表示不限速。</summary>
    long BytesPerSecond { get; }

    /// <summary>运行时改限速；设置页改动即时生效，不需要重启。</summary>
    void Update(long bytesPerSecond);

    /// <summary>写入 bytes 字节后调用，超预算就等到配额恢复。</summary>
    Task ConsumeAsync(int bytes, CancellationToken ct = default);
}

/// <summary>
/// 令牌桶限速。桶容量取 1 秒的量：允许短时间突发，但长期平均不超过限速。
/// 超支不阻塞——记账成负数，按欠账折算等待时间，等完再继续写。
/// </summary>
public sealed class TokenBucketSpeedLimiter : ISpeedLimiter
{
    private readonly object _sync = new();
    private long _bytesPerSecond;
    private double _tokens;
    private long _lastTicks = Stopwatch.GetTimestamp();

    public TokenBucketSpeedLimiter(long bytesPerSecond = 0)
    {
        _bytesPerSecond = Math.Max(0, bytesPerSecond);
        _tokens = Capacity;
    }

    /// <summary>1 秒的量。速率为 0（不限速）时用 1，避免除零。</summary>
    private long Capacity => Math.Max(1, _bytesPerSecond);

    public long BytesPerSecond
    {
        get { lock (_sync) return _bytesPerSecond; }
    }

    public void Update(long bytesPerSecond)
    {
        lock (_sync)
        {
            var next = Math.Max(0, bytesPerSecond);
            if (next == _bytesPerSecond) return;

            // 换速率时把桶补满：否则旧速率残留的欠账会继续拖慢新速率。
            _bytesPerSecond = next;
            _tokens = Capacity;
            _lastTicks = Stopwatch.GetTimestamp();
        }
    }

    public async Task ConsumeAsync(int bytes, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (_bytesPerSecond <= 0 || bytes <= 0) return;

            RefillLocked();
            _tokens -= bytes;
        }

        // 分段等待直到欠账补齐，同时及时响应运行时修改速率与取消。
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int waitMs;
            lock (_sync)
            {
                if (_bytesPerSecond <= 0) return;
                RefillLocked();
                if (_tokens >= 0) return;
                waitMs = (int)Math.Clamp(Math.Ceiling(-_tokens * 1000.0 / _bytesPerSecond), 1, 100);
            }
            await Task.Delay(waitMs, ct).ConfigureAwait(false);
        }
    }

    private void RefillLocked()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsedMs = (now - _lastTicks) * 1000.0 / Stopwatch.Frequency;
        _lastTicks = now;
        if (elapsedMs <= 0) return;

        _tokens = Math.Min(Capacity, _tokens + elapsedMs * _bytesPerSecond / 1000.0);
    }
}
