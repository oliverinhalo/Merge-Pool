using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace MergePool.Service;

/// <summary>
/// Opens and closes the inbound firewall port for the web interface.
/// </summary>
/// <remarks>
/// Without this, turning the interface on and pointing another machine at it simply times out, with
/// nothing to suggest the firewall is the reason. The service runs as LocalSystem, so it can do
/// this; the window could not. The rule is removed again the moment the interface is turned off or
/// moved back to this computer only — an open port that nothing is listening on is still a door.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsFirewallRule(ILogger logger)
{
    private const string RuleName = "MergePool web interface";

    private int _openPort;

    /// <summary>Makes the firewall match <paramref name="port"/>, or closes the rule when null.</summary>
    public void Apply(int? port)
    {
        if (_openPort == (port ?? 0))
        {
            return;
        }

        Remove();

        if (port is not { } wanted)
        {
            return;
        }

        var arguments = string.Create(
            CultureInfo.InvariantCulture,
            $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={wanted} profile=private,domain");

        if (Run(arguments))
        {
            _openPort = wanted;
            logger.LogInformation("Opened port {Port} in Windows Firewall for the web interface.", wanted);
        }
        else
        {
            logger.LogWarning(
                "Could not open port {Port} in Windows Firewall. The web interface will only be reachable "
                + "from this computer until the port is allowed manually.",
                wanted);
        }
    }

    public void Remove()
    {
        if (_openPort == 0)
        {
            return;
        }

        // Only the private and domain profiles were ever opened, so this never touches a public one.
        if (Run($"advfirewall firewall delete rule name=\"{RuleName}\""))
        {
            logger.LogInformation("Closed the web interface's firewall port.");
        }

        _openPort = 0;
    }

    private bool Run(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "netsh.exe"),
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit(milliseconds: 15_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            logger.LogWarning(exception, "Could not run netsh to change the firewall.");
            return false;
        }
    }
}
