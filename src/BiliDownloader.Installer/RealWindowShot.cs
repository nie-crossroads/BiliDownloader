using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using BiliDownloader.SetupShared;

namespace BiliDownloader.Installer;

/// <summary>
/// 诊断工具：把安装向导三步分别在**真实窗口**里显示一遍并截图。
///
/// 与 <see cref="InstallerShot"/> 的离屏渲染不同，这条路径会经过 DWM 合成，
/// 因此能如实反映 ClearType、DPI 缩放和圆角效果——用来验证「文字是否清晰」。
/// </summary>
internal static class RealWindowShot
{
    public static async void RenderAll(string directory)
    {
        Directory.CreateDirectory(directory);
        // 每抓完一步都会 Close 窗口，默认策略会让进程提前退出。
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        foreach (var step in new[] { InstallStep.Welcome, InstallStep.Installing, InstallStep.Done })
        {
            var model = new InstallerModel { Step = step };

            // 给「安装中」注入一组演示进度，否则截图里进度条是 0%，看不出填充与渐变。
            if (step == InstallStep.Installing)
            {
                model.BytesTotal = 260L * 1024 * 1024;
                model.BytesWritten = 164L * 1024 * 1024;
                model.Percent = 63;
                model.StepCopy = SubStepState.Done;
                model.StepFfmpeg = SubStepState.Running;
                model.StepShortcut = SubStepState.Pending;
                model.StepRegister = SubStepState.Pending;
            }

            var window = new MainWindow
            {
                DataContext = model,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 80,
                Top = 80,
                ShowInTaskbar = false
            };

            window.Show();
            window.Topmost = true;
            window.Activate();
            window.UpdateLayout();

            // 留出一次 DWM 合成的时间，否则可能抓到未绘制的窗口。
            await Task.Delay(800);

            var path = Path.Combine(directory, $"real-{step}.png");
            try
            {
                WindowCapture.CaptureToPng(window, path);
                Console.WriteLine("[shot-real] 已保存 -> " + path);
                foreach (var button in FindButtons(window))
                    Console.WriteLine($"[btn] {button.Content} IsEnabled={button.IsEnabled}");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[shot-real] 失败: " + ex.Message);
            }

            window.Close();
            await Task.Delay(120);
        }

        Application.Current.Shutdown();
    }

    /// <summary>诊断：列出窗口内所有按钮的启用状态（验证「取消」不再是禁用态）。</summary>
    private static IEnumerable<System.Windows.Controls.Button> FindButtons(DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.Button button) yield return button;
            foreach (var nested in FindButtons(child)) yield return nested;
        }
    }
}
