using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BiliDownloader.SetupShared;

/// <summary>
/// 诊断用：把一个**真实显示中**的窗口截图存成 PNG。
///
/// 与离屏 RenderTargetBitmap 不同，这条路径经过 DWM 合成，能如实反映
/// ClearType 抗锯齿与 DPI 缩放效果，用来验证「文字是否清晰」。
/// </summary>
public static class WindowCapture
{
    private const int SrcCopy = 0x00CC0020;
    private const uint PwRenderFullContent = 0x00000002;

    public static void CaptureToPng(Window window, string path)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) throw new InvalidOperationException("窗口句柄为空");
        if (!GetWindowRect(handle, out var rect)) throw new InvalidOperationException("GetWindowRect 失败");

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) throw new InvalidOperationException($"窗口尺寸异常 {width}x{height}");

        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBitmap = CreateCompatibleBitmap(screenDc, width, height);
        var previous = SelectObject(memDc, hBitmap);
        try
        {
            // 优先让窗口自己绘制到 DC：不受其它窗口遮挡影响。
            if (!PrintWindow(handle, memDc, PwRenderFullContent))
            {
                if (!BitBlt(memDc, 0, 0, width, height, screenDc, rect.Left, rect.Top, SrcCopy))
                    throw new InvalidOperationException("PrintWindow 与 BitBlt 均失败");
            }

            // GetDIBits 要求位图未被选入 DC
            SelectObject(memDc, previous);
            previous = IntPtr.Zero;

            var bitmap = ToBitmapSource(memDc, hBitmap, width, height);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memDc, previous);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static BitmapSource ToBitmapSource(IntPtr hdc, IntPtr hBitmap, int width, int height)
    {
        var header = new BitmapInfoHeader
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width,
            Height = -height, // 负值表示自上而下
            Planes = 1,
            BitCount = 32,
            Compression = 0
        };

        var buffer = new byte[width * height * 4];
        if (GetDIBits(hdc, hBitmap, 0, (uint)height, buffer, ref header, 0) == 0)
            throw new InvalidOperationException("GetDIBits 失败");

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, buffer, width * 4);
        source.Freeze();
        return source;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int width, int height,
        IntPtr hdcSrc, int srcX, int srcY, int rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hBitmap, uint start, uint lines,
        byte[] bits, ref BitmapInfoHeader header, uint usage);
}
