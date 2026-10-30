using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BiliDownloader.Installer.Infra;

/// <summary>把 Step 枚举映射为可见性（参数指定目标步骤名，匹配则 Visible）。</summary>
public sealed class StepToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is InstallStep step && parameter is string name)
        {
            if (Enum.TryParse<InstallStep>(name, out var target) && step == target)
                return Visibility.Visible;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
