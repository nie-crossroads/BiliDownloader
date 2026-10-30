using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BiliDownloader.SetupShared;

/// <summary>
/// 无边框窗口的圆角处理。
///
/// 背景：WPF 一旦设置 AllowsTransparency=true，会强制使用分层窗口（WS_EX_LAYERED）渲染，
/// 副作用是**禁用 ClearType**（退回灰度抗锯齿）并失去部分硬件加速，在 125%/150% 缩放下
/// 中文小字会明显发虚。所以安装器/卸载器改用 AllowsTransparency=false + WindowChrome，
/// 圆角交给 DWM 处理（Windows 11 的 DWMWA_WINDOW_CORNER_PREFERENCE）。
/// </summary>
public static class WindowChromeHelper
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmcpRound = 2;

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>在窗口 SourceInitialized 之后调用，使标题栏隐藏的窗口获得系统圆角与投影。</summary>
    public static void ApplyRoundedCorners(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var preference = DwmcpRound;
        try
        {
            // Windows 11 生效；Windows 10 及更早版本会返回失败码，窗口保持直角，不影响功能。
            DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // 极老系统无 dwmapi.dll
        }
        catch (EntryPointNotFoundException)
        {
        }
    }
}
