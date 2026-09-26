namespace MergePool.Core.Update;

/// <summary>
/// Works out which copy of MergePool ought to be running, given the one that was actually started.
/// </summary>
/// <remarks>
/// <para>MergePool installs side by side: every version lives in <c>versions\{version}</c> under the
/// install root, and <c>current</c> is a link to whichever one is live. Updates add a version
/// directory and move the link; they never overwrite a running build.</para>
/// <para>That leaves one way to get stuck. A shortcut, a start-up entry or a command line that names
/// a version directory keeps opening that version for ever, however many updates install — and when
/// the old version is eventually pruned, it stops opening anything at all. So a build started from a
/// version directory checks whether a newer one is installed and hands over to it. Nothing here
/// looks at <c>current</c> to decide: a build started through the link is already whatever the link
/// points at, and reading through a link that is broken or unreadable is exactly the failure this
/// has to survive.</para>
/// </remarks>
public static class VersionHandoff
{
    /// <summary>The directory, under the install root, that holds one directory per version.</summary>
    public const string VersionsFolder = "versions";

    /// <summary>The link, beside <see cref="VersionsFolder"/>, that points at the live version.</summary>
    public const string CurrentLink = "current";

    /// <summary>
    /// The same executable in the newest installed version, or <c>null</c> when the one that was
    /// started is already the newest — or is not running from a version directory at all.
    /// </summary>
    public static string? NewerExecutable(string? startedExecutablePath)
    {
        if (Describe(startedExecutablePath) is not { } started)
        {
            return null;
        }

        string? bestPath = null;
        var best = started.Version;

        foreach (var directory in SafeDirectories(started.VersionsPath))
        {
            if (!Version.TryParse(Path.GetFileName(directory), out var candidate) || candidate <= best)
            {
                continue;
            }

            var executable = Path.Combine(directory, started.ExecutableName);
            if (!File.Exists(executable))
            {
                // A half-unpacked or half-removed version directory is not something to hand over to.
                continue;
            }

            best = candidate;
            bestPath = executable;
        }

        return bestPath;
    }

    /// <summary>
    /// The same executable at the version-independent <c>current</c> path, when that resolves — the
    /// path worth writing into a shortcut or a start-up entry, because it survives every update.
    /// </summary>
    public static string? StableLaunchPath(string? startedExecutablePath)
    {
        if (Describe(startedExecutablePath) is not { } started)
        {
            return null;
        }

        var candidate = Path.Combine(started.InstallRoot, CurrentLink, started.ExecutableName);
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// The same executable in a named version, when it is installed and newer than the one that was
    /// started. Used to move a window onto the version the service has already restarted onto.
    /// </summary>
    public static string? ExecutableForVersion(string? startedExecutablePath, string? version)
    {
        if (Describe(startedExecutablePath) is not { } started
            || !Version.TryParse(version, out var wanted)
            || wanted <= started.Version)
        {
            return null;
        }

        var candidate = Path.Combine(started.VersionsPath, version!, started.ExecutableName);
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// The executable a running window should restart into so that it catches up with
    /// <paramref name="version"/> — the version the service is now running — or <c>null</c> when
    /// there is nothing to catch up with, or no way to get there.
    /// </summary>
    /// <remarks>
    /// A window started from a version directory restarts into the named version's directory. One
    /// started through <c>current</c> restarts from the same path it was started from, because the
    /// link already points at the new version. Anything else — a development build, a copy
    /// elsewhere — is left alone.
    /// </remarks>
    public static string? RestartTargetForVersion(string? startedExecutablePath, string? version)
    {
        if (!Version.TryParse(version, out _))
        {
            return null;
        }

        if (Describe(startedExecutablePath) is not null)
        {
            return ExecutableForVersion(startedExecutablePath, version);
        }

        return StartedThroughLink(startedExecutablePath) ? Path.GetFullPath(startedExecutablePath!) : null;
    }

    private static bool StartedThroughLink(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
        return directory is not null
               && string.Equals(Path.GetFileName(directory), CurrentLink, StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct Layout(
        string InstallRoot,
        string VersionsPath,
        Version Version,
        string ExecutableName);

    /// <summary>
    /// Reads <c>…\{root}\versions\{version}\{name}.exe</c>. Anything else — a development build, a
    /// copy somewhere else, or a launch through <c>current</c> — is left alone.
    /// </summary>
    private static Layout? Describe(string? executablePath)
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

        var name = Path.GetFileName(full);
        var versionPath = Path.GetDirectoryName(full);
        var versionsPath = versionPath is null ? null : Path.GetDirectoryName(versionPath);
        var installRoot = versionsPath is null ? null : Path.GetDirectoryName(versionsPath);

        if (name.Length == 0 || versionPath is null || versionsPath is null || installRoot is null)
        {
            return null;
        }

        if (!string.Equals(Path.GetFileName(versionsPath), VersionsFolder, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Version.TryParse(Path.GetFileName(versionPath), out var version)
            ? new Layout(installRoot, versionsPath, version, name)
            : null;
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}
