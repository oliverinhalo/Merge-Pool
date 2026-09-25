using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using MergePool.Core.Config;
using MergePool.Core.FileSystem;
using MergePool.Core.Model;
using MergePool.Core.Placement;
using MergePool.Core.Rebalance;
using MergePool.Core.Paths;
using MergePool.Core.Volumes;

namespace MergePool.Engine;

/// <summary>What to do with the pool's content when a drive leaves.</summary>
public enum DriveRemoval
{
    /// <summary>
    /// Forget the drive. Its pool part folder and everything in it stay on the drive as ordinary
    /// files; they just stop being part of the pool. Nothing is deleted or moved.
    /// </summary>
    Detach = 0,

    /// <summary>
    /// Move the pool's content off the drive onto the pool's other drives first, then forget it.
    /// The drive only leaves once it is actually empty.
    /// </summary>
    Evacuate = 1,
}

public sealed record DriveRemovalResult
{
    /// <summary>False when the drive stayed in the pool because its content could not all be moved.</summary>
    public required bool Removed { get; init; }

    public required DriveRemoval Mode { get; init; }

    /// <summary>Set for <see cref="DriveRemoval.Evacuate"/>: what actually moved.</summary>
    public EvacuationResult? Evacuation { get; init; }

    /// <summary>True when the emptied pool part folder was cleaned off the drive.</summary>
    public required bool PartFolderRemoved { get; init; }

    public required string Message { get; init; }
}

public sealed record PoolEngineOptions
{
    public required ConfigStore ConfigStore { get; init; }

    public required IVolumeProvider VolumeProvider { get; init; }

    public required IPoolMountService MountService { get; init; }

    public IFileSecurityCopier? SecurityCopier { get; init; }

    public TimeProvider? TimeProvider { get; init; }

    /// <summary>How long a measured part size is trusted before it is walked again.</summary>
    public TimeSpan? UsageMeasurementTtl { get; init; }
}

/// <summary>
/// The pool engine the service hosts: owns the configuration, the per-pool runtimes, drive metrics
/// and the rebalancer, and is the only thing that mounts or unmounts.
/// </summary>
public sealed class PoolEngine : IDisposable
{
    private readonly ConfigStore _configStore;
    private readonly IVolumeProvider _volumes;
    private readonly IPoolMountService _mounts;
    private readonly PoolPartManager _partManager;
    private readonly FileRelocator _relocator;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<Guid, PoolRuntime> _runtimes = [];
    private readonly object _gate = new();
    private readonly long _startedTimestamp = Stopwatch.GetTimestamp();

    private MergePoolConfig _config;
    private bool _draining;

    public PoolEngine(PoolEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _configStore = options.ConfigStore;
        _volumes = options.VolumeProvider;
        _mounts = options.MountService;
        _timeProvider = options.TimeProvider ?? TimeProvider.System;
        UsageMeter = new PartUsageMeter(options.UsageMeasurementTtl, _timeProvider);
        _partManager = new PoolPartManager(_volumes, _timeProvider, UsageMeter);
        _relocator = new FileRelocator(options.SecurityCopier);

        var load = _configStore.Load();
        _config = load.Config;
        ConfigWasMigrated = load.Migrated;
        ConfigBackupPath = load.BackupPath;

        Metrics = new DriveMetricsTracker(_config.Placement, _timeProvider);
        IdleProbe = new IdleProbe(Metrics, _config.Placement, _timeProvider);

        foreach (var definition in _config.Pools)
        {
            _runtimes[definition.Id] = CreateRuntime(definition);
        }
    }

    public DriveMetricsTracker Metrics { get; }

    /// <summary>What the pool is holding on each drive, as opposed to what the drive is holding.</summary>
    public PartUsageMeter UsageMeter { get; }

    public IdleProbe IdleProbe { get; }

    public bool ConfigWasMigrated { get; }

    public string? ConfigBackupPath { get; }

    public bool IsDraining
    {
        get
        {
            lock (_gate)
            {
                return _draining;
            }
        }
    }

    public string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public double UptimeSeconds => Stopwatch.GetElapsedTime(_startedTimestamp).TotalSeconds;

    public MergePoolConfig Config
    {
        get
        {
            lock (_gate)
            {
                return _config;
            }
        }
    }

    public IReadOnlyList<PoolRuntime> Pools
    {
        get
        {
            lock (_gate)
            {
                return _runtimes.Values.ToArray();
            }
        }
    }

    public PoolRuntime? FindPool(Guid poolId)
    {
        lock (_gate)
        {
            return _runtimes.GetValueOrDefault(poolId);
        }
    }

    public IReadOnlyList<VolumeInfo> GetVolumes() => _volumes.GetVolumes();

    /// <summary>Maps a volume to the pool that already has a part on it, if any.</summary>
    public Guid? FindPoolForVolume(VolumeId volumeId)
    {
        lock (_gate)
        {
            foreach (var runtime in _runtimes.Values)
            {
                if (runtime.Definition.Drives.Any(d =>
                        VolumeId.TryParse(d.VolumeId, out var id) && id == volumeId))
                {
                    return runtime.PoolId;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Creates a pool: a <c>.PoolPart-{GUID}</c> folder on each chosen drive, a config entry, and
    /// (optionally) a mount. Existing drive content is not touched.
    /// </summary>
    public PoolRuntime CreatePool(
        string name,
        string mountPoint,
        IReadOnlyList<VolumeId> volumeIds,
        bool adoptExistingContent = false,
        bool mountImmediately = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(volumeIds);

        if (volumeIds.Count == 0)
        {
            throw new ArgumentException("A pool needs at least one drive.", nameof(volumeIds));
        }

        var definition = new PoolDefinition { Name = name, MountPoint = mountPoint };
        var adopter = new PoolAdopter();

        foreach (var volumeId in volumeIds)
        {
            var volume = _volumes.TryGetVolume(volumeId)
                ?? throw new InvalidOperationException($"Drive {volumeId} is not available.");

            if (!volume.IsPoolable)
            {
                throw new InvalidOperationException($"Drive {volume.Label} ({volumeId}) cannot be pooled.");
            }

            var part = _partManager.CreatePart(volume, definition.Id, name);

            definition.Drives.Add(new PoolDriveDefinition
            {
                VolumeId = volumeId.ToVolumePath(),
                PartId = part.PartId,
                Label = volume.Label,
                LastKnownLetter = volume.DriveLetter?.ToString(CultureInfo.InvariantCulture),
            });

            if (adoptExistingContent && volume.RootPath is not null)
            {
                var plan = adopter.Plan(volume.RootPath, part.PartId);
                adopter.Adopt(volume.RootPath, part.PartId, plan);
            }
        }

        PoolRuntime runtime;
        lock (_gate)
        {
            _config.Pools.Add(definition);
            runtime = CreateRuntime(definition);
            _runtimes[definition.Id] = runtime;
            _configStore.Save(_config);
        }

        if (mountImmediately && !string.IsNullOrEmpty(mountPoint))
        {
            Mount(definition.Id, mountPoint);
        }

        return runtime;
    }

    /// <summary>
    /// Adds drives to a pool that already exists. Each new drive gets its own
    /// <c>.PoolPart-{GUID}</c> folder and nothing else on it is read, moved or modified unless
    /// adoption is asked for. The pool does not need unmounting: the extra space and any adopted
    /// files show up in the mounted drive as soon as the topology is re-resolved.
    /// </summary>
    public PoolRuntime AddDrives(
        Guid poolId,
        IReadOnlyList<VolumeId> volumeIds,
        bool adoptExistingContent = false)
    {
        ArgumentNullException.ThrowIfNull(volumeIds);

        if (volumeIds.Count == 0)
        {
            throw new ArgumentException("No drives were given to add.", nameof(volumeIds));
        }

        var runtime = FindPool(poolId) ?? throw new KeyNotFoundException($"No pool {poolId:D}.");

        // Everything is checked before any drive is touched, so one unusable drive in the list
        // cannot leave the pool half-extended.
        var volumes = new List<VolumeInfo>(volumeIds.Count);
        foreach (var volumeId in volumeIds.Distinct())
        {
            var owner = FindPoolForVolume(volumeId);
            if (owner == poolId)
            {
                throw new InvalidOperationException($"Drive {volumeId} is already in this pool.");
            }

            if (owner is not null)
            {
                throw new InvalidOperationException($"Drive {volumeId} is already in another pool.");
            }

            var volume = _volumes.TryGetVolume(volumeId)
                ?? throw new InvalidOperationException($"Drive {volumeId} is not available.");

            if (!volume.IsPoolable)
            {
                throw new InvalidOperationException($"Drive {volume.Label} ({volumeId}) cannot be pooled.");
            }

            volumes.Add(volume);
        }

        var adopter = new PoolAdopter();
        var added = new List<PoolDriveDefinition>(volumes.Count);

        foreach (var volume in volumes)
        {
            var part = _partManager.CreatePart(volume, poolId, runtime.Definition.Name);

            added.Add(new PoolDriveDefinition
            {
                VolumeId = volume.Id.ToVolumePath(),
                PartId = part.PartId,
                Label = volume.Label,
                LastKnownLetter = volume.DriveLetter?.ToString(CultureInfo.InvariantCulture),
            });

            if (adoptExistingContent && volume.RootPath is not null)
            {
                var plan = adopter.Plan(volume.RootPath, part.PartId);
                adopter.Adopt(volume.RootPath, part.PartId, plan);
            }
        }

        lock (_gate)
        {
            runtime.Definition.Drives.AddRange(added);
            _configStore.Save(_config);
        }

        // The union view reads the definition on every resolve, so this is all a mounted pool needs.
        runtime.Invalidate();
        return runtime;
    }

    /// <summary>
    /// Plans what would be moved if a drive were taken out of a pool with its content kept, without
    /// moving anything. Lets the UI say up front whether the remaining drives have room.
    /// </summary>
    public EvacuationPlan PlanDriveRemoval(Guid poolId, VolumeId volumeId)
    {
        var (runtime, drive) = RequireDrive(poolId, volumeId);
        _ = drive;

        return new PartEvacuator(_relocator).Plan(
            runtime.FreshSnapshot,
            drive.PartId,
            _config.Placement.WriteHeadroomBytes);
    }

    /// <summary>
    /// Takes a drive out of a pool.
    /// </summary>
    /// <remarks>
    /// <para><see cref="DriveRemoval.Detach"/> only forgets the drive. Its
    /// <c>.PoolPart-{GUID}</c> folder and every file in it stay exactly where they are, as ordinary
    /// files — they simply stop appearing in the pool. Nothing is deleted.</para>
    /// <para><see cref="DriveRemoval.Evacuate"/> first moves the pool's content off that drive onto
    /// the pool's other drives, whole file by whole file, and only detaches once the drive is
    /// actually empty. If anything could not be moved — no room, or a file another process has open
    /// — the drive stays in the pool and the caller is told why, because a half-emptied drive that
    /// has already left the pool is how data gets lost.</para>
    /// </remarks>
    public DriveRemovalResult RemoveDrive(
        Guid poolId,
        VolumeId volumeId,
        DriveRemoval mode = DriveRemoval.Detach,
        IProgress<EvacuationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var (runtime, drive) = RequireDrive(poolId, volumeId);

        if (runtime.Definition.Drives.Count <= 1 && mode is DriveRemoval.Evacuate)
        {
            throw new InvalidOperationException(
                "This is the pool's only drive, so there is nowhere to move its files to. "
                + "Remove the pool instead — that leaves every file on the drive.");
        }

        EvacuationResult? evacuation = null;
        var partRemoved = false;

        if (mode is DriveRemoval.Evacuate)
        {
            var evacuator = new PartEvacuator(_relocator);
            var plan = evacuator.Plan(
                runtime.FreshSnapshot, drive.PartId, _config.Placement.WriteHeadroomBytes);

            if (!plan.Fits)
            {
                throw new InvalidOperationException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The pool's other drives have {plan.DestinationFreeBytes:N0} bytes free, which is not "
                    + $"enough for the {plan.WithoutRoom.Count} file(s) that would have to move. "
                    + $"Free up space, or remove the drive without moving its files."));
            }

            evacuation = evacuator.Run(runtime.Topology, plan, progress, cancellationToken);

            if (!evacuation.IsComplete)
            {
                // The drive stays in the pool. Its files are either still on it or safely on another
                // drive; either way the pool can still see all of them.
                return new DriveRemovalResult
                {
                    Removed = false,
                    Mode = mode,
                    Evacuation = evacuation,
                    PartFolderRemoved = false,
                    Message = DescribeIncompleteEvacuation(evacuation),
                };
            }
        }

        lock (_gate)
        {
            runtime.Definition.Drives.RemoveAll(d =>
                VolumeId.TryParse(d.VolumeId, out var id) && id == volumeId);
            _configStore.Save(_config);
        }

        UsageMeter.Invalidate(drive.PartId);
        runtime.Invalidate();

        if (mode is DriveRemoval.Evacuate)
        {
            // Only ever the empty shell: TryRemoveEmptyPart stops at the first thing that is not empty.
            var root = ResolvePartRoot(volumeId, drive.PartId);
            partRemoved = root is not null && PartEvacuator.TryRemoveEmptyPart(root);
        }

        return new DriveRemovalResult
        {
            Removed = true,
            Mode = mode,
            Evacuation = evacuation,
            PartFolderRemoved = partRemoved,
            Message = mode is DriveRemoval.Evacuate
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"Moved {evacuation!.MovedCount} file(s) onto the remaining drives and removed the drive from the pool.")
                : "The drive left the pool. Every file it held is still on it, in its pool part folder.",
        };
    }

    private (PoolRuntime Runtime, PoolDriveDefinition Drive) RequireDrive(Guid poolId, VolumeId volumeId)
    {
        var runtime = FindPool(poolId) ?? throw new KeyNotFoundException($"No pool {poolId:D}.");

        var drive = runtime.Definition.Drives.FirstOrDefault(d =>
                        VolumeId.TryParse(d.VolumeId, out var id) && id == volumeId)
                    ?? throw new KeyNotFoundException($"Drive {volumeId} is not in pool '{runtime.Definition.Name}'.");

        return (runtime, drive);
    }

    private string? ResolvePartRoot(VolumeId volumeId, Guid partId)
    {
        var volume = _volumes.TryGetVolume(volumeId);
        return volume?.RootPath is null ? null : PoolPartLayout.RootPathFor(volume.RootPath, partId);
    }

    private static string DescribeIncompleteEvacuation(EvacuationResult evacuation)
    {
        if (evacuation.Cancelled)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Stopped after moving {evacuation.MovedCount} file(s). The drive is still in the pool and nothing was lost.");
        }

        var parts = new List<string>();
        if (evacuation.SkippedInUse.Count > 0)
        {
            parts.Add($"{evacuation.SkippedInUse.Count} file(s) are open in another program");
        }

        if (evacuation.Failed.Count > 0)
        {
            parts.Add($"{evacuation.Failed.Count} file(s) could not be moved");
        }

        const string advice = "The drive is still in the pool: close the files and try again, or "
            + "remove the drive without moving its files.";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Moved {evacuation.MovedCount} file(s), but {string.Join(" and ", parts)}. {advice}");
    }

    /// <summary>
    /// Removes a pool from the configuration. Pool parts are left on the drives unless explicitly
    /// asked for, because removing a pool must never destroy data.
    /// </summary>
    public void RemovePool(Guid poolId, bool deletePoolParts = false)
    {
        Unmount(poolId);

        PoolRuntime? runtime;
        lock (_gate)
        {
            if (!_runtimes.Remove(poolId, out runtime))
            {
                throw new KeyNotFoundException($"No pool {poolId:D}.");
            }

            _config.Pools.RemoveAll(p => p.Id == poolId);
            _configStore.Save(_config);
        }

        if (deletePoolParts)
        {
            foreach (var part in runtime!.Snapshot.OnlineParts)
            {
                try
                {
                    Directory.Delete(part.RequireRootPath(), recursive: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Reported through health; a locked file must not fail the removal.
                }
            }
        }

        runtime!.Dispose();
    }

    public void Mount(Guid poolId, string? mountPointOverride = null)
    {
        var runtime = FindPool(poolId) ?? throw new KeyNotFoundException($"No pool {poolId:D}.");

        if (IsDraining)
        {
            throw new InvalidOperationException("The service is draining for an upgrade.");
        }

        if (_mounts.IsMounted(poolId))
        {
            return;
        }

        var mountPoint = mountPointOverride ?? runtime.Definition.MountPoint;
        if (string.IsNullOrWhiteSpace(mountPoint))
        {
            throw new InvalidOperationException("The pool has no mount point.");
        }

        runtime.Invalidate();
        _mounts.Mount(runtime.FileSystem, new PoolMountOptions
        {
            PoolId = poolId,
            MountPoint = mountPoint,
            VolumeLabel = runtime.Definition.Name,
        });

        if (!string.Equals(runtime.Definition.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase))
        {
            lock (_gate)
            {
                runtime.Definition.MountPoint = mountPoint;
                _configStore.Save(_config);
            }
        }
    }

    public void Unmount(Guid poolId) => _mounts.Unmount(poolId);

    public bool IsMounted(Guid poolId) => _mounts.IsMounted(poolId);

    public string? GetMountPoint(Guid poolId) => _mounts.GetMountPoint(poolId);

    /// <summary>
    /// Stops accepting new work and unmounts every pool, so the service can be replaced. Called by
    /// the updater before swapping versions.
    /// </summary>
    public void Drain()
    {
        List<PoolRuntime> runtimes;
        lock (_gate)
        {
            _draining = true;
            runtimes = _runtimes.Values.ToList();
        }

        foreach (var runtime in runtimes)
        {
            runtime.Rebalancer.Pause();
            _mounts.Unmount(runtime.PoolId);
        }
    }

    /// <summary>Mounts everything again after a drain, and reports what came back.</summary>
    public IReadOnlyList<Guid> Resume()
    {
        lock (_gate)
        {
            _draining = false;
        }

        var mounted = new List<Guid>();
        foreach (var runtime in Pools)
        {
            runtime.Invalidate();
            runtime.Rebalancer.Resume();

            if (string.IsNullOrWhiteSpace(runtime.Definition.MountPoint) || !runtime.Definition.Enabled)
            {
                continue;
            }

            try
            {
                Mount(runtime.PoolId);
                mounted.Add(runtime.PoolId);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                // Health check reports the pool that did not come back.
            }
        }

        return mounted;
    }

    /// <summary>Mounts every enabled pool. Called at service start.</summary>
    public void MountAll() => Resume();

    /// <summary>
    /// One pass of background upkeep: re-resolve drives, probe idle ones, re-evaluate throttles.
    /// Cheap enough to run on a short timer.
    /// </summary>
    public void Tick(CancellationToken cancellationToken = default)
    {
        Metrics.Tick();

        foreach (var runtime in Pools)
        {
            cancellationToken.ThrowIfCancellationRequested();
            runtime.Invalidate();

            // Measuring re-walks a part only once its cached figure has expired, so this is nearly
            // free on most ticks.
            UsageMeter.Refresh(runtime.Snapshot, cancellationToken);
            runtime.Invalidate();

            IdleProbe.ProbeIdleDrives(runtime.Snapshot, cancellationToken);
        }
    }

    /// <summary>Runs one rebalance pass for a pool, if it is enabled and the pool needs it.</summary>
    public RebalanceResult RebalanceOnce(Guid poolId, CancellationToken cancellationToken = default)
    {
        var runtime = FindPool(poolId) ?? throw new KeyNotFoundException($"No pool {poolId:D}.");
        return runtime.Rebalancer.RunOnce(cancellationToken);
    }

    /// <summary>Applies runtime tuning and persists it. Unknown fields in the config are preserved.</summary>
    public void UpdatePlacement(Action<PlacementOptions> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        lock (_gate)
        {
            update(_config.Placement);
            _configStore.Save(_config);
        }
    }

    /// <summary>Applies update preferences and persists them, preserving unknown config fields.</summary>
    public void UpdateUpdateOptions(Action<UpdateOptions> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        lock (_gate)
        {
            update(_config.Updates);
            _configStore.Save(_config);
        }
    }

    /// <summary>Everything wrong with the pools right now, empty when all is well.</summary>
    public IReadOnlyList<string> CheckHealth()
    {
        var problems = new List<string>();

        if (!_mounts.IsAvailable(out _))
        {
            problems.Add("WinFsp is not installed, so pools cannot be mounted.");
        }

        foreach (var runtime in Pools)
        {
            var snapshot = runtime.FreshSnapshot;

            foreach (var part in snapshot.Parts.Where(p => !p.IsOnline))
            {
                problems.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Pool '{runtime.Definition.Name}': drive {part.Volume} is missing."));
            }

            if (!string.IsNullOrWhiteSpace(runtime.Definition.MountPoint)
                && runtime.Definition.Enabled
                && !_mounts.IsMounted(runtime.PoolId)
                && !IsDraining)
            {
                problems.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Pool '{runtime.Definition.Name}' is not mounted at {runtime.Definition.MountPoint}."));
            }
        }

        return problems;
    }

    private PoolRuntime CreateRuntime(PoolDefinition definition) =>
        new(definition, _partManager, _config.Placement, Metrics, _relocator, _timeProvider);

    public void Dispose()
    {
        foreach (var runtime in Pools)
        {
            _mounts.Unmount(runtime.PoolId);
            runtime.Dispose();
        }
    }
}
