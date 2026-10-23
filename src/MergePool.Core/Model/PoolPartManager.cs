using System.Reflection;
using System.Text;
using System.Text.Json;
using MergePool.Core.Config;
using MergePool.Core.Paths;
using MergePool.Core.Volumes;

namespace MergePool.Core.Model;

/// <summary>
/// Creates, discovers and resolves <c>.PoolPart-{GUID}</c> folders. This is the only component that
/// writes at a volume root, and it only ever creates the part folder itself.
/// </summary>
public sealed class PoolPartManager(
    IVolumeProvider volumeProvider,
    TimeProvider? timeProvider = null,
    PartUsageMeter? usageMeter = null)
{
    private readonly IVolumeProvider _volumeProvider =
        volumeProvider ?? throw new ArgumentNullException(nameof(volumeProvider));

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// When each part was last resolved as definitely present. Drives it to
    /// <see cref="PoolPartState.Unavailable"/> rather than straight to
    /// <see cref="PoolPartState.Offline"/> while it is within the grace period, so one failed volume
    /// query cannot make a drive's files look deleted.
    /// </summary>
    private readonly Dictionary<Guid, DateTimeOffset> _lastSeen = [];

    private readonly object _lastSeenGate = new();

    /// <summary>
    /// How long a drive that has stopped answering is treated as unreachable rather than gone. Long
    /// enough to cover a volume query that timed out, a letter in flux or an enclosure that
    /// re-enumerated; short enough that genuinely pulling a drive settles into degraded service
    /// while someone is still watching the window.
    /// </summary>
    public TimeSpan PresenceGracePeriod { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Supplies each part's measured size. Optional: without one, parts report drive figures only
    /// and the pool's own usage reads as unknown rather than as zero.
    /// </summary>
    public PartUsageMeter? UsageMeter { get; } = usageMeter;

    /// <summary>
    /// Creates the pool part folder on <paramref name="volume"/> if it is not already there. Nothing
    /// else on the volume is read, moved or modified.
    /// </summary>
    public PoolPart CreatePart(VolumeInfo volume, Guid poolId, string poolName, Guid? partId = null)
    {
        ArgumentNullException.ThrowIfNull(volume);
        if (volume.RootPath is null)
        {
            throw new InvalidOperationException($"Volume {volume.Id} has no mount point.");
        }

        var id = partId ?? Guid.NewGuid();
        var root = PoolPartLayout.RootPathFor(volume.RootPath, id);
        Directory.CreateDirectory(root);
        WriteMarker(root, id, poolId, poolName);

        return ToPart(volume, id, root);
    }

    public void WriteMarker(string partRoot, Guid partId, Guid poolId, string poolName)
    {
        ArgumentException.ThrowIfNullOrEmpty(partRoot);

        var marker = new PoolPartMarker
        {
            PartId = partId,
            PoolId = poolId,
            PoolName = poolName,
            CreatedUtc = _timeProvider.GetUtcNow(),
            CreatedByVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
        };

        var path = PoolPath.ToHostPath(partRoot, PoolPartLayout.MarkerFileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(marker, ConfigSchema.SerializerOptions), Encoding.UTF8);
        File.Move(temp, path, overwrite: true);
    }

    public PoolPartMarker? TryReadMarker(string partRoot)
    {
        var path = PoolPath.ToHostPath(partRoot, PoolPartLayout.MarkerFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PoolPartMarker>(
                File.ReadAllText(path, Encoding.UTF8),
                ConfigSchema.SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Lists the pool part folders sitting at a volume root.</summary>
    public IReadOnlyList<Guid> DiscoverParts(string volumeRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(volumeRoot);
        if (!Directory.Exists(volumeRoot))
        {
            return Array.Empty<Guid>();
        }

        var result = new List<Guid>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(volumeRoot, PoolPartLayout.FolderPrefix + "*"))
            {
                if (PoolPartLayout.TryParseFolderName(Path.GetFileName(directory), out var id))
                {
                    result.Add(id);
                }
            }
        }
        catch (IOException)
        {
            return result;
        }
        catch (UnauthorizedAccessException)
        {
            return result;
        }

        return result;
    }

    /// <summary>
    /// Resolves a pool definition against the volumes present right now. Missing volumes come back
    /// as offline parts, which is what makes the pool degrade instead of fail; they rejoin on the
    /// next resolve once the drive is back.
    /// </summary>
    public PoolSnapshot Resolve(PoolDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var volumes = _volumeProvider.GetVolumes();
        var parts = new List<PoolPart>(definition.Drives.Count);
        var now = _timeProvider.GetUtcNow();

        foreach (var drive in definition.Drives)
        {
            if (!drive.Enabled)
            {
                continue;
            }

            var volume = ResolveVolume(drive, volumes);
            if (volume?.RootPath is null)
            {
                // The volume is not in the enumeration, or has no mount point. Within the grace
                // period that is "we cannot see it right now"; after it, the drive really is gone.
                parts.Add(new PoolPart
                {
                    PartId = drive.PartId,
                    Volume = VolumeId.TryParse(drive.VolumeId, out var id) ? id : VolumeId.Empty,
                    State = AbsenceState(drive.PartId, now),
                    Label = drive.Label ?? string.Empty,
                    SpeedFactor = drive.SpeedFactorOverride ?? 1.0,
                });
                continue;
            }

            var root = PoolPartLayout.RootPathFor(volume.RootPath, drive.PartId);
            var presence = PartFolderPresence(root);

            if (presence is FolderPresence.Unreadable)
            {
                // We asked and could not get an answer: busy, locked, a letter mid-flux. Reporting
                // this as absent is what made a populated folder list as empty, so it is reported
                // as exactly what it is.
                parts.Add(new PoolPart
                {
                    PartId = drive.PartId,
                    Volume = volume.Id,
                    State = PoolPartState.Unavailable,
                    Label = volume.Label,
                    DriveLetter = volume.DriveLetter,
                    SpeedFactor = drive.SpeedFactorOverride ?? 1.0,
                });
                continue;
            }

            if (presence is FolderPresence.Absent)
            {
                // Volume is present but the part folder is definitively not there: offline rather
                // than silently recreating it and hiding data loss. Still graced, because a drive
                // that has just re-enumerated can answer this way for a moment.
                parts.Add(new PoolPart
                {
                    PartId = drive.PartId,
                    Volume = volume.Id,
                    State = AbsenceState(drive.PartId, now),
                    Label = volume.Label,
                    DriveLetter = volume.DriveLetter,
                    SpeedFactor = drive.SpeedFactorOverride ?? 1.0,
                });
                continue;
            }

            MarkSeen(drive.PartId, now);

            var usage = UsageMeter?.Get(drive.PartId) ?? PartUsage.Unknown;

            parts.Add(ToPart(volume, drive.PartId, root) with
            {
                SpeedFactor = drive.SpeedFactorOverride ?? 1.0,
                PoolBytes = usage.IsKnown ? usage.Bytes : null,
                PoolFileCount = usage.IsKnown ? usage.FileCount : null,
            });
        }

        return new PoolSnapshot
        {
            PoolId = definition.Id,
            Name = definition.Name,
            Parts = parts,
        };
    }

    /// <summary>
    /// Finds the drive's volume by GUID. When the config has no GUID yet (a migrated v0 document),
    /// falls back to scanning volumes for the part folder, which also recovers a re-imaged config.
    /// </summary>
    private VolumeInfo? ResolveVolume(PoolDriveDefinition drive, IReadOnlyList<VolumeInfo> volumes)
    {
        if (VolumeId.TryParse(drive.VolumeId, out var volumeId))
        {
            var match = volumes.FirstOrDefault(v => v.Id == volumeId);
            if (match is not null)
            {
                return match;
            }
        }

        if (drive.PartId == Guid.Empty)
        {
            return null;
        }

        foreach (var volume in volumes)
        {
            if (volume.RootPath is null)
            {
                continue;
            }

            if (Directory.Exists(PoolPartLayout.RootPathFor(volume.RootPath, drive.PartId)))
            {
                return volume;
            }
        }

        return null;
    }

    /// <summary>What we could establish about a part folder — which is not always a yes or a no.</summary>
    private enum FolderPresence
    {
        Present,
        Absent,
        Unreadable,
    }

    /// <summary>
    /// Whether the part folder is there. <see cref="Directory.Exists(string)"/> alone cannot answer
    /// this: it returns false both for "not there" and for "I could not find out", and treating
    /// those the same is what let a drive's files vanish from the pool for a moment.
    /// </summary>
    private static FolderPresence PartFolderPresence(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                return FolderPresence.Present;
            }

            // Exists said no. Ask whether the volume root itself is readable: if the drive answers
            // normally, the folder really is gone; if even this fails, we learned nothing.
            var volumeRoot = Path.GetPathRoot(root);
            if (string.IsNullOrEmpty(volumeRoot))
            {
                return FolderPresence.Absent;
            }

            _ = new DirectoryInfo(volumeRoot).GetDirectories(PoolPartLayout.FolderPrefix + "*");
            return FolderPresence.Absent;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return FolderPresence.Unreadable;
        }
    }

    /// <summary>
    /// A part we cannot find is unreachable until the grace period runs out, and only then offline.
    /// </summary>
    private PoolPartState AbsenceState(Guid partId, DateTimeOffset now)
    {
        lock (_lastSeenGate)
        {
            if (!_lastSeen.TryGetValue(partId, out var seen))
            {
                // Never seen in this process: nothing to protect, so take it at face value.
                return PoolPartState.Offline;
            }

            if (now - seen <= PresenceGracePeriod)
            {
                return PoolPartState.Unavailable;
            }

            _lastSeen.Remove(partId);
            return PoolPartState.Offline;
        }
    }

    private void MarkSeen(Guid partId, DateTimeOffset now)
    {
        lock (_lastSeenGate)
        {
            _lastSeen[partId] = now;
        }
    }

    private static PoolPart ToPart(VolumeInfo volume, Guid partId, string root) => new()
    {
        PartId = partId,
        Volume = volume.Id,
        RootPath = root,
        State = PoolPartState.Online,
        TotalBytes = volume.TotalBytes,
        FreeBytes = volume.FreeBytes,
        Label = volume.Label,
        DriveLetter = volume.DriveLetter,
    };
}
