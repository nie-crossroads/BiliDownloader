using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IconForge;

// ---------------------------------------------------------------------------
// 规格模型：一份 JSON 描述"母版"和"小尺寸手工版"两套几何，本工具负责光栅化 + 打包 .ico
// ---------------------------------------------------------------------------

public sealed class GradientSpec
{
    /// <summary>渐变起始色（CSS 角度 0deg 指向正上方的"起点"侧）。</summary>
    public string From { get; set; } = "#FF8CAD";
    public string To { get; set; } = "#F0578A";
    /// <summary>CSS linear-gradient 角度：0=向上，顺时针递增。160 表示"上偏右 → 下偏左"。</summary>
    public double AngleDeg { get; set; } = 160;
}

public sealed class LayerSpec
{
    /// <summary>roundrect | path | ellipse</summary>
    public string Kind { get; set; } = "path";

    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public double R { get; set; }

    public double? Cx { get; set; }
    public double? Cy { get; set; }
    public double? Rx { get; set; }
    public double? Ry { get; set; }

    public string? D { get; set; }
    public string? Fill { get; set; }
    /// <summary>描边宽度（viewBox 单位），仅 path 有效。</summary>
    public double? Stroke { get; set; }
    public string? StrokeFill { get; set; }
    public double? Opacity { get; set; }
    public GradientSpec? Gradient { get; set; }
}

public sealed class IconSpec
{
    public double ViewBox { get; set; } = 256;
    public List<LayerSpec> Layers { get; set; } = [];
}

public sealed class RootSpec
{
    public List<int> Sizes { get; set; } = [];
    public Dictionary<int, string> SizeMap { get; set; } = [];
    public string DefaultVariant { get; set; } = "master";
    public string OutputIco { get; set; } = "app.ico";
    /// <summary>额外把最大尺寸的 PNG 复制到该路径 —— Window.Icon 需要单张位图，给 ico 会取到首帧（16px）导致任务栏图标被拉伸。</summary>
    public string? OutputIconPng { get; set; }
    public string OutputPngDir { get; set; } = "png";
    public List<int> DumpAscii { get; set; } = [];
    public Dictionary<string, IconSpec> Variants { get; set; } = [];
}

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "capture")
            return CaptureWindow(int.Parse(args[1]), Path.GetFullPath(args[2]));

        if (args.Length >= 2 && args[0] == "focus")
            return FocusWindow(int.Parse(args[1]), args.Length > 2 ? args[2..] : []);

        if (args.Length >= 2 && args[0] == "info")
            return QueryWindow(int.Parse(args[1]));

        if (args.Length >= 3 && args[0] == "click")
            return ClickAt(int.Parse(args[1]), int.Parse(args[2]), args.Length > 3 && args[3] == "keep");

        if (args.Length >= 8 && args[0] == "crop")
            return CropImage(Path.GetFullPath(args[1]), int.Parse(args[2]), int.Parse(args[3]),
                int.Parse(args[4]), int.Parse(args[5]), int.Parse(args[6]), Path.GetFullPath(args[7]));

        if (args.Length >= 5 && args[0] == "zoom")
            return RenderZoom(Path.GetFullPath(args[1]), int.Parse(args[2]), int.Parse(args[3]), Path.GetFullPath(args[4]));

        if (args.Length >= 2 && args[0] == "exeicon")
            return VerifyExeIcon(Path.GetFullPath(args[1]));

        if (args.Length >= 2 && args[0] == "verify")
            return VerifyIco(Path.GetFullPath(args[1]));

        if (args.Length >= 3 && args[0] == "sheet")
            return RenderSheet(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));

        if (args.Length < 2 || args[0] != "render")
        {
            Console.Error.WriteLine("usage: IconForge render <spec.json>");
            Console.Error.WriteLine("       IconForge sheet  <spec.json> <out.png>");
            return 2;
        }

        var specPath = Path.GetFullPath(args[1]);
        var baseDir = Path.GetDirectoryName(specPath)!;
        var json = File.ReadAllText(specPath);
        var root = JsonSerializer.Deserialize<RootSpec>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        }) ?? throw new InvalidOperationException("spec 解析失败");

        var pngDir = Path.GetFullPath(Path.Combine(baseDir, root.OutputPngDir));
        Directory.CreateDirectory(pngDir);

        var entries = new List<(int Size, byte[] Data)>();
        var report = new StringBuilder();

        foreach (var size in root.Sizes)
        {
            var variantName = root.SizeMap.TryGetValue(size, out var v) ? v : root.DefaultVariant;
            if (!root.Variants.TryGetValue(variantName, out var variant))
                throw new InvalidOperationException($"缺少 variant: {variantName}");

            var rtb = Render(variant, size);
            var png = EncodePng(rtb);
            File.WriteAllBytes(Path.Combine(pngDir, $"app-{size}.png"), png);

            if (size == root.Sizes.Max() && !string.IsNullOrWhiteSpace(root.OutputIconPng))
            {
                var pngPath = Path.GetFullPath(Path.Combine(baseDir, root.OutputIconPng));
                Directory.CreateDirectory(Path.GetDirectoryName(pngPath)!);
                File.WriteAllBytes(pngPath, png);
            }

            // 小尺寸塞 DIB（老 shell 兼容更好），大尺寸塞 PNG
            var payload = size <= 64 ? EncodeDib(rtb) : png;
            entries.Add((size, payload));

            report.AppendLine($"  {size,3}px  variant={variantName,-7} payload={(size <= 64 ? "DIB" : "PNG"),-4} {payload.Length,7} B");
        }

        var icoPath = Path.GetFullPath(Path.Combine(baseDir, root.OutputIco));
        Directory.CreateDirectory(Path.GetDirectoryName(icoPath)!);
        WriteIco(icoPath, entries);
        report.AppendLine();
        report.AppendLine($"  -> {icoPath} ({new FileInfo(icoPath).Length} B)");

        foreach (var size in root.DumpAscii)
        {
            var variantName = root.SizeMap.TryGetValue(size, out var v) ? v : root.DefaultVariant;
            report.AppendLine();
            report.AppendLine($"=== {size}px pixel map (variant={variantName}) ===");
            report.AppendLine(DumpAscii(Render(root.Variants[variantName], size), size));
        }

        Console.Write(report.ToString());
        return 0;
    }

    // ------------------------------------------------------------ 窗口前台化
    // 只是为了让「启动项目看看」时窗口不被其它最大化程序挡住；不属于图标链路。

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);

    /// <summary>窗口是否被 DWM "cloak"（典型原因：它在另一个虚拟桌面，或窗口挂在已隐藏的 shell 上）。</summary>
    private static bool IsCloaked(IntPtr h)
    {
        const int DWMWA_CLOAKED = 14;
        return DwmGetWindowAttribute(h, DWMWA_CLOAKED, out var v, sizeof(int)) == 0 && v != 0;
    }

    private static int FocusWindow(int pid, string[] rest)
    {
        var proc = System.Diagnostics.Process.GetProcessById(pid);
        var h = proc.MainWindowHandle;
        if (h == IntPtr.Zero)
        {
            Console.Error.WriteLine($"pid {pid} 没有主窗口句柄");
            return 1;
        }

        Console.Error.WriteLine($"before: iconic={IsIconic(h)} visible={IsWindowVisible(h)} cloaked={IsCloaked(h)}");
        ShowWindow(h, 9); // SW_RESTORE（无论是否最小化都调一次，幂等）
        ShowWindow(h, 5); // SW_SHOW
        ShowWindow(h, 1); // SW_SHOWNORMAL：会同时把窗口提到 z 序顶层

        // 可选：focus <pid> <x> <y> <w> <h> —— 挪到指定位置并短暂置顶，方便在别的窗口全屏时截图核对
        if (rest.Length >= 4)
        {
            // 第 5 个参数写 top 才置顶。默认用 NOTOPMOST：临时置顶的窗口会一直压着用户的其他程序，
            // 调试完必须还原，否则等于给用户留了个"顽固的置顶窗"。
            var after = rest.Length >= 5 && rest[4] == "top" ? HWND_TOPMOST : HWND_NOTOPMOST;
            var moved = SetWindowPos(h, after, int.Parse(rest[0]), int.Parse(rest[1]),
                int.Parse(rest[2]), int.Parse(rest[3]), SWP_SHOWWINDOW);
            Console.Error.WriteLine($"setWindowPos(after={after})={moved} err={Marshal.GetLastWin32Error()}");
        }

        // 前台进程才有权调 SetForegroundWindow，这里挂到当前前台线程上借权。
        var fg = GetForegroundWindow();
        var fgThread = GetWindowThreadProcessId(fg, out _);
        var myThread = GetWindowThreadProcessId(h, out _);
        AttachThreadInput(fgThread, myThread, true);
        BringWindowToTop(h);
        var ok = SetForegroundWindow(h);
        AttachThreadInput(fgThread, myThread, false);

        Console.WriteLine($"pid={pid} hwnd=0x{h.ToInt64():X} setForeground={ok} iconic={IsIconic(h)} title={proc.MainWindowTitle}");
        return ok ? 0 : 1;
    }

    // ------------------------------------------------------------ 窗口样式诊断
    // info <pid>：打印扩展样式与 z 序邻居，用来判断「窗口是不是被设成了置顶」。

    private const int GWL_EXSTYLE = -20;
    private const uint GW_HWNDPREV = 3;   // z 序上「在它上面」的窗口
    private const uint GW_HWNDNEXT = 2;   // z 序上「在它下面」的窗口
    private const long WS_EX_TOPMOST = 0x00000008;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_APPWINDOW = 0x00040000;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const long WS_EX_LAYERED = 0x00080000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr hWnd);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder buf, int max);

    private static int QueryWindow(int pid)
    {
        var proc = System.Diagnostics.Process.GetProcessById(pid);
        var h = proc.MainWindowHandle;
        if (h == IntPtr.Zero)
        {
            Console.Error.WriteLine($"pid {pid} 没有主窗口句柄");
            return 1;
        }

        var ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        Console.WriteLine($"pid={pid} hwnd=0x{h.ToInt64():X}");
        Console.WriteLine($"title        = {proc.MainWindowTitle}");
        Console.WriteLine($"class        = {ClassName(h)}");
        GetWindowRect(h, out var wr);
        Console.WriteLine($"rect         = ({wr.Left},{wr.Top})-({wr.Right},{wr.Bottom})  {wr.Right - wr.Left}x{wr.Bottom - wr.Top}");
        Console.WriteLine($"exstyle      = 0x{ex:X8}");
        Console.WriteLine($"  TO PMOST      {(ex & WS_EX_TOPMOST) != 0}   <- true 就是被钉在最上层");
        Console.WriteLine($"  TOOLWINDOW    {(ex & WS_EX_TOOLWINDOW) != 0}");
        Console.WriteLine($"  APPWINDOW     {(ex & WS_EX_APPWINDOW) != 0}");
        Console.WriteLine($"  NOACTIVATE    {(ex & WS_EX_NOACTIVATE) != 0}");
        Console.WriteLine($"  LAYERED       {(ex & WS_EX_LAYERED) != 0}");
        Console.WriteLine($"cloaked      = {IsCloaked(h)}  iconic={IsIconic(h)}  visible={IsWindowVisible(h)}");
        Console.WriteLine($"foreground   = 0x{GetForegroundWindow().ToInt64():X}  (mine={(GetForegroundWindow() == h)})");
        Console.WriteLine($"z-above      = {Describe(GetWindow(h, GW_HWNDPREV))}");
        Console.WriteLine($"z-below      = {Describe(GetWindow(h, GW_HWNDNEXT))}");

        // 从最顶层往下数，看自己排第几个可见窗口 —— 这是「是否真的压在别人上面」的直接证据
        Console.WriteLine("--- z-order (top -> bottom, visible only)");
        var cur = GetTopWindow(IntPtr.Zero);
        var rank = 0;
        var self = -1;
        while (cur != IntPtr.Zero && rank < 25)
        {
            if (IsWindowVisible(cur))
            {
                GetWindowThreadProcessId(cur, out var p);
                if ((int)p == pid && self < 0) self = rank;
                Console.WriteLine($"  #{rank,-2} pid={p,-6} {ClassName(cur),-28} {((int)p == pid ? "<== 本窗口" : "")}");
                rank++;
            }
            cur = GetWindow(cur, GW_HWNDNEXT);
        }
        Console.WriteLine($"本窗口在可见顶层窗口里排第 {self} 位（0 = 最顶层）");

        // 该进程所有顶层窗口：揪出「看不见但压在最上层」的隐藏窗口
        Console.WriteLine($"--- 进程 {pid} 的全部顶层窗口");
        EnumWindows((w, _) =>
        {
            GetWindowThreadProcessId(w, out var wp);
            if ((int)wp == pid)
            {
                var wex = GetWindowLongPtr(w, GWL_EXSTYLE).ToInt64();
                Console.WriteLine($"  0x{w.ToInt64():X} {ClassName(w),-26} visible={IsWindowVisible(w),-5} " +
                                  $"topmost={(wex & WS_EX_TOPMOST) != 0,-5} ex=0x{wex:X8}");
            }
            return true;
        }, IntPtr.Zero);
        return 0;
    }

    // click <x> <y>：发一次真实鼠标左键点击（走用户输入路径，和手工点窗口等价）。
    // 默认点完把指针还原，避免扰乱用户鼠标。
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    private static int ClickAt(int x, int y, bool keepCursor)
    {
        GetCursorPos(out var old);
        SetCursorPos(x, y);
        Thread.Sleep(80);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(40);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(120);
        if (!keepCursor) SetCursorPos(old.X, old.Y);

        var fg = GetForegroundWindow();
        Console.WriteLine($"clicked ({x},{y})  foreground=0x{fg.ToInt64():X}  cursor={(keepCursor ? "kept" : "restored")}");
        return 0;
    }

    private static string ClassName(IntPtr h)
    {
        var sb = new System.Text.StringBuilder(256);
        return GetClassName(h, sb, sb.Capacity) > 0 ? sb.ToString() : "(?)";
    }

    private static string Describe(IntPtr h)
    {
        if (h == IntPtr.Zero) return "(none)";
        GetWindowThreadProcessId(h, out var pid);
        string title = "";
        try { title = System.Diagnostics.Process.GetProcessById((int)pid).MainWindowTitle; }
        catch (ArgumentException) { }
        return $"0x{h.ToInt64():X} pid={pid} class={ClassName(h)} title=\"{title}\"";
    }

    // ----------------------------------------------------- 直接抓窗口（不受遮挡影响）
    // PrintWindow(PW_RENDERFULLCONTENT) 走 DWM，被别的窗口压住也能拿到内容，比全屏截图可靠。

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);

    private static int CaptureWindow(int pid, string outPath)
    {
        var proc = System.Diagnostics.Process.GetProcessById(pid);
        var h = proc.MainWindowHandle;
        if (h == IntPtr.Zero) { Console.Error.WriteLine("没有主窗口句柄"); return 1; }
        if (!GetWindowRect(h, out var r)) { Console.Error.WriteLine("GetWindowRect 失败"); return 1; }

        var w = r.Right - r.Left;
        var hgt = r.Bottom - r.Top;
        if (w <= 0 || hgt <= 0) { Console.Error.WriteLine($"窗口尺寸异常 {w}x{hgt}"); return 1; }

        var screenDc = GetWindowDC(h);
        var memDc = CreateCompatibleDC(screenDc);
        var bmp = CreateCompatibleBitmap(screenDc, w, hgt);
        var old = SelectObject(memDc, bmp);
        var ok = PrintWindow(h, memDc, 2 /* PW_RENDERFULLCONTENT */);
        SelectObject(memDc, old);

        var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
            bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        DeleteObject(bmp);
        DeleteDC(memDc);
        ReleaseDC(h, screenDc);

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using (var fs = File.Create(outPath)) enc.Save(fs);

        Console.WriteLine($"capture printWindow={ok} size={w}x{hgt} rect=({r.Left},{r.Top}) -> {outPath}");
        return 0;
    }

    // ------------------------------------------------------------------ 渲染

    private static RenderTargetBitmap Render(IconSpec spec, int size)
    {
        var scale = size / spec.ViewBox;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            foreach (var layer in spec.Layers)
                DrawLayer(dc, layer);
            dc.Pop();
        }

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private static void DrawLayer(DrawingContext dc, LayerSpec layer)
    {
        var opacity = layer.Opacity ?? 1.0;
        Brush? fill = layer.Gradient is not null
            ? BuildGradient(layer)
            : ParseBrush(layer.Fill);
        if (fill is not null && opacity < 1.0)
            fill.Opacity = opacity;

        Pen? pen = null;
        if (layer.Stroke is > 0)
        {
            pen = new Pen(ParseBrush(layer.StrokeFill ?? layer.Fill ?? "#FFFFFF")!, layer.Stroke.Value)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
        }

        switch (layer.Kind)
        {
            case "roundrect":
                dc.DrawRoundedRectangle(fill, pen,
                    new Rect(layer.X, layer.Y, layer.W, layer.H),
                    layer.R, layer.R);
                break;
            case "ellipse":
                dc.DrawEllipse(fill, pen,
                    new Point(layer.Cx ?? 0, layer.Cy ?? 0),
                    layer.Rx ?? 0, layer.Ry ?? 0);
                break;
            default:
                if (string.IsNullOrWhiteSpace(layer.D)) break;
                var geo = Geometry.Parse(layer.D);
                geo.Freeze();
                dc.DrawGeometry(fill, pen, geo);
                break;
        }
    }

    /// <summary>把 CSS 角度（0=上，顺时针）转成覆盖整个图层的线性渐变。</summary>
    private static Brush BuildGradient(LayerSpec layer)
    {
        var g = layer.Gradient!;
        var w = layer.Kind == "roundrect" ? layer.W : layer.Kind == "ellipse" ? (layer.Rx ?? 1) * 2 : 1;
        var h = layer.Kind == "roundrect" ? layer.H : layer.Kind == "ellipse" ? (layer.Ry ?? 1) * 2 : 1;
        var ox = layer.Kind == "roundrect" ? layer.X : layer.Kind == "ellipse" ? (layer.Cx ?? 0) - (layer.Rx ?? 0) : 0;
        var oy = layer.Kind == "roundrect" ? layer.Y : layer.Kind == "ellipse" ? (layer.Cy ?? 0) - (layer.Ry ?? 0) : 0;

        var theta = g.AngleDeg * Math.PI / 180.0;
        var dx = Math.Sin(theta);
        var dy = -Math.Cos(theta);

        var len = Math.Abs(w * Math.Sin(theta)) + Math.Abs(h * Math.Cos(theta));
        if (len < 1e-6) len = Math.Max(w, h);

        var cx = ox + w / 2.0;
        var cy = oy + h / 2.0;

        var sx = cx - dx * len / 2.0;
        var sy = cy - dy * len / 2.0;
        var ex = cx + dx * len / 2.0;
        var ey = cy + dy * len / 2.0;

        return new LinearGradientBrush(ParseColor(g.From), ParseColor(g.To),
            new Point((sx - ox) / w, (sy - oy) / h),
            new Point((ex - ox) / w, (ey - oy) / h));
    }

    private static Brush? ParseBrush(string? hex) =>
        string.IsNullOrWhiteSpace(hex) ? null : new SolidColorBrush(ParseColor(hex));

    private static Color ParseColor(string hex)
    {
        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 3)
            hex = string.Concat(hex.Select(c => $"{c}{c}"));
        byte a = 255, r, g, b;
        if (hex.Length == 8)
        {
            a = byte.Parse(hex[..2], NumberStyles.HexNumber);
            hex = hex[2..];
        }
        r = byte.Parse(hex[..2], NumberStyles.HexNumber);
        g = byte.Parse(hex[2..4], NumberStyles.HexNumber);
        b = byte.Parse(hex[4..6], NumberStyles.HexNumber);
        return Color.FromArgb(a, r, g, b);
    }

    // ------------------------------------------------------------------ 编码

    private static byte[] EncodePng(BitmapSource bmp)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static byte[] ReadStraightBgra(BitmapSource bmp, out int w, out int h)
    {
        w = bmp.PixelWidth;
        h = bmp.PixelHeight;
        var premul = new byte[w * h * 4];
        bmp.CopyPixels(premul, w * 4, 0);

        var straight = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            int b = premul[i * 4], g = premul[i * 4 + 1], r = premul[i * 4 + 2], a = premul[i * 4 + 3];
            if (a == 0) continue;
            if (a != 255)
            {
                b = Math.Min(255, b * 255 / a);
                g = Math.Min(255, g * 255 / a);
                r = Math.Min(255, r * 255 / a);
            }
            straight[i * 4] = (byte)b;
            straight[i * 4 + 1] = (byte)g;
            straight[i * 4 + 2] = (byte)r;
            straight[i * 4 + 3] = (byte)a;
        }
        return straight;
    }

    /// <summary>32bpp BGRA DIB（bottom-up）+ 全零 AND mask。</summary>
    private static byte[] EncodeDib(BitmapSource bmp)
    {
        var px = ReadStraightBgra(bmp, out var w, out var h);
        var maskRow = (w + 31) / 32 * 4;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, true);
        bw.Write(40);            // biSize
        bw.Write(w);             // biWidth
        bw.Write(h * 2);         // biHeight（XOR + AND）
        bw.Write((short)1);      // biPlanes
        bw.Write((short)32);     // biBitCount
        bw.Write(0);             // BI_RGB
        bw.Write(w * h * 4);     // biSizeImage
        bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);

        for (var y = h - 1; y >= 0; y--)
            bw.Write(px, y * w * 4, w * 4);

        var zero = new byte[maskRow];
        for (var y = 0; y < h; y++)
            bw.Write(zero, 0, maskRow);

        bw.Flush();
        return ms.ToArray();
    }

    private static void WriteIco(string path, List<(int Size, byte[] Data)> items)
    {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs, Encoding.UTF8, true);

        bw.Write((short)0);              // reserved
        bw.Write((short)1);              // type = icon
        bw.Write((short)items.Count);

        var offset = 6 + 16 * items.Count;
        foreach (var (size, data) in items)
        {
            bw.Write((byte)(size >= 256 ? 0 : size));
            bw.Write((byte)(size >= 256 ? 0 : size));
            bw.Write((byte)0);           // colorCount
            bw.Write((byte)0);           // reserved
            bw.Write((short)1);          // planes
            bw.Write((short)32);         // bitCount
            bw.Write(data.Length);
            bw.Write(offset);
            offset += data.Length;
        }

        foreach (var (_, data) in items)
            bw.Write(data);

        bw.Flush();
    }

    // ------------------------------------------------------------------ 校验

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct BitmapInfo2
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out IconInfo info);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr h, int size, out BitmapInfo2 info);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);

    /// <summary>exe 的图标资源是否真的嵌进去了（走 shell 的同一套读取路径）。</summary>
    private static int VerifyExeIcon(string path)
    {
        var large = new IntPtr[1];
        var small = new IntPtr[1];
        var count = ExtractIconEx(path, -1, null, null, 0); // 先问共有几个
        uint got = ExtractIconEx(path, 0, large, small, 1);

        Console.WriteLine($"{Path.GetFileName(path)}  iconGroups={count}  extracted={got}");
        foreach (var (h, label) in new[] { (large[0], "large"), (small[0], "small") })
        {
            if (h == IntPtr.Zero) { Console.WriteLine($"  {label}: none"); continue; }
            if (GetIconInfo(h, out var info) && info.hbmColor != IntPtr.Zero
                && GetObject(info.hbmColor, System.Runtime.InteropServices.Marshal.SizeOf<BitmapInfo2>(), out var bmp) > 0)
            {
                Console.WriteLine($"  {label}: {bmp.bmWidth}x{bmp.bmHeight} {bmp.bmBitsPixel}bpp");
                DeleteObject(info.hbmColor);
            }
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
            DestroyIcon(h);
        }
        foreach (var h in small) if (h != IntPtr.Zero) DestroyIcon(h);
        return got > 0 ? 0 : 1;
    }


    /// <summary>用 WPF 的 IconBitmapDecoder 读 ico —— 与 Window.Icon 的实际加载路径一致。</summary>
    private static int VerifyIco(string path)
    {
        using var fs = File.OpenRead(path);
        var dec = new IconBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Console.WriteLine($"{Path.GetFileName(path)}  frames={dec.Frames.Count}  {new FileInfo(path).Length} B");
        foreach (var frame in dec.Frames)
            Console.WriteLine($"  {frame.PixelWidth,3}x{frame.PixelHeight,-3} {frame.Format} dpi={frame.DpiX:0}");
        return dec.Frames.Count > 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ 放大导出

    /// <summary>裁剪截图的一块并放大，用来核对窗口内小尺寸元素（标题栏标志之类）。</summary>
    private static int CropImage(string inPath, int x, int y, int w, int h, int scale, string outPath)
    {
        using var fs = File.OpenRead(inPath);
        var src = new PngBitmapDecoder(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var cropped = new CroppedBitmap(src, new Int32Rect(x, y, Math.Min(w, src.PixelWidth - x), Math.Min(h, src.PixelHeight - y)));

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        using (var dc = visual.RenderOpen())
            dc.DrawImage(cropped, new Rect(0, 0, cropped.PixelWidth * scale, cropped.PixelHeight * scale));

        var rtb = new RenderTargetBitmap(cropped.PixelWidth * scale, cropped.PixelHeight * scale, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        File.WriteAllBytes(outPath, EncodePng(rtb));
        Console.WriteLine($"crop -> {outPath} ({cropped.PixelWidth * scale}x{cropped.PixelHeight * scale})");
        return 0;
    }

    /// <summary>把某个尺寸按整数倍最近邻放大，附带格线，用来肉眼核对单像素级对称性。</summary>
    private static int RenderZoom(string specPath, int size, int factor, string outPath)
    {
        var baseDir = Path.GetDirectoryName(specPath)!;
        var root = JsonSerializer.Deserialize<RootSpec>(File.ReadAllText(specPath), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        })!;

        var variantName = root.SizeMap.TryGetValue(size, out var v) ? v : root.DefaultVariant;
        var bmp = Render(root.Variants[variantName], size);
        var w = size * factor;

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(ParseColor("#F3F3F3")), null, new Rect(0, 0, w, w));
            dc.DrawImage(bmp, new Rect(0, 0, w, w));

            // 每格一条极淡的中线，方便判断左右是否对称
            var grid = new Pen(new SolidColorBrush(Color.FromArgb(38, 0, 0, 255)), 1);
            for (var i = 1; i < size; i++)
            {
                dc.DrawLine(grid, new Point(i * factor, 0), new Point(i * factor, w));
                dc.DrawLine(grid, new Point(0, i * factor), new Point(w, i * factor));
            }
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 0, 0)), 1),
                new Point(w / 2.0, 0), new Point(w / 2.0, w));
        }

        var rtb = new RenderTargetBitmap(w, w, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        File.WriteAllBytes(outPath, EncodePng(rtb));
        Console.WriteLine($"zoom -> {outPath} ({size}px x{factor} = {w}x{w}, variant={variantName})");
        return 0;
    }

    // ------------------------------------------------------------------ 对比图

    /// <summary>把各尺寸按整数倍最近邻放大，上下两行分别压在浅色/深色任务栏底色上。</summary>
    private static int RenderSheet(string specPath, string outPath)
    {
        var baseDir = Path.GetDirectoryName(specPath)!;
        var root = JsonSerializer.Deserialize<RootSpec>(File.ReadAllText(specPath), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        })!;

        var sizes = root.Sizes.Where(s => s <= 128).ToList();
        var cells = new List<(int X, int W, BitmapSource Bmp, int Size)>();

        var x = 20;
        foreach (var size in sizes)
        {
            var variantName = root.SizeMap.TryGetValue(size, out var v) ? v : root.DefaultVariant;
            var bmp = Render(root.Variants[variantName], size);
            var factor = Math.Max(1, Math.Min(12, 128 / size));
            var w = size * factor;
            cells.Add((x, w, bmp, size));
            x += w + 28;
        }

        const int rowH = 176;
        var totalW = x + 4;
        var totalH = rowH * 2 + 8;

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(ParseColor("#F3F3F3")), null, new Rect(0, 0, totalW, rowH));
            dc.DrawRectangle(new SolidColorBrush(ParseColor("#202020")), null, new Rect(0, rowH + 8, totalW, rowH));

            var typeface = new Typeface("Segoe UI");
            for (var row = 0; row < 2; row++)
            {
                var top = row == 0 ? 0 : rowH + 8;
                var labelBrush = new SolidColorBrush(ParseColor(row == 0 ? "#8A93A0" : "#8A93A0"));

                foreach (var (cx, cw, bmp, size) in cells)
                {
                    var drawH = bmp.PixelHeight * (cw / bmp.PixelWidth);
                    var dy = top + (rowH - 24 - drawH) / 2 + 2;
                    dc.DrawImage(bmp, new Rect(cx, dy, cw, drawH));

                    var text = new FormattedText($"{size}px",
                        CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        typeface, 11, labelBrush, 1.0);
                    dc.DrawText(text, new Point(cx + (cw - text.Width) / 2, top + rowH - 22));
                }
            }
        }

        var rtb = new RenderTargetBitmap(totalW, totalH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        File.WriteAllBytes(outPath, EncodePng(rtb));
        Console.WriteLine($"sheet -> {outPath} ({totalW}x{totalH})");
        return 0;
    }

    // ------------------------------------------------------------------ 逐像素自检

    private static string DumpAscii(BitmapSource bmp, int size)
    {
        var px = ReadStraightBgra(bmp, out _, out _);
        var sb = new StringBuilder();
        sb.Append("   |").AppendJoin("", Enumerable.Range(0, size).Select(i => i % 10)).AppendLine("|");
        for (var y = 0; y < size; y++)
        {
            sb.Append($"{y,3}|");
            for (var x = 0; x < size; x++)
            {
                var i = (y * size + x) * 4;
                int b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
                if (a < 32) { sb.Append(' '); continue; }

                // 相对砖色（#14171E ≈ 23）归一化的"白度"：0 = 纯砖，9 = 纯白，粉托约落在 5
                var luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                var t = Math.Clamp((luma - 23) / 232.0, 0, 1);
                sb.Append((char)('0' + (int)Math.Round(t * 9)));
            }
            sb.AppendLine("|");
        }
        return sb.ToString();
    }
}
