using System;
using System.Threading;
using System.Threading.Tasks;

namespace BiliDownloader.Core.Utils;

/// <summary>指数退避重试（限流/412/网络抖动兜底）。</summary>
public static class RetryHelper
{
    public static async Task<T> RetryAsync<T>(Func<CancellationToken, Task<T>> work, int maxAttempts = 3,
                                              int baseDelayMs = 500, CancellationToken ct = default)
    {
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                return await work(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) when (attempt < maxAttempts - 1)
            {
                await Task.Delay(baseDelayMs * (1 << attempt), ct).ConfigureAwait(false);
            }
        }
        return await work(ct).ConfigureAwait(false); // 最后一次不再吞异常
    }
}
