using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace BiliDownloader.Installer.Views;

public partial class StepWelcome : UserControl
{
    public StepWelcome()
    {
        InitializeComponent();
    }

    private void OpenLicense(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html")
            {
                UseShellExecute = true
            });
        }
        catch { /* 忽略：无默认浏览器时静默失败 */ }
    }
}
