using MergePool.Core.Health;

namespace MergePool.Core.Model;

/// <summary>
/// Serves the resolved topology to the file system without ever blocking a file system call on
/// resolving it.
/// </summary>
/// <remarks>
/// <para>Resolving touches every volume on the machine, which is far too expensive to do on each
/// file system call and — this is the part that matters — can block for an unbounded time. A dead
/// network mapping, a card reader with no card, or simply a drive saturated by the copy that is
/// going through the pool right now can take many seconds to answer a volume query.</para>
/// <para>Worse, one of the volumes Windows enumerates is the pool's own mount. Querying it comes
/// straight back into the file system as a <c>GetVolumeInfo</c> callback that needs the topology
/// too. An earlier version of this class held a lock across the resolve, so those two met head on:
/// the resolving thread waited for a volume query against itself while holding the lock that query
/// needed. Both threads waited for ever, inside the kernel's I/O path, which is why the mount went
/// unresponsive and the processes using it could not even be killed.</para>
/// <para>So the rules here are absolute:</para>
/// <list type="number">
/// <item>No lock is ever held across <c>resolve</c>.</item>
/// <item><c>resolve</c> never runs on the calling thread. It runs on the thread pool, so a file
/// system callback cannot be the thing that blocks in it.</item>
/// <item>A caller that already has a snapshot is given it immediately and the refresh happens
/// behind them — stale by up to the TTL beats correct but hung.</item>
/// <item>A caller with nothing cached waits a bounded time and then gets an explicitly unknown
/// topology, so the file system fails fast with "device not ready" instead of hanging.</item>
/// <item>A resolve that fails or times out never replaces a good topology with an empty one. A pool
/// whose drives we momentarily cannot see is not a pool with no drives.</item>
/// </list>
/// </remarks>
public sealed class CachingPoolTopology : IPoolTopology
{
    /// <summary>How long a caller with nothing cached waits before being told the pool is unknown.</summary>
    public static readonly TimeSpan DefaultColdStartTimeout = TimeSpan.FromSeconds(5);

    /// <summary>A resolve slower than this is recorded as a stall, whatever it eventually returns.</summary>
    public static readonly TimeSpan DefaultResolveStallThreshold = TimeSpan.FromSeconds(10);

    private readonly Func<PoolSnapshot> _resolve;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ttl;
    private readonly TimeSpan _coldStartTimeout;
    private readonly TimeSpan _stallThreshold;
    private readonly IoHealthMonitor? _health;

    /// <summary>Guards the bookkeeping below, and nothing that can block.</summary>
    private readonly object _gate = new();

    private Entry? _entry;
    private Task? _inFlight;
    private bool _invalidated;

    /// <summary>
    /// Set while this thread is inside the resolve. A resolve that comes back round into the pool on
    /// the same thread is served the cached answer rather than recursing.
    /// </summary>
    [ThreadStatic]
    private static bool _resolvingOnThisThread;

    public CachingPoolTopology(
        Func<PoolSnapshot> resolve,
        TimeSpan? ttl = null,
        TimeProvider? timeProvider = null,
        TimeSpan? coldStartTimeout = null,
        IoHealthMonitor? health = null,
        TimeSpan? stallThreshold = null)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ttl = ttl ?? TimeSpan.FromSeconds(2);
        _coldStartTimeout = coldStartTimeout ?? DefaultColdStartTimeout;
        _stallThreshold = stallThreshold ?? DefaultResolveStallThreshold;
        _health = health;
    }

    /// <summary>The topology as last resolved. Never blocks for longer than the cold-start timeout.</summary>
    public PoolSnapshot Current
    {
        get
        {
            var entry = Fresh();
            if (entry is not null)
            {
                return entry.Snapshot;
            }

            // Re-entered from inside our own resolve: answer with whatever we have rather than
            // waiting on the resolve that is waiting on us.
            if (_resolvingOnThisThread)
            {
                return Last()?.Snapshot ?? PoolSnapshot.Unknown;
            }

            var refresh = StartOrJoinRefresh();

            // Stale while revalidating: the whole point. A two-second-old list of drives is a fine
            // answer; making sixteen file system callbacks queue behind a volume scan is not.
            if (Last() is { } stale)
            {
                return stale.Snapshot;
            }

            // Nothing cached at all, so there is no honest answer but to wait — briefly.
            if (refresh.Wait(_coldStartTimeout))
            {
                return Last()?.Snapshot ?? PoolSnapshot.Unknown;
            }

            _health?.RecordStall("topology", _coldStartTimeout, "the drives could not be resolved in time");
            return PoolSnapshot.Unknown;
        }
    }

    /// <summary>Whether a usable topology has been resolved at least once.</summary>
    public bool HasSnapshot => Last() is not null;

    /// <summary>Forces the next read to re-resolve. Called when a volume arrives or leaves.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _invalidated = true;
        }
    }

    /// <summary>
    /// Resolves now and waits for the answer, for the places that must not be served a stale one —
    /// status, health and the window. Never called from a file system callback.
    /// </summary>
    /// <returns>The fresh topology, or the last known one when the resolve did not finish in time.</returns>
    public PoolSnapshot Refresh(TimeSpan? timeout = null)
    {
        Invalidate();

        var refresh = StartOrJoinRefresh();
        if (!refresh.Wait(timeout ?? _coldStartTimeout))
        {
            _health?.RecordStall("topology", timeout ?? _coldStartTimeout, "a forced refresh did not finish in time");
            return Last()?.Snapshot ?? PoolSnapshot.Unknown;
        }

        return Last()?.Snapshot ?? PoolSnapshot.Unknown;
    }

    private Entry? Fresh()
    {
        lock (_gate)
        {
            if (_entry is null || _invalidated)
            {
                return null;
            }

            var age = _timeProvider.GetUtcNow() - _entry.ResolvedAt;
            return age < _ttl ? _entry : null;
        }
    }

    private Entry? Last()
    {
        lock (_gate)
        {
            return _entry;
        }
    }

    /// <summary>
    /// Starts a refresh if none is running, and hands back the task to wait on. The resolve itself
    /// runs on the thread pool: a file system callback must never be the thread that blocks in it.
    /// </summary>
    private Task StartOrJoinRefresh()
    {
        lock (_gate)
        {
            if (_inFlight is { IsCompleted: false })
            {
                return _inFlight;
            }

            _invalidated = false;
            _inFlight = Task.Run(ResolveOnce);
            return _inFlight;
        }
    }

    private void ResolveOnce()
    {
        var started = _timeProvider.GetTimestamp();

        try
        {
            _resolvingOnThisThread = true;
            var snapshot = _resolve();
            var elapsed = _timeProvider.GetElapsedTime(started);

            if (elapsed > _stallThreshold)
            {
                _health?.RecordStall("topology", elapsed, "resolving the drives took far longer than it should");
            }
            else
            {
                _health?.RecordHealthy("topology");
            }

            Publish(snapshot);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or TimeoutException
            or InvalidOperationException)
        {
            // Keep the last good topology. Publishing an empty one here is what made a live pool
            // report a populated folder as empty: the drives had not gone anywhere, we had simply
            // failed to ask.
            _health?.RecordFault("topology", exception.Message);
        }
        finally
        {
            _resolvingOnThisThread = false;
        }
    }

    private void Publish(PoolSnapshot snapshot)
    {
        lock (_gate)
        {
            _entry = new Entry(snapshot, _timeProvider.GetUtcNow());
        }
    }

    private sealed record Entry(PoolSnapshot Snapshot, DateTimeOffset ResolvedAt);
}
