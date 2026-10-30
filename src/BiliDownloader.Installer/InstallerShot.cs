using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BiliDownloader.Installer.Views;
using BiliDownloader.SetupShared;

namespace BiliDownloader.Installer;

/// <summary>离线渲染向导各步骤到 PNG，便于与设计稿逐一对齐（不影响真实安装流程）。</summary>
internal static class InstallerShot
{
    public static void RenderAll(string dir)
    {
        RenderStep(dir, "step-welcome.png", InstallStep.Welcome, m => { });
        RenderStep(dir, "step-installing.png", InstallStep.Installing, m =>
        {
            m.StepCopy = SubStepState.Done;
            m.StepFfmpeg = SubStepState.Done;
            m.StepShortcut = SubStepState.Running;
            m.Percent = 62;
            m.BytesWritten = 118L * 1024 * 1024;
            m.BytesTotal = 186L * 1024 * 1024;
        });
        RenderStep(dir, "step-done.png", InstallStep.Done, m =>
        {
            m.StatusText = "已安装到 " + ProductInfo.DefaultInstallDir;
        });
    }

    private static void RenderStep(string dir, string file, InstallStep step, Action<InstallerModel> tune)
    {
        var model = new InstallerModel();
        tune(model);
        model.Step = step;

        var card = new WizardCard { DataContext = model };
        // 离线渲染无活动 PresentationSource，Visibility 绑定不会自动求值；截屏专用：先解绑再显式切换。
        var w = card.FindName("StepWelcomeEl");
        var i = card.FindName("StepInstallingEl");
        var d = card.FindName("StepDoneEl");
        if (w is FrameworkElement we)
        {
            BindingOperations.ClearBinding(we, UIElement.VisibilityProperty);
            we.Visibility = step == InstallStep.Welcome ? Visibility.Visible : Visibility.Collapsed;
        }
        if (i is FrameworkElement ie)
        {
            BindingOperations.ClearBinding(ie, UIElement.VisibilityProperty);
            ie.Visibility = step == InstallStep.Installing ? Visibility.Visible : Visibility.Collapsed;
        }
        if (d is FrameworkElement de)
        {
            BindingOperations.ClearBinding(de, UIElement.VisibilityProperty);
            de.Visibility = step == InstallStep.Done ? Visibility.Visible : Visibility.Collapsed;
        }
        var wrap = new Border
        {
            Background = GetBrush(),
            Padding = new Thickness(40),
            Child = card
        };

        RenderElement(wrap, Path.Combine(dir, file));
    }

    private static Brush GetBrush()
    {
        if (Application.Current?.Resources["Bg"] is Brush b) return b;
        return new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xF9));
    }

    private static void RenderElement(FrameworkElement el, string path)
    {
        el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        el.Arrange(new Rect(0, 0, el.DesiredSize.Width, el.DesiredSize.Height));
        var w = Math.Max(1, (int)Math.Ceiling(el.DesiredSize.Width));
        var h = Math.Max(1, (int)Math.Ceiling(el.DesiredSize.Height));
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(el);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
