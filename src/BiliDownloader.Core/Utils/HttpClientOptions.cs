namespace BiliDownloader.Core.Utils;

/// <summary>代理模式（设置项 ProxyMode）。</summary>
public enum ProxyMode
{
    /// <summary>跟随系统代理（HttpClient 的默认行为）。</summary>
    System,

    /// <summary>不使用代理，直连。</summary>
    None,

    /// <summary>手动指定代理地址。</summary>
    Manual
}

/// <summary>
/// HTTP 客户端参数：代理与请求超时。
/// 由 UI 在启动时按设置组装后传入，Core 不需要认识 AppSettings。
/// </summary>
/// <remarks>
/// 这两项只在创建 HttpClient 时生效，而客户端是单例，所以改设置需要重启程序才能生效。
/// </remarks>
public sealed record HttpClientOptions(
    ProxyMode Proxy = ProxyMode.System,
    string? ProxyAddress = null,
    int TimeoutSeconds = 30);
