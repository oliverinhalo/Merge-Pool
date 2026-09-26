using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using MergePool.Core.Update;
using Microsoft.Win32;

namespace MergePool.Ui.Services;

/// <summary>
/// Whether MergePool's window opens when the user signs in. The service starts on its own — it is
/// registered <c>start= auto</c> — so this is only about the window being there to look at.
/// </summary>
/// <remarks>
/// Written under the current user's own Run key, never the machine's: one person enabling this
/// should not put a window in front of everyone who uses the PC. Every operation is best-effort,
/// because a locked-down registry is a reason to show the toggle as off, not to fail.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private const string ValueName = "MergePool";

    /// <summary>The window is launched into the notification area, not in the user's face.</summary>
    public const string TrayArgument = "--tray";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                key.SetValue(ValueName, $"\"{ExecutablePath()}\" {TrayArgument}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Repoints a start-up entry that names a version directory at the version-independent path
    /// instead, and says whether it changed anything.
    /// </summary>
    /// <remarks>
    /// Versions before 0.5.0 wrote whatever path the window happened to be started from, so an
    /// entry can already be pinned to a version that updates leave behind and pruning eventually
    /// deletes. Only an entry that is genuinely pinned is touched, and only when the link it would
    /// be moved to actually resolves: a build somewhere else is someone's deliberate choice.
    /// </remarks>
    public static bool RepairPinnedEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is not string value || string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var pinned = ExecutableFrom(value);
            var stable = VersionHandoff.StableLaunchPath(pinned);

            if (stable is null || string.Equals(stable, pinned, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            key.SetValue(ValueName, $"\"{stable}\" {TrayArgument}");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>The executable out of a Run value, which carries its arguments after it.</summary>
    private static string ExecutableFrom(string value)
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

    /// <summary>
    /// The path to write into the Run key: the version-independent <c>current</c> one wherever it
    /// resolves, and only otherwise the running process's own path.
    /// </summary>
    /// <remarks>
    /// Writing the running process's path was a mistake worth naming. A window opened from a
    /// version directory — as anyone does when the link is broken, or from a build they are testing
    /// — then wrote that directory into the Run key, and every sign-in after it opened that version
    /// for ever, however many updates installed. It also broke outright once the version was pruned.
    /// The <c>current</c> link is what survives an update, so that is what gets written; the
    /// hand-over in <c>App</c> covers the entries already written the old way.
    /// </remarks>
    private static string ExecutablePath()
    {
        var running = RunningExecutablePath();
        return VersionHandoff.StableLaunchPath(running) ?? running;
    }

    private static string RunningExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(path) && !path.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        return Process.GetCurrentProcess().MainModule?.FileName
               ?? Path.Combine(AppContext.BaseDirectory, "MergePool.exe");
    }
}
