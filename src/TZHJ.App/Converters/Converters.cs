using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace TZHJ.App.Converters;

/// <summary>状态色键（Gray/Blue/Green/Orange/Red）→ 前景画刷（深色，用于文字/图标）。
/// 取值与 FluentTheme 调色板一致，是状态色的唯一来源。</summary>
public sealed class StatusKindToBrushConverter : IValueConverter
{
    // 深色（前景）/ 浅底（徽章背景）成对，与 FluentTheme.xaml 的语义色完全对齐。
    internal static (string Fg, string Bg) Palette(string? key) => key switch
    {
        "Blue" => ("#2563EB", "#EEF4FF"),
        "Green" => ("#15803D", "#E9F7EF"),
        "Orange" => ("#C2680A", "#FDF3E7"),
        "Red" => ("#DC2626", "#FDECEC"),
        _ => ("#5B6470", "#F0F1F3"), // Gray
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var (fg, _) = Palette(value as string);
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(fg));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>状态色键 → 浅底画刷（用于徽章背景）。与 <see cref="StatusKindToBrushConverter"/> 的深色成对。</summary>
public sealed class StatusKindToBgConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var (_, bg) = StatusKindToBrushConverter.Palette(value as string);
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool → Visibility（true=Visible）。参数 "Invert" 反转。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is true;
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase)) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>bool 取反。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;
}

/// <summary>非空字符串 → Visible，否则 Collapsed。</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
