using MergePool.Core.Paths;

namespace MergePool.Core.Model;

/// <summary>How much of a drive the pool itself is using, as opposed to the drive's total usage.</summary>
public readonly record struct PartUsage
{
    /// <summary>Bytes held inside the <c>.PoolPart-{GUID}</c> folder, excluding its marker file.</summary>
    public required long Bytes { get; init; }

    public required int FileCount { get; init; }

    public required DateTimeOffset MeasuredUtc { get; init; }

    /// <summary>False until the part has been walked at least once.</summary>
    public bool IsKnown => MeasuredUtc != default;

    public static PartUsage Unknown => default;
}

/// <summary>
/// Measures what the pool is actually holding on each drive. A drive's free space says nothing
/// about this: most of a drive may be taken by files that are not in the pool at all.
/// </summary>
/// <remarks>
/// Walking a part is proportional to the number of files on it, so a measurement is cached per part
/// and refreshed no more often than <see cref="Ttl"/>. Nothing here writes: the meter only ever
/// reads sizes inside a pool part.
/// </remarks>
public sealed class PartUsageMeter(TimeSpan? ttl = null, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<Guid, PartUsage> _usage = [];
    private readonly object _gate = new();

    public TimeSpan Ttl { get; } = ttl ?? TimeSpan.FromSeconds(60);

    /// <summary>The last measurement for a part, or <see cref="PartUsage.Unknown"/>.</summary>
    public PartUsage Get(Guid partId)
    {
        lock (_gate)
        {
            return _usage.GetValueOrDefault(partId);
        }
    }

    /// <summary>Drops a part's measurement, so the next refresh re-walks it.</summary>
    public void Invalidate(Guid partId)
    {
        lock (_gate)
        {
            _usage.Remove(partId);
        }
    }

    /// <summary>
    /// Re-measures the parts whose cached figure has expired. Cheap when nothing is stale, so it is
    /// safe to call from the engine's regular tick.
    /// </summary>
    public void Refresh(PoolSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        foreach (var part in snapshot.OnlineParts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsStale(part.PartId))
            {
                continue;
            }

            Measure(part, cancellationToken);
        }
    }

    /// <summary>Walks a part now and stores the result.</summary>
    public PartUsage Measure(PoolPart part, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(part);

        var root = part.RootPath;
        if (root is null)
        {
            return PartUsage.Unknown;
        }

        long bytes = 0;
        var files = 0;

        try
        {
            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // The part's own marker is bookkeeping, not pooled content.
                if (PoolPath.Comparer.Equals(file.Name, PoolPartLayout.MarkerFileName)
                    && PoolPath.Comparer.Equals(file.DirectoryName, root.TrimEnd(Path.DirectorySeparatorChar)))
                {
                    continue;
                }

                bytes += file.Length;
                files++;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A drive that left mid-walk keeps whatever it last reported rather than reading as empty.
            return Get(part.PartId);
        }

        var usage = new PartUsage
        {
            Bytes = bytes,
            FileCount = files,
            MeasuredUtc = _timeProvider.GetUtcNow(),
        };

        lock (_gate)
        {
            _usage[part.PartId] = usage;
        }

        return usage;
    }

    private bool IsStale(Guid partId)
    {
        lock (_gate)
        {
            if (!_usage.TryGetValue(partId, out var usage))
            {
                return true;
            }

            return _timeProvider.GetUtcNow() - usage.MeasuredUtc >= Ttl;
        }
    }
}
