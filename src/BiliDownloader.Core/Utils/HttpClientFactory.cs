using System;
using System.Net.Http;

namespace BiliDownloader.Core.Utils;

/// <summary>统一的 HttpClient 工厂：浏览器 UA + Referer，绕过基础风控。</summary>
public static class HttpClientFactory
{
    public static HttpClient CreateBiliClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.bilibili.com");
        return client;
    }
}
