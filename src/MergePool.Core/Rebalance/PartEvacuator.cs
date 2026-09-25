using MergePool.Core.FileSystem;
using MergePool.Core.Model;
using MergePool.Core.Paths;
using MergePool.Core.Union;

namespace MergePool.Core.Rebalance;

public sealed record EvacuationPlan
{
    public required Guid PartId { get; init; }

    public required IReadOnlyList<EvacuationItem> Items { get; init; }

    /// <summary>Files that cannot be moved because nothing has room for them.</summary>
    public required IReadOnlyList<string> WithoutRoom { get; init; }

    /// <summary>Free space on the drives that would receive the files.</summary>
    public required long DestinationFreeBytes { get; init; }

    public long TotalBytes => Items.Sum(static i => i.Bytes);

    public bool Fits => WithoutRoom.Count == 0;
}

public sealed record EvacuationItem
{
    public required string PoolPath { get; init; }

    public required Guid ToPartId { get; init; }

    public required long Bytes { get; init; }
}

public sealed record EvacuationResult
{
    public required int MovedCount { get; init; }

    public required long MovedBytes { get; init; }

    /// <summary>Files another process had open. They stayed where they are.</summary>
    public required IReadOnlyList<string> SkippedInUse { get; init; }

    public required IReadOnlyList<string> Failed { get; init; }

    public bool Cancelled { get; init; }

    /// <summary>True only when the part has nothing left on it.</summary>
    public bool IsComplete => SkippedInUse.Count == 0 && Failed.Count == 0 && !Cancelled;
}

/// <summary>
/// Moves everything the pool holds on one drive onto the pool's other drives, so that drive can
/// leave without taking any pool content with it.
/// </summary>
/// <remarks>
/// Every move is a whole file — a file is never split — and every move is all-or-nothing: the copy
/// lands and is flushed before the original is removed. A file another process has open is left
/// alone and reported, because moving it would leave that process holding a handle to a path that
/// no longer exists. Nothing outside the pool parts is read or written.
/// </remarks>
public sealed class PartEvacuator(FileRelocator? relocator = null)
{
    private readonly FileRelocator _relocator = relocator ?? new FileRelocator();

    /// <summary>
    /// Works out where each file would go, largest first onto whichever remaining drive has most
    /// room. Reports anything that will not fit rather than starting a move that cannot finish.
    /// </summary>
    public EvacuationPlan Plan(PoolSnapshot snapshot, Guid partId, long headroomBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var source = snapshot.FindPart(partId);
        var destinations = snapshot.OnlineParts
            .Where(part => part.PartId != partId)
            .ToList();

        // Some headroom is kept so a destination is not driven to completely full, but it is capped
        // at a tenth of what the drive has free: a fixed placement headroom meant for new writes
        // would otherwise make a small drive look like it has no room at all.
        var projected = destinations.ToDictionary(
            part => part.PartId,
            part => Math.Max(0, part.FreeBytes - Math.Min(headroomBytes, part.FreeBytes / 10)));

        var items = new List<EvacuationItem>();
        var withoutRoom = new List<string>();

        if (source?.RootPath is not null)
        {
            foreach (var file in Files(source).OrderByDescending(static f => f.Bytes))
            {
                // Largest first, onto whichever drive has most room left. Placement scoring is for
                // new files; here the only question is whether everything fits at all.
                var target = projected
                    .Where(entry => entry.Value >= file.Bytes)
                    .OrderByDescending(entry => entry.Value)
                    .Select(entry => (Guid?)entry.Key)
                    .FirstOrDefault();

                if (target is null)
                {
                    withoutRoom.Add(file.PoolPath);
                    continue;
                }

                items.Add(new EvacuationItem
                {
                    PoolPath = file.PoolPath,
                    ToPartId = target.Value,
                    Bytes = file.Bytes,
                });

                projected[target.Value] -= file.Bytes;
            }
        }

        return new EvacuationPlan
        {
            PartId = partId,
            Items = items,
            WithoutRoom = withoutRoom,
            DestinationFreeBytes = destinations.Sum(static part => part.FreeBytes),
        };
    }

    /// <summary>
    /// Executes a plan. The destination is re-picked per file if the planned one has filled up or
    /// gone away, so a stale plan degrades into skipped files rather than failures.
    /// </summary>
    public EvacuationResult Run(
        IPoolTopology topology,
        EvacuationPlan plan,
        IProgress<EvacuationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(plan);

        var moved = 0;
        long movedBytes = 0;
        var inUse = new List<string>();
        var failed = new List<string>();

        foreach (var item in plan.Items)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Result(moved, movedBytes, inUse, failed, cancelled: true);
            }

            var snapshot = topology.Current;
            var source = snapshot.FindPart(plan.PartId);
            if (source?.RootPath is null)
            {
                // The drive being emptied has gone. Nothing more can be moved off it.
                return Result(moved, movedBytes, inUse, failed, cancelled: true);
            }

            var hostPath = PoolPath.ToHostPath(source.RootPath, item.PoolPath);
            if (!File.Exists(hostPath))
            {
                continue;
            }

            var target = snapshot.FindPart(item.ToPartId) is { } planned
                         && planned.IsOnline
                         && planned.FreeBytes >= item.Bytes
                ? planned
                : snapshot.OnlineParts
                    .Where(part => part.PartId != plan.PartId && part.FreeBytes >= item.Bytes)
                    .MaxBy(static part => part.FreeBytes);

            if (target is null)
            {
                failed.Add(item.PoolPath);
                continue;
            }

            if (IsInUse(hostPath))
            {
                inUse.Add(item.PoolPath);
                continue;
            }

            try
            {
                var result = _relocator.Relocate(
                    new PoolLocation
                    {
                        Part = source,
                        PoolPath = item.PoolPath,
                        HostPath = hostPath,
                        IsDirectory = false,
                    },
                    target,
                    progress: null,
                    cancellationToken);

                moved++;
                movedBytes += result.Bytes;
                progress?.Report(new EvacuationProgress
                {
                    PoolPath = item.PoolPath,
                    MovedCount = moved,
                    MovedBytes = movedBytes,
                    TotalCount = plan.Items.Count,
                    TotalBytes = plan.TotalBytes,
                });
            }
            catch (OperationCanceledException)
            {
                return Result(moved, movedBytes, inUse, failed, cancelled: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failed.Add(item.PoolPath);
            }
        }

        return Result(moved, movedBytes, inUse, failed, cancelled: false);
    }

    /// <summary>
    /// Removes the empty directory tree left behind inside a part, and the part folder itself. Stops
    /// at the first thing that is not empty: this must never delete pool content.
    /// </summary>
    public static bool TryRemoveEmptyPart(string partRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(partRoot);

        if (!Directory.Exists(partRoot))
        {
            return true;
        }

        try
        {
            foreach (var directory in Directory
                         .EnumerateDirectories(partRoot, "*", SearchOption.AllDirectories)
                         .OrderByDescending(static path => path.Length))
            {
                if (!IsEmpty(directory))
                {
                    return false;
                }

                Directory.Delete(directory);
            }

            // Only the marker may remain; anything else means there is still pool content here.
            var remaining = Directory.GetFiles(partRoot);
            if (remaining.Any(path =>
                    !PoolPath.Comparer.Equals(Path.GetFileName(path), PoolPartLayout.MarkerFileName)))
            {
                return false;
            }

            foreach (var path in remaining)
            {
                File.Delete(path);
            }

            Directory.Delete(partRoot);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsEmpty(string directory) =>
        !Directory.EnumerateFileSystemEntries(directory).Any();

    private static EvacuationResult Result(
        int moved,
        long bytes,
        List<string> inUse,
        List<string> failed,
        bool cancelled) => new()
        {
            MovedCount = moved,
            MovedBytes = bytes,
            SkippedInUse = inUse,
            Failed = failed,
            Cancelled = cancelled,
        };

    private readonly record struct FileCandidate(string PoolPath, long Bytes);

    private static IEnumerable<FileCandidate> Files(PoolPart part)
    {
        var root = part.RootPath!;
        List<FileCandidate> found = [];

        try
        {
            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                var relative = PoolPath.Normalize(Path.GetRelativePath(root, file.FullName));
                if (PoolPath.Comparer.Equals(relative, PoolPartLayout.MarkerFileName))
                {
                    continue;
                }

                found.Add(new FileCandidate(relative, file.Length));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return found;
        }

        return found;
    }

    /// <summary>
    /// A file another process has open must not be moved: its handle would follow the old path.
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
}

public sealed record EvacuationProgress
{
    public required string PoolPath { get; init; }

    public required int MovedCount { get; init; }

    public required long MovedBytes { get; init; }

    public required int TotalCount { get; init; }

    public required long TotalBytes { get; init; }
}
