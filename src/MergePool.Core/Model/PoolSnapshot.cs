namespace MergePool.Core.Model;

public enum PoolHealth
{
    /// <summary>Every configured drive is present.</summary>
    Healthy = 0,

    /// <summary>At least one configured drive is missing; the rest of the pool keeps serving.</summary>
    Degraded = 1,

    /// <summary>No drive is present. The pool cannot serve anything.</summary>
    Offline = 2,
}

/// <summary>An immutable view of the pool's parts at a point in time.</summary>
public sealed record PoolSnapshot
{
    public required Guid PoolId { get; init; }

    public string Name { get; init; } = string.Empty;

    public required IReadOnlyList<PoolPart> Parts { get; init; }

    public IEnumerable<PoolPart> OnlineParts => Parts.Where(static p => p.IsOnline);

    public IEnumerable<PoolPart> WritableParts => Parts.Where(static p => p.AcceptsNewFiles);

    /// <summary>Parts we currently cannot read and whose content we therefore cannot account for.</summary>
    public IEnumerable<PoolPart> UnavailableParts => Parts.Where(static p => !p.IsPresenceKnown);

    /// <summary>
    /// Whether this view accounts for every configured drive. False means at least one drive could
    /// not be reached, so what the merged view does <em>not</em> contain proves nothing.
    /// </summary>
    /// <remarks>
    /// The file system refuses listings and "no such file" answers from an incomplete view rather
    /// than serving them as fact. A drive that is genuinely gone makes the pool degraded, not
    /// incomplete, and degraded answers are served normally — losing a drive is a supported state;
    /// quietly hiding the files on a drive that is merely slow to answer is not.
    /// </remarks>
    public bool IsComplete => Parts.All(static p => p.IsPresenceKnown);

    /// <summary>A topology we have not managed to resolve. Every answer from it is "not ready".</summary>
    public static PoolSnapshot Unknown { get; } = new()
    {
        PoolId = Guid.Empty,
        Parts = [],
        IsResolved = false,
    };

    /// <summary>False only for <see cref="Unknown"/>: no resolve has succeeded yet.</summary>
    public bool IsResolved { get; init; } = true;

    public PoolHealth Health =>
        Parts.Count == 0 || Parts.All(static p => !p.IsOnline) ? PoolHealth.Offline
        : Parts.Any(static p => !p.IsOnline) ? PoolHealth.Degraded
        : PoolHealth.Healthy;

    /// <summary>Pool capacity is the sum of the parts we can actually see right now.</summary>
    public long TotalBytes => OnlineParts.Sum(static p => p.TotalBytes);

    public long FreeBytes => OnlineParts.Sum(static p => p.FreeBytes);

    /// <summary>What the pool itself holds, across every drive that is present.</summary>
    public long PoolUsedBytes => OnlineParts.Sum(static p => p.PoolBytes ?? 0);

    public int PoolFileCount => OnlineParts.Sum(static p => p.PoolFileCount ?? 0);

    /// <summary>
    /// What the pool can hold: its own content plus the free space on its drives. Deliberately not
    /// the drives' total size — space occupied by files outside the pool was never the pool's.
    /// </summary>
    public long PoolCapacityBytes => OnlineParts.Sum(static p => p.PoolCapacityBytes);

    /// <summary>True once every present drive has been measured, so the pool figures are real.</summary>
    public bool IsUsageMeasured => Parts.Count > 0 && OnlineParts.All(static p => p.PoolBytes is not null);

    /// <summary>Everything on the pool's drives that is not in the pool.</summary>
    public long ForeignBytes => OnlineParts.Sum(static p => p.ForeignBytes ?? 0);

    public PoolPart? FindPart(Guid partId) => Parts.FirstOrDefault(p => p.PartId == partId);
}
