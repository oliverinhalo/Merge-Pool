namespace MergePool.Core.Model;

/// <summary>
/// Caches the resolved topology for a short window. Resolving touches every drive, which is far too
/// expensive to do on each file system call, but the window must stay short enough that a drive
/// arriving or leaving is noticed quickly.
/// </summary>
public sealed class CachingPoolTopology : IPoolTopology
{
    private readonly Func<PoolSnapshot> _resolve;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ttl;
    private readonly object _gate = new();

    private PoolSnapshot? _cached;
    private long _cachedAtTicks;

    public CachingPoolTopology(Func<PoolSnapshot> resolve, TimeSpan? ttl = null, TimeProvider? timeProvider = null)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ttl = ttl ?? TimeSpan.FromSeconds(2);
    }

    public PoolSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                var now = _timeProvider.GetUtcNow().Ticks;
                if (_cached is not null && now - _cachedAtTicks < _ttl.Ticks)
                {
                    return _cached;
                }

                _cached = _resolve();
                _cachedAtTicks = now;
                return _cached;
            }
        }
    }

    /// <summary>Forces the next read to re-resolve. Called when a volume arrives or leaves.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _cached = null;
        }
    }
}
