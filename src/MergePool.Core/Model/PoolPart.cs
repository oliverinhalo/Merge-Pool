using MergePool.Core.Volumes;

namespace MergePool.Core.Model;

public enum PoolPartState
{
    /// <summary>The volume is not present. The pool runs degraded and rejoins automatically.</summary>
    Offline = 0,

    /// <summary>Present and taking new files.</summary>
    Online = 1,

    /// <summary>Present and readable, but excluded from new placement (throttled, full, read-only, draining).</summary>
    ReadOnly = 2,
}

/// <summary>A resolved pool part: a <c>.PoolPart-{GUID}</c> folder on a currently known volume.</summary>
public sealed record PoolPart
{
    public required Guid PartId { get; init; }

    public required VolumeId Volume { get; init; }

    /// <summary>Absolute host path of the pool part folder, or <c>null</c> when offline.</summary>
    public string? RootPath { get; init; }

    public PoolPartState State { get; init; } = PoolPartState.Offline;

    public long TotalBytes { get; init; }

    public long FreeBytes { get; init; }

    /// <summary>Relative speed of this drive, 1.0 being the nominal baseline. Feeds placement scoring.</summary>
    public double SpeedFactor { get; init; } = 1.0;

    /// <summary>Set when the throttle detector has parked this drive; it still serves reads.</summary>
    public bool IsThrottled { get; init; }

    public string Label { get; init; } = string.Empty;

    public char? DriveLetter { get; init; }

    public bool IsOnline => State is not PoolPartState.Offline && RootPath is not null;

    public bool AcceptsNewFiles => State is PoolPartState.Online && RootPath is not null;

    /// <summary>Host root, or a throw when the caller forgot to check <see cref="IsOnline"/>.</summary>
    public string RequireRootPath() =>
        RootPath ?? throw new InvalidOperationException($"Pool part {PartId:D} is offline.");
}
