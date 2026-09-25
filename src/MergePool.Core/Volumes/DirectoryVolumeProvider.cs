namespace MergePool.Core.Volumes;

/// <summary>
/// Treats a set of directories as volumes. Used by tests (temp folders, VHDX mount points) and by
/// dry-run tooling; the pool logic cannot tell the difference.
/// </summary>
public sealed class DirectoryVolumeProvider : IVolumeProvider
{
    private readonly List<Entry> _entries = [];
    private readonly object _gate = new();

    private sealed record Entry(VolumeId Id, string RootPath, string Label, VolumeKind Kind, long? TotalOverride, long? FreeOverride)
    {
        public bool Present { get; set; } = true;
    }

    /// <summary>Registers a directory as a volume and returns its generated identity.</summary>
    public VolumeId Add(
        string rootPath,
        string label = "",
        VolumeKind kind = VolumeKind.Fixed,
        Guid? volumeGuid = null,
        long? totalBytes = null,
        long? freeBytes = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootPath);
        Directory.CreateDirectory(rootPath);

        var id = VolumeId.FromGuid(volumeGuid ?? Guid.NewGuid());
        lock (_gate)
        {
            _entries.Add(new Entry(id, Path.GetFullPath(rootPath), label, kind, totalBytes, freeBytes));
        }

        return id;
    }

    /// <summary>Simulates a drive being unplugged or re-attached.</summary>
    public void SetPresent(VolumeId id, bool present)
    {
        lock (_gate)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == id);
            if (entry is not null)
            {
                entry.Present = present;
            }
        }
    }

    /// <summary>Overrides reported capacity so placement can be exercised without filling real disks.</summary>
    public void SetCapacity(VolumeId id, long totalBytes, long freeBytes)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(e => e.Id == id);
            if (index >= 0)
            {
                var existing = _entries[index];
                _entries[index] = existing with { TotalOverride = totalBytes, FreeOverride = freeBytes };
                _entries[index].Present = existing.Present;
            }
        }
    }

    public IReadOnlyList<VolumeInfo> GetVolumes()
    {
        lock (_gate)
        {
            return _entries.Where(static e => e.Present).Select(Describe).ToArray();
        }
    }

    public VolumeInfo? TryGetVolume(VolumeId id)
    {
        lock (_gate)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == id && e.Present);
            return entry is null ? null : Describe(entry);
        }
    }

    private static VolumeInfo Describe(Entry entry)
    {
        long total = entry.TotalOverride ?? 0;
        long free = entry.FreeOverride ?? 0;

        if (entry.TotalOverride is null || entry.FreeOverride is null)
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(entry.RootPath) ?? entry.RootPath);
                total = entry.TotalOverride ?? drive.TotalSize;
                free = entry.FreeOverride ?? drive.AvailableFreeSpace;
            }
            catch (ArgumentException)
            {
                // Fall through with whatever overrides were supplied.
            }
            catch (IOException)
            {
            }
        }

        return new VolumeInfo
        {
            Id = entry.Id,
            RootPath = entry.RootPath,
            DriveLetter = null,
            Label = entry.Label,
            FileSystem = "NTFS",
            Kind = entry.Kind,
            TotalBytes = total,
            FreeBytes = free,
            IsReady = Directory.Exists(entry.RootPath),
        };
    }
}
