namespace MergePool.Core.Config;

/// <summary>
/// The range every tunable is allowed to hold, and the clamp that enforces it.
/// </summary>
/// <remarks>
/// <para>These numbers used to be reachable only by hand-editing the config file, where a typo was
/// the editor's own problem. They are now in the window behind the Advanced toggle, so a value that
/// would stop the engine placing files at all — a zero scan interval, an exit ratio below the enter
/// ratio, a speed-factor ceiling under its floor — has to be impossible rather than merely
/// discouraged. Every setting is clamped into a range that still works, and the pairs that have to
/// stay ordered are ordered.</para>
/// <para>A clamp is applied on the way in, so the config file never holds a value the engine would
/// have to defend itself against, and what the window reads back is what the engine is using.</para>
/// </remarks>
public static class SettingLimits
{
    public const long Gigabyte = 1024L * 1024 * 1024;

    public const long Megabyte = 1024L * 1024;

    /// <summary>A value out of range, or not a number at all, becomes <paramref name="fallback"/>.</summary>
    public static double Clamp(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    public static long Clamp(long value, long minimum, long maximum) => Math.Clamp(value, minimum, maximum);

    public static int Clamp(int value, int minimum, int maximum) => Math.Clamp(value, minimum, maximum);

    /// <summary>
    /// Brings every placement tunable into range and keeps the ordered pairs ordered. Returns the
    /// same instance so it can be used inline.
    /// </summary>
    public static PlacementOptions Clamped(this PlacementOptions placement)
    {
        ArgumentNullException.ThrowIfNull(placement);

        placement.FreeSpaceWeight = Clamp(placement.FreeSpaceWeight, 0, 3, 1.0);
        placement.SpeedWeight = Clamp(placement.SpeedWeight, 0, 3, 1.0);
        placement.MinFreeBytes = Clamp(placement.MinFreeBytes, 0, 2048 * Gigabyte);
        placement.WriteHeadroomBytes = Clamp(placement.WriteHeadroomBytes, 0, 64 * Gigabyte);

        placement.EwmaAlpha = Clamp(placement.EwmaAlpha, 0.01, 1.0, 0.25);
        placement.BaselineAlpha = Clamp(placement.BaselineAlpha, 0.001, 0.5, 0.02);
        placement.BaselineRiseAlpha = Clamp(placement.BaselineRiseAlpha, 0.01, 1.0, 0.3);

        // A drive comes back at a higher ratio than it was parked at, or it flaps in and out of the
        // pool on one sample's noise.
        placement.ThrottleEnterRatio = Clamp(placement.ThrottleEnterRatio, 0.05, 0.9, 0.45);
        placement.ThrottleExitRatio = Clamp(
            placement.ThrottleExitRatio,
            placement.ThrottleEnterRatio + 0.05,
            0.99,
            0.75);

        placement.LatencyThrottleRatio = Clamp(placement.LatencyThrottleRatio, 1.2, 20, 3.0);
        placement.ThrottleCooldownSeconds = Clamp(placement.ThrottleCooldownSeconds, 5, 3600, 60);
        placement.ThrottleEnterSamples = Clamp(placement.ThrottleEnterSamples, 1, 50);
        placement.ThrottleExitSamples = Clamp(placement.ThrottleExitSamples, 1, 50);
        placement.MinSamplesForThrottleDetection = Clamp(placement.MinSamplesForThrottleDetection, 1, 100);

        placement.ReferenceThroughputBytesPerSecond = Clamp(
            placement.ReferenceThroughputBytesPerSecond,
            Megabyte,
            10_000d * Megabyte,
            120d * Megabyte);

        // The ceiling has to leave room above the floor: a drive measured at exactly one speed is
        // a drive placement cannot tell apart from any other.
        placement.MinSpeedFactor = Clamp(placement.MinSpeedFactor, 0.001, 1.0, 0.05);
        placement.MaxSpeedFactor = Clamp(
            placement.MaxSpeedFactor,
            Math.Max(1.0, placement.MinSpeedFactor * 2),
            64,
            8.0);

        placement.MinThroughputSampleBytes = Clamp(placement.MinThroughputSampleBytes, 4 * 1024, Gigabyte);
        placement.IdleProbeIntervalSeconds = Clamp(placement.IdleProbeIntervalSeconds, 10, 86_400, 120);
        placement.IdleProbeBytes = Clamp(placement.IdleProbeBytes, 64 * 1024, 64 * 1024 * 1024);

        placement.Rebalance.Clamped();
        return placement;
    }

    public static RebalanceOptions Clamped(this RebalanceOptions rebalance)
    {
        ArgumentNullException.ThrowIfNull(rebalance);

        rebalance.TriggerImbalance = Clamp(rebalance.TriggerImbalance, 0.02, 0.9, 0.15);

        // Stopping has to happen at a smaller gap than starting, or rebalancing never finishes.
        rebalance.TargetImbalance = Clamp(
            rebalance.TargetImbalance,
            0.005,
            Math.Max(0.005, rebalance.TriggerImbalance - 0.01),
            0.05);

        rebalance.MaxBytesPerSecond = Clamp(rebalance.MaxBytesPerSecond, Megabyte, 2048 * Megabyte);
        rebalance.MaxFileBytes = Clamp(rebalance.MaxFileBytes, Megabyte, 1024 * Gigabyte);
        return rebalance;
    }

    public static DuplicationSettings Clamped(this DuplicationSettings duplication)
    {
        ArgumentNullException.ThrowIfNull(duplication);

        duplication.MaxBytesPerSecond = Clamp(duplication.MaxBytesPerSecond, Megabyte, 2048 * Megabyte);
        duplication.ScanIntervalMinutes = Clamp(duplication.ScanIntervalMinutes, 1, 24 * 60, 15);
        duplication.CopyHeadroomBytes = Clamp(duplication.CopyHeadroomBytes, 0, 64 * Gigabyte);
        return duplication;
    }

    public static UpdateOptions Clamped(this UpdateOptions updates)
    {
        ArgumentNullException.ThrowIfNull(updates);

        updates.CheckIntervalHours = Clamp(updates.CheckIntervalHours, 1, 24 * 14, 6);
        return updates;
    }
}
