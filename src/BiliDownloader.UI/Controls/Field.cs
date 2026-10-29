using System.Windows;

namespace BiliDownloader.UI.Controls;

/// <summary>
/// 输入框附加属性：占位文案 + 错误态（WPF TextBox 没有 Placeholder，也不支持 box-shadow 光环）。
/// 模板里用这两个属性驱动占位文字与「红色描边 + 3px 低透明光环」。
/// </summary>
public static class Field
{
    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.RegisterAttached("Placeholder", typeof(string), typeof(Field),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsErrorProperty =
        DependencyProperty.RegisterAttached("IsError", typeof(bool), typeof(Field),
            new PropertyMetadata(false));

    public static string GetPlaceholder(DependencyObject obj) => (string)obj.GetValue(PlaceholderProperty);

    public static void SetPlaceholder(DependencyObject obj, string value) => obj.SetValue(PlaceholderProperty, value);

    public static bool GetIsError(DependencyObject obj) => (bool)obj.GetValue(IsErrorProperty);

    public static void SetIsError(DependencyObject obj, bool value) => obj.SetValue(IsErrorProperty, value);
}
