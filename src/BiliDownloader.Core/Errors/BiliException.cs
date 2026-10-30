namespace BiliDownloader.Core.Errors;

/// <summary>
/// 错误分类（方案设计 §9）。UI 只认这一份分类来映射中文文案与可操作按钮，
/// 不允许直接把异常堆栈/英文错误码显示给用户（堆栈只进日志）。
/// </summary>
public enum ErrorKind
{
    Unknown,
    AuthExpired,
    VipRequired,
    PaidContent,
    StreamExpired,
    RiskControl,
    Network,
    DiskFull,
    NoPermission,
    PathTooLong,
    FfmpegMissing,
    MergeFailed,
    VideoUnavailable
}

/// <summary>带分类的业务异常：<see cref="UserMessage"/> 可直接展示给用户。</summary>
public sealed class BiliException : Exception
{
    public ErrorKind Kind { get; }

    public string UserMessage { get; }

    public BiliException(ErrorKind kind, string userMessage, Exception? inner = null)
        : base(userMessage, inner)
    {
        Kind = kind;
        UserMessage = userMessage;
    }

    public static BiliException Network(Exception? inner = null) =>
        new(ErrorKind.Network, "网络异常，请检查网络后重试", inner);

    public static BiliException VideoUnavailable(string? detail = null) =>
        new(ErrorKind.VideoUnavailable, detail ?? "视频不存在或已被删除");

    public static BiliException AuthExpired() =>
        new(ErrorKind.AuthExpired, "登录已过期，请重新扫码登录");

    public static BiliException StreamExpired(Exception? inner = null) =>
        new(ErrorKind.StreamExpired, "直链已过期，点重试会重新获取", inner);

    public static BiliException DiskFull(Exception? inner = null) =>
        new(ErrorKind.DiskFull, "磁盘空间不足，请更换保存目录", inner);

    public static BiliException NoPermission(Exception? inner = null) =>
        new(ErrorKind.NoPermission, "目标目录无写入权限，请更换保存目录", inner);

    /// <summary>把底层异常翻译成用户可读的错误，避免漏网的原始异常文案。</summary>
    public static BiliException From(Exception ex) => ex switch
    {
        BiliException bili => bili,
        UnauthorizedAccessException => NoPermission(ex),
        PathTooLongException => new(ErrorKind.PathTooLong, "文件路径过长，请缩短保存路径或文件名", ex),
        IOException io when (io.HResult & 0xFFFF) is 0x70 or 0x27 => DiskFull(ex),
        HttpRequestException => Network(ex),
        TaskCanceledException => StreamExpired(ex),
        _ => new BiliException(ErrorKind.Unknown, "下载失败，请重试", ex)
    };
}
