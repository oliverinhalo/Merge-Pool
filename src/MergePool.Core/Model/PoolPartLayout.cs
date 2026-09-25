using System.Globalization;
using MergePool.Core.Paths;

namespace MergePool.Core.Model;

/// <summary>
/// On-disk layout of a pool part. This is the stable data format contract: the folder name and the
/// mirrored-path rule below must never change in a way that an older or newer app version cannot
/// read.
/// </summary>
public static class PoolPartLayout
{
    public const string FolderPrefix = ".PoolPart-";

    /// <summary>Metadata file written inside a pool part. Advisory only: the files are the truth.</summary>
    public const string MarkerFileName = "poolpart.json";

    public static string FolderName(Guid partId) =>
        string.Create(CultureInfo.InvariantCulture, $"{FolderPrefix}{partId:D}");

    public static string RootPathFor(string volumeRoot, Guid partId)
    {
        ArgumentException.ThrowIfNullOrEmpty(volumeRoot);
        return PoolPath.ToHostPath(volumeRoot, FolderName(partId));
    }

    public static bool TryParseFolderName(string? folderName, out Guid partId)
    {
        partId = Guid.Empty;
        if (folderName is null || !folderName.StartsWith(FolderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Guid.TryParseExact(folderName.AsSpan(FolderPrefix.Length), "D", out partId);
    }

    /// <summary>
    /// True when <paramref name="hostPath"/> is inside the given pool part root. Every mutating
    /// operation is checked against this: MergePool must never touch anything outside a pool part.
    /// </summary>
    public static bool IsInsidePart(string partRoot, string hostPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(partRoot);
        ArgumentException.ThrowIfNullOrEmpty(hostPath);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(partRoot));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(hostPath));

        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return full.Length == root.Length
            || full[root.Length] == Path.DirectorySeparatorChar
            || full[root.Length] == Path.AltDirectorySeparatorChar;
    }
}
