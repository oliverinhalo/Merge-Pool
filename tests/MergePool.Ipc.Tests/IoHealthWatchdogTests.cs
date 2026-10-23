using MergePool.Core.Config;
using MergePool.Core.Health;
using MergePool.Core.Volumes;
using MergePool.Engine;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MergePool.Ipc.Tests;

/// <summary>
/// The recovery path for a mount that has stopped answering. Remounting is disruptive — open
/// handles break — so these tests are mostly about the watchdog declining to do it.
/// </summary>
public sealed class IoHealthWatchdogTests : IDisposable
{
    private readonly string _root;
    private readonly DirectoryVolumeProvider _volumes = new();
    private readonly InMemoryMountService _mounts = new();
    private readonly PoolEngine _engine;
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-10-23T09:00:00Z"));
    private readonly ServiceOptions _options = new();
    private readonly IoHealthWatchdog _watchdog;

    public IoHealthWatchdogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mergepool-watchdog", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _engine = new PoolEngine(new PoolEngineOptions
        {
            ConfigStore = new ConfigStore(Path.Combine(_root, "config.json")),
            VolumeProvider = _volumes,
            MountService = _mounts,
            PresenceGracePeriod = TimeSpan.Zero,

            // The same clock the watchdog reads, so that advancing it advances what the pool's
            // health monitor thinks has happened. Without this the monitor timestamps stalls from
            // the real clock and no amount of advancing makes a pool look stalled.
            TimeProvider = _clock,
        });

        var drivePath = Path.Combine(_root, "A");
        Directory.CreateDirectory(drivePath);
        var volume = _volumes.Add(drivePath, label: "A");

        _engine.CreatePool("Media", "P:", [volume]);

        _watchdog = new IoHealthWatchdog(_engine, () => _options, _clock);
    }

    /// <summary>
    /// One watchdog for the whole test, because that is how the service uses it: the cooldown that
    /// stops a pool being remounted over and over lives in the instance, so a fresh one per tick
    /// would quietly have no cooldown at all.
    /// </summary>
    private IoHealthWatchdog Watchdog() => _watchdog;

    private IoHealthMonitor PoolHealth() => _engine.IoHealth.Single().Health;

    /// <summary>
    /// Drives the pool into the state the watchdog acts on: a wedged mount as it really looks, with
    /// a bounded wait running out every thirty seconds for as long as it is wedged.
    /// </summary>
    /// <remarks>
    /// The spacing matters. <see cref="IoHealthMonitor.StallWindow"/> starts a fresh run when
    /// stalls arrive further apart than a minute, on the grounds that a pool which is briefly slow
    /// twice an hour has not stalled — so a test that jumped the clock past that window in one go
    /// would be setting up a different situation from the one it means.
    /// </remarks>
    private void Stall(TimeSpan forHowLong)
    {
        var health = PoolHealth();
        var step = TimeSpan.FromSeconds(30);
        var wait = TimeSpan.FromSeconds(30);

        health.RecordStall("read", wait, "did not return");

        var stalls = 1;
        var elapsed = TimeSpan.Zero;

        while (elapsed < forHowLong)
        {
            var next = forHowLong - elapsed < step ? forHowLong - elapsed : step;
            _clock.Advance(next);
            elapsed += next;

            health.RecordStall("read", wait, "did not return");
            stalls++;
        }

        // The watchdog needs both: a run of stalls rather than one slow operation, and that run
        // spanning long enough to rule out a burst of slowness under load. A short span still has
        // to produce the run, or the test would be exercising "slow" rather than "stalled".
        while (stalls < IoHealthMonitor.StallsBeforeStalled)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            health.RecordStall("read", wait, "did not return");
            stalls++;
        }
    }

    [Fact]
    public void A_healthy_pool_is_left_alone()
    {
        PoolHealth().RecordHealthy("read");

        Assert.Empty(Watchdog().Check());
        Assert.True(_engine.IsMounted(_engine.Pools.Single().PoolId));
    }

    [Fact]
    public void A_pool_that_has_only_just_stalled_is_not_remounted_yet()
    {
        // A burst of slowness under load is not a wedged mount, and a remount costs every
        // application its open handles.
        Stall(TimeSpan.FromSeconds(5));

        var outcome = Assert.Single(Watchdog().Check());

        Assert.False(outcome.Recovered);
        Assert.Contains("long enough", outcome.Declined);
    }

    [Fact]
    public void A_pool_stalled_for_long_enough_is_remounted()
    {
        Stall(IoHealthWatchdog.StalledFor + TimeSpan.FromSeconds(10));

        var outcome = Assert.Single(Watchdog().Check());

        Assert.True(outcome.Recovered);
        Assert.True(_engine.IsMounted(_engine.Pools.Single().PoolId));

        // The run is cleared, so the next verdict is made on what happens after the recovery rather
        // than on the stalls that led to it.
        Assert.Equal(IoHealthState.Unknown, PoolHealth().State);
    }

    [Fact]
    public void A_pool_is_not_remounted_twice_in_a_row()
    {
        Stall(IoHealthWatchdog.StalledFor + TimeSpan.FromSeconds(10));
        Assert.True(Assert.Single(Watchdog().Check()).Recovered);

        Stall(IoHealthWatchdog.StalledFor + TimeSpan.FromSeconds(10));
        var second = Assert.Single(Watchdog().Check());

        Assert.False(second.Recovered);
        Assert.Contains("recently", second.Declined);

        // Once the cooldown is over it will try again: a pool that stalls repeatedly is still worth
        // recovering, it just is not worth recovering every fifteen seconds.
        _clock.Advance(IoHealthWatchdog.Cooldown + TimeSpan.FromMinutes(1));
        Stall(IoHealthWatchdog.StalledFor + TimeSpan.FromSeconds(10));

        Assert.True(Assert.Single(Watchdog().Check()).Recovered);
    }

    [Fact]
    public void Recovery_can_be_switched_off()
    {
        Stall(IoHealthWatchdog.StalledFor + TimeSpan.FromSeconds(10));

        _options.RecoverStalledMounts = false;

        var outcome = Assert.Single(Watchdog().Check());

        Assert.False(outcome.Recovered);
        Assert.Contains("switched off", outcome.Declined);
    }

    [Fact]
    public void A_draining_service_is_left_alone()
    {
        // An upgrade is already taking the mount down; remounting underneath it would fight it.
        Stall(IoHealthWatchdog.StalledFor + TimeSpan.FromSeconds(10));
        _engine.Drain();

        var outcome = Assert.Single(Watchdog().Check());

        Assert.False(outcome.Recovered);
        Assert.Contains("draining", outcome.Declined);
    }

    public void Dispose()
    {
        _engine.Dispose();

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
