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

        foreach (var drive in definition.Drives)
        {
            if (!drive.Enabled)
            {
                continue;
            }

            var volume = ResolveVolume(drive, volumes);
            if (volume?.RootPath is null)
            {
                parts.Add(new PoolPart
                {
                    PartId = drive.PartId,
                    Volume = VolumeId.TryParse(drive.VolumeId, out var id) ? id : VolumeId.Empty,
                    State = PoolPartState.Offline,
                    Label = drive.Label ?? string.Empty,
                    SpeedFactor = drive.SpeedFactorOverride ?? 1.0,
                });
                continue;
            }

            var root = PoolPartLayout.RootPathFor(volume.RootPath, drive.PartId);
            if (!Directory.Exists(root))
            {
                // Volume is present but the part folder is gone: treat as offline rather than
                // silently recreating it and hiding data loss.
                parts.Add(new PoolPart
                {
                    PartId = drive.PartId,
                    Volume = volume.Id,
                    State = PoolPartState.Offline,
                    Label = volume.Label,
                    DriveLetter = volume.DriveLetter,
                    SpeedFactor = drive.SpeedFactorOverride ?? 1.0,
                });
                continue;
            }

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
