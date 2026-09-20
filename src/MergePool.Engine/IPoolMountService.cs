using MergePool.Core.FileSystem;

namespace MergePool.Engine;

public sealed record PoolMountOptions
{
    public required Guid PoolId { get; init; }

    public required string MountPoint { get; init; }

    public string VolumeLabel { get; init; } = "MergePool";
}

/// <summary>
/// Mounts a pool as a drive. Abstracted so the engine (and its tests) never depend on WinFsp; the
/// Windows service supplies the real implementation.
/// </summary>
public interface IPoolMountService
{
    void Mount(PoolFileSystemEngine engine, PoolMountOptions options);

    void Unmount(Guid poolId);

    bool IsMounted(Guid poolId);

    string? GetMountPoint(Guid poolId);

    /// <summary>True when the platform can mount at all (WinFsp present).</summary>
    bool IsAvailable(out string? version);
}

/// <summary>
/// Records mounts without touching the OS. Used on non-Windows hosts and in tests, so engine
/// behaviour around mounting is exercised without a driver.
/// </summary>
public sealed class InMemoryMountService : IPoolMountService
{
    private readonly Dictionary<Guid, string> _mounts = [];
    private readonly object _gate = new();

    public IReadOnlyDictionary<Guid, string> Mounts
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<Guid, string>(_mounts);
            }
        }
    }

    public void Mount(PoolFileSystemEngine engine, PoolMountOptions options)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);

        lock (_gate)
        {
            if (_mounts.ContainsKey(options.PoolId))
            {
                throw new InvalidOperationException($"Pool {options.PoolId:D} is already mounted.");
            }

            _mounts[options.PoolId] = options.MountPoint;
        }
    }

    public void Unmount(Guid poolId)
    {
        lock (_gate)
        {
            _mounts.Remove(poolId);
        }
    }

    public bool IsMounted(Guid poolId)
    {
        lock (_gate)
        {
            return _mounts.ContainsKey(poolId);
        }
    }

    public string? GetMountPoint(Guid poolId)
    {
        lock (_gate)
        {
            return _mounts.TryGetValue(poolId, out var mountPoint) ? mountPoint : null;
        }
    }

    public bool IsAvailable(out string? version)
    {
        version = "in-memory";
        return true;
    }
}
