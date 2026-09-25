using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
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
    /// The launcher path. Taken from the running process, so it follows the <c>current</c> link and
    /// keeps working across upgrades instead of pinning one version directory.
    /// </summary>
    private static string ExecutablePath()
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
