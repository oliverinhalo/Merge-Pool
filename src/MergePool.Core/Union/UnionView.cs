using MergePool.Core.Model;
using MergePool.Core.Paths;

namespace MergePool.Core.Union;

/// <summary>
/// The read side of the pool: merges every online part's <c>.PoolPart-{GUID}</c> tree into one
/// namespace. Nothing here mutates the disk.
/// </summary>
/// <remarks>
/// Merge rules:
/// <list type="bullet">
/// <item>A directory may exist on any number of parts; its listing is the union of all of them.</item>
/// <item>A file lives whole on exactly one part. If a drive rejoins carrying a stale duplicate, the
/// newest last-write wins deterministically (ties broken by part id) and the loser is reported.</item>
/// <item>A directory shadows a file of the same name, so a listing never contains both.</item>
/// </list>
/// </remarks>
public sealed class UnionView(IPoolTopology topology)
{
    private readonly IPoolTopology _topology = topology ?? throw new ArgumentNullException(nameof(topology));

    public PoolSnapshot Snapshot => _topology.Current;

    /// <summary>Resolves a pool path to a file or directory, or <c>null</c> when it exists nowhere.</summary>
    public PoolLocation? Find(string poolPath)
    {
        var normalized = PoolPath.Normalize(poolPath);
        if (PoolPath.IsRoot(normalized))
        {
            var first = _topology.Current.OnlineParts.FirstOrDefault();
            return first is null
                ? null
                : new PoolLocation
                {
                    Part = first,
                    PoolPath = string.Empty,
                    HostPath = first.RequireRootPath(),
                    IsDirectory = true,
                };
        }

        var directory = FindDirectory(normalized);
        return directory ?? FindFile(normalized);
    }

    /// <summary>The first online part carrying this path as a directory.</summary>
    public PoolLocation? FindDirectory(string poolPath)
    {
        var normalized = PoolPath.Normalize(poolPath);
        foreach (var part in _topology.Current.OnlineParts)
        {
            var host = PoolPath.ToHostPath(part.RequireRootPath(), normalized);
            if (Directory.Exists(host))
            {
                return new PoolLocation
                {
                    Part = part,
                    PoolPath = normalized,
                    HostPath = host,
                    IsDirectory = true,
                };
            }
        }

        return null;
    }

    /// <summary>Every online part carrying this path as a directory, in topology order.</summary>
    public IReadOnlyList<PoolLocation> FindAllDirectories(string poolPath)
    {
        var normalized = PoolPath.Normalize(poolPath);
        var result = new List<PoolLocation>();
        foreach (var part in _topology.Current.OnlineParts)
        {
            var host = PoolPath.ToHostPath(part.RequireRootPath(), normalized);
            if (normalized.Length == 0 || Directory.Exists(host))
            {
                result.Add(new PoolLocation
                {
                    Part = part,
                    PoolPath = normalized,
                    HostPath = host,
                    IsDirectory = true,
                });
            }
        }

        return result;
    }

    /// <summary>Resolves a file, applying the duplicate-resolution rule.</summary>
    public PoolLocation? FindFile(string poolPath)
    {
        var normalized = PoolPath.Normalize(poolPath);
        if (normalized.Length == 0)
        {
            return null;
        }

        PoolLocation? winner = null;
        DateTime winnerWrite = default;

        foreach (var part in _topology.Current.OnlineParts)
        {
            var host = PoolPath.ToHostPath(part.RequireRootPath(), normalized);
            var info = new FileInfo(host);
            if (!info.Exists)
            {
                continue;
            }

            var write = info.LastWriteTimeUtc;
            if (winner is null
                || write > winnerWrite
                || (write == winnerWrite && part.PartId.CompareTo(winner.Part.PartId) < 0))
            {
                winner = new PoolLocation
                {
                    Part = part,
                    PoolPath = normalized,
                    HostPath = host,
                    IsDirectory = false,
                };
                winnerWrite = write;
            }
        }

        return winner;
    }

    /// <summary>All online parts holding this path as a file. More than one means a stale duplicate.</summary>
    public IReadOnlyList<PoolLocation> FindAllFiles(string poolPath)
    {
        var normalized = PoolPath.Normalize(poolPath);
        if (normalized.Length == 0)
        {
            return Array.Empty<PoolLocation>();
        }

        var result = new List<PoolLocation>();
        foreach (var part in _topology.Current.OnlineParts)
        {
            var host = PoolPath.ToHostPath(part.RequireRootPath(), normalized);
            if (File.Exists(host))
            {
                result.Add(new PoolLocation
                {
                    Part = part,
                    PoolPath = normalized,
                    HostPath = host,
                    IsDirectory = false,
                });
            }
        }

        return result;
    }

    public bool DirectoryExists(string poolPath) =>
        PoolPath.IsRoot(PoolPath.Normalize(poolPath))
            ? _topology.Current.OnlineParts.Any()
            : FindDirectory(poolPath) is not null;

    public bool FileExists(string poolPath) => FindFile(poolPath) is not null;

    public bool Exists(string poolPath) => Find(poolPath) is not null;

    /// <summary>Merged directory listing. Throws <see cref="DirectoryNotFoundException"/> if nothing holds it.</summary>
    public IReadOnlyList<PoolEntry> EnumerateEntries(string poolPath, string searchPattern = "*") =>
        List(poolPath, searchPattern).Entries;

    /// <summary>
    /// Merged directory listing, with whether it can be trusted to be the whole of it.
    /// </summary>
    /// <remarks>
    /// The completeness flag is the point of this method. A listing assembled while one of the
    /// pool's drives could not be read is not a listing of the folder — it is a listing of the part
    /// of the folder we could see. Served as fact, it reads to an application as "these files have
    /// been deleted", which is how a healthy pool with every byte still on disk caused a scare. The
    /// file system refuses an incomplete listing instead.
    /// </remarks>
    public PoolListing List(string poolPath, string searchPattern = "*")
    {
        var normalized = PoolPath.Normalize(poolPath);
        var snapshot = _topology.Current;
        var sources = FindAllDirectories(normalized);

        if (sources.Count == 0)
        {
            // Nothing we can see holds it. If we cannot see every drive, that is not the same as it
            // not being there.
            if (!snapshot.IsComplete)
            {
                return new PoolListing { Entries = [], IsComplete = false };
            }

            throw new DirectoryNotFoundException($"'{normalized}' is not a directory in the pool.");
        }

        // Starts as whatever the topology can vouch for, and is lowered further by any source we
        // then fail to read.
        var complete = snapshot.IsComplete;
        var isRoot = PoolPath.IsRoot(normalized);
        var merged = new Dictionary<string, PoolEntry>(PoolPath.Comparer);

        foreach (var source in sources)
        {
            DirectoryInfo directory;
            IEnumerable<FileSystemInfo> children;
            try
            {
                directory = new DirectoryInfo(source.HostPath);
                if (!directory.Exists)
                {
                    continue;
                }

                children = directory.EnumerateFileSystemInfos(searchPattern, SearchOption.TopDirectoryOnly);
            }
            catch (DirectoryNotFoundException)
            {
                // The drive went away mid-enumeration. The pool runs degraded, but this listing is
                // missing whatever that drive held here and must say so.
                complete = false;
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                complete = false;
                continue;
            }
            catch (IOException)
            {
                complete = false;
                continue;
            }

            foreach (var child in EnumerateSafely(children))
            {
                if (isRoot && IsPoolPartInternal(child.Name))
                {
                    continue;
                }

                var isDirectory = (child.Attributes & FileAttributes.Directory) != 0;
                var entry = new PoolEntry
                {
                    Name = child.Name,
                    IsDirectory = isDirectory,
                    Length = isDirectory ? 0 : ((FileInfo)child).Length,
                    Attributes = child.Attributes,
                    CreationTimeUtc = child.CreationTimeUtc,
                    LastWriteTimeUtc = child.LastWriteTimeUtc,
                    LastAccessTimeUtc = child.LastAccessTimeUtc,
                    PartId = source.Part.PartId,
                    HostPath = child.FullName,
                };

                if (!merged.TryGetValue(entry.Name, out var existing))
                {
                    merged[entry.Name] = entry;
                    continue;
                }

                merged[entry.Name] = ResolveConflict(existing, entry);
            }
        }

        return new PoolListing
        {
            Entries = [.. merged.Values.OrderBy(static e => e.Name, PoolPath.Comparer)],
            IsComplete = complete,
        };
    }

    public IReadOnlyList<string> EnumerateNames(string poolPath) =>
        EnumerateEntries(poolPath).Select(static e => e.Name).ToArray();

    /// <summary>A directory shadows a file; between two files the newest last-write wins.</summary>
    private static PoolEntry ResolveConflict(PoolEntry existing, PoolEntry candidate)
    {
        if (existing.IsDirectory != candidate.IsDirectory)
        {
            return existing.IsDirectory ? existing : candidate;
        }

        if (existing.IsDirectory)
        {
            return existing;
        }

        if (candidate.LastWriteTimeUtc > existing.LastWriteTimeUtc)
        {
            return candidate;
        }

        if (candidate.LastWriteTimeUtc == existing.LastWriteTimeUtc
            && candidate.PartId.CompareTo(existing.PartId) < 0)
        {
            return candidate;
        }

        return existing;
    }

    /// <summary>Pool-part bookkeeping never appears inside the pool namespace.</summary>
    private static bool IsPoolPartInternal(string name) => PoolPartLayout.IsInternalRootName(name);

    /// <summary>Enumeration can throw per item when a removable drive disappears mid-listing.</summary>
    private static IEnumerable<FileSystemInfo> EnumerateSafely(IEnumerable<FileSystemInfo> source)
    {
        using var enumerator = source.GetEnumerator();
        while (true)
        {
            FileSystemInfo current;
            try
            {
                if (!enumerator.MoveNext())
                {
                    yield break;
                }

                current = enumerator.Current;
            }
            catch (IOException)
            {
                // Cut short rather than crashing the listing. The caller's completeness flag is
                // already false whenever the drive this came from is one we cannot read.
                yield break;
            }
            catch (UnauthorizedAccessException)
            {
                yield break;
            }

            yield return current;
        }
    }
}

/// <summary>
/// A merged listing and whether it is the whole of it. See <see cref="UnionView.List"/>.
/// </summary>
public sealed record PoolListing
{
    public required IReadOnlyList<PoolEntry> Entries { get; init; }

    /// <summary>
    /// False when a drive that may hold entries here could not be read. The entries are still
    /// valid — they exist — but their absence proves nothing.
    /// </summary>
    public required bool IsComplete { get; init; }
}
