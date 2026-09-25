using MergePool.Core.Config;
using MergePool.Core.FileSystem;
using MergePool.Core.Model;
using MergePool.Core.Placement;
using MergePool.Core.Rebalance;

namespace MergePool.Engine;

/// <summary>Everything the service keeps alive for one configured pool.</summary>
public sealed class PoolRuntime : IDisposable
{
    public PoolRuntime(
        PoolDefinition definition,
        PoolPartManager partManager,
        PlacementOptions placementOptions,
        DriveMetricsTracker metrics,
        FileRelocator relocator,
        TimeProvider timeProvider)
    {
        Definition = definition;
        Topology = new CachingPoolTopology(() => partManager.Resolve(definition), TimeSpan.FromSeconds(2), timeProvider);
        Placement = new FreeSpaceSpeedPlacement(placementOptions, metrics);
        FileSystem = new PoolFileSystemEngine(Topology, Placement, relocator, new PoolFileSystemOptions(), metrics);
        Rebalancer = new Rebalancer(Topology, placementOptions.Rebalance, relocator, metrics, timeProvider);
    }

    public PoolDefinition Definition { get; }

    public Guid PoolId => Definition.Id;

    public CachingPoolTopology Topology { get; }

    public FreeSpaceSpeedPlacement Placement { get; }

    public PoolFileSystemEngine FileSystem { get; }

    public Rebalancer Rebalancer { get; }

    public PoolSnapshot Snapshot => Topology.Current;

    /// <summary>
    /// Re-resolves the drives before reading. Status and health must reflect a drive that has just
    /// been unplugged, not whatever the file system cache last saw.
    /// </summary>
    public PoolSnapshot FreshSnapshot
    {
        get
        {
            Topology.Invalidate();
            return Topology.Current;
        }
    }

    /// <summary>Forces the next topology read to re-scan, e.g. after a drive arrives or leaves.</summary>
    public void Invalidate() => Topology.Invalidate();

    public void Dispose() => Rebalancer.Pause();
}
