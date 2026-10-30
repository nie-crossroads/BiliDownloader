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

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
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
            Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 5, 300))
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.bilibili.com");
        return client;
    }
}
