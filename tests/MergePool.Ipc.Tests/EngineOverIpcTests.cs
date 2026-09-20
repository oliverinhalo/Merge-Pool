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
}
