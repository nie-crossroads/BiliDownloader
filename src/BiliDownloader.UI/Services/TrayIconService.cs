using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace BiliDownloader.UI.Services;

/// <summary>
/// 任务栏右下角的托盘图标。只有设置里「关闭时：最小化到任务栏」才会出现 ——
/// 关掉窗口后下载还在跑，得给用户留一个恢复窗口和真正退出的入口，否则程序只能靠任务管理器杀。
/// </summary>
/// <remarks>
/// 必须在 UI 线程创建：NotifyIcon 内部会建一个隐藏窗口接收托盘消息，
/// WPF 的 Dispatcher 消息循环（底层就是标准 GetMessage/DispatchMessage）能正常泵到它。
/// </remarks>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notify;

    /// <summary>双击图标、左键单击、或点菜单「打开主窗口」。</summary>
    public event Action? OpenRequested;

    /// <summary>点菜单「退出」。</summary>
    public event Action? ExitRequested;

    public TrayIconService(string tooltip)
    {
        _notify = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = tooltip,
            Visible = false
        };

        var menu = new ContextMenuStrip();

        var open = new ToolStripMenuItem("打开主窗口");
        open.Click += (_, _) => OpenRequested?.Invoke();

        var exit = new ToolStripMenuItem("退出 BiliDownloader");
        exit.Click += (_, _) => ExitRequested?.Invoke();

        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);

        _notify.ContextMenuStrip = menu;
        _notify.DoubleClick += (_, _) => OpenRequested?.Invoke();

        // 左键单击也恢复：多数人只会点一下，不会双击。右键交给菜单，不在这里处理。
        _notify.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left) OpenRequested?.Invoke();
        };
    }

    public bool IsVisible { get; private set; }

    public void Show()
    {
        _notify.Visible = true;
        IsVisible = true;
    }

    public void Hide()
    {
        _notify.Visible = false;
        IsVisible = false;
    }

    public void Dispose()
    {
        // 必须先 Visible=false 再 Dispose：否则进程退出后图标会赖在托盘里，
        // 要等鼠标划过才消失（Explorer 的经典行为）。
        _notify.Visible = false;
        _notify.Dispose();
    }

    /// <summary>
    /// 图标取自程序内嵌的 app.ico（pack URI），不依赖 exe 所在目录有没有这个文件。
    /// 取不到就退回系统图标 —— 图标样式不对不该影响托盘能不能用。
    /// </summary>
    private static Icon LoadAppIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Resources/app.ico"));

            if (info?.Stream is { } stream)
            {
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                buffer.Position = 0;
                return new Icon(buffer);
            }
        }
        catch (Exception)
        {
            // 落到下面的兜底
        }

        return SystemIcons.Application;
    }
}
