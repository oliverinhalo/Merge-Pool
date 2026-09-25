using MergePool.Core.Model;

namespace MergePool.Core.Placement;

public interface IPlacementStrategy
{
    /// <summary>Picks the part a new file should land on, or <c>null</c> when nothing can hold it.</summary>
    PlacementDecision? Select(PoolSnapshot snapshot, PlacementRequest request);
}
