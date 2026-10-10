using MergePool.Core.Config;
using MergePool.Core.Duplication;
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
        TimeProvider timeProvider,
        DuplicationSettings? duplicationSettings = null)
    {
        Definition = definition;
        Topology = new CachingPoolTopology(() => partManager.Resolve(definition), TimeSpan.FromSeconds(2), timeProvider);
        Placement = new FreeSpaceSpeedPlacement(placementOptions, metrics);
        FileSystem = new PoolFileSystemEngine(Topology, Placement, relocator, new PoolFileSystemOptions(), metrics);
        Rebalancer = new Rebalancer(Topology, placementOptions.Rebalance, relocator, metrics, timeProvider);

        DuplicationSettings = duplicationSettings ?? new DuplicationSettings();

        var duplication = new DuplicationOptions
        {
            CopyHeadroomBytes = DuplicationSettings.CopyHeadroomBytes,
            MegabytesPerSecond = DuplicationSettings.MegabytesPerSecond,
        };

        DuplicationPlanner = new DuplicationPlanner(Topology, duplication);
        Duplicator = new Duplicator(Topology, duplication, timeProvider);
    }

    public PoolDefinition Definition { get; }

    public Guid PoolId => Definition.Id;

    public CachingPoolTopology Topology { get; }

    public FreeSpaceSpeedPlacement Placement { get; }

    public PoolFileSystemEngine FileSystem { get; }

    public Rebalancer Rebalancer { get; }

    public DuplicationSettings DuplicationSettings { get; }

    public DuplicationPlanner DuplicationPlanner { get; }

    public Duplicator Duplicator { get; }

    /// <summary>The protected folders of this pool, as configured.</summary>
    public IReadOnlyList<DuplicationRule> DuplicationRules => Definition.Duplication;

    /// <summary>What duplication would do right now, and what it cannot.</summary>
    public DuplicationPlan PlanDuplication() =>
        Definition.Duplication.Count == 0 || !DuplicationSettings.Enabled
            ? DuplicationPlan.Empty
            : DuplicationPlanner.Plan(Definition.Duplication);

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

    public void Dispose()
    {
        Rebalancer.Pause();
        Duplicator.Pause();
    }
}
