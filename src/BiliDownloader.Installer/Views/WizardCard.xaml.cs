using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BiliDownloader.Installer.Views;

public partial class WizardCard : UserControl
{
    public WizardCard()
    {
        InitializeComponent();
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            Window.GetWindow(this)?.DragMove();
    }
}
