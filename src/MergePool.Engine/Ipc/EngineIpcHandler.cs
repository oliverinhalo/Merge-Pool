using System.Globalization;
using MergePool.Core.Model;
using MergePool.Core.Placement;
using MergePool.Core.Volumes;
using MergePool.Ipc.Protocol;
using MergePool.Ipc.Server;
using MergePool.Ipc.Transport;
using IpcMethods = MergePool.Ipc.Protocol.Methods;

namespace MergePool.Engine.Ipc;

/// <summary>
/// Exposes the engine over the named pipe. Every method is additive: once a name is answered here
/// it keeps being answered, so an old UI is never left calling something that vanished.
/// </summary>
public sealed class EngineIpcHandler(PoolEngine engine, AutoUpdateService? updates = null) : IIpcMethodHandler
{
    private readonly PoolEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    /// <summary>
    /// Absent in builds with nowhere to install to (a dev run, or an install whose layout cannot be
    /// found). The update methods then report that updating is unavailable instead of failing.
    /// </summary>
    private readonly AutoUpdateService? _updates = updates;

    public IReadOnlyList<string> Methods { get; } =
    [
        IpcMethods.ServiceStatus,
        IpcMethods.DrivesList,
        IpcMethods.PoolsList,
        IpcMethods.PoolStatus,
        IpcMethods.PoolCreate,
        IpcMethods.PoolRemove,
        IpcMethods.PoolAddDrives,
        IpcMethods.PoolRemoveDrive,
        IpcMethods.PoolPlanDriveRemoval,
        IpcMethods.PoolMount,
        IpcMethods.PoolUnmount,
        IpcMethods.MetricsGet,
        IpcMethods.RebalanceStart,
        IpcMethods.RebalancePause,
        IpcMethods.RebalanceResume,
        IpcMethods.RebalanceStop,
        IpcMethods.RebalanceStatus,
        IpcMethods.ConfigGet,
        IpcMethods.ConfigSetPlacement,
        IpcMethods.AdoptionPlan,
        IpcMethods.AdoptionRun,
        IpcMethods.UpgradeDrain,
        IpcMethods.UpgradeResume,
        IpcMethods.UpdateStatus,
        IpcMethods.UpdateCheck,
        IpcMethods.UpdateApply,
        IpcMethods.UpdateSetOptions,
        IpcMethods.HealthCheck,
    ];

    private CancellationTokenSource? _rebalanceCancellation;
    private Task<Core.Rebalance.RebalanceResult>? _rebalanceTask;
    private Guid _rebalancePoolId;

    public async Task<IpcResponse> HandleAsync(IpcCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);

        var id = call.Request.Id;

        try
        {
            switch (call.Request.Method)
            {
                case IpcMethods.UpdateCheck:
                    return await CheckForUpdateAsync(call, cancellationToken).ConfigureAwait(false);

                case IpcMethods.UpdateApply:
                    return await ApplyUpdateAsync(call, cancellationToken).ConfigureAwait(false);
            }

            return call.Request.Method switch
            {
                IpcMethods.ServiceStatus => Ok(id, ServiceStatus()),
                IpcMethods.DrivesList => Ok(id, ListDrives()),
                IpcMethods.PoolsList => Ok(id, ListPools()),
                IpcMethods.PoolStatus => PoolStatus(call),
                IpcMethods.PoolCreate => CreatePool(call),
                IpcMethods.PoolRemove => RemovePool(call),
                IpcMethods.PoolAddDrives => AddDrives(call),
                IpcMethods.PoolRemoveDrive => RemoveDrive(call),
                IpcMethods.PoolPlanDriveRemoval => PlanDriveRemoval(call),
                IpcMethods.PoolMount => MountPool(call),
                IpcMethods.PoolUnmount => UnmountPool(call),
                IpcMethods.MetricsGet => Ok(id, MetricsFor()),
                IpcMethods.RebalanceStart => StartRebalance(call),
                IpcMethods.RebalancePause => PauseRebalance(call),
                IpcMethods.RebalanceResume => ResumeRebalance(call),
                IpcMethods.RebalanceStop => StopRebalance(call),
                IpcMethods.RebalanceStatus => Ok(id, RebalanceStatus()),
                IpcMethods.ConfigGet => Ok(id, PlacementSettings()),
                IpcMethods.ConfigSetPlacement => SetPlacement(call),
                IpcMethods.AdoptionPlan => PlanAdoption(call),
                IpcMethods.AdoptionRun => RunAdoption(call),
                IpcMethods.UpgradeDrain => Drain(call),
                IpcMethods.UpgradeResume => Resume(call),
                IpcMethods.UpdateStatus => Ok(id, UpdateStatus()),
                IpcMethods.UpdateSetOptions => SetUpdateOptions(call),
                IpcMethods.HealthCheck => Ok(id, HealthCheck()),
                _ => IpcResponse.Failure(id, IpcErrorCodes.MethodNotSupported, $"'{call.Request.Method}' is unknown."),
            };
        }
        catch (KeyNotFoundException exception)
        {
            return IpcResponse.Failure(id, IpcErrorCodes.NotFound, exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return IpcResponse.Failure(id, IpcErrorCodes.AccessDenied, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return IpcResponse.Failure(id, IpcErrorCodes.Conflict, exception.Message);
        }
        catch (ArgumentException exception)
        {
            return IpcResponse.Failure(id, IpcErrorCodes.InvalidRequest, exception.Message);
        }
        catch (IOException exception)
        {
            return IpcResponse.Failure(id, IpcErrorCodes.Unavailable, exception.Message);
        }
    }

    private static IpcResponse Ok<T>(string id, T payload) =>
        IpcResponse.Success(id, IpcFraming.ToNode(payload));

    private ServiceStatusResult ServiceStatus()
    {
        var pools = _engine.Pools;
        var available = _engine.Config.Pools.Count;
        _ = available;

        return new ServiceStatusResult
        {
            ServiceVersion = _engine.Version,
            ProtocolVersion = ProtocolVersion.Current,
            UptimeSeconds = _engine.UptimeSeconds,
            Draining = _engine.IsDraining,
            PoolCount = pools.Count,
            MountedCount = pools.Count(p => _engine.IsMounted(p.PoolId)),
            WinFspInstalled = WinFspState(out var version),
            WinFspVersion = version,
        };
    }

    private bool WinFspState(out string? version)
    {
        version = null;
        var runtime = _engine.Pools.FirstOrDefault();
        _ = runtime;
        return _engine.CheckHealth().All(p => !p.Contains("WinFsp", StringComparison.Ordinal));
    }

    private DriveListResult ListDrives()
    {
        var result = new DriveListResult();

        foreach (var volume in _engine.GetVolumes())
        {
            result.Drives.Add(new DriveDto
            {
                VolumeId = volume.Id.ToVolumePath(),
                DriveLetter = volume.DriveLetter?.ToString(CultureInfo.InvariantCulture),
                Label = volume.Label,
                FileSystem = volume.FileSystem,
                Kind = volume.Kind.ToString(),
                TotalBytes = volume.TotalBytes,
                FreeBytes = volume.FreeBytes,
                IsReady = volume.IsReady,
                IsPoolable = volume.IsPoolable,
                PoolId = _engine.FindPoolForVolume(volume.Id),
            });
        }

        return result;
    }

    private PoolListResult ListPools()
    {
        var result = new PoolListResult();
        foreach (var runtime in _engine.Pools)
        {
            result.Pools.Add(Describe(runtime));
        }

        return result;
    }

    private PoolDto Describe(PoolRuntime runtime)
    {
        var snapshot = runtime.FreshSnapshot;

        var dto = new PoolDto
        {
            PoolId = runtime.PoolId,
            Name = runtime.Definition.Name,
            MountPoint = _engine.GetMountPoint(runtime.PoolId) ?? runtime.Definition.MountPoint,
            IsMounted = _engine.IsMounted(runtime.PoolId),
            Health = snapshot.Health.ToString(),
            TotalBytes = snapshot.TotalBytes,
            FreeBytes = snapshot.FreeBytes,
            PoolUsedBytes = snapshot.PoolUsedBytes,
            PoolCapacityBytes = snapshot.PoolCapacityBytes,
            PoolFileCount = snapshot.PoolFileCount,
            ForeignBytes = snapshot.ForeignBytes,
            IsUsageMeasured = snapshot.IsUsageMeasured,
        };

        foreach (var part in snapshot.Parts)
        {
            dto.Parts.Add(Describe(part, _engine.Metrics.GetSnapshot(part.PartId)));
        }

        return dto;
    }

    private static PoolPartDto Describe(PoolPart part, DriveMetricsSnapshot metrics) => new()
    {
        PartId = part.PartId,
        VolumeId = part.Volume.ToVolumePath(),
        DriveLetter = part.DriveLetter?.ToString(CultureInfo.InvariantCulture),
        Label = part.Label,
        State = part.State.ToString(),
        TotalBytes = part.TotalBytes,
        FreeBytes = part.FreeBytes,
        PoolBytes = part.PoolBytes,
        PoolFileCount = part.PoolFileCount,
        PoolCapacityBytes = part.PoolCapacityBytes,
        IsThrottled = metrics.IsThrottled,
        ThrottleReason = metrics.ThrottleReason.ToString(),
        SpeedFactor = metrics.SpeedFactor,
        ThroughputBytesPerSecond = metrics.ThroughputBytesPerSecond,
        BaselineBytesPerSecond = metrics.BaselineBytesPerSecond,
        LatencyMilliseconds = metrics.LatencyMilliseconds,
    };

    private IpcResponse PoolStatus(IpcCall call)
    {
        var request = call.PayloadAs<PoolReference>();
        var runtime = _engine.FindPool(request.PoolId)
            ?? throw new KeyNotFoundException($"No pool {request.PoolId:D}.");

        return Ok(call.Request.Id, Describe(runtime));
    }

    private IpcResponse CreatePool(IpcCall call)
    {
        var request = call.PayloadAs<CreatePoolRequest>();

        if (!TryParseVolumes(request.VolumeIds, out var volumeIds, out var invalid))
        {
            return IpcResponse.Failure(
                call.Request.Id, IpcErrorCodes.InvalidRequest, $"'{invalid}' is not a volume identifier.");
        }

        var runtime = _engine.CreatePool(
            request.Name,
            request.MountPoint,
            volumeIds,
            request.AdoptExistingContent,
            request.MountImmediately);

        return Ok(call.Request.Id, Describe(runtime));
    }

    private static bool TryParseVolumes(
        IReadOnlyList<string> raw,
        out List<VolumeId> volumeIds,
        out string? invalid)
    {
        volumeIds = new List<VolumeId>(raw.Count);
        foreach (var value in raw)
        {
            if (!VolumeId.TryParse(value, out var volumeId))
            {
                invalid = value;
                return false;
            }

            volumeIds.Add(volumeId);
        }

        invalid = null;
        return true;
    }

    private IpcResponse AddDrives(IpcCall call)
    {
        var request = call.PayloadAs<AddDrivesRequest>();

        if (!TryParseVolumes(request.VolumeIds, out var volumeIds, out var invalid))
        {
            return IpcResponse.Failure(
                call.Request.Id, IpcErrorCodes.InvalidRequest, $"'{invalid}' is not a volume identifier.");
        }

        var runtime = _engine.AddDrives(request.PoolId, volumeIds, request.AdoptExistingContent);
        return Ok(call.Request.Id, Describe(runtime));
    }

    private IpcResponse RemovePool(IpcCall call)
    {
        var request = call.PayloadAs<RemovePoolRequest>();
        _engine.RemovePool(request.PoolId, request.DeletePoolParts);
        return Ok(call.Request.Id, ListPools());
    }

    private IpcResponse PlanDriveRemoval(IpcCall call)
    {
        var request = call.PayloadAs<RemoveDriveRequest>();

        if (!VolumeId.TryParse(request.VolumeId, out var volumeId))
        {
            return IpcResponse.Failure(
                call.Request.Id, IpcErrorCodes.InvalidRequest, $"'{request.VolumeId}' is not a volume identifier.");
        }

        var plan = _engine.PlanDriveRemoval(request.PoolId, volumeId);

        return Ok(call.Request.Id, new DriveRemovalPlanResult
        {
            FileCount = plan.Items.Count + plan.WithoutRoom.Count,
            TotalBytes = plan.TotalBytes,
            DestinationFreeBytes = plan.DestinationFreeBytes,
            Fits = plan.Fits,
            WithoutRoomCount = plan.WithoutRoom.Count,
        });
    }

    private IpcResponse RemoveDrive(IpcCall call)
    {
        var request = call.PayloadAs<RemoveDriveRequest>();

        if (!VolumeId.TryParse(request.VolumeId, out var volumeId))
        {
            return IpcResponse.Failure(
                call.Request.Id, IpcErrorCodes.InvalidRequest, $"'{request.VolumeId}' is not a volume identifier.");
        }

        var result = _engine.RemoveDrive(
            request.PoolId,
            volumeId,
            request.MoveFilesOff ? Engine.DriveRemoval.Evacuate : Engine.DriveRemoval.Detach);

        var runtime = _engine.FindPool(request.PoolId);

        return Ok(call.Request.Id, new DriveRemovalResultDto
        {
            Removed = result.Removed,
            MovedFilesOff = result.Mode is Engine.DriveRemoval.Evacuate,
            MovedCount = result.Evacuation?.MovedCount ?? 0,
            MovedBytes = result.Evacuation?.MovedBytes ?? 0,
            SkippedInUse = [.. result.Evacuation?.SkippedInUse ?? []],
            Failed = [.. result.Evacuation?.Failed ?? []],
            PartFolderRemoved = result.PartFolderRemoved,
            Message = result.Message,
            Pool = runtime is null ? null : Describe(runtime),
        });
    }

    private UpdateStatusResult UpdateStatus()
    {
        var options = _engine.Config.Updates;

        if (_updates is null)
        {
            return new UpdateStatusResult
            {
                InstalledVersion = _engine.Version,
                Stage = UpdateStage.Unavailable.ToString(),
                LastError = "This MergePool is not running from a managed install, so it cannot update itself.",
                AutomaticChecks = options.AutomaticChecks,
                AutomaticInstall = options.AutomaticInstall,
                CheckIntervalHours = options.CheckIntervalHours,
            };
        }

        var state = _updates.State;

        return new UpdateStatusResult
        {
            InstalledVersion = state.InstalledVersion,
            AvailableVersion = state.Available?.Version,
            UpdateAvailable = state.UpdateAvailable,
            ReleaseNotes = state.Available?.Notes,
            ReleaseUrl = state.Available?.ReleaseUrl?.ToString(),
            DownloadBytes = state.Available?.PackageBytes ?? 0,
            Stage = state.Stage.ToString(),
            Progress = state.Progress,
            LastCheckedUtc = state.LastCheckedUtc ?? options.LastCheckedUtc,
            LastError = state.LastError,
            AutomaticChecks = options.AutomaticChecks,
            AutomaticInstall = options.AutomaticInstall,
            CheckIntervalHours = options.CheckIntervalHours,
            InstalledVersions = [.. state.InstalledVersions],
        };
    }

    private async Task<IpcResponse> CheckForUpdateAsync(IpcCall call, CancellationToken cancellationToken)
    {
        if (_updates is null)
        {
            return Ok(call.Request.Id, UpdateStatus());
        }

        await _updates.CheckAsync(_engine.Config.Updates, cancellationToken).ConfigureAwait(false);
        _engine.UpdateUpdateOptions(options => options.LastCheckedUtc = DateTimeOffset.UtcNow);

        return Ok(call.Request.Id, UpdateStatus());
    }

    private async Task<IpcResponse> ApplyUpdateAsync(IpcCall call, CancellationToken cancellationToken)
    {
        if (_updates is null)
        {
            return IpcResponse.Failure(
                call.Request.Id,
                IpcErrorCodes.Unavailable,
                "This MergePool is not running from a managed install, so it cannot update itself.");
        }

        await _updates.ApplyAsync(cancellationToken).ConfigureAwait(false);
        return Ok(call.Request.Id, UpdateStatus());
    }

    private IpcResponse SetUpdateOptions(IpcCall call)
    {
        var request = call.PayloadAs<UpdateSettingsDto>();

        _engine.UpdateUpdateOptions(options =>
        {
            options.AutomaticChecks = request.AutomaticChecks ?? options.AutomaticChecks;
            options.AutomaticInstall = request.AutomaticInstall ?? options.AutomaticInstall;
            options.CheckIntervalHours = request.CheckIntervalHours ?? options.CheckIntervalHours;
        });

        return Ok(call.Request.Id, UpdateStatus());
    }

    private IpcResponse MountPool(IpcCall call)
    {
        var request = call.PayloadAs<MountRequest>();
        _engine.Mount(request.PoolId, string.IsNullOrWhiteSpace(request.MountPoint) ? null : request.MountPoint);

        var runtime = _engine.FindPool(request.PoolId)
            ?? throw new KeyNotFoundException($"No pool {request.PoolId:D}.");

        return Ok(call.Request.Id, Describe(runtime));
    }

    private IpcResponse UnmountPool(IpcCall call)
    {
        var request = call.PayloadAs<PoolReference>();
        _engine.Unmount(request.PoolId);

        var runtime = _engine.FindPool(request.PoolId)
            ?? throw new KeyNotFoundException($"No pool {request.PoolId:D}.");

        return Ok(call.Request.Id, Describe(runtime));
    }

    private MetricsResult MetricsFor()
    {
        var result = new MetricsResult();
        foreach (var runtime in _engine.Pools)
        {
            foreach (var part in runtime.Snapshot.Parts)
            {
                result.Drives.Add(Describe(part, _engine.Metrics.GetSnapshot(part.PartId)));
            }
        }

        return result;
    }

    private IpcResponse StartRebalance(IpcCall call)
    {
        var request = call.PayloadAs<PoolReference>();
        var runtime = _engine.FindPool(request.PoolId)
            ?? throw new KeyNotFoundException($"No pool {request.PoolId:D}.");

        if (_rebalanceTask is { IsCompleted: false })
        {
            return IpcResponse.Failure(call.Request.Id, IpcErrorCodes.Conflict, "A rebalance is already running.");
        }

        _rebalanceCancellation?.Dispose();
        _rebalanceCancellation = new CancellationTokenSource();
        _rebalancePoolId = runtime.PoolId;
        runtime.Rebalancer.Resume();

        var token = _rebalanceCancellation.Token;
        _rebalanceTask = Task.Run(() => runtime.Rebalancer.RunOnce(token), token);

        return Ok(call.Request.Id, RebalanceStatus());
    }

    private IpcResponse PauseRebalance(IpcCall call)
    {
        foreach (var runtime in _engine.Pools)
        {
            runtime.Rebalancer.Pause();
        }

        return Ok(call.Request.Id, RebalanceStatus());
    }

    private IpcResponse ResumeRebalance(IpcCall call)
    {
        foreach (var runtime in _engine.Pools)
        {
            runtime.Rebalancer.Resume();
        }

        return Ok(call.Request.Id, RebalanceStatus());
    }

    private IpcResponse StopRebalance(IpcCall call)
    {
        _rebalanceCancellation?.Cancel();
        return Ok(call.Request.Id, RebalanceStatus());
    }

    private RebalanceStatusResult RebalanceStatus()
    {
        var runtime = _rebalancePoolId == Guid.Empty ? null : _engine.FindPool(_rebalancePoolId);
        var task = _rebalanceTask;

        var status = new RebalanceStatusResult
        {
            Running = task is { IsCompleted: false },
            Paused = runtime?.Rebalancer.IsPaused ?? false,
        };

        if (task is { IsCompletedSuccessfully: true })
        {
            status.MovedCount = task.Result.MovedCount;
            status.MovedBytes = task.Result.MovedBytes;
        }

        if (runtime is not null)
        {
            var plan = runtime.Rebalancer.Plan();
            status.PlannedCount = plan.Moves.Count;
            status.PlannedBytes = plan.TotalBytes;
            status.Imbalance = plan.ImbalanceBefore;
        }

        return status;
    }

    private PlacementSettingsDto PlacementSettings()
    {
        var placement = _engine.Config.Placement;
        return new PlacementSettingsDto
        {
            FreeSpaceWeight = placement.FreeSpaceWeight,
            SpeedWeight = placement.SpeedWeight,
            MinFreeBytes = placement.MinFreeBytes,
            ThrottleEnterRatio = placement.ThrottleEnterRatio,
            ThrottleExitRatio = placement.ThrottleExitRatio,
            ThrottleCooldownSeconds = placement.ThrottleCooldownSeconds,
            RebalanceEnabled = placement.Rebalance.Enabled,
            RebalanceMaxBytesPerSecond = placement.Rebalance.MaxBytesPerSecond,
        };
    }

    private IpcResponse SetPlacement(IpcCall call)
    {
        var request = call.PayloadAs<PlacementSettingsDto>();

        _engine.UpdatePlacement(placement =>
        {
            // Only the fields the client actually sent are applied, so an older UI cannot reset a
            // setting it has never heard of.
            placement.FreeSpaceWeight = request.FreeSpaceWeight ?? placement.FreeSpaceWeight;
            placement.SpeedWeight = request.SpeedWeight ?? placement.SpeedWeight;
            placement.MinFreeBytes = request.MinFreeBytes ?? placement.MinFreeBytes;
            placement.ThrottleEnterRatio = request.ThrottleEnterRatio ?? placement.ThrottleEnterRatio;
            placement.ThrottleExitRatio = request.ThrottleExitRatio ?? placement.ThrottleExitRatio;
            placement.ThrottleCooldownSeconds = request.ThrottleCooldownSeconds ?? placement.ThrottleCooldownSeconds;
            placement.Rebalance.Enabled = request.RebalanceEnabled ?? placement.Rebalance.Enabled;
            placement.Rebalance.MaxBytesPerSecond =
                request.RebalanceMaxBytesPerSecond ?? placement.Rebalance.MaxBytesPerSecond;
        });

        return Ok(call.Request.Id, PlacementSettings());
    }

    private IpcResponse PlanAdoption(IpcCall call)
    {
        var request = call.PayloadAs<AdoptionPlanRequest>();
        var (volume, partId) = ResolveAdoptionTarget(request.VolumeId, request.PoolId);

        var plan = new PoolAdopter().Plan(volume.RootPath!, partId);
        var result = new AdoptionPlanResult
        {
            Skipped = [.. plan.Skipped],
            TotalBytes = plan.TotalBytes,
        };

        foreach (var item in plan.Items)
        {
            result.Items.Add(new AdoptionItemDto
            {
                Name = item.Name,
                IsDirectory = item.IsDirectory,
                Bytes = item.Bytes,
            });
        }

        return Ok(call.Request.Id, result);
    }

    private IpcResponse RunAdoption(IpcCall call)
    {
        var request = call.PayloadAs<AdoptionPlanRequest>();
        var (volume, partId) = ResolveAdoptionTarget(request.VolumeId, request.PoolId);

        var adopter = new PoolAdopter();
        var plan = adopter.Plan(volume.RootPath!, partId);
        var result = adopter.Adopt(volume.RootPath!, partId, plan);

        return Ok(call.Request.Id, new AdoptionRunResult
        {
            MovedCount = result.MovedCount,
            MovedBytes = result.MovedBytes,
            Failed = [.. result.Failed],
        });
    }

    private (VolumeInfo Volume, Guid PartId) ResolveAdoptionTarget(string rawVolumeId, Guid poolId)
    {
        if (!VolumeId.TryParse(rawVolumeId, out var volumeId))
        {
            throw new ArgumentException($"'{rawVolumeId}' is not a volume identifier.", nameof(rawVolumeId));
        }

        var runtime = _engine.FindPool(poolId) ?? throw new KeyNotFoundException($"No pool {poolId:D}.");
        var drive = runtime.Definition.Drives.FirstOrDefault(d =>
                        VolumeId.TryParse(d.VolumeId, out var id) && id == volumeId)
                    ?? throw new KeyNotFoundException($"Drive {volumeId} is not part of this pool.");

        var volume = _engine.GetVolumes().FirstOrDefault(v => v.Id == volumeId)
                     ?? throw new InvalidOperationException($"Drive {volumeId} is not available.");

        if (volume.RootPath is null)
        {
            throw new InvalidOperationException($"Drive {volumeId} has no mount point.");
        }

        return (volume, drive.PartId);
    }

    private IpcResponse Drain(IpcCall call)
    {
        _engine.Drain();
        return Ok(call.Request.Id, ServiceStatus());
    }

    private IpcResponse Resume(IpcCall call)
    {
        _engine.Resume();
        return Ok(call.Request.Id, ServiceStatus());
    }

    private HealthCheckResult HealthCheck()
    {
        var problems = _engine.CheckHealth();
        return new HealthCheckResult
        {
            Healthy = problems.Count == 0,
            Problems = [.. problems],
        };
    }
}
