using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using BiliDownloader.UI.ViewModels;

namespace BiliDownloader.UI;

/// <summary>设置窗口（700 宽，左侧 5 个分类）。改动即时写回 settings.json，没有「确定」。</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();

        DataContext = viewModel;
        viewModel.SyncThemeFromSettings();
        SourceInitialized += (_, _) => ApplyRoundCorners();
    }

    private void ApplyRoundCorners()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var preference = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(handle, 33, ref preference, sizeof(int));
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
