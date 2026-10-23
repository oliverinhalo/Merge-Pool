namespace MergePool.Core.Volumes;

/// <summary>
/// The mount points MergePool is currently serving, so that enumerating volumes never asks the
/// pool about itself.
/// </summary>
/// <remarks>
/// <para>This exists to break a loop that took a production pool down. Resolving the topology asks
/// Windows to describe every volume on the machine, and one of those volumes is the pool's own
/// mount. Describing it means a volume query against <c>P:\</c>, which Windows sends to WinFsp,
/// which dispatches it back into MergePool as a <c>GetVolumeInfo</c> callback — on a different
/// thread, needing the same topology that the thread doing the describing is in the middle of
/// resolving. Both waited for the other, inside the kernel's I/O path, for ever.</para>
/// <para>The topology cache no longer holds a lock across a resolve, so the loop cannot deadlock any
/// more. This closes it at the other end as well: there is nothing to learn from asking ourselves
/// how much space we have, and the question is expensive even when it answers — it is a round trip
/// through the kernel and back into our own callbacks, repeated for every pool, every refresh.</para>
/// <para>Registration is by mount root (<c>P:\</c>) because that is what a volume's mount point
/// looks like from <c>GetVolumePathNamesForVolumeName</c>. Matching is case-insensitive and
/// tolerant of a missing trailing separator.</para>
/// </remarks>
public sealed class OwnMountRegistry
{
    /// <summary>
    /// The registry the service and the volume provider share. An instance can be used instead in
    /// tests, but the two sides have to agree on which one, and in a service they are wired far
    /// apart.
    /// </summary>
    public static OwnMountRegistry Shared { get; } = new();

    private readonly HashSet<string> _roots = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public void Register(string? mountPoint)
    {
        var normalized = Normalize(mountPoint);
        if (normalized is null)
        {
            return;
        }

        lock (_gate)
        {
            _roots.Add(normalized);
        }
    }

    public void Unregister(string? mountPoint)
    {
        var normalized = Normalize(mountPoint);
        if (normalized is null)
        {
            return;
        }

        lock (_gate)
        {
            _roots.Remove(normalized);
        }
    }

    /// <summary>Whether this path is one of our own mounts, and so must not be queried.</summary>
    public bool IsOwnMount(string? path)
    {
        var normalized = Normalize(path);
        if (normalized is null)
        {
            return false;
        }

        lock (_gate)
        {
            return _roots.Contains(normalized);
        }
    }

    public IReadOnlyList<string> Mounts
    {
        get
        {
            lock (_gate)
            {
                return [.. _roots];
            }
        }
    }

    /// <summary>
    /// Reduces a mount point to one canonical form, so that the way the configuration spells it
    /// (<c>P:</c>) and the way volume enumeration reports it (<c>P:\</c>) are the same mount.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="Path"/>-based. These are Windows paths whichever host is running
    /// the code — a mount point comes from the configuration or from
    /// <c>GetVolumePathNamesForVolumeName</c>, never from the local file system — so using the
    /// running platform's separator would make this behave one way on Windows and another in the
    /// tests, and a missed match here means asking Windows about our own mount, which is the
    /// question that used to hang the pool.
    /// </remarks>
    private static string? Normalize(string? mountPoint)
    {
        if (string.IsNullOrWhiteSpace(mountPoint))
        {
            return null;
        }

        var trimmed = mountPoint.Trim().TrimEnd('\\', '/');

        return trimmed.Length == 0 ? null : trimmed + '\\';
    }
}
