using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BiliDownloader.Installer.Views;

public partial class SubStepRow : UserControl
{
    public static readonly DependencyProperty StateProperty =
        DependencyProperty.Register(nameof(State), typeof(SubStepState), typeof(SubStepRow),
            new PropertyMetadata(SubStepState.Pending, OnStateChanged));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SubStepRow),
            new PropertyMetadata(string.Empty));

    public SubStepState State
    {
        get => (SubStepState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public SubStepRow()
    {
        InitializeComponent();
        ApplyState();
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SubStepRow)d).ApplyState();

    private void ApplyState()
    {
        var pending = (Brush)FindResource("Line2");
        var surface3 = (Brush)FindResource("Surface3");
        var accent = (Brush)FindResource("Accent");
        var ok = (Brush)FindResource("Ok");
        var fg = (Brush)FindResource("Fg");
        var fg2 = (Brush)FindResource("Fg2");
        var fg3 = (Brush)FindResource("Fg3");

        switch (State)
        {
            case SubStepState.Pending:
                Dot.BorderBrush = pending; Dot.Background = surface3;
                Check.Visibility = Visibility.Collapsed;
                Pulse.Visibility = Visibility.Collapsed;
                StatusTb.Text = "等待"; StatusTb.Foreground = fg3;
                TitleTb.Foreground = fg3;
                break;
            case SubStepState.Running:
                Dot.BorderBrush = accent; Dot.Background = Brushes.Transparent;
                Check.Visibility = Visibility.Collapsed;
                Pulse.Visibility = Visibility.Visible;
                StatusTb.Text = "进行中"; StatusTb.Foreground = (Brush)FindResource("Accent");
                TitleTb.Foreground = fg;
                break;
            case SubStepState.Done:
                Dot.BorderBrush = ok; Dot.Background = ok;
                Check.Visibility = Visibility.Visible;
                Pulse.Visibility = Visibility.Collapsed;
                StatusTb.Text = "完成"; StatusTb.Foreground = fg2;
                TitleTb.Foreground = fg2;
                break;
        }
    }
}
