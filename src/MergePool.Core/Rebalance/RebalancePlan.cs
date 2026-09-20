using MergePool.Core.Model;

namespace MergePool.Core.Rebalance;

public sealed record RebalanceMove
{
    public required string PoolPath { get; init; }

    public required Guid FromPartId { get; init; }

    public required Guid ToPartId { get; init; }

    public required long Bytes { get; init; }
}

public sealed record RebalancePlan
{
    public required IReadOnlyList<RebalanceMove> Moves { get; init; }

    /// <summary>Spread between the fullest and emptiest drive before the plan runs.</summary>
    public required double ImbalanceBefore { get; init; }

    /// <summary>Spread the plan is expected to leave behind.</summary>
    public required double ImbalanceAfter { get; init; }

    public long TotalBytes => Moves.Sum(static m => m.Bytes);

    public bool IsEmpty => Moves.Count == 0;

    public static RebalancePlan Empty(double imbalance) => new()
    {
        Moves = Array.Empty<RebalanceMove>(),
        ImbalanceBefore = imbalance,
        ImbalanceAfter = imbalance,
    };
}

public sealed record RebalanceResult
{
    public required int MovedCount { get; init; }

    public required long MovedBytes { get; init; }

    /// <summary>Files left where they were because something had them open.</summary>
    public required int SkippedInUse { get; init; }

    public required int Failed { get; init; }

    public required bool Cancelled { get; init; }
}

public static class PoolBalance
{
    /// <summary>Fraction of a drive that is in use. Empty or unknown drives count as empty.</summary>
    public static double Usage(PoolPart part) =>
        part.TotalBytes <= 0 ? 0 : Math.Clamp((part.TotalBytes - part.FreeBytes) / (double)part.TotalBytes, 0, 1);

    /// <summary>Spread between the fullest and the emptiest online drive.</summary>
    public static double Imbalance(PoolSnapshot snapshot)
    {
        var usages = snapshot.OnlineParts.Select(Usage).ToArray();
        return usages.Length < 2 ? 0 : usages.Max() - usages.Min();
    }
}
