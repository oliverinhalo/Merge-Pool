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
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string status)
        {
            return Brush("SuccessBrush");
        }

        if (status.StartsWith("Throttled", StringComparison.Ordinal))
        {
            return Brush("WarningBrush");
        }

        return status switch
        {
            "Missing" or "Offline" => Brush("DangerBrush"),
            "Not ready" or "Cannot be pooled" => Brush("DangerBrush"),
            "Read-only" or "Degraded" => Brush("WarningBrush"),
            "Already pooled" => Brush("MutedTextBrush"),
            _ => Brush("SuccessBrush"),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <summary>Resolved from the active palette, so these colours follow light and dark.</summary>
    private static object Brush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}

/// <summary>Turns a 0..1 fraction into a percentage for the usage bars.</summary>
public sealed class FractionToPercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double fraction ? Math.Clamp(fraction, 0, 1) * 100 : 0d;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Shows an element only when a count is zero — the "nothing here yet" panels.</summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int count && count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Colours the connection pill. Theme brushes are looked up by name so the pill follows a theme
/// change, rather than being frozen at whatever the palette was when the window opened.
/// </summary>
public abstract class ThemeBrushConverter : IValueConverter
{
    protected abstract string WhenTrue { get; }

    protected abstract string WhenFalse { get; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is true ? WhenTrue : WhenFalse;
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class StatePillBrushConverter : ThemeBrushConverter
{
    protected override string WhenTrue => "SuccessSoftBrush";

    protected override string WhenFalse => "DangerSoftBrush";
}

public sealed class StateDotBrushConverter : ThemeBrushConverter
{
    protected override string WhenTrue => "SuccessBrush";

    protected override string WhenFalse => "DangerBrush";
}

public sealed class StateTextBrushConverter : ThemeBrushConverter
{
    protected override string WhenTrue => "SuccessBrush";

    protected override string WhenFalse => "DangerBrush";
}
