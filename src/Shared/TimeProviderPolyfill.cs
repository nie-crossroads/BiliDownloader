// net48 没有 System.TimeProvider（.NET 8 引入，BCL 自带）。
// 业务代码只用到 GetLocalNow / GetUtcNow，这里补一个最小可用实现，
// 好处是 BiliApiClient 的构造函数签名和 UI 的 DI 注册都能原样保留。
//
// 只由 BiliDownloader.Core 编译（UI 通过项目引用拿到这个类型）。
// 如果 UI 也 Link 本文件，会生成第二份 public TimeProvider，
// 与引用程序集里的同名类型冲突（CS0436），所以不要两边都加。
//
// ReSharper disable once CheckNamespace
namespace System
{
    /// <summary>时间的抽象来源，便于测试注入固定时钟。</summary>
    public abstract class TimeProvider
    {
        private static readonly TimeProvider SystemInstance = new SystemTimeProvider();

        /// <summary>进程默认时钟。</summary>
        public static TimeProvider System => SystemInstance;

        /// <summary>UTC 当前时间。</summary>
        public virtual DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;

        /// <summary>本地当前时间（用于 WBI 签名密钥的「按天轮换」判断）。</summary>
        public virtual DateTimeOffset GetLocalNow() => DateTimeOffset.Now;

        public virtual TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;

        public virtual long TimestampFrequency => global::System.Diagnostics.Stopwatch.Frequency;

        public virtual long GetTimestamp() => global::System.Diagnostics.Stopwatch.GetTimestamp();

        private sealed class SystemTimeProvider : TimeProvider
        {
        }
    }
}
