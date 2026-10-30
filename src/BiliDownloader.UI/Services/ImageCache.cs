using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;

namespace BiliDownloader.UI.Services;

/// <summary>
/// 封面 / 头像加载：统一按显示尺寸解码（视觉规范 §10.6），避免 1920×1080 原图进内存。
/// </summary>
public static class ImageCache
{
    // net48 没有 SocketsHttpHandler；连接回收交给 ServicePointManager 的全局空闲上限
    private static readonly HttpClient Http = new(new HttpClientHandler())
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    static ImageCache()
    {
        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        Http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.bilibili.com");
    }

    public static async Task<BitmapImage?> LoadAsync(string? url, int decodePixelWidth, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        try
        {
            // net48 的 GetByteArrayAsync 没有 CancellationToken 重载：
            // 用 GetAsync(带 ct) 取到响应头，再读字节（此时数据已在管道里）
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            return FromBytes(bytes, decodePixelWidth);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
    }

    public static BitmapImage? FromBytes(byte[] bytes, int decodePixelWidth)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = decodePixelWidth;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
