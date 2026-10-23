using System.Globalization;

namespace MergePool.Ui.ViewModels.Settings;

/// <summary>
/// How a setting's value is written out. Kept in one place so a rate is a rate everywhere and no
/// two sliders disagree about what a megabyte looks like.
/// </summary>
internal static class SettingFormats
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>A byte count the way Explorer writes it.</summary>
    public static string Bytes(double bytes)
    {
        if (bytes < 1)
        {
            return "0 B";
        }

        var size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        // Whole numbers below a gigabyte: "64 MB" rather than "64.0 MB".
        return unit <= 2 && Math.Abs(size - Math.Round(size)) < 0.05
            ? string.Create(CultureInfo.CurrentCulture, $"{size:N0} {Units[unit]}")
            : string.Create(CultureInfo.CurrentCulture, $"{size:N1} {Units[unit]}");
    }

    public static string Megabytes(double megabytes) => Bytes(megabytes * 1024 * 1024);

    public static string Gigabytes(double gigabytes) => Bytes(gigabytes * 1024 * 1024 * 1024);

    public static string Kilobytes(double kilobytes) => Bytes(kilobytes * 1024);

    public static string Rate(double megabytesPerSecond) =>
        string.Create(CultureInfo.CurrentCulture, $"{megabytesPerSecond:N0} MB/s");

    public static string Percent(double fraction) =>
        string.Create(CultureInfo.CurrentCulture, $"{fraction:P0}");

    public static string Multiple(double multiple) =>
        string.Create(CultureInfo.CurrentCulture, $"{multiple:N1}×");

    public static string Factor(double factor) =>
        string.Create(CultureInfo.CurrentCulture, $"{factor:0.###}");

    public static string Samples(double samples) => samples <= 1
        ? "1 sample"
        : string.Create(CultureInfo.CurrentCulture, $"{samples:N0} samples");

    /// <summary>A duration in the largest unit that stays readable: seconds, minutes or hours.</summary>
    public static string Seconds(double seconds) => seconds switch
    {
        < 60 => string.Create(CultureInfo.CurrentCulture, $"{seconds:N0} seconds"),
        < 3600 => string.Create(CultureInfo.CurrentCulture, $"{seconds / 60:N0} minutes"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{seconds / 3600:N1} hours"),
    };

    public static string Minutes(double minutes) => minutes < 60
        ? string.Create(CultureInfo.CurrentCulture, $"{minutes:N0} minutes")
        : string.Create(CultureInfo.CurrentCulture, $"{minutes / 60:N1} hours");

    public static string Hours(double hours) => hours < 24
        ? string.Create(CultureInfo.CurrentCulture, $"{hours:N0} hours")
        : string.Create(CultureInfo.CurrentCulture, $"{hours / 24:N1} days");
}
