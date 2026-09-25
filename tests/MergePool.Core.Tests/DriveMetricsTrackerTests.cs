using MergePool.Core.Config;
using MergePool.Core.Placement;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MergePool.Core.Tests;

public sealed class DriveMetricsTrackerTests
{
    private const long MegaByte = 1024 * 1024;

    private static PlacementOptions Options() => new()
    {
        EwmaAlpha = 0.5,
        BaselineAlpha = 0.02,
        BaselineRiseAlpha = 0.5,
        ThrottleEnterRatio = 0.45,
        ThrottleExitRatio = 0.75,
        ThrottleEnterSamples = 3,
        ThrottleExitSamples = 3,
        ThrottleCooldownSeconds = 60,
        MinSamplesForThrottleDetection = 5,
        MinThroughputSampleBytes = 64 * 1024,
        ReferenceThroughputBytesPerSecond = 100 * MegaByte,
    };

    /// <summary>Feeds samples at a given rate in MB/s.</summary>
    private static void Feed(DriveMetricsTracker tracker, Guid part, double megabytesPerSecond, int count, long bytes = 8 * MegaByte)
    {
        var seconds = bytes / (megabytesPerSecond * MegaByte);
        for (var i = 0; i < count; i++)
        {
            tracker.RecordWrite(part, bytes, TimeSpan.FromSeconds(seconds));
        }
    }

    [Fact]
    public void Speed_factor_follows_measured_throughput()
    {
        var tracker = new DriveMetricsTracker(Options());
        var part = Guid.NewGuid();

        Feed(tracker, part, megabytesPerSecond: 200, count: 10);

        var snapshot = tracker.GetSnapshot(part);
        Assert.InRange(snapshot.SpeedFactor, 1.9, 2.1);
        Assert.InRange(snapshot.ThroughputBytesPerSecond, 190 * MegaByte, 210 * MegaByte);
    }

    [Fact]
    public void Speed_factor_is_clamped()
    {
        var options = Options();
        options.MaxSpeedFactor = 3.0;
        options.MinSpeedFactor = 0.2;

        var fast = Guid.NewGuid();
        var slow = Guid.NewGuid();
        var tracker = new DriveMetricsTracker(options);

        Feed(tracker, fast, megabytesPerSecond: 5000, count: 10);
        Feed(tracker, slow, megabytesPerSecond: 1, count: 10);

        Assert.Equal(3.0, tracker.GetSpeedFactor(fast));
        Assert.Equal(0.2, tracker.GetSpeedFactor(slow));
    }

    [Fact]
    public void An_unknown_drive_has_a_neutral_speed_factor()
    {
        var tracker = new DriveMetricsTracker(Options());
        Assert.Equal(1.0, tracker.GetSpeedFactor(Guid.NewGuid()));
        Assert.False(tracker.IsThrottled(Guid.NewGuid()));
    }

    [Fact]
    public void A_collapse_against_the_drives_own_baseline_is_throttling()
    {
        var tracker = new DriveMetricsTracker(Options());
        var part = Guid.NewGuid();

        // An SMR drive writing happily into its cache...
        Feed(tracker, part, megabytesPerSecond: 180, count: 10);
        Assert.False(tracker.IsThrottled(part));

        // ...then the cache is exhausted and it falls off a cliff.
        Feed(tracker, part, megabytesPerSecond: 20, count: 6);

        Assert.True(tracker.IsThrottled(part));
        var snapshot = tracker.GetSnapshot(part);
        Assert.Equal(ThrottleReason.ThroughputCollapse, snapshot.ThrottleReason);
        Assert.NotNull(snapshot.ThrottledSince);
        Assert.True(snapshot.HealthRatio < 0.45);
    }

    [Fact]
    public void A_consistently_slow_drive_is_not_throttled()
    {
        var tracker = new DriveMetricsTracker(Options());
        var part = Guid.NewGuid();

        // A slow archive disk that has always been slow is healthy, not throttled.
        Feed(tracker, part, megabytesPerSecond: 12, count: 40);

        Assert.False(tracker.IsThrottled(part));
        Assert.InRange(tracker.GetSnapshot(part).HealthRatio, 0.9, 1.1);
    }

    [Fact]
    public void Throttling_needs_several_consecutive_bad_samples()
    {
        var tracker = new DriveMetricsTracker(Options());
        var part = Guid.NewGuid();

        Feed(tracker, part, megabytesPerSecond: 200, count: 10);
        Feed(tracker, part, megabytesPerSecond: 5, count: 1);

        // One bad sample is a hiccup, not a throttle.
        Assert.False(tracker.IsThrottled(part));

        Feed(tracker, part, megabytesPerSecond: 5, count: 5);
        Assert.True(tracker.IsThrottled(part));
    }

    [Fact]
    public void A_latency_spike_also_counts_as_throttling()
    {
        var options = Options();
        options.ThrottleEnterRatio = 0.0001; // take throughput out of the picture
        var tracker = new DriveMetricsTracker(options);
        var part = Guid.NewGuid();

        for (var i = 0; i < 20; i++)
        {
            tracker.RecordWrite(part, 8 * MegaByte, TimeSpan.FromMilliseconds(40));
        }

        Assert.False(tracker.IsThrottled(part));

        for (var i = 0; i < 6; i++)
        {
            tracker.RecordWrite(part, 8 * MegaByte, TimeSpan.FromMilliseconds(4000));
        }

        Assert.True(tracker.IsThrottled(part));
        Assert.Equal(ThrottleReason.LatencySpike, tracker.GetSnapshot(part).ThrottleReason);
    }

    [Fact]
    public void Recovery_needs_hysteresis_and_the_cooldown_to_elapse()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var tracker = new DriveMetricsTracker(Options(), time);
        var part = Guid.NewGuid();

        Feed(tracker, part, megabytesPerSecond: 200, count: 10);
        Feed(tracker, part, megabytesPerSecond: 10, count: 6);
        Assert.True(tracker.IsThrottled(part));

        // Throughput is back, but the cooldown has not elapsed: still parked.
        Feed(tracker, part, megabytesPerSecond: 200, count: 6);
        Assert.True(tracker.IsThrottled(part));

        time.Advance(TimeSpan.FromSeconds(61));
        Feed(tracker, part, megabytesPerSecond: 200, count: 4);

        Assert.False(tracker.IsThrottled(part));
        Assert.Null(tracker.GetSnapshot(part).ThrottledSince);
    }

    [Fact]
    public void A_half_recovered_drive_stays_parked()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var tracker = new DriveMetricsTracker(Options(), time);
        var part = Guid.NewGuid();

        Feed(tracker, part, megabytesPerSecond: 200, count: 10);
        Feed(tracker, part, megabytesPerSecond: 10, count: 6);
        time.Advance(TimeSpan.FromSeconds(120));

        // 60% of baseline is above the enter ratio but below the exit ratio: hysteresis holds.
        Feed(tracker, part, megabytesPerSecond: 120, count: 10);

        Assert.True(tracker.IsThrottled(part));
    }

    [Fact]
    public void The_baseline_does_not_sink_to_the_throttled_rate()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var tracker = new DriveMetricsTracker(Options(), time);
        var part = Guid.NewGuid();

        Feed(tracker, part, megabytesPerSecond: 200, count: 10);
        var baseline = tracker.GetSnapshot(part).BaselineBytesPerSecond;

        Feed(tracker, part, megabytesPerSecond: 10, count: 200);

        // The baseline decays only over the few samples before the drive is parked, and then
        // holds: it must stay near the healthy rate, nowhere near the throttled one.
        var after = tracker.GetSnapshot(part).BaselineBytesPerSecond;
        Assert.True(tracker.IsThrottled(part));
        Assert.InRange(after, baseline * 0.9, baseline);
        Assert.True(after > 100 * MegaByte);
    }

    [Fact]
    public void Throttle_transitions_are_reported()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var tracker = new DriveMetricsTracker(Options(), time);
        var part = Guid.NewGuid();
        var transitions = new List<bool>();
        tracker.ThrottleStateChanged += (_, snapshot) => transitions.Add(snapshot.IsThrottled);

        Feed(tracker, part, megabytesPerSecond: 200, count: 10);
        Feed(tracker, part, megabytesPerSecond: 10, count: 6);
        time.Advance(TimeSpan.FromSeconds(61));
        Feed(tracker, part, megabytesPerSecond: 200, count: 6);

        Assert.Equal([true, false], transitions);
    }

    [Fact]
    public void Small_operations_do_not_pollute_throughput()
    {
        var tracker = new DriveMetricsTracker(Options());
        var part = Guid.NewGuid();

        Feed(tracker, part, megabytesPerSecond: 200, count: 10);
        var before = tracker.GetSnapshot(part).ThroughputBytesPerSecond;

        // A burst of 4 KiB writes: slow in bytes-per-second terms, but meaningless as a measurement.
        for (var i = 0; i < 50; i++)
        {
            tracker.RecordWrite(part, 4096, TimeSpan.FromMilliseconds(5));
        }

        Assert.Equal(before, tracker.GetSnapshot(part).ThroughputBytesPerSecond, 3);
        Assert.False(tracker.IsThrottled(part));
    }

    [Fact]
    public void Tick_clears_a_throttle_once_the_cooldown_passes()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var tracker = new DriveMetricsTracker(Options(), time);
        var part = Guid.NewGuid();

        Feed(tracker, part, megabytesPerSecond: 200, count: 10);
        Feed(tracker, part, megabytesPerSecond: 10, count: 6);
        Feed(tracker, part, megabytesPerSecond: 200, count: 4);
        Assert.True(tracker.IsThrottled(part));

        time.Advance(TimeSpan.FromSeconds(120));
        tracker.Tick();

        Assert.False(tracker.IsThrottled(part));
    }

    [Fact]
    public void Placement_uses_live_metrics_to_skip_a_throttled_drive()
    {
        using var pool = new TempPool();
        var (_, fast) = pool.AddDrive("Fast", totalBytes: 1_000_000, freeBytes: 900_000);
        var (_, slow) = pool.AddDrive("Slow", totalBytes: 1_000_000, freeBytes: 800_000);

        var tracker = new DriveMetricsTracker(Options());
        var placement = new FreeSpaceSpeedPlacement(pool.Options, tracker);

        Feed(tracker, fast, megabytesPerSecond: 200, count: 10);
        Feed(tracker, slow, megabytesPerSecond: 100, count: 10);
        Assert.Equal(fast, placement.Select(pool.Current, new PlacementRequest { PoolPath = "x" })!.PartId);

        // The fast drive falls off its own cliff; new writes must avoid it.
        Feed(tracker, fast, megabytesPerSecond: 8, count: 8);
        Assert.True(tracker.IsThrottled(fast));

        var decision = placement.Select(pool.Current, new PlacementRequest { PoolPath = "y" });
        Assert.Equal(slow, decision!.PartId);
        Assert.False(decision.AllCandidatesThrottled);
    }
}
