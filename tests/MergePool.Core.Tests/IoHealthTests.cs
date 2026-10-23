using MergePool.Core.Config;
using MergePool.Core.Health;
using MergePool.Core.Model;
using MergePool.Core.Volumes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MergePool.Core.Tests;

/// <summary>
/// The reporting and classification behind the recovery path: telling a drive that cannot be
/// reached from one that is gone, and noticing a mount that has stopped answering at all.
/// </summary>
public sealed class IoHealthTests
{
    [Fact]
    public void A_drive_that_stops_answering_is_unreachable_first_and_gone_only_later()
    {
        // The distinction the reported empty-listing depends on. Immediately after a drive stops
        // answering we do not know which of the two it is, and guessing "gone" is the dangerous
        // guess: it drops the drive's files out of the pool as though they had been deleted.
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-23T09:00:00Z"));
        using var temp = new TempDirectory();

        var volumes = new DirectoryVolumeProvider();
        var manager = new PoolPartManager(volumes, clock) { PresenceGracePeriod = TimeSpan.FromSeconds(30) };

        var drivePath = Path.Combine(temp.Path, "A");
        Directory.CreateDirectory(drivePath);
        var volumeId = volumes.Add(drivePath, label: "A");
        var part = manager.CreatePart(volumes.TryGetVolume(volumeId)!, Guid.NewGuid(), "pool");

        var definition = new PoolDefinition { Name = "pool", MountPoint = "P:" };
        definition.Drives.Add(new PoolDriveDefinition
        {
            VolumeId = volumeId.ToVolumePath(),
            PartId = part.PartId,
            Label = "A",
        });

        Assert.Equal(PoolPartState.Online, manager.Resolve(definition).Parts[0].State);
        Assert.True(manager.Resolve(definition).IsComplete);

        volumes.SetPresent(volumeId, present: false);

        var hiccup = manager.Resolve(definition);
        Assert.Equal(PoolPartState.Unavailable, hiccup.Parts[0].State);
        Assert.False(hiccup.IsComplete);

        // Still within the grace period: we are not yet willing to call it gone.
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(PoolPartState.Unavailable, manager.Resolve(definition).Parts[0].State);

        // Past it, this is a drive someone has removed, which is a supported state: the pool is
        // degraded, complete, and serves what remains as fact.
        clock.Advance(TimeSpan.FromSeconds(2));
        var settled = manager.Resolve(definition);
        Assert.Equal(PoolPartState.Offline, settled.Parts[0].State);
        Assert.True(settled.IsComplete);
        Assert.Equal(PoolHealth.Offline, settled.Health);
    }

    [Fact]
    public void A_drive_that_comes_back_within_the_grace_period_is_never_reported_as_gone()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-23T09:00:00Z"));
        using var temp = new TempDirectory();

        var volumes = new DirectoryVolumeProvider();
        var manager = new PoolPartManager(volumes, clock) { PresenceGracePeriod = TimeSpan.FromSeconds(30) };

        var drivePath = Path.Combine(temp.Path, "A");
        Directory.CreateDirectory(drivePath);
        var volumeId = volumes.Add(drivePath, label: "A");
        var part = manager.CreatePart(volumes.TryGetVolume(volumeId)!, Guid.NewGuid(), "pool");

        var definition = new PoolDefinition { Name = "pool", MountPoint = "P:" };
        definition.Drives.Add(new PoolDriveDefinition
        {
            VolumeId = volumeId.ToVolumePath(),
            PartId = part.PartId,
            Label = "A",
        });

        _ = manager.Resolve(definition);

        volumes.SetPresent(volumeId, present: false);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(PoolPartState.Unavailable, manager.Resolve(definition).Parts[0].State);

        volumes.SetPresent(volumeId, present: true);
        clock.Advance(TimeSpan.FromSeconds(1));

        var back = manager.Resolve(definition);
        Assert.Equal(PoolPartState.Online, back.Parts[0].State);
        Assert.True(back.IsComplete);
    }

    [Fact]
    public void One_slow_operation_is_slow_and_a_run_of_them_is_a_stall()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-23T09:00:00Z"));
        var health = new IoHealthMonitor(clock);

        Assert.Equal(IoHealthState.Unknown, health.State);

        health.RecordHealthy("read");
        Assert.Equal(IoHealthState.Healthy, health.State);

        health.RecordStall("read", TimeSpan.FromSeconds(30), "did not return");
        Assert.Equal(IoHealthState.Slow, health.State);

        health.RecordStall("read", TimeSpan.FromSeconds(30), "did not return");
        health.RecordStall("read", TimeSpan.FromSeconds(30), "did not return");
        Assert.Equal(IoHealthState.Stalled, health.State);
        Assert.NotNull(health.StalledFor);

        // One read completing means the read path is answering again.
        health.RecordHealthy("read");
        Assert.Equal(IoHealthState.Healthy, health.State);
        Assert.Null(health.StalledFor);
    }

    [Fact]
    public void A_healthy_area_does_not_clear_another_areas_stall()
    {
        // The flaw this guards against would have made the whole recovery path useless. A drive
        // that has stopped answering reads while volume queries still work stalls only the read
        // path — and the topology refresh succeeding every couple of seconds would otherwise wipe
        // out the record of it several times a minute, so the watchdog would never act.
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-23T09:00:00Z"));
        var health = new IoHealthMonitor(clock);

        for (var i = 0; i < IoHealthMonitor.StallsBeforeStalled; i++)
        {
            health.RecordStall("read", TimeSpan.FromSeconds(30), "did not return");
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(IoHealthState.Stalled, health.State);
        Assert.Equal("read", health.WorstArea);

        // Hundreds of successful resolves later, the reads are still wedged.
        for (var i = 0; i < 200; i++)
        {
            health.RecordHealthy("topology");
        }

        Assert.Equal(IoHealthState.Stalled, health.State);
        Assert.Equal("read", health.WorstArea);

        health.RecordHealthy("read");
        Assert.Equal(IoHealthState.Healthy, health.State);
    }

    [Fact]
    public void Stalls_long_apart_are_not_treated_as_one_ongoing_problem()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-23T09:00:00Z"));
        var health = new IoHealthMonitor(clock);

        health.RecordStall("write", TimeSpan.FromSeconds(30), "slow");
        health.RecordStall("write", TimeSpan.FromSeconds(30), "slow");

        // An hour later, one more. Three stalls in total, but not three in a row: a drive that is
        // briefly slow once a day has not stalled.
        clock.Advance(TimeSpan.FromHours(1));
        health.RecordStall("write", TimeSpan.FromSeconds(30), "slow");

        Assert.Equal(IoHealthState.Slow, health.State);
        Assert.Equal(3, health.Stalls);
    }

    [Fact]
    public void How_long_it_has_been_stalled_is_measured_from_the_first_stall_in_the_run()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-23T09:00:00Z"));
        var health = new IoHealthMonitor(clock);

        health.RecordStall("read", TimeSpan.FromSeconds(30), "slow");
        clock.Advance(TimeSpan.FromSeconds(20));
        health.RecordStall("read", TimeSpan.FromSeconds(30), "slow");
        clock.Advance(TimeSpan.FromSeconds(20));
        health.RecordStall("read", TimeSpan.FromSeconds(30), "slow");

        Assert.Equal(IoHealthState.Stalled, health.State);
        Assert.Equal(TimeSpan.FromSeconds(40), health.StalledFor);
    }

    [Fact]
    public void Only_a_bounded_number_of_faults_is_kept()
    {
        var health = new IoHealthMonitor(faultsKept: 4);

        for (var i = 0; i < 50; i++)
        {
            health.RecordFault("read", $"failure {i}");
        }

        Assert.Equal(4, health.RecentFaults.Count);
        Assert.Equal(50, health.Failures);
        Assert.Contains("failure 49", health.RecentFaults[^1].Detail);
    }

    [Theory]
    [InlineData("P:", "P:\\")]
    [InlineData("P:\\", "p:\\")]
    [InlineData("C:\\pools\\media", "C:\\Pools\\Media\\")]
    public void Our_own_mounts_are_recognised_however_they_are_spelled(string registered, string queried)
    {
        // Matters because the two sides spell it differently: the configuration holds "P:" and
        // volume enumeration reports "P:\". A missed match means we ask Windows about our own mount,
        // which is the question that used to hang the pool.
        var registry = new OwnMountRegistry();
        registry.Register(registered);

        Assert.True(registry.IsOwnMount(queried));

        registry.Unregister(registered);
        Assert.False(registry.IsOwnMount(queried));
    }

    [Fact]
    public void A_path_that_is_not_ours_is_not_treated_as_ours()
    {
        var registry = new OwnMountRegistry();
        registry.Register("P:");

        Assert.False(registry.IsOwnMount("Q:\\"));
        Assert.False(registry.IsOwnMount(null));
        Assert.False(registry.IsOwnMount(" "));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "mergepool-health", Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
