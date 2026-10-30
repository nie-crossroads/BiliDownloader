using System.Windows;
using BiliDownloader.SetupShared;

namespace BiliDownloader.Installer;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowChromeHelper.ApplyRoundedCorners(this);
    }
}
