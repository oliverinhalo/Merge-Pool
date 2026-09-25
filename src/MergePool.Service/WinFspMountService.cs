using System.Runtime.Versioning;
using MergePool.Core.FileSystem;
using MergePool.Engine;
using MergePool.Fs.WinFsp;
using Microsoft.Extensions.Logging;

namespace MergePool.Service;

/// <summary>Mounts pools with WinFsp. One <see cref="PoolMounter"/> per pool.</summary>
[SupportedOSPlatform("windows")]
public sealed class WinFspMountService(ILogger<WinFspMountService> logger) : IPoolMountService, IDisposable
{
    private readonly Dictionary<Guid, PoolMounter> _mounters = [];
    private readonly object _gate = new();

    public void Mount(PoolFileSystemEngine engine, PoolMountOptions options)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);

        lock (_gate)
        {
            if (_mounters.TryGetValue(options.PoolId, out var existing) && existing.IsMounted)
            {
                return;
            }

            var mounter = new PoolMounter();
            mounter.Mount(engine, new MountRequest
            {
                MountPoint = options.MountPoint,
                VolumeLabel = options.VolumeLabel,
            });

            _mounters[options.PoolId] = mounter;
        }

        logger.LogInformation(
            "Mounted pool {PoolId} at {MountPoint}.", options.PoolId, options.MountPoint);
    }

    public void Unmount(Guid poolId)
    {
        PoolMounter? mounter;
        lock (_gate)
        {
            if (!_mounters.Remove(poolId, out mounter))
            {
                return;
            }
        }

        mounter.Dispose();
        logger.LogInformation("Unmounted pool {PoolId}.", poolId);
    }

    public bool IsMounted(Guid poolId)
    {
        lock (_gate)
        {
            return _mounters.TryGetValue(poolId, out var mounter) && mounter.IsMounted;
        }
    }

    public string? GetMountPoint(Guid poolId)
    {
        lock (_gate)
        {
            return _mounters.TryGetValue(poolId, out var mounter) ? mounter.MountPoint : null;
        }
    }

    public bool IsAvailable(out string? version) => WinFspProbe.IsInstalled(out version);

    public void Dispose()
    {
        List<PoolMounter> mounters;
        lock (_gate)
        {
            mounters = _mounters.Values.ToList();
            _mounters.Clear();
        }

        foreach (var mounter in mounters)
        {
            mounter.Dispose();
        }
    }
}
