using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace MergePool.Ui.Converters;

/// <summary>Formats a byte count the way Explorer does, so the numbers look familiar.</summary>
public sealed class ByteSizeConverter : IValueConverter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not long bytes)
        {
            return "—";
        }

        if (bytes <= 0)
        {
            return "0 B";
        }

        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return string.Create(culture, $"{size:N1} {Units[unit]}");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>Set to true to show when the bound value is false.</summary>
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (Invert)
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Colours a drive's status: missing is a problem, throttled is only a warning.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Problem = Frozen(0xC4, 0x2B, 0x1C);
    private static readonly SolidColorBrush Warning = Frozen(0x8A, 0x63, 0x00);
    private static readonly SolidColorBrush Muted = Frozen(0x60, 0x60, 0x60);
    private static readonly SolidColorBrush Good = Frozen(0x0F, 0x7B, 0x0F);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string status)
        {
            return Good;
        }

        if (status.StartsWith("Throttled", StringComparison.Ordinal))
        {
            return Warning;
        }

        return status switch
        {
            "Missing" or "Offline" => Problem,
            "Not ready" or "Cannot be pooled" => Problem,
            "Read-only" or "Degraded" => Warning,
            "Already pooled" => Muted,
            _ => Good,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Turns a 0..1 fraction into a percentage for the usage bars.</summary>
public sealed class FractionToPercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double fraction ? Math.Clamp(fraction, 0, 1) * 100 : 0d;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
