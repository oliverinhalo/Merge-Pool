using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using MergePool.Engine;
using MergePool.Update;
using Microsoft.Extensions.Logging;

namespace MergePool.Service;

/// <summary>
/// Starts the updater that will stop this service, repoint <c>current</c> and start it again.
/// </summary>
/// <remarks>
/// <para>The updater is run from the <em>new</em> version's own directory, not from
/// <c>current</c>. That matters: the process doing the swap must not be running out of the directory
/// the swap repoints, or stopping the service could pull the ground out from under it.</para>
/// <para>The service cannot do this itself — a process cannot stop the service it is hosting and
/// keep executing — so the handoff is a separate, detached process.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ProcessUpgradeLauncher(ILogger<ProcessUpgradeLauncher> logger) : IUpgradeLauncher
{
    public bool Launch(StagedRelease release, string installRoot, string serviceName, string pipeName)
    {
        ArgumentNullException.ThrowIfNull(release);

        if (!File.Exists(release.UpdaterPath))
        {
            logger.LogError(
                "Version {Version} is staged at {Path} but has no updater executable, so it was not applied.",
                release.Version,
                release.Path);

            return false;
        }

        var arguments = string.Create(
            CultureInfo.InvariantCulture,
            $"--version \"{release.Version}\" --install-root \"{installRoot}\" --service \"{serviceName}\" --pipe \"{pipeName}\"");

        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = release.UpdaterPath,
                Arguments = arguments,
                WorkingDirectory = release.Path,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                logger.LogError("The updater for version {Version} did not start.", release.Version);
                return false;
            }

            logger.LogInformation(
                "Applying version {Version}: updater started as process {ProcessId}. This service is about to restart.",
                release.Version,
                process.Id);

            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            logger.LogError(exception, "Could not start the updater for version {Version}.", release.Version);
            return false;
        }
    }
}
