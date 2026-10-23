using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using MergePool.Core.Sharing;
using Microsoft.Extensions.Logging;

namespace MergePool.Service;

/// <summary>Creates and removes the SMB shares MergePool manages. Windows only.</summary>
public interface ISmbShareManager
{
    /// <summary>Every share the machine currently has, or <c>null</c> when they cannot be read.</summary>
    IReadOnlyList<ExistingShare>? List();

    /// <summary>Creates the share and grants the accounts it names. Returns why not, or <c>null</c>.</summary>
    string? Create(DesiredShare share);

    /// <summary>Removes a share by name. Returns why not, or <c>null</c>.</summary>
    string? Remove(string shareName);
}

/// <summary>
/// The real thing, driven through PowerShell's SMB share cmdlets.
/// </summary>
/// <remarks>
/// <para>PowerShell rather than <c>net share</c> because <c>New-SmbShare</c> takes the access list
/// as part of creating the share, so a share never exists for even a moment without its
/// permissions. <c>net share</c> would need a second command to grant access, and a share that is
/// briefly open to its default access list is not something to put a pool behind.</para>
/// <para>MergePool never grants <c>Everyone</c>, and never invents an account: the accounts come
/// from the configuration, and with none the share is not created at all.</para>
/// <para>Nothing here opens the firewall. SMB is served by Windows on port 445 under the built-in
/// File and Printer Sharing rules, and quietly opening a system service port to the network on
/// someone's behalf is a bigger decision than MergePool should make for them. The window says
/// whether those rules are on instead.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsSmbShares(ILogger logger) : ISmbShareManager
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public IReadOnlyList<ExistingShare>? List()
    {
        // Special = administrative shares (C$, ADMIN$, IPC$): never ours, and never to be touched.
        var result = Run(
            "Get-SmbShare | Where-Object { -not $_.Special } | "
            + "ForEach-Object { \"$($_.Name)`t$($_.Path)\" }");

        if (!result.Succeeded)
        {
            return null;
        }

        var shares = new List<ExistingShare>();

        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split('\t');
            if (parts.Length == 2 && parts[0].Length > 0)
            {
                shares.Add(new ExistingShare { ShareName = parts[0], Path = parts[1] });
            }
        }

        return shares;
    }

    public string? Create(DesiredShare share)
    {
        ArgumentNullException.ThrowIfNull(share);

        if (share.AllowedAccounts.Count == 0)
        {
            // Belt and braces: the configuration refuses this too. A share with no access list
            // takes the SMB server's default, which is not a decision to arrive at by accident.
            return "no account was named, so the share was not created";
        }

        var accounts = string.Join(
            ",",
            share.AllowedAccounts.Select(account => "'" + account.Replace("'", "''", StringComparison.Ordinal) + "'"));

        var access = share.ReadOnly ? "-ReadAccess" : "-FullAccess";

        var command = string.Create(
            CultureInfo.InvariantCulture,
            $"New-SmbShare -Name '{Escape(share.ShareName)}' -Path '{Escape(share.Path)}' "
            + $"{access} {accounts} -ErrorAction Stop | Out-Null");

        var result = Run(command);
        return result.Succeeded ? null : Describe(result);
    }

    public string? Remove(string shareName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shareName);

        var result = Run(
            $"Remove-SmbShare -Name '{Escape(shareName)}' -Force -ErrorAction Stop");

        return result.Succeeded ? null : Describe(result);
    }

    /// <summary>
    /// Whether Windows is set to serve SMB to the network at all. Reported rather than changed:
    /// turning file sharing on for someone is not MergePool's call.
    /// </summary>
    public bool? FileSharingLooksEnabled()
    {
        var result = Run(
            "@(Get-NetFirewallRule -DisplayGroup 'File and Printer Sharing' -ErrorAction Stop | "
            + "Where-Object { $_.Enabled -eq 'True' -and $_.Direction -eq 'Inbound' }).Count");

        if (!result.Succeeded || !int.TryParse(result.Output.Trim(), out var enabled))
        {
            return null;
        }

        return enabled > 0;
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string Describe(CommandResult result) =>
        result.Error.Length > 0 ? result.Error.Split('\n')[0].Trim() : $"PowerShell exited with {result.ExitCode}";

    private CommandResult Run(string command)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList =
                {
                    "-NoProfile",
                    "-NonInteractive",
                    "-ExecutionPolicy", "Bypass",
                    "-Command", command,
                },
            });

            if (process is null)
            {
                return new CommandResult(false, string.Empty, "PowerShell would not start", -1);
            }

            // Read before waiting: a full pipe buffer with nobody draining it is a deadlock, and
            // Get-SmbShare on a machine with many shares produces more than one bufferful.
            var output = new StringBuilder();
            var error = new StringBuilder();

            var readOutput = Task.Run(() => output.Append(process.StandardOutput.ReadToEnd()));
            var readError = Task.Run(() => error.Append(process.StandardError.ReadToEnd()));

            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                TryKill(process);
                return new CommandResult(false, string.Empty, "PowerShell did not finish in time", -1);
            }

            Task.WaitAll([readOutput, readError], Timeout);

            return new CommandResult(
                process.ExitCode == 0 && error.Length == 0,
                output.ToString(),
                error.ToString(),
                process.ExitCode);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or IOException
            or InvalidOperationException)
        {
            logger.LogWarning(exception, "Could not run PowerShell to manage an SMB share.");
            return new CommandResult(false, string.Empty, exception.Message, -1);
        }
    }

    private void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning("A PowerShell run for SMB shares would neither finish nor be stopped.");
        }
    }

    private sealed record CommandResult(bool Succeeded, string Output, string Error, int ExitCode);
}
