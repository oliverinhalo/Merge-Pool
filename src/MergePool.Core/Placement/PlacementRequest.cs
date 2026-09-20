namespace MergePool.Core.Placement;

/// <summary>What placement is being asked to decide.</summary>
public sealed record PlacementRequest
{
    public required string PoolPath { get; init; }

    /// <summary>Best known size of the file. 0 when a brand new file is being created.</summary>
    public long EstimatedSize { get; init; }

    /// <summary>Parts that must not be chosen (e.g. the drive we are moving off).</summary>
    public IReadOnlyCollection<Guid> ExcludedParts { get; init; } = Array.Empty<Guid>();

    /// <summary>
    /// When set, placement keeps the file on this part if it still fits. Keeps a rename or an
    /// in-place grow from bouncing between drives.
    /// </summary>
    public Guid? PreferredPart { get; init; }

    /// <summary>Set for background work so a throttled-but-usable drive is not treated as a fallback.</summary>
    public bool IsBackground { get; init; }
}

public sealed record PlacementDecision
{
    public required Guid PartId { get; init; }

    public required double Score { get; init; }

    /// <summary>True when every candidate was throttled and we picked the least bad one.</summary>
    public bool AllCandidatesThrottled { get; init; }

    public required string Reason { get; init; }
}
