using System;
using System.Net;
using System.Net.Http;

namespace BiliDownloader.Core.Utils;

/// <summary>统一的 HttpClient 工厂：浏览器 UA + Referer，绕过基础风控。</summary>
public static class HttpClientFactory
{
    public static HttpClient CreateBiliClient(HttpClientOptions? options = null)
    {
        options ??= new HttpClientOptions();

        // net48 没有 SocketsHttpHandler（那是 .NET Core 2.1+ 的），退回 HttpClientHandler。
        // 原配置的 PooledConnectionLifetime = 2 分钟，在 net48 上对应 ServicePoint 的空闲回收上限，
        // 通过 ServicePointManager 全局设置近似等价。
        ServicePointManager.MaxServicePointIdleTime = (int)TimeSpan.FromMinutes(2).TotalMilliseconds;

        // .NET Framework 4.8 默认值偏保守，显式打开 TLS 1.2，否则 B 站接口可能握手失败。
        // （TLS 1.3 在 4.8 上没有对应枚举值，只能交给系统在 Tls12 之上的协商）
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true
        };

        switch (options.Proxy)
        {
            case ProxyMode.None:
                handler.UseProxy = false;
                handler.Proxy = null;
                break;

            case ProxyMode.Manual when !string.IsNullOrWhiteSpace(options.ProxyAddress):
                handler.Proxy = new WebProxy(options.ProxyAddress.Trim(), BypassOnLocal: true);
                handler.UseProxy = true;
                break;

            default:
                // System：不设置就用 HttpClient.DefaultProxy，跟随系统代理设置。
                break;
        }

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Net48.Clamp(options.TimeoutSeconds, 5, 300))
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.bilibili.com");
        return client;
    }
}
