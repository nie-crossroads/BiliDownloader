using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BiliDownloader.Uninstaller.Views;

public partial class UninstallDialog : UserControl
{
    public UninstallDialog()
    {
        InitializeComponent();
        HeaderDock.MouseLeftButtonDown += (s, e) =>
        {
            if (e.ChangedButton == MouseButton.Left)
                Window.GetWindow(this)?.DragMove();
        };
    }
}
