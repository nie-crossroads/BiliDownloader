using System.Windows;
using BiliDownloader.SetupShared;

namespace BiliDownloader.Uninstaller;

public partial class UninstallWindow : Window
{
    public UninstallWindow()
    {
        InitializeComponent();
        DataContext = new UninstallModel(() => Close());
        SourceInitialized += (_, _) => WindowChromeHelper.ApplyRoundedCorners(this);
    }
}
