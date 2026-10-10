using System.Runtime.Versioning;
using MergePool.Update;
using Microsoft.Win32;

namespace MergePool.Updater;

/// <summary>
/// Finds version directories that a sign-in entry still names directly, so an upgrade does not
/// delete what somebody's sign-in is pointing at.
/// </summary>
/// <remarks>
/// <para>Before 0.5.0 the window wrote its own path into the user's Run key, so an entry can read
/// <c>…\versions\0.4.0\MergePool.exe</c> rather than the version-independent <c>…\current\…</c>
/// path. Pruning that version would leave that person with nothing opening at sign-in and no
/// explanation — so a version still named this way is kept until the entry is repaired, which the
/// window does for itself the next time it starts.</para>
/// <para>Read-only, deliberately. The service runs as LocalSystem and could rewrite other people's
/// registry from here, but quietly editing a signed-out user's settings is not something an updater
/// should do; keeping a directory is the conservative half of the same fix.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class StartupEntryScan
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private const string ValueName = "MergePool";

    /// <summary>
    /// Version names under <paramref name="layout"/> that a Run entry names directly, across every
    /// user hive currently loaded.
    /// </summary>
    public static IReadOnlyCollection<string> ReferencedVersions(InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in RunEntries())
        {
            if (VersionFrom(layout, path) is { } version)
            {
                found.Add(version);
            }
        }

        return found;
    }

    /// <summary>The version directory a path sits in, when it is one of this install's.</summary>
    internal static string? VersionFrom(InstallLayout layout, string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        string full;

        try
        {
            full = Path.GetFullPath(executablePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var versionDirectory = Path.GetDirectoryName(full);
        var versionsRoot = versionDirectory is null ? null : Path.GetDirectoryName(versionDirectory);

        if (versionDirectory is null || versionsRoot is null)
        {
            return null;
        }

        // Only this install's versions folder. Another MergePool somewhere else is not ours to keep.
        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(versionsRoot),
                Path.TrimEndingDirectorySeparator(layout.VersionsRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetFileName(versionDirectory);
    }

    /// <summary>Every user's MergePool Run value, from the hives that are loaded right now.</summary>
    private static IEnumerable<string> RunEntries()
    {
        foreach (var sid in UserHives())
        {
            string? value = null;

            try
            {
                using var key = Registry.Users.OpenSubKey($@"{sid}\{RunKey}");
                value = key?.GetValue(ValueName) as string;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return ExecutableFrom(value);
            }
        }
    }

    private static IEnumerable<string> UserHives()
    {
        string[] names;

        try
        {
            names = Registry.Users.GetSubKeyNames();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }

        // _Classes hives are per-user class registrations, not a user's own settings.
        return names.Where(static name => !name.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The executable out of a Run value, which carries its arguments after it.</summary>
    internal static string ExecutableFrom(string value)
    {
        var trimmed = value.Trim();

        if (trimmed.StartsWith('"'))
        {
            var closing = trimmed.IndexOf('"', 1);
            return closing > 1 ? trimmed[1..closing] : trimmed.Trim('"');
        }

        var argument = trimmed.IndexOf(" -", StringComparison.Ordinal);
        return argument > 0 ? trimmed[..argument].TrimEnd() : trimmed;
    }
}
