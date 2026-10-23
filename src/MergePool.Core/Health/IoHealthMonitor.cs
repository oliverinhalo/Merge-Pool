using System.Collections.Concurrent;
using System.Globalization;

namespace MergePool.Core.Health;

/// <summary>What the pool's I/O looks like from the inside right now.</summary>
public enum IoHealthState
{
    /// <summary>Nothing has been measured yet.</summary>
    Unknown = 0,

    /// <summary>Operations are completing in a reasonable time.</summary>
    Healthy = 1,

    /// <summary>Something is slow or has failed, but the pool is still answering.</summary>
    Slow = 2,

    /// <summary>
    /// Operations are not completing. This is the state that used to be an unrecoverable hang: the
    /// service acts on it rather than leaving the mount wedged.
    /// </summary>
    Stalled = 3,
}

/// <summary>One thing that went wrong, or took far too long, in the I/O path.</summary>
public sealed record IoFault
{
    /// <summary>Which part of the path: <c>topology</c>, <c>read</c>, <c>write</c>, <c>rename</c>…</summary>
    public required string Area { get; init; }

    public required string Detail { get; init; }

    public required DateTimeOffset AtUtc { get; init; }

    /// <summary>How long the operation took, when it was a stall rather than a failure.</summary>
    public TimeSpan? Elapsed { get; init; }

    public bool IsStall => Elapsed is not null;

    public override string ToString() => Elapsed is { } elapsed
        ? string.Create(CultureInfo.InvariantCulture, $"{Area}: {Detail} (after {elapsed.TotalSeconds:N1}s)")
        : $"{Area}: {Detail}";
}

/// <summary>
/// Records stalls and failures in the I/O path and turns them into a health state the service can
/// act on.
/// </summary>
/// <remarks>
/// <para>This exists because of how the pool used to fail. A blocking call on a file system
/// callback thread does not produce an error anyone can see; it produces a thread stuck in the
/// kernel, then a dispatcher pool with no free threads, then a volume that answers nothing at all
/// and processes that cannot be killed. From outside, the service looked perfectly healthy
/// throughout — it was answering its own named pipe, because that is a different thread.</para>
/// <para>Health is tracked <em>per area</em> — reads, writes, resolving the drives, and so on —
/// rather than as one number, because the parts of the path fail independently. A drive that has
/// stopped answering reads while volume queries still work would otherwise have its stalls wiped
/// out several times a second by the background refresh succeeding, and the watchdog would never
/// act on the one thing that was actually broken. The pool's state is the worst of its areas.</para>
/// <para>Deliberately cheap: a healthy operation takes no lock and allocates nothing, so this can
/// sit on the hot path.</para>
/// </remarks>
public sealed class IoHealthMonitor(TimeProvider? timeProvider = null, int faultsKept = 32)
{
    /// <summary>Stalls within this window of each other are treated as one ongoing problem.</summary>
    public static readonly TimeSpan StallWindow = TimeSpan.FromSeconds(60);

    /// <summary>Consecutive stalls in one area before the pool is called stalled rather than slow.</summary>
    public const int StallsBeforeStalled = 3;

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly int _faultsKept = Math.Max(1, faultsKept);
    private readonly ConcurrentQueue<IoFault> _faults = new();
    private readonly ConcurrentDictionary<string, Area> _areas = new(StringComparer.Ordinal);

    private long _completed;
    private long _stalls;
    private long _failures;

    /// <summary>Raised when the state changes, so the service can log it once rather than poll.</summary>
    public event EventHandler<IoHealthState>? StateChanged;

    public IoHealthState State => Worst().State;

    /// <summary>How long the pool has been stalled, or <c>null</c> when it is not.</summary>
    public TimeSpan? StalledFor
    {
        get
        {
            var worst = Worst();
            if (worst.State is not IoHealthState.Stalled || worst.Area is null)
            {
                return null;
            }

            lock (worst.Area.Gate)
            {
                return worst.Area.FirstStallAt is { } since ? _timeProvider.GetUtcNow() - since : null;
            }
        }
    }

    /// <summary>The part of the I/O path in the worst state, for a log line that says what broke.</summary>
    public string? WorstArea => Worst().Area?.Name;

    public long CompletedOperations => Interlocked.Read(ref _completed);

    public long Stalls => Interlocked.Read(ref _stalls);

    public long Failures => Interlocked.Read(ref _failures);

    /// <summary>The most recent faults, newest last. Bounded, so this never grows without end.</summary>
    public IReadOnlyList<IoFault> RecentFaults => [.. _faults];

    /// <summary>An operation completed in a reasonable time.</summary>
    public void RecordHealthy(string area)
    {
        Interlocked.Increment(ref _completed);

        var entry = AreaFor(area);
        var before = State;

        lock (entry.Gate)
        {
            entry.LastHealthyAt = _timeProvider.GetUtcNow();

            // Clears this area's run only. A read path that is wedged stays wedged however well the
            // drives are resolving.
            entry.ConsecutiveStalls = 0;
            entry.FirstStallAt = null;
        }

        Announce(before);
    }

    /// <summary>A bounded wait ran out, or an operation took far longer than it ever should.</summary>
    public void RecordStall(string area, TimeSpan elapsed, string detail)
    {
        Interlocked.Increment(ref _stalls);

        var entry = AreaFor(area);
        var before = State;
        var now = _timeProvider.GetUtcNow();

        lock (entry.Gate)
        {
            // A stall long after the last one starts a new run rather than adding to a stale one: a
            // pool that is briefly slow twice an hour has not stalled.
            if (entry.LastStallAt is { } last && now - last > StallWindow)
            {
                entry.ConsecutiveStalls = 0;
                entry.FirstStallAt = null;
            }

            entry.ConsecutiveStalls++;
            entry.FirstStallAt ??= now;
            entry.LastStallAt = now;
        }

        Add(new IoFault { Area = area, Detail = detail, Elapsed = elapsed, AtUtc = now });
        Announce(before);
    }

    /// <summary>An operation failed outright. Reported, but not on its own a sign of a stall.</summary>
    public void RecordFault(string area, string detail)
    {
        Interlocked.Increment(ref _failures);

        Add(new IoFault
        {
            Area = area,
            Detail = detail,
            AtUtc = _timeProvider.GetUtcNow(),
        });
    }

    /// <summary>
    /// Called after the service has recovered the mount, so the next stall is judged on its own
    /// rather than on the run that led to the recovery.
    /// </summary>
    public void Reset()
    {
        var before = State;

        foreach (var entry in _areas.Values)
        {
            lock (entry.Gate)
            {
                entry.ConsecutiveStalls = 0;
                entry.FirstStallAt = null;
                entry.LastStallAt = null;
                entry.LastHealthyAt = null;
            }
        }

        Announce(before);
    }

    /// <summary>One line for a log or the window: what the pool's I/O is doing and why we say so.</summary>
    public string Describe()
    {
        var (state, area) = Worst();
        var latest = _faults.LastOrDefault();

        return state switch
        {
            IoHealthState.Unknown => "No pool I/O measured yet.",
            IoHealthState.Healthy => string.Create(
                CultureInfo.CurrentCulture,
                $"Pool I/O is answering normally ({Interlocked.Read(ref _completed):N0} operations)."),
            IoHealthState.Slow => $"Pool I/O is slow ({area?.Name}). Most recent: {latest}",
            _ => string.Create(
                CultureInfo.CurrentCulture,
                $"Pool I/O has stalled ({area?.Name}) for {StalledFor?.TotalSeconds ?? 0:N0}s. Most recent: {latest}"),
        };
    }

    private Area AreaFor(string name) =>
        _areas.GetOrAdd(name ?? "io", static key => new Area(key));

    /// <summary>
    /// The worst state across the areas. One wedged area means a wedged pool, however healthy the
    /// others are.
    /// </summary>
    private (IoHealthState State, Area? Area) Worst()
    {
        var worst = IoHealthState.Unknown;
        Area? worstArea = null;

        foreach (var entry in _areas.Values)
        {
            IoHealthState state;
            lock (entry.Gate)
            {
                state = entry.ConsecutiveStalls >= StallsBeforeStalled ? IoHealthState.Stalled
                    : entry.ConsecutiveStalls > 0 ? IoHealthState.Slow
                    : entry.LastHealthyAt is null ? IoHealthState.Unknown
                    : IoHealthState.Healthy;
            }

            if (state > worst)
            {
                worst = state;
                worstArea = entry;
            }
        }

        return (worst, worstArea);
    }

    private void Add(IoFault fault)
    {
        _faults.Enqueue(fault);
        while (_faults.Count > _faultsKept && _faults.TryDequeue(out _))
        {
        }
    }

    private void Announce(IoHealthState before)
    {
        var after = State;
        if (before != after)
        {
            StateChanged?.Invoke(this, after);
        }
    }

    /// <summary>One part of the I/O path, tracked on its own.</summary>
    private sealed class Area(string name)
    {
        public string Name { get; } = name;

        public object Gate { get; } = new();

        public int ConsecutiveStalls { get; set; }

        public DateTimeOffset? FirstStallAt { get; set; }

        public DateTimeOffset? LastStallAt { get; set; }

        public DateTimeOffset? LastHealthyAt { get; set; }
    }
}
