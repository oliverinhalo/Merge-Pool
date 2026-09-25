namespace MergePool.Core.Model;

/// <summary>Supplies the current parts of a pool. Refreshed when volumes arrive or leave.</summary>
public interface IPoolTopology
{
    PoolSnapshot Current { get; }
}

/// <summary>A fixed topology, useful for tests and for one-shot operations.</summary>
public sealed class StaticPoolTopology(PoolSnapshot snapshot) : IPoolTopology
{
    public PoolSnapshot Current { get; } = snapshot;
}
