using System.Globalization;

namespace MergePool.Update;

/// <summary>
/// Where an installation lives on disk.
/// </summary>
/// <remarks>
/// Versions are installed side by side under <c>versions\{version}</c> and a single
/// <c>current</c> link points at the active one. Nothing is ever overwritten in place, so a failed
/// upgrade is undone by pointing the link back — the previous version's files were never touched.
/// Configuration and pool data live outside the install root and are untouched by any upgrade.
/// </remarks>
public sealed class InstallLayout(string installRoot)
{
    public const string VersionsFolder = "versions";

    public const string CurrentLink = "current";

    /// <summary>Part of the on-disk layout: scratch space for a download being unpacked.</summary>
    public const string StagingFolder = ".staging";

    public string InstallRoot { get; } = Path.GetFullPath(
        !string.IsNullOrWhiteSpace(installRoot) ? installRoot : throw new ArgumentException("An install root is required.", nameof(installRoot)));

    public string VersionsRoot => Path.Combine(InstallRoot, VersionsFolder);

    public string CurrentPath => Path.Combine(InstallRoot, CurrentLink);

    /// <summary>
    /// Where a download is unpacked before it becomes a version. Inside the install root, and
    /// deliberately not inside <see cref="VersionsRoot"/>: a half-unpacked directory there would be
    /// listed as an installed version and could be pruned mid-download.
    /// </summary>
    public string StagingRoot => Path.Combine(InstallRoot, StagingFolder);

    public string VersionPath(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        return Path.Combine(VersionsRoot, version);
    }

    public bool VersionExists(string version) => Directory.Exists(VersionPath(version));

    /// <summary>Installed versions, newest first where they parse as versions.</summary>
    public IReadOnlyList<string> InstalledVersions()
    {
        if (!Directory.Exists(VersionsRoot))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateDirectories(VersionsRoot)
            .Select(path => Path.GetFileName(path)!)
            .OrderByDescending(name => Version.TryParse(name, out var version) ? version : new Version(0, 0))
            .ThenByDescending(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>The version installed before <paramref name="version"/>, for rollback.</summary>
    public string? PreviousVersion(string version) =>
        InstalledVersions().FirstOrDefault(v => !string.Equals(v, version, StringComparison.OrdinalIgnoreCase));

    /// <summary>Removes old versions, always keeping the active one and the newest few.</summary>
    /// <param name="activeVersion">The version running now. Never removed.</param>
    /// <param name="keep">How many of the newest versions to keep, the active one included.</param>
    /// <param name="stillReferenced">
    /// Versions something is still pointing at by name — a start-up entry or a shortcut written
    /// before MergePool used the version-independent path. Removing one of those would leave that
    /// shortcut opening nothing at all, which is worse than a directory nobody needed, so they are
    /// kept until whatever names them has been repaired.
    /// </param>
    public IReadOnlyList<string> PruneOldVersions(
        string activeVersion,
        int keep = 2,
        IEnumerable<string>? stillReferenced = null)
    {
        var removed = new List<string>();
        var versions = InstalledVersions();

        var protectedVersions = versions
            .Where(v => string.Equals(v, activeVersion, StringComparison.OrdinalIgnoreCase))
            .Concat(versions.Take(Math.Max(1, keep)))
            .Concat(stillReferenced ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var version in versions.Where(v => !protectedVersions.Contains(v)))
        {
            try
            {
                Directory.Delete(VersionPath(version), recursive: true);
                removed.Add(version);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A version still in use stays: pruning is housekeeping, never a reason to fail.
            }
        }

        return removed;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"MergePool at {InstallRoot}");
}
