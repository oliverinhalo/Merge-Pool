using MergePool.Core.Config;
using MergePool.Core.Placement;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MergePool.Core.Tests;

public sealed class IdleProbeTests
{
    private static PlacementOptions Options() => new()
    {
        IdleProbeBytes = 64 * 1024,
        IdleProbeIntervalSeconds = 60,
        MinThroughputSampleBytes = 4096,
        MinSamplesForThrottleDetection = 3,
    };

    [Fact]
    public void Probing_produces_a_measurement_and_leaves_nothing_behind()
    {
        using var pool = new TempPool();
        var (_, a) = pool.AddDrive("A");

        var options = Options();
        var tracker = new DriveMetricsTracker(options);
        var probe = new IdleProbe(tracker, options);

        Assert.True(probe.Probe(pool.Current.FindPart(a)!));

        var snapshot = tracker.GetSnapshot(a);
        Assert.True(snapshot.ThroughputBytesPerSecond > 0);
        Assert.NotNull(snapshot.LastSampleUtc);

        // Only the part's own marker file remains: the probe file is cleaned up.
        var remaining = Directory.GetFiles(pool.PartRoot(a)).Select(f => Path.GetFileName(f)!).ToArray();
        Assert.Equal(["poolpart.json"], remaining);
        Assert.Empty(pool.View.EnumerateNames(""));
    }

    [Fact]
    public void Only_idle_drives_are_probed()
    {
        using var pool = new TempPool();
        var (_, busy) = pool.AddDrive("Busy");
        pool.AddDrive("Idle");

        var options = Options();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var tracker = new DriveMetricsTracker(options, time);
        var probe = new IdleProbe(tracker, options, time);

        // The busy drive was measured a moment ago; the other has never been seen.
        tracker.RecordWrite(busy, 8 * 1024 * 1024, TimeSpan.FromMilliseconds(50));

        Assert.Equal(1, probe.ProbeIdleDrives(pool.Current));

        // Once enough time passes, the busy drive is probed too.
        time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(2, probe.ProbeIdleDrives(pool.Current));
    }

    [Fact]
    public void An_offline_drive_is_not_probed()
    {
        using var pool = new TempPool();
        var (volume, _) = pool.AddDrive("A");
        pool.Volumes.SetPresent(volume, present: false);

        var options = Options();
        var tracker = new DriveMetricsTracker(options);
        var probe = new IdleProbe(tracker, options);

        Assert.Equal(0, probe.ProbeIdleDrives(pool.Current));
    }

    [Fact]
    public void A_probe_keeps_a_quiet_throttled_drive_measurable()
    {
        using var pool = new TempPool();
        var (_, a) = pool.AddDrive("A");

        var options = Options();
        options.MinSamplesForThrottleDetection = 3;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var tracker = new DriveMetricsTracker(options, time);
        var probe = new IdleProbe(tracker, options, time);

        var before = tracker.GetSnapshot(a).SampleCount;
        for (var i = 0; i < 3; i++)
        {
            time.Advance(TimeSpan.FromSeconds(61));
            probe.ProbeIdleDrives(pool.Current);
        }

        Assert.True(tracker.GetSnapshot(a).SampleCount > before);
    }
}
