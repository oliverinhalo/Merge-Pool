using System.Globalization;
using MergePool.Core.Config;
using MergePool.Core.Model;

namespace MergePool.Core.Placement;

/// <summary>
/// Score = free space ^ freeSpaceWeight × speed factor ^ speedWeight. Throttled drives are skipped
/// entirely unless every candidate is throttled, in which case the best of them is used rather than
/// failing the write.
/// </summary>
public sealed class FreeSpaceSpeedPlacement(PlacementOptions options, ISpeedFactorSource? speedSource = null)
    : IPlacementStrategy
{
    private readonly PlacementOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly ISpeedFactorSource? _speedSource = speedSource;

    public PlacementDecision? Select(PoolSnapshot snapshot, PlacementRequest request)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);

        var required = RequiredBytes(request.EstimatedSize);
        var candidates = snapshot.Parts
            .Where(p => p.AcceptsNewFiles)
            .Where(p => !request.ExcludedParts.Contains(p.PartId))
            .Where(p => p.FreeBytes >= required)
            .ToArray();

        if (candidates.Length == 0)
        {
            return null;
        }

        // Keeping a file where it already is beats a marginally better score elsewhere.
        if (request.PreferredPart is { } preferred)
        {
            var stay = candidates.FirstOrDefault(p => p.PartId == preferred);
            if (stay is not null && !IsThrottled(stay))
            {
                return new PlacementDecision
                {
                    PartId = stay.PartId,
                    Score = Score(stay),
                    Reason = "stay-on-current-drive",
                };
            }
        }

        var healthy = candidates.Where(p => !IsThrottled(p)).ToArray();
        var allThrottled = healthy.Length == 0;
        var pool = allThrottled ? candidates : healthy;

        PoolPart? best = null;
        var bestScore = double.NegativeInfinity;
        foreach (var part in pool)
        {
            var score = Score(part);
            // Ties break on part id so placement is deterministic and testable.
            if (score > bestScore || (score == bestScore && best is not null && part.PartId.CompareTo(best.PartId) < 0))
            {
                best = part;
                bestScore = score;
            }
        }

        if (best is null)
        {
            return null;
        }

        return new PlacementDecision
        {
            PartId = best.PartId,
            Score = bestScore,
            AllCandidatesThrottled = allThrottled,
            Reason = allThrottled
                ? "all-drives-throttled-picked-best"
                : string.Create(CultureInfo.InvariantCulture, $"score={bestScore:G6}"),
        };
    }

    /// <summary>A write needs its own size plus headroom, and must leave the floor intact.</summary>
    public long RequiredBytes(long estimatedSize) =>
        Math.Max(0, estimatedSize) + Math.Max(0, _options.WriteHeadroomBytes) + Math.Max(0, _options.MinFreeBytes);

    private bool IsThrottled(PoolPart part) =>
        part.IsThrottled || (_speedSource?.IsThrottled(part.PartId) ?? false);

    private double Score(PoolPart part)
    {
        var free = Math.Max(1d, part.FreeBytes);
        var speed = Math.Max(0.0001, _speedSource?.GetSpeedFactor(part.PartId) ?? part.SpeedFactor);
        return Math.Pow(free, _options.FreeSpaceWeight) * Math.Pow(speed, _options.SpeedWeight);
    }
}

/// <summary>
/// Supplies live per-drive speed factors and throttle state. Implemented by the metrics engine in
/// the service; <c>null</c> here means fall back to the static factors on the snapshot.
/// </summary>
public interface ISpeedFactorSource
{
    double GetSpeedFactor(Guid partId);

    bool IsThrottled(Guid partId);
}
