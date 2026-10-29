using System.Windows.Media.Imaging;
using QRCoder;

namespace BiliDownloader.UI.Services;

/// <summary>
/// 二维码渲染：QRCoder 输出 PNG 再解码为 BitmapImage。
/// 不用 Path 拼模块——抗锯齿会把窄模块糊掉（视觉规范 §6.12）。
/// </summary>
public static class QrImageRenderer
{
    public static BitmapImage? Render(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(8);
            var image = ImageCache.FromBytes(png, 336);
            return image;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
