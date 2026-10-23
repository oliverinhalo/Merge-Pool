using MergePool.Core.Config;
using MergePool.Core.Volumes;
using MergePool.Engine;
using MergePool.Engine.Ipc;
using MergePool.Ipc.Client;
using MergePool.Ipc.Protocol;
using MergePool.Ipc.Server;
using Xunit;

namespace MergePool.Ipc.Tests;

/// <summary>
/// The real engine handler behind a real pipe: what the UI will actually talk to, minus WinFsp.
/// </summary>
public sealed class EngineOverIpcTests : IAsyncLifetime
{
    private readonly string _pipeName = "mergepool-engine-" + Guid.NewGuid().ToString("N");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mergepool-ipc", Guid.NewGuid().ToString("N"));
    private readonly DirectoryVolumeProvider _volumes = new();
    private readonly InMemoryMountService _mounts = new();

    private PoolEngine _engine = null!;
    private IpcServer _server = null!;

    private VolumeId AddDrive(string name, long total = 10_000_000, long free = 9_000_000)
    {
        var path = Path.Combine(_root, name);
        return _volumes.Add(path, label: name, totalBytes: total, freeBytes: free);
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        _engine = new PoolEngine(new PoolEngineOptions
        {
            ConfigStore = new ConfigStore(Path.Combine(_root, "config.json")),
            VolumeProvider = _volumes,
            MountService = _mounts,

            // These tests are about steady-state degraded service: a drive that is gone reports as
            // offline straight away rather than being graced first. The grace period itself is
            // tested in MergePool.Core.Tests, where a clock can be held still.
            PresenceGracePeriod = TimeSpan.Zero,
        });

        var router = new IpcRouter().Register(new EngineIpcHandler(_engine));
        _server = new IpcServer(
            new IpcServerOptions { PipeName = _pipeName, ServiceVersion = "0.1.0" },
            router);
        _server.Start();

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        _engine.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<IpcClient> ConnectAsync()
    {
        var client = new IpcClient(new IpcClientOptions { PipeName = _pipeName });
        await client.ConnectAsync(CancellationToken.None);
        return client;
    }

    [Fact]
    public async Task Drives_are_listed_with_size_label_and_poolability()
    {
        AddDrive("Alpha", total: 500, free: 200);
        await using var client = await ConnectAsync();

        var drives = await client.InvokeAsync<DriveListResult>(Methods.DrivesList, null, CancellationToken.None);

        var drive = Assert.Single(drives.Drives);
        Assert.Equal("Alpha", drive.Label);
        Assert.Equal(500, drive.TotalBytes);
        Assert.Equal(200, drive.FreeBytes);
        Assert.True(drive.IsPoolable);
        Assert.Null(drive.PoolId);
    }

    [Fact]
    public async Task Creating_a_pool_returns_it_mounted_with_its_parts()
    {
        var first = AddDrive("One");
        var second = AddDrive("Two");
        await using var client = await ConnectAsync();

        var pool = await client.InvokeAsync<PoolDto>(
            Methods.PoolCreate,
            new CreatePoolRequest
            {
                Name = "Media",
                MountPoint = "P:",
                VolumeIds = [first.ToVolumePath(), second.ToVolumePath()],
            },
            CancellationToken.None);

        Assert.Equal("Media", pool.Name);
        Assert.Equal("P:", pool.MountPoint);
        Assert.True(pool.IsMounted);
        Assert.Equal("Healthy", pool.Health);
        Assert.Equal(2, pool.Parts.Count);
        Assert.All(pool.Parts, part => Assert.Equal("Online", part.State));

        // And the pool part folders really exist on the drives.
        Assert.All(
            pool.Parts,
            part => Assert.True(Directory.Exists(
                Path.Combine(_root, part.Label, ".PoolPart-" + part.PartId.ToString("D")))));
    }

    [Fact]
    public async Task A_missing_drive_shows_as_a_degraded_pool()
    {
        var first = AddDrive("One");
        var second = AddDrive("Two");
        await using var client = await ConnectAsync();

        var pool = await client.InvokeAsync<PoolDto>(
            Methods.PoolCreate,
            new CreatePoolRequest { Name = "Media", MountPoint = "P:", VolumeIds = [first.ToVolumePath(), second.ToVolumePath()] },
            CancellationToken.None);

        _volumes.SetPresent(second, present: false);

        var status = await client.InvokeAsync<PoolDto>(
            Methods.PoolStatus,
            new PoolReference { PoolId = pool.PoolId },
            CancellationToken.None);

        Assert.Equal("Degraded", status.Health);
        Assert.Contains(status.Parts, part => part.State == "Offline");

        // It rejoins on its own once the drive is back.
        _volumes.SetPresent(second, present: true);

        var rejoined = await client.InvokeAsync<PoolDto>(
            Methods.PoolStatus,
            new PoolReference { PoolId = pool.PoolId },
            CancellationToken.None);

        Assert.Equal("Healthy", rejoined.Health);
    }

    [Fact]
    public async Task Removing_a_pool_leaves_the_data_on_the_drives()
    {
        var volume = AddDrive("One");
        await using var client = await ConnectAsync();

        var pool = await client.InvokeAsync<PoolDto>(
            Methods.PoolCreate,
            new CreatePoolRequest { Name = "Media", MountPoint = "P:", VolumeIds = [volume.ToVolumePath()] },
            CancellationToken.None);

        var partRoot = Path.Combine(_root, "One", ".PoolPart-" + pool.Parts[0].PartId.ToString("D"));
        File.WriteAllText(Path.Combine(partRoot, "keepme.txt"), "precious");

        var remaining = await client.InvokeAsync<PoolListResult>(
            Methods.PoolRemove,
            new RemovePoolRequest { PoolId = pool.PoolId },
            CancellationToken.None);

        Assert.Empty(remaining.Pools);
        Assert.Equal("precious", File.ReadAllText(Path.Combine(partRoot, "keepme.txt")));
    }

    [Fact]
    public async Task Unmounting_and_mounting_again_works()
    {
        var volume = AddDrive("One");
        await using var client = await ConnectAsync();

        var pool = await client.InvokeAsync<PoolDto>(
            Methods.PoolCreate,
            new CreatePoolRequest { Name = "Media", MountPoint = "P:", VolumeIds = [volume.ToVolumePath()] },
            CancellationToken.None);

        var unmounted = await client.InvokeAsync<PoolDto>(
            Methods.PoolUnmount, new PoolReference { PoolId = pool.PoolId }, CancellationToken.None);
        Assert.False(unmounted.IsMounted);

        var mounted = await client.InvokeAsync<PoolDto>(
            Methods.PoolMount, new MountRequest { PoolId = pool.PoolId, MountPoint = "Q:" }, CancellationToken.None);

        Assert.True(mounted.IsMounted);
        Assert.Equal("Q:", mounted.MountPoint);
    }

    [Fact]
    public async Task Adoption_is_planned_before_it_is_run()
    {
        var volume = AddDrive("One");
        Directory.CreateDirectory(Path.Combine(_root, "One"));
        File.WriteAllText(Path.Combine(_root, "One", "existing.txt"), "mine");

        await using var client = await ConnectAsync();

        var pool = await client.InvokeAsync<PoolDto>(
            Methods.PoolCreate,
            new CreatePoolRequest { Name = "Media", MountPoint = "P:", VolumeIds = [volume.ToVolumePath()] },
            CancellationToken.None);

        var request = new AdoptionPlanRequest { PoolId = pool.PoolId, VolumeId = volume.ToVolumePath() };

        var plan = await client.InvokeAsync<AdoptionPlanResult>(Methods.AdoptionPlan, request, CancellationToken.None);
        Assert.Contains(plan.Items, item => item.Name == "existing.txt");
        Assert.True(File.Exists(Path.Combine(_root, "One", "existing.txt")));

        var run = await client.InvokeAsync<AdoptionRunResult>(Methods.AdoptionRun, request, CancellationToken.None);
        Assert.Equal(1, run.MovedCount);

        var partRoot = Path.Combine(_root, "One", ".PoolPart-" + pool.Parts[0].PartId.ToString("D"));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(partRoot, "existing.txt")));
    }

    [Fact]
    public async Task Placement_settings_round_trip_and_only_sent_fields_change()
    {
        await using var client = await ConnectAsync();

        var before = await client.InvokeAsync<PlacementSettingsDto>(Methods.ConfigGet, null, CancellationToken.None);

        var after = await client.InvokeAsync<PlacementSettingsDto>(
            Methods.ConfigSetPlacement,
            new PlacementSettingsDto { SpeedWeight = 2.5 },
            CancellationToken.None);

        Assert.Equal(2.5, after.SpeedWeight);
        Assert.Equal(before.FreeSpaceWeight, after.FreeSpaceWeight);
        Assert.Equal(before.ThrottleCooldownSeconds, after.ThrottleCooldownSeconds);
    }

    [Fact]
    public async Task The_advanced_tunables_round_trip()
    {
        // These were reachable only by hand-editing the config file before protocol 6. The window
        // now shows them, so every one of them has to survive the trip to the service and back.
        await using var client = await ConnectAsync();

        var after = await client.InvokeAsync<PlacementSettingsDto>(
            Methods.ConfigSetPlacement,
            new PlacementSettingsDto
            {
                WriteHeadroomBytes = 512L * 1024 * 1024,
                LatencyThrottleRatio = 4.5,
                ThrottleEnterSamples = 7,
                ThrottleExitSamples = 9,
                MinSamplesForThrottleDetection = 11,
                EwmaAlpha = 0.4,
                BaselineAlpha = 0.03,
                BaselineRiseAlpha = 0.5,
                ReferenceThroughputBytesPerSecond = 250d * 1024 * 1024,
                MinSpeedFactor = 0.2,
                MaxSpeedFactor = 12,
                MinThroughputSampleBytes = 128 * 1024,
                IdleProbeIntervalSeconds = 300,
                IdleProbeBytes = 2 * 1024 * 1024,
                RebalanceTriggerImbalance = 0.25,
                RebalanceTargetImbalance = 0.08,
                RebalanceMaxFileBytes = 128L * 1024 * 1024 * 1024,
                RebalancePauseWhileBusy = false,
            },
            CancellationToken.None);

        Assert.Equal(512L * 1024 * 1024, after.WriteHeadroomBytes);
        Assert.Equal(4.5, after.LatencyThrottleRatio);
        Assert.Equal(7, after.ThrottleEnterSamples);
        Assert.Equal(9, after.ThrottleExitSamples);
        Assert.Equal(11, after.MinSamplesForThrottleDetection);
        Assert.Equal(0.4, after.EwmaAlpha);
        Assert.Equal(0.03, after.BaselineAlpha);
        Assert.Equal(0.5, after.BaselineRiseAlpha);
        Assert.Equal(250d * 1024 * 1024, after.ReferenceThroughputBytesPerSecond);
        Assert.Equal(0.2, after.MinSpeedFactor);
        Assert.Equal(12, after.MaxSpeedFactor);
        Assert.Equal(128 * 1024, after.MinThroughputSampleBytes);
        Assert.Equal(300, after.IdleProbeIntervalSeconds);
        Assert.Equal(2 * 1024 * 1024, after.IdleProbeBytes);
        Assert.Equal(0.25, after.RebalanceTriggerImbalance);
        Assert.Equal(0.08, after.RebalanceTargetImbalance);
        Assert.Equal(128L * 1024 * 1024 * 1024, after.RebalanceMaxFileBytes);
        Assert.False(after.RebalancePauseWhileBusy);
    }

    [Fact]
    public async Task A_tunable_that_would_wedge_the_engine_comes_back_clamped()
    {
        await using var client = await ConnectAsync();

        var after = await client.InvokeAsync<PlacementSettingsDto>(
            Methods.ConfigSetPlacement,
            new PlacementSettingsDto
            {
                // Exit under enter, a ceiling under its floor, and a rebalance that would never
                // consider itself finished. The service stores none of it as asked.
                ThrottleEnterRatio = 0.85,
                ThrottleExitRatio = 0.2,
                MinSpeedFactor = 0.9,
                MaxSpeedFactor = 0.2,
                RebalanceTriggerImbalance = 0.1,
                RebalanceTargetImbalance = 0.8,
            },
            CancellationToken.None);

        Assert.True(after.ThrottleExitRatio > after.ThrottleEnterRatio);
        Assert.True(after.MaxSpeedFactor > after.MinSpeedFactor);
        Assert.True(after.RebalanceTargetImbalance < after.RebalanceTriggerImbalance);
    }

    [Fact]
    public async Task Drain_and_resume_take_the_pools_down_and_bring_them_back()
    {
        var volume = AddDrive("One");
        await using var client = await ConnectAsync();

        var pool = await client.InvokeAsync<PoolDto>(
            Methods.PoolCreate,
            new CreatePoolRequest { Name = "Media", MountPoint = "P:", VolumeIds = [volume.ToVolumePath()] },
            CancellationToken.None);

        var draining = await client.InvokeAsync<ServiceStatusResult>(Methods.UpgradeDrain, null, CancellationToken.None);
        Assert.True(draining.Draining);
        Assert.Equal(0, draining.MountedCount);

        var resumed = await client.InvokeAsync<ServiceStatusResult>(Methods.UpgradeResume, null, CancellationToken.None);
        Assert.False(resumed.Draining);
        Assert.Equal(1, resumed.MountedCount);

        var status = await client.InvokeAsync<PoolDto>(
            Methods.PoolStatus, new PoolReference { PoolId = pool.PoolId }, CancellationToken.None);
        Assert.True(status.IsMounted);
    }

    [Fact]
    public async Task A_missing_pool_is_a_not_found_error()
    {
        await using var client = await ConnectAsync();

        var exception = await Assert.ThrowsAsync<IpcException>(
            () => client.InvokeAsync<PoolDto>(
                Methods.PoolStatus, new PoolReference { PoolId = Guid.NewGuid() }, CancellationToken.None));

        Assert.Equal(IpcErrorCodes.NotFound, exception.Code);
    }

    [Fact]
    public async Task A_bad_volume_identifier_is_an_invalid_request()
    {
        await using var client = await ConnectAsync();

        var exception = await Assert.ThrowsAsync<IpcException>(
            () => client.InvokeAsync<PoolDto>(
                Methods.PoolCreate,
                new CreatePoolRequest { Name = "X", MountPoint = "P:", VolumeIds = ["D:"] },
                CancellationToken.None));

        Assert.Equal(IpcErrorCodes.InvalidRequest, exception.Code);
    }

    [Fact]
    public async Task Health_reports_a_missing_drive()
    {
        var volume = AddDrive("One");
        await using var client = await ConnectAsync();

        await client.InvokeAsync<PoolDto>(
            Methods.PoolCreate,
            new CreatePoolRequest { Name = "Media", MountPoint = "P:", VolumeIds = [volume.ToVolumePath()] },
            CancellationToken.None);

        Assert.True((await client.InvokeAsync<HealthCheckResult>(Methods.HealthCheck, null, CancellationToken.None)).Healthy);

        _volumes.SetPresent(volume, present: false);

        var health = await client.InvokeAsync<HealthCheckResult>(Methods.HealthCheck, null, CancellationToken.None);
        Assert.False(health.Healthy);
        Assert.Contains(health.Problems, problem => problem.Contains("missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Metrics_are_reported_per_drive()
    {
        var volume = AddDrive("One");
        await using var client = await ConnectAsync();

        var pool = await client.InvokeAsync<PoolDto>(
            Methods.PoolCreate,
            new CreatePoolRequest { Name = "Media", MountPoint = "P:", VolumeIds = [volume.ToVolumePath()] },
            CancellationToken.None);

        _engine.Metrics.RecordWrite(pool.Parts[0].PartId, 16 * 1024 * 1024, TimeSpan.FromMilliseconds(100));

        var metrics = await client.InvokeAsync<MetricsResult>(Methods.MetricsGet, null, CancellationToken.None);

        var drive = Assert.Single(metrics.Drives);
        Assert.True(drive.ThroughputBytesPerSecond > 0);
        Assert.False(drive.IsThrottled);
    }

    [Fact]
    public async Task Configuration_survives_a_service_restart()
    {
        var volume = AddDrive("One");
        await using (var client = await ConnectAsync())
        {
            await client.InvokeAsync<PoolDto>(
                Methods.PoolCreate,
                new CreatePoolRequest { Name = "Media", MountPoint = "P:", VolumeIds = [volume.ToVolumePath()] },
                CancellationToken.None);
        }

        // A fresh engine over the same config file, as if the service had restarted.
        using var restarted = new PoolEngine(new PoolEngineOptions
        {
            ConfigStore = new ConfigStore(Path.Combine(_root, "config.json")),
            VolumeProvider = _volumes,
            MountService = new InMemoryMountService(),
        });

        var pool = Assert.Single(restarted.Pools);
        Assert.Equal("Media", pool.Definition.Name);
        Assert.Equal("P:", pool.Definition.MountPoint);
        Assert.Equal(Core.Model.PoolHealth.Healthy, pool.Snapshot.Health);

        restarted.MountAll();
        Assert.True(restarted.IsMounted(pool.PoolId));
    }

    [Fact]
    public async Task A_drive_can_be_added_to_a_pool_that_is_already_mounted()
    {
        var first = AddDrive("One", total: 1_000, free: 400);
        var second = AddDrive("Two", total: 3_000, free: 2_500);
        await using var client = await ConnectAsync();

        var pool = await client.InvokeAsync<PoolDto>(
            Methods.PoolCreate,
            new CreatePoolRequest { Name = "Media", MountPoint = "P:", VolumeIds = [first.ToVolumePath()] },
            CancellationToken.None);

        var partRoot = Path.Combine(_root, "One", ".PoolPart-" + pool.Parts[0].PartId.ToString("D"));
        File.WriteAllText(Path.Combine(partRoot, "already-pooled.txt"), "still here");

        var grown = await client.InvokeAsync<PoolDto>(
            Methods.PoolAddDrives,
            new AddDrivesRequest { PoolId = pool.PoolId, VolumeIds = [second.ToVolumePath()] },
            CancellationToken.None);

        Assert.Equal(2, grown.Parts.Count);
        Assert.Equal("Healthy", grown.Health);
        Assert.Equal(4_000, grown.TotalBytes);
        Assert.Equal(2_900, grown.FreeBytes);

        // It never went down, and what was already pooled did not move.
        Assert.True(grown.IsMounted);
        Assert.Equal("still here", File.ReadAllText(Path.Combine(partRoot, "already-pooled.txt")));
    }

    [Fact]
    public async Task Adding_a_drive_leaves_the_data_already_on_it_untouched()
    {
        var first = AddDrive("One");
        var second = AddDrive("Two");

        // Two is not empty: it carries files that must survive being pooled, byte for byte.
        var twoRoot = Path.Combine(_root, "Two");
        Directory.CreateDirectory(Path.Combine(twoRoot, "Photos"));
        File.WriteAllText(Path.Combine(twoRoot, "Photos", "holiday.raw"), "irreplaceable");
        File.WriteAllText(Path.Combine(twoRoot, "taxes.pdf"), "also irreplaceable");

        await using var client = await ConnectAsync();

        var pool = await client.InvokeAsync<PoolDto>(
            Methods.PoolCreate,
            new CreatePoolRequest { Name = "Media", MountPoint = "P:", VolumeIds = [first.ToVolumePath()] },
            CancellationToken.None);

        var grown = await client.InvokeAsync<PoolDto>(
            Methods.PoolAddDrives,
            new AddDrivesRequest { PoolId = pool.PoolId, VolumeIds = [second.ToVolumePath()] },
            CancellationToken.None);

        // The only thing added to the drive is its own pool part folder.
        var added = grown.Parts.Single(part => part.Label == "Two");
        Assert.True(Directory.Exists(Path.Combine(twoRoot, ".PoolPart-" + added.PartId.ToString("D"))));

        Assert.Equal("irreplaceable", File.ReadAllText(Path.Combine(twoRoot, "Photos", "holiday.raw")));
        Assert.Equal("also irreplaceable", File.ReadAllText(Path.Combine(twoRoot, "taxes.pdf")));
    }

    [Fact]
    public async Task Adding_a_drive_with_adoption_moves_its_files_in_without_copying_them()
    {
        var first = AddDrive("One");
        var second = AddDrive("Two");

        var twoRoot = Path.Combine(_root, "Two");
        Directory.CreateDirectory(twoRoot);
        File.WriteAllText(Path.Combine(twoRoot, "film.mkv"), "whole file");

        await using var client = await ConnectAsync();

        var pool = await client.InvokeAsync<PoolDto>(
            Methods.PoolCreate,
            new CreatePoolRequest { Name = "Media", MountPoint = "P:", VolumeIds = [first.ToVolumePath()] },
            CancellationToken.None);

        var grown = await client.InvokeAsync<PoolDto>(
            Methods.PoolAddDrives,
            new AddDrivesRequest
            {
                PoolId = pool.PoolId,
                VolumeIds = [second.ToVolumePath()],
                AdoptExistingContent = true,
            },
            CancellationToken.None);

        var added = grown.Parts.Single(part => part.Label == "Two");
        var adopted = Path.Combine(twoRoot, ".PoolPart-" + added.PartId.ToString("D"), "film.mkv");

        // Moved, not copied: it is inside the part and gone from the root, still on the same drive.
        Assert.Equal("whole file", File.ReadAllText(adopted));
        Assert.False(File.Exists(Path.Combine(twoRoot, "film.mkv")));
    }

    [Fact]
    public async Task A_drive_cannot_join_the_same_pool_twice()
    {
        var volume = AddDrive("One");
        await using var client = await ConnectAsync();

        var pool = await client.InvokeAsync<PoolDto>(
            Methods.PoolCreate,
            new CreatePoolRequest { Name = "Media", MountPoint = "P:", VolumeIds = [volume.ToVolumePath()] },
            CancellationToken.None);

        var failure = await Assert.ThrowsAsync<IpcException>(() => client.InvokeAsync<PoolDto>(
            Methods.PoolAddDrives,
            new AddDrivesRequest { PoolId = pool.PoolId, VolumeIds = [volume.ToVolumePath()] },
            CancellationToken.None));

        Assert.Equal(IpcErrorCodes.Conflict, failure.Code);

        var unchanged = await client.InvokeAsync<PoolDto>(
            Methods.PoolStatus, new PoolReference { PoolId = pool.PoolId }, CancellationToken.None);
        Assert.Single(unchanged.Parts);
    }

    [Fact]
    public async Task A_drive_in_another_pool_is_refused_and_nothing_is_half_added()
    {
        var first = AddDrive("One");
        var second = AddDrive("Two");
        var third = AddDrive("Three");
        await using var client = await ConnectAsync();

        var media = await client.InvokeAsync<PoolDto>(
            Methods.PoolCreate,
            new CreatePoolRequest { Name = "Media", MountPoint = "P:", VolumeIds = [first.ToVolumePath()] },
            CancellationToken.None);

        var backups = await client.InvokeAsync<PoolDto>(
            Methods.PoolCreate,
            new CreatePoolRequest { Name = "Backups", MountPoint = "Q:", VolumeIds = [second.ToVolumePath()] },
            CancellationToken.None);

        // Three is free, Two belongs to Backups. The whole call must fail, including Three.
        var failure = await Assert.ThrowsAsync<IpcException>(() => client.InvokeAsync<PoolDto>(
            Methods.PoolAddDrives,
            new AddDrivesRequest
            {
                PoolId = media.PoolId,
                VolumeIds = [third.ToVolumePath(), second.ToVolumePath()],
            },
            CancellationToken.None));

        Assert.Equal(IpcErrorCodes.Conflict, failure.Code);

        var unchanged = await client.InvokeAsync<PoolDto>(
            Methods.PoolStatus, new PoolReference { PoolId = media.PoolId }, CancellationToken.None);
        Assert.Single(unchanged.Parts);

        Assert.Single(
            (await client.InvokeAsync<PoolDto>(
                Methods.PoolStatus, new PoolReference { PoolId = backups.PoolId }, CancellationToken.None)).Parts);

        // And no pool part was left behind on the drive that would have been fine.
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "Three"), ".PoolPart-*"));
    }

    [Fact]
    public async Task An_added_drive_survives_a_service_restart()
    {
        var first = AddDrive("One");
        var second = AddDrive("Two");

        await using (var client = await ConnectAsync())
        {
            var pool = await client.InvokeAsync<PoolDto>(
                Methods.PoolCreate,
                new CreatePoolRequest { Name = "Media", MountPoint = "P:", VolumeIds = [first.ToVolumePath()] },
                CancellationToken.None);

            await client.InvokeAsync<PoolDto>(
                Methods.PoolAddDrives,
                new AddDrivesRequest { PoolId = pool.PoolId, VolumeIds = [second.ToVolumePath()] },
                CancellationToken.None);
        }

        using var restarted = new PoolEngine(new PoolEngineOptions
        {
            ConfigStore = new ConfigStore(Path.Combine(_root, "config.json")),
            VolumeProvider = _volumes,
            MountService = new InMemoryMountService(),
        });

        var reloaded = Assert.Single(restarted.Pools);
        Assert.Equal(2, reloaded.Snapshot.Parts.Count);
        Assert.Equal(Core.Model.PoolHealth.Healthy, reloaded.Snapshot.Health);
    }

    [Fact]
    public async Task Growing_a_pool_is_announced_as_a_capability()
    {
        await using var client = await ConnectAsync();

        Assert.True(client.Supports(Capabilities.PoolEdit));
    }

    [Fact]
    public async Task A_drive_can_leave_a_pool_with_its_files_left_on_it()
    {
        var first = AddDrive("One");
        var second = AddDrive("Two");
        await using var client = await ConnectAsync();

        var pool = await client.InvokeAsync<PoolDto>(
            Methods.PoolCreate,
            new CreatePoolRequest
            {
                Name = "Media",
                MountPoint = "P:",
                VolumeIds = [first.ToVolumePath(), second.ToVolumePath()],
            },
            CancellationToken.None);

        var leaving = pool.Parts.Single(part => part.Label == "Two");
        var partRoot = Path.Combine(_root, "Two", ".PoolPart-" + leaving.PartId.ToString("D"));
        File.WriteAllText(Path.Combine(partRoot, "keepme.txt"), "precious");

        var result = await client.InvokeAsync<DriveRemovalResultDto>(
            Methods.PoolRemoveDrive,
            new RemoveDriveRequest { PoolId = pool.PoolId, VolumeId = second.ToVolumePath() },
            CancellationToken.None);

        Assert.True(result.Removed);
        Assert.False(result.MovedFilesOff);
        Assert.False(result.PartFolderRemoved);
        Assert.Single(result.Pool!.Parts);

        // Nothing was deleted: the file is still on the drive, in the same place.
        Assert.Equal("precious", File.ReadAllText(Path.Combine(partRoot, "keepme.txt")));
    }

    [Fact]
    public async Task A_drive_can_leave_safely_with_its_files_moved_onto_the_others()
    {
        var first = AddDrive("One", total: 10_000, free: 9_000);
        var second = AddDrive("Two", total: 10_000, free: 9_000);
        await using var client = await ConnectAsync();

        var pool = await client.InvokeAsync<PoolDto>(
            Methods.PoolCreate,
            new CreatePoolRequest
            {
                Name = "Media",
                MountPoint = "P:",
                VolumeIds = [first.ToVolumePath(), second.ToVolumePath()],
            },
            CancellationToken.None);

        var leaving = pool.Parts.Single(part => part.Label == "Two");
        var staying = pool.Parts.Single(part => part.Label == "One");
        var leavingRoot = Path.Combine(_root, "Two", ".PoolPart-" + leaving.PartId.ToString("D"));
        Directory.CreateDirectory(Path.Combine(leavingRoot, "Movies"));
        File.WriteAllText(Path.Combine(leavingRoot, "Movies", "film.mkv"), "irreplaceable");

        var plan = await client.InvokeAsync<DriveRemovalPlanResult>(
            Methods.PoolPlanDriveRemoval,
            new RemoveDriveRequest { PoolId = pool.PoolId, VolumeId = second.ToVolumePath(), MoveFilesOff = true },
            CancellationToken.None);

        Assert.True(plan.Fits);
        Assert.Equal(1, plan.FileCount);

        var result = await client.InvokeAsync<DriveRemovalResultDto>(
            Methods.PoolRemoveDrive,
            new RemoveDriveRequest { PoolId = pool.PoolId, VolumeId = second.ToVolumePath(), MoveFilesOff = true },
            CancellationToken.None);

        Assert.True(result.Removed);
        Assert.Equal(1, result.MovedCount);
        Assert.Empty(result.Failed);
        Assert.Single(result.Pool!.Parts);

        // The file is on the drive that stayed, whole, and the emptied part folder is gone.
        var stayingRoot = Path.Combine(_root, "One", ".PoolPart-" + staying.PartId.ToString("D"));
        Assert.Equal("irreplaceable", File.ReadAllText(Path.Combine(stayingRoot, "Movies", "film.mkv")));
        Assert.True(result.PartFolderRemoved);
        Assert.False(Directory.Exists(leavingRoot));
    }

    [Fact]
    public async Task A_safe_removal_that_does_not_fit_is_refused_before_anything_moves()
    {
        var first = AddDrive("One", total: 10_000, free: 4);
        var second = AddDrive("Two", total: 10_000, free: 9_000);
        await using var client = await ConnectAsync();

        var pool = await client.InvokeAsync<PoolDto>(
            Methods.PoolCreate,
            new CreatePoolRequest
            {
                Name = "Media",
                MountPoint = "P:",
                VolumeIds = [first.ToVolumePath(), second.ToVolumePath()],
            },
            CancellationToken.None);

        var leaving = pool.Parts.Single(part => part.Label == "Two");
        var leavingRoot = Path.Combine(_root, "Two", ".PoolPart-" + leaving.PartId.ToString("D"));
        File.WriteAllText(Path.Combine(leavingRoot, "big.bin"), new string('x', 500));

        var failure = await Assert.ThrowsAsync<IpcException>(() =>
            client.InvokeAsync<DriveRemovalResultDto>(
                Methods.PoolRemoveDrive,
                new RemoveDriveRequest { PoolId = pool.PoolId, VolumeId = second.ToVolumePath(), MoveFilesOff = true },
                CancellationToken.None));

        Assert.Equal(IpcErrorCodes.Conflict, failure.Code);

        // The drive is still in the pool and its file has not moved.
        var unchanged = await client.InvokeAsync<PoolDto>(
            Methods.PoolStatus, new PoolReference { PoolId = pool.PoolId }, CancellationToken.None);
        Assert.Equal(2, unchanged.Parts.Count);
        Assert.True(File.Exists(Path.Combine(leavingRoot, "big.bin")));
    }

    [Fact]
    public async Task Removing_a_drive_that_is_not_in_the_pool_is_a_not_found()
    {
        var first = AddDrive("One");
        var stranger = AddDrive("Stranger");
        await using var client = await ConnectAsync();

        var pool = await client.InvokeAsync<PoolDto>(
            Methods.PoolCreate,
            new CreatePoolRequest { Name = "Media", MountPoint = "P:", VolumeIds = [first.ToVolumePath()] },
            CancellationToken.None);

        var failure = await Assert.ThrowsAsync<IpcException>(() =>
            client.InvokeAsync<DriveRemovalResultDto>(
                Methods.PoolRemoveDrive,
                new RemoveDriveRequest { PoolId = pool.PoolId, VolumeId = stranger.ToVolumePath() },
                CancellationToken.None));

        Assert.Equal(IpcErrorCodes.NotFound, failure.Code);
    }

    [Fact]
    public async Task A_pool_reports_what_it_holds_separately_from_what_its_drives_hold()
    {
        var volume = AddDrive("One", total: 1_000, free: 400);
        await using var client = await ConnectAsync();

        var pool = await client.InvokeAsync<PoolDto>(
            Methods.PoolCreate,
            new CreatePoolRequest { Name = "Media", MountPoint = "P:", VolumeIds = [volume.ToVolumePath()] },
            CancellationToken.None);

        var partRoot = Path.Combine(_root, "One", ".PoolPart-" + pool.Parts[0].PartId.ToString("D"));
        File.WriteAllText(Path.Combine(partRoot, "pooled.bin"), new string('x', 120));

        _engine.Tick();

        var measured = await client.InvokeAsync<PoolDto>(
            Methods.PoolStatus, new PoolReference { PoolId = pool.PoolId }, CancellationToken.None);

        Assert.True(measured.IsUsageMeasured);
        Assert.Equal(120, measured.PoolUsedBytes);
        Assert.Equal(1, measured.PoolFileCount);

        // The drive is 600 used of 1000, but the pool's own ceiling is its 120 bytes plus the 400
        // that are actually free.
        Assert.Equal(1_000, measured.TotalBytes);
        Assert.Equal(520, measured.PoolCapacityBytes);
        Assert.Equal(480, measured.ForeignBytes);
        Assert.Equal(120, measured.Parts[0].PoolBytes);
    }

    [Fact]
    public async Task Update_status_says_so_when_there_is_nothing_to_update_from()
    {
        await using var client = await ConnectAsync();

        var status = await client.InvokeAsync<UpdateStatusResult>(
            Methods.UpdateStatus, null, CancellationToken.None);

        // No install layout in a test run, so the engine is honest about it rather than failing.
        Assert.Equal("Unavailable", status.Stage);
        Assert.False(status.UpdateAvailable);
        Assert.NotNull(status.LastError);
    }

    [Fact]
    public async Task Update_preferences_are_stored_and_survive_a_restart()
    {
        await using (var client = await ConnectAsync())
        {
            var status = await client.InvokeAsync<UpdateStatusResult>(
                Methods.UpdateSetOptions,
                new UpdateSettingsDto { AutomaticInstall = false, CheckIntervalHours = 12 },
                CancellationToken.None);

            Assert.False(status.AutomaticInstall);
            Assert.Equal(12, status.CheckIntervalHours);
        }

        using var restarted = new PoolEngine(new PoolEngineOptions
        {
            ConfigStore = new ConfigStore(Path.Combine(_root, "config.json")),
            VolumeProvider = _volumes,
            MountService = new InMemoryMountService(),
        });

        Assert.False(restarted.Config.Updates.AutomaticInstall);
        Assert.Equal(12, restarted.Config.Updates.CheckIntervalHours);
        Assert.True(restarted.Config.Updates.AutomaticChecks);
    }

    [Fact]
    public async Task Pre_releases_are_opt_in_and_stay_opted_in()
    {
        await using var client = await ConnectAsync();

        var before = await client.InvokeAsync<UpdateStatusResult>(
            Methods.UpdateStatus, null, CancellationToken.None);

        Assert.False(before.IncludePrereleases);

        var after = await client.InvokeAsync<UpdateStatusResult>(
            Methods.UpdateSetOptions,
            new UpdateSettingsDto { IncludePrereleases = true },
            CancellationToken.None);

        Assert.True(after.IncludePrereleases);
        Assert.Equal(before.CheckIntervalHours, after.CheckIntervalHours);
    }

    [Fact]
    public async Task How_copying_is_paced_round_trips()
    {
        var volume = AddDrive("One");
        await using var client = await ConnectAsync();

        await client.InvokeAsync<PoolDto>(
            Methods.PoolCreate,
            new CreatePoolRequest { Name = "Media", MountPoint = "P:", VolumeIds = [volume.ToVolumePath()] },
            CancellationToken.None);

        var stored = await client.InvokeAsync<DuplicationResult>(
            Methods.DuplicationSetOptions,
            new DuplicationOptionsDto
            {
                MaxBytesPerSecond = 64L * 1024 * 1024,
                ScanIntervalMinutes = 45,
                CopyHeadroomBytes = 512L * 1024 * 1024,
                PauseWhileBusy = false,
            },
            CancellationToken.None);

        Assert.Equal(64L * 1024 * 1024, stored.MaxBytesPerSecond);
        Assert.Equal(45, stored.ScanIntervalMinutes);
        Assert.Equal(512L * 1024 * 1024, stored.CopyHeadroomBytes);
        Assert.False(stored.PauseWhileBusy);
    }

    [Fact]
    public async Task Growing_and_shrinking_a_pool_are_announced_as_capabilities()
    {
        await using var client = await ConnectAsync();

        Assert.True(client.Supports(Capabilities.PoolEdit));
        Assert.True(client.Supports(Capabilities.DriveRemoval));
        Assert.True(client.Supports(Capabilities.Usage));
        Assert.True(client.Supports(Capabilities.AutoUpdate));
    }

    [Fact]
    public async Task A_pool_that_has_stopped_answering_makes_the_health_check_unhealthy()
    {
        // The configuration health check cannot see this on its own: it reads config and resolves
        // drives, and a mount whose callbacks have stopped completing looks fine to both. This reply
        // is written by a different thread from the ones that are stuck, which is exactly why the
        // service used to report itself perfectly healthy while the pool was wedged.
        var volume = AddDrive("One");
        await using var client = await ConnectAsync();

        await client.InvokeAsync<PoolDto>(
            Methods.PoolCreate,
            new CreatePoolRequest { Name = "Media", MountPoint = "P:", VolumeIds = [volume.ToVolumePath()] },
            CancellationToken.None);

        var healthy = await client.InvokeAsync<HealthCheckResult>(
            Methods.HealthCheck, null, CancellationToken.None);

        Assert.True(healthy.Healthy);
        Assert.NotEqual(
            nameof(MergePool.Core.Health.IoHealthState.Stalled), Assert.Single(healthy.Io).State);

        var health = _engine.IoHealth.Single().Health;
        for (var i = 0; i < MergePool.Core.Health.IoHealthMonitor.StallsBeforeStalled; i++)
        {
            health.RecordStall("read", TimeSpan.FromSeconds(30), "did not return");
        }

        var stalled = await client.InvokeAsync<HealthCheckResult>(
            Methods.HealthCheck, null, CancellationToken.None);

        Assert.False(stalled.Healthy);
        Assert.Contains(stalled.Problems, problem => problem.Contains("not answering"));

        var io = Assert.Single(stalled.Io);
        Assert.Equal(nameof(MergePool.Core.Health.IoHealthState.Stalled), io.State);
        Assert.NotEmpty(io.RecentFaults);
    }

    [Fact]
    public async Task The_io_health_of_each_pool_is_announced_as_a_capability()
    {
        await using var client = await ConnectAsync();

        Assert.True(client.Supports(Capabilities.IoHealth));
    }

    [Fact]
    public async Task The_expert_tunables_are_announced_as_a_capability()
    {
        // The window asks for this rather than for a version number, so an older service simply
        // shows the everyday settings instead of writing fields it would ignore.
        await using var client = await ConnectAsync();

        Assert.True(client.Supports(Capabilities.AdvancedSettings));
    }
}
