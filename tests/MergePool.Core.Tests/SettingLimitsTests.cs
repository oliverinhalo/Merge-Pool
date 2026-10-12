using MergePool.Core.Config;
using Xunit;

namespace MergePool.Core.Tests;

/// <summary>
/// The tunables are now in the window rather than only in the config file, so a value that would
/// stop the engine working has to be impossible instead of merely discouraged.
/// </summary>
public sealed class SettingLimitsTests
{
    [Fact]
    public void Defaults_are_left_exactly_as_they_are()
    {
        var placement = new PlacementOptions();
        var before = Snapshot(placement);

        placement.Clamped();

        Assert.Equal(before, Snapshot(placement));
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_nonsense_weight_cannot_be_stored(double weight)
    {
        var placement = new PlacementOptions { SpeedWeight = weight, FreeSpaceWeight = weight };

        placement.Clamped();

        Assert.InRange(placement.SpeedWeight, 0, 3);
        Assert.InRange(placement.FreeSpaceWeight, 0, 3);
    }

    [Fact]
    public void A_drive_always_has_to_come_back_faster_than_it_was_parked()
    {
        // Enter above exit would flap a borderline drive in and out of the pool on one sample.
        var placement = new PlacementOptions { ThrottleEnterRatio = 0.8, ThrottleExitRatio = 0.2 };

        placement.Clamped();

        Assert.True(
            placement.ThrottleExitRatio > placement.ThrottleEnterRatio,
            $"exit {placement.ThrottleExitRatio} must be above enter {placement.ThrottleEnterRatio}");
    }

    [Fact]
    public void Rebalancing_always_stops_at_a_smaller_gap_than_it_starts_at()
    {
        // Target above trigger would mean a rebalance that never considers itself finished.
        var rebalance = new RebalanceOptions { TriggerImbalance = 0.1, TargetImbalance = 0.9 };

        rebalance.Clamped();

        Assert.True(
            rebalance.TargetImbalance < rebalance.TriggerImbalance,
            $"target {rebalance.TargetImbalance} must be under trigger {rebalance.TriggerImbalance}");
    }

    [Fact]
    public void The_speed_factor_ceiling_stays_above_its_floor()
    {
        var placement = new PlacementOptions { MinSpeedFactor = 0.9, MaxSpeedFactor = 0.1 };

        placement.Clamped();

        Assert.True(
            placement.MaxSpeedFactor > placement.MinSpeedFactor,
            $"ceiling {placement.MaxSpeedFactor} must be above floor {placement.MinSpeedFactor}");
    }

    [Fact]
    public void A_zero_scan_interval_cannot_turn_duplication_into_a_busy_loop()
    {
        var duplication = new DuplicationSettings { ScanIntervalMinutes = 0, MaxBytesPerSecond = 0 };

        duplication.Clamped();

        Assert.InRange(duplication.ScanIntervalMinutes, 1, 24 * 60);
        Assert.True(duplication.MaxBytesPerSecond >= 1024 * 1024);
    }

    [Fact]
    public void A_zero_rebalance_rate_cannot_stall_the_rebalancer()
    {
        var rebalance = new RebalanceOptions { MaxBytesPerSecond = 0, MaxFileBytes = 0 };

        rebalance.Clamped();

        Assert.True(rebalance.MaxBytesPerSecond >= 1024 * 1024);
        Assert.True(rebalance.MaxFileBytes >= 1024 * 1024);
    }

    [Fact]
    public void An_update_check_never_runs_more_than_hourly()
    {
        var updates = new UpdateOptions { CheckIntervalHours = 0 };

        updates.Clamped();

        Assert.InRange(updates.CheckIntervalHours, 1, 24 * 14);
    }

    [Fact]
    public void Clamping_leaves_a_reasonable_hand_edited_value_alone()
    {
        // The limits exist to stop the engine being wedged, not to second-guess a deliberate choice.
        var placement = new PlacementOptions
        {
            SpeedWeight = 2.5,
            MinFreeBytes = 40L * 1024 * 1024 * 1024,
            ThrottleEnterRatio = 0.3,
            ThrottleExitRatio = 0.6,
            IdleProbeIntervalSeconds = 900,
        };

        placement.Clamped();

        Assert.Equal(2.5, placement.SpeedWeight);
        Assert.Equal(40L * 1024 * 1024 * 1024, placement.MinFreeBytes);
        Assert.Equal(0.3, placement.ThrottleEnterRatio);
        Assert.Equal(0.6, placement.ThrottleExitRatio);
        Assert.Equal(900, placement.IdleProbeIntervalSeconds);
    }

    private static string Snapshot(PlacementOptions placement) => string.Join(
        '|',
        placement.FreeSpaceWeight,
        placement.SpeedWeight,
        placement.MinFreeBytes,
        placement.WriteHeadroomBytes,
        placement.EwmaAlpha,
        placement.BaselineAlpha,
        placement.BaselineRiseAlpha,
        placement.ThrottleEnterRatio,
        placement.ThrottleExitRatio,
        placement.LatencyThrottleRatio,
        placement.ThrottleCooldownSeconds,
        placement.ThrottleEnterSamples,
        placement.ThrottleExitSamples,
        placement.MinSamplesForThrottleDetection,
        placement.ReferenceThroughputBytesPerSecond,
        placement.MinSpeedFactor,
        placement.MaxSpeedFactor,
        placement.MinThroughputSampleBytes,
        placement.IdleProbeIntervalSeconds,
        placement.IdleProbeBytes,
        placement.Rebalance.TriggerImbalance,
        placement.Rebalance.TargetImbalance,
        placement.Rebalance.MaxBytesPerSecond,
        placement.Rebalance.MaxFileBytes);
}
