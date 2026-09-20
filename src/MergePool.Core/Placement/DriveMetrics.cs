namespace MergePool.Core.Placement;

public enum IoKind
{
    Read = 0,
    Write = 1,

    /// <summary>A synthetic sample from the idle probe rather than real application IO.</summary>
    Probe = 2,
}

/// <summary>One measured IO operation.</summary>
public readonly record struct IoSample(Guid PartId, IoKind Kind, long Bytes, TimeSpan Elapsed)
{
    /// <summary>Bytes per second, or 0 when the operation was too fast to time meaningfully.</summary>
    public double ThroughputBytesPerSecond =>
        Elapsed.TotalSeconds > 0 ? Bytes / Elapsed.TotalSeconds : 0;

    public double LatencyMilliseconds => Elapsed.TotalMilliseconds;
}

/// <summary>Why a drive is currently parked, for display in the UI and the logs.</summary>
public enum ThrottleReason
{
    None = 0,

    /// <summary>Throughput collapsed against this drive's own baseline (SMR cache exhaustion, USB, thermal).</summary>
    ThroughputCollapse = 1,

    /// <summary>Latency climbed far above this drive's own baseline.</summary>
    LatencySpike = 2,
}

/// <summary>An immutable read of a drive's measured behaviour.</summary>
public sealed record DriveMetricsSnapshot
{
    public required Guid PartId { get; init; }

    /// <summary>Smoothed throughput in bytes per second.</summary>
    public double ThroughputBytesPerSecond { get; init; }

    /// <summary>The drive's own healthy throughput, which throttling is measured against.</summary>
    public double BaselineBytesPerSecond { get; init; }

    public double LatencyMilliseconds { get; init; }

    public double BaselineLatencyMilliseconds { get; init; }

    public double SpeedFactor { get; init; } = 1.0;

    public bool IsThrottled { get; init; }

    public ThrottleReason ThrottleReason { get; init; }

    public DateTimeOffset? ThrottledSince { get; init; }

    public DateTimeOffset? LastSampleUtc { get; init; }

    public long SampleCount { get; init; }

    public long BytesObserved { get; init; }

    /// <summary>Throughput as a fraction of this drive's baseline. 1.0 means "behaving normally".</summary>
    public double HealthRatio =>
        BaselineBytesPerSecond > 0 ? ThroughputBytesPerSecond / BaselineBytesPerSecond : 1.0;
}
