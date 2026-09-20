using MergePool.Core.Config;
using MergePool.Core.FileSystem;
using MergePool.Core.Model;
using MergePool.Core.Paths;
using MergePool.Core.Placement;
using MergePool.Core.Union;

namespace MergePool.Core.Rebalance;

/// <summary>
/// Background evening-out of drive usage. It is deliberately timid: it runs at a capped rate, it
/// pauses on request, it never moves a file anything else has open, and it stops as soon as the
/// pool is balanced enough. Real IO always wins.
/// </summary>
public sealed class Rebalancer(
    IPoolTopology topology,
    RebalanceOptions options,
    FileRelocator? relocator = null,
    ISpeedFactorSource? speedSource = null,
    TimeProvider? timeProvider = null)
{
    private readonly IPoolTopology _topology = topology ?? throw new ArgumentNullException(nameof(topology));
    private readonly RebalanceOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly FileRelocator _relocator = relocator ?? new FileRelocator();
    private readonly ISpeedFactorSource? _speedSource = speedSource;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ManualResetEventSlim _resumed = new(initialState: true);

    public bool IsPaused => !_resumed.IsSet;

    public event EventHandler<RebalanceMove>? FileMoved;

    /// <summary>Stops after the file currently in flight. Used while the pool is busy or upgrading.</summary>
    public void Pause() => _resumed.Reset();

    public void Resume() => _resumed.Set();

    /// <summary>
    /// Works out which files to move. Files are taken from the fullest drive, largest first, until
    /// the projected imbalance is under the target.
    /// </summary>
    public RebalancePlan Plan()
    {
        var snapshot = _topology.Current;
        var imbalance = PoolBalance.Imbalance(snapshot);

        if (!_options.Enabled || imbalance <= _options.TriggerImbalance)
        {
            return RebalancePlan.Empty(imbalance);
        }

        // Project each drive's free space as moves are planned, so the plan terminates instead of
        // emptying the fullest drive entirely.
        var projected = snapshot.OnlineParts.ToDictionary(p => p.PartId, p => (double)p.FreeBytes);
        var totals = snapshot.OnlineParts.ToDictionary(p => p.PartId, p => (double)p.TotalBytes);
        var moves = new List<RebalanceMove>();
        var view = new UnionView(_topology);

        for (var round = 0; round < MaxRounds; round++)
        {
            var current = ProjectedImbalance(projected, totals);
            if (current <= _options.TargetImbalance)
            {
                break;
            }

            var source = Fullest(projected, totals);
            var destination = Emptiest(projected, totals, source);
            if (source == Guid.Empty || destination == Guid.Empty || source == destination)
            {
                break;
            }

            var candidate = NextCandidate(view, snapshot, source, moves);
            if (candidate is null)
            {
                break;
            }

            if (projected[destination] - candidate.Bytes < 0)
            {
                break;
            }

            moves.Add(new RebalanceMove
            {
                PoolPath = candidate.PoolPath,
                FromPartId = source,
                ToPartId = destination,
                Bytes = candidate.Bytes,
            });

            projected[source] += candidate.Bytes;
            projected[destination] -= candidate.Bytes;
        }

        return new RebalancePlan
        {
            Moves = moves,
            ImbalanceBefore = imbalance,
            ImbalanceAfter = ProjectedImbalance(projected, totals),
        };
    }

    /// <summary>Executes a plan. Safe to cancel at any point: each file is moved whole or not at all.</summary>
    public RebalanceResult Run(RebalancePlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var moved = 0;
        var skipped = 0;
        var failed = 0;
        long movedBytes = 0;
        var throttle = new RateLimiter(_options.MaxBytesPerSecond, _timeProvider);

        foreach (var move in plan.Moves)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Result(moved, movedBytes, skipped, failed, cancelled: true);
            }

            try
            {
                _resumed.Wait(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return Result(moved, movedBytes, skipped, failed, cancelled: true);
            }

            var snapshot = _topology.Current;
            var source = snapshot.FindPart(move.FromPartId);
            var target = snapshot.FindPart(move.ToPartId);

            // Anything can have changed since the plan was made: a drive may have left, the file
            // may have been deleted or already moved.
            if (source is null || target is null || !source.IsOnline || !target.AcceptsNewFiles)
            {
                skipped++;
                continue;
            }

            if (_speedSource?.IsThrottled(target.PartId) == true)
            {
                skipped++;
                continue;
            }

            var hostPath = PoolPath.ToHostPath(source.RequireRootPath(), move.PoolPath);
            if (!File.Exists(hostPath))
            {
                skipped++;
                continue;
            }

            if (IsInUse(hostPath))
            {
                skipped++;
                continue;
            }

            try
            {
                var location = new PoolLocation
                {
                    Part = source,
                    PoolPath = move.PoolPath,
                    HostPath = hostPath,
                    IsDirectory = false,
                };

                var result = _relocator.Relocate(location, target, progress: null, cancellationToken);
                moved++;
                movedBytes += result.Bytes;
                FileMoved?.Invoke(this, move);
                throttle.Consume(result.Bytes, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return Result(moved, movedBytes, skipped, failed, cancelled: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }

        return Result(moved, movedBytes, skipped, failed, cancelled: false);
    }

    public RebalanceResult RunOnce(CancellationToken cancellationToken = default) =>
        Run(Plan(), cancellationToken);

    private const int MaxRounds = 512;

    private static RebalanceResult Result(int moved, long bytes, int skipped, int failed, bool cancelled) => new()
    {
        MovedCount = moved,
        MovedBytes = bytes,
        SkippedInUse = skipped,
        Failed = failed,
        Cancelled = cancelled,
    };

    /// <summary>
    /// A file another process has open must not be moved: the handle would follow the old path.
    /// An exclusive open is the cheapest reliable test on Windows.
    /// </summary>
    private static bool IsInUse(string hostPath)
    {
        try
        {
            using var stream = new FileStream(hostPath, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static double ProjectedImbalance(Dictionary<Guid, double> free, Dictionary<Guid, double> total)
    {
        if (free.Count < 2)
        {
            return 0;
        }

        var usages = free.Select(kvp => Usage(kvp.Value, total[kvp.Key])).ToArray();
        return usages.Max() - usages.Min();
    }

    private static double Usage(double free, double total) =>
        total <= 0 ? 0 : Math.Clamp((total - free) / total, 0, 1);

    private static Guid Fullest(Dictionary<Guid, double> free, Dictionary<Guid, double> total) =>
        free.Count == 0 ? Guid.Empty : free.MaxBy(kvp => Usage(kvp.Value, total[kvp.Key])).Key;

    private static Guid Emptiest(Dictionary<Guid, double> free, Dictionary<Guid, double> total, Guid exclude) =>
        free.Where(kvp => kvp.Key != exclude)
            .Select(kvp => (kvp.Key, Usage: Usage(kvp.Value, total[kvp.Key])))
            .OrderBy(entry => entry.Usage)
            .Select(entry => entry.Key)
            .FirstOrDefault();

    /// <summary>Largest file on the drive that is not already planned for a move.</summary>
    private RebalanceMove? NextCandidate(
        UnionView view,
        PoolSnapshot snapshot,
        Guid sourcePartId,
        List<RebalanceMove> planned)
    {
        var part = snapshot.FindPart(sourcePartId);
        if (part?.RootPath is null)
        {
            return null;
        }

        var alreadyPlanned = planned.Select(static m => m.PoolPath).ToHashSet(PoolPath.Comparer);

        FileInfo? best = null;
        try
        {
            foreach (var file in new DirectoryInfo(part.RootPath).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (file.Length > _options.MaxFileBytes || file.Length <= 0)
                {
                    continue;
                }

                var poolPath = ToPoolPath(part.RootPath, file.FullName);
                if (poolPath is null || alreadyPlanned.Contains(poolPath))
                {
                    continue;
                }

                if (best is null || file.Length > best.Length)
                {
                    best = file;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (best is null)
        {
            return null;
        }

        var path = ToPoolPath(part.RootPath, best.FullName);
        if (path is null || view.FindDirectory(path) is not null)
        {
            return null;
        }

        return new RebalanceMove
        {
            PoolPath = path,
            FromPartId = sourcePartId,
            ToPartId = Guid.Empty,
            Bytes = best.Length,
        };
    }

    private static string? ToPoolPath(string partRoot, string hostPath)
    {
        if (!PoolPartLayout.IsInsidePart(partRoot, hostPath))
        {
            return null;
        }

        var relative = Path.GetRelativePath(partRoot, hostPath);
        var poolPath = PoolPath.Normalize(relative);

        // The part's own bookkeeping file is not pool content.
        return PoolPath.Comparer.Equals(poolPath, PoolPartLayout.MarkerFileName) ? null : poolPath;
    }

    /// <summary>Caps background copy rate so rebalancing never competes with real IO.</summary>
    private sealed class RateLimiter(long bytesPerSecond, TimeProvider timeProvider)
    {
        private readonly long _bytesPerSecond = bytesPerSecond;

        public void Consume(long bytes, CancellationToken cancellationToken)
        {
            if (_bytesPerSecond <= 0 || bytes <= 0)
            {
                return;
            }

            var seconds = bytes / (double)_bytesPerSecond;
            if (seconds <= 0)
            {
                return;
            }

            var delay = TimeSpan.FromSeconds(Math.Min(seconds, MaxDelaySeconds));
            Task.Delay(delay, timeProvider).Wait(cancellationToken);
        }

        private const double MaxDelaySeconds = 30;
    }
}
