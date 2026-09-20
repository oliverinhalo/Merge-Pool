using System.Buffers;

namespace MergePool.Core.Paths;

/// <summary>
/// Canonical form of a path *inside* the pool: backslash separated, no leading or trailing
/// separator, no drive qualifier. The empty string is the pool root.
/// </summary>
/// <remarks>
/// The on-disk mirror of a pool path is always <c>&lt;poolPartRoot&gt;\&lt;poolPath&gt;</c>, which is
/// what keeps pooled files readable with the app uninstalled.
/// </remarks>
public static class PoolPath
{
    public const char Separator = '\\';

    /// <summary>Comparer used for all pool path / name comparisons (Windows semantics).</summary>
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    public static StringComparison Comparison => StringComparison.OrdinalIgnoreCase;

    /// <summary>Normalizes any caller-supplied pool path into canonical form.</summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        Span<char> buffer = path.Length <= 512 ? stackalloc char[path.Length] : new char[path.Length];
        var length = 0;
        var lastWasSeparator = true; // collapses a leading separator run

        foreach (var raw in path)
        {
            var c = raw is '/' or '\\' ? Separator : raw;
            if (c == Separator)
            {
                if (lastWasSeparator)
                {
                    continue;
                }

                lastWasSeparator = true;
            }
            else
            {
                lastWasSeparator = false;
            }

            buffer[length++] = c;
        }

        while (length > 0 && buffer[length - 1] == Separator)
        {
            length--;
        }

        return length == 0 ? string.Empty : new string(buffer[..length]);
    }

    public static bool IsRoot(string poolPath) => string.IsNullOrEmpty(poolPath);

    /// <summary>Returns the parent pool path, or <c>null</c> when <paramref name="poolPath"/> is the root.</summary>
    public static string? GetParent(string poolPath)
    {
        var normalized = Normalize(poolPath);
        if (normalized.Length == 0)
        {
            return null;
        }

        var index = normalized.LastIndexOf(Separator);
        return index < 0 ? string.Empty : normalized[..index];
    }

    /// <summary>Returns the final component of the path (empty for the root).</summary>
    public static string GetName(string poolPath)
    {
        var normalized = Normalize(poolPath);
        var index = normalized.LastIndexOf(Separator);
        return index < 0 ? normalized : normalized[(index + 1)..];
    }

    public static string Combine(string poolPath, string name)
    {
        var normalized = Normalize(poolPath);
        var normalizedName = Normalize(name);
        if (normalizedName.Length == 0)
        {
            return normalized;
        }

        return normalized.Length == 0 ? normalizedName : normalized + Separator + normalizedName;
    }

    /// <summary>Splits a pool path into its components. The root yields an empty array.</summary>
    public static string[] Split(string poolPath)
    {
        var normalized = Normalize(poolPath);
        return normalized.Length == 0
            ? Array.Empty<string>()
            : normalized.Split(Separator);
    }

    /// <summary>True when <paramref name="candidate"/> is <paramref name="ancestor"/> or lives beneath it.</summary>
    public static bool IsAtOrUnder(string candidate, string ancestor)
    {
        var c = Normalize(candidate);
        var a = Normalize(ancestor);
        if (a.Length == 0)
        {
            return true;
        }

        if (!c.StartsWith(a, Comparison))
        {
            return false;
        }

        return c.Length == a.Length || c[a.Length] == Separator;
    }

    /// <summary>
    /// Joins a pool path onto a host directory, converting to the host separator. The result keeps
    /// the extended-length prefix when the host root already carries one, so long paths survive.
    /// </summary>
    public static string ToHostPath(string hostRoot, string poolPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostRoot);

        var normalized = Normalize(poolPath);
        if (normalized.Length == 0)
        {
            return hostRoot;
        }

        var native = Path.DirectorySeparatorChar == Separator
            ? normalized
            : normalized.Replace(Separator, Path.DirectorySeparatorChar);

        var trimmedRoot = hostRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmedRoot.Length == 0
            ? hostRoot + native
            : trimmedRoot + Path.DirectorySeparatorChar + native;
    }

    private static readonly SearchValues<char> InvalidNameChars =
        SearchValues.Create("\\/:*?\"<>|\0");

    /// <summary>Rejects names that cannot exist on NTFS, so we never build an unusable host path.</summary>
    public static bool IsValidName(string name) =>
        name.Length > 0
        && name.AsSpan().IndexOfAny(InvalidNameChars) < 0
        && name is not ("." or "..");
}
