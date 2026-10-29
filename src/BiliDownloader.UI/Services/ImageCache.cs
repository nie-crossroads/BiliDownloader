using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;

namespace BiliDownloader.UI.Services;

/// <summary>
/// 封面 / 头像加载：统一按显示尺寸解码（视觉规范 §10.6），避免 1920×1080 原图进内存。
/// </summary>
public static class ImageCache
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
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
            var bytes = await Http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
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
