using MergePool.Core.Model;
using MergePool.Core.Paths;
using MergePool.Core.Placement;

namespace MergePool.Core.Union;

/// <summary>
/// The write side of the pool. Every mutation is checked against the owning part root first, so
/// MergePool can only ever change things inside a <c>.PoolPart-{GUID}</c> folder.
/// </summary>
/// <remarks>
/// Invariants enforced here:
/// <list type="bullet">
/// <item>A file is never split: it is created whole on one part and only ever moved whole.</item>
/// <item>A rename or move keeps the file on its current drive.</item>
/// <item>Directories are mirrored across writable parts so a listing is stable while drives come
/// and go.</item>
/// </list>
/// </remarks>
public sealed class PoolOperations(IPoolTopology topology, IPlacementStrategy placement)
{
    private readonly IPoolTopology _topology = topology ?? throw new ArgumentNullException(nameof(topology));
    private readonly IPlacementStrategy _placement = placement ?? throw new ArgumentNullException(nameof(placement));

    public UnionView View { get; } = new UnionView(topology);

    /// <summary>
    /// Mirrors a directory onto every writable part. Returns the host paths created or already
    /// present.
    /// </summary>
    public IReadOnlyList<string> CreateDirectory(string poolPath)
    {
        var normalized = PoolPath.Normalize(poolPath);
        if (PoolPath.IsRoot(normalized))
        {
            return Array.Empty<string>();
        }

        ValidatePath(normalized);

        if (View.FindFile(normalized) is not null)
        {
            throw new IOException($"'{normalized}' already exists as a file.");
        }

        var snapshot = _topology.Current;
        var targets = snapshot.WritableParts.ToArray();
        if (targets.Length == 0)
        {
            // Degraded but readable: mirror onto whatever is online so the pool stays usable.
            targets = snapshot.OnlineParts.ToArray();
        }

        if (targets.Length == 0)
        {
            throw new IOException("The pool has no online drives.");
        }

        var created = new List<string>(targets.Length);
        foreach (var part in targets)
        {
            var host = HostPathIn(part, normalized);
            Directory.CreateDirectory(host);
            created.Add(host);
        }

        return created;
    }

    /// <summary>Removes a directory from every part holding it.</summary>
    public void DeleteDirectory(string poolPath, bool recursive = false)
    {
        var normalized = PoolPath.Normalize(poolPath);
        if (PoolPath.IsRoot(normalized))
        {
            throw new IOException("The pool root cannot be deleted.");
        }

        var locations = View.FindAllDirectories(normalized);
        if (locations.Count == 0)
        {
            throw new DirectoryNotFoundException($"'{normalized}' is not a directory in the pool.");
        }

        if (!recursive && View.EnumerateEntries(normalized).Count > 0)
        {
            throw new IOException($"'{normalized}' is not empty.");
        }

        foreach (var location in locations)
        {
            GuardInsidePart(location.Part, location.HostPath);
            if (Directory.Exists(location.HostPath))
            {
                Directory.Delete(location.HostPath, recursive);
            }
        }
    }

    /// <summary>
    /// Chooses a drive for a new file and returns the host path to create it at, with its parent
    /// directory chain already present on that drive.
    /// </summary>
    public PoolLocation PrepareNewFile(string poolPath, long estimatedSize = 0, Guid? preferredPart = null)
    {
        var normalized = PoolPath.Normalize(poolPath);
        ValidatePath(normalized);

        if (PoolPath.IsRoot(normalized))
        {
            throw new IOException("The pool root is not a file.");
        }

        var snapshot = _topology.Current;
        var decision = _placement.Select(snapshot, new PlacementRequest
        {
            PoolPath = normalized,
            EstimatedSize = estimatedSize,
            PreferredPart = preferredPart,
        }) ?? throw new IOException($"No drive in the pool can hold '{normalized}' ({estimatedSize} bytes).");

        var part = snapshot.FindPart(decision.PartId)
            ?? throw new InvalidOperationException($"Placement returned unknown part {decision.PartId:D}.");

        var host = HostPathIn(part, normalized);
        var parent = Path.GetDirectoryName(host);
        if (!string.IsNullOrEmpty(parent))
        {
            GuardInsidePart(part, parent);
            Directory.CreateDirectory(parent);
        }

        return new PoolLocation
        {
            Part = part,
            PoolPath = normalized,
            HostPath = host,
            IsDirectory = false,
        };
    }

    /// <summary>Deletes a file from every part holding it (normally exactly one).</summary>
    public void DeleteFile(string poolPath)
    {
        var normalized = PoolPath.Normalize(poolPath);
        var locations = View.FindAllFiles(normalized);
        if (locations.Count == 0)
        {
            throw new FileNotFoundException($"'{normalized}' is not a file in the pool.");
        }

        foreach (var location in locations)
        {
            GuardInsidePart(location.Part, location.HostPath);
            File.Delete(location.HostPath);
        }
    }

    /// <summary>
    /// Renames or moves within the pool. A file stays on its current drive: only its path inside
    /// that part changes, so a move is a metadata operation and never a copy.
    /// </summary>
    public void Rename(string sourcePoolPath, string destinationPoolPath, bool replaceExisting = false)
    {
        var source = PoolPath.Normalize(sourcePoolPath);
        var destination = PoolPath.Normalize(destinationPoolPath);
        ValidatePath(destination);

        if (PoolPath.IsRoot(source) || PoolPath.IsRoot(destination))
        {
            throw new IOException("The pool root cannot be renamed.");
        }

        if (PoolPath.Comparer.Equals(source, destination))
        {
            return;
        }

        var directories = View.FindAllDirectories(source);
        if (directories.Count > 0)
        {
            if (PoolPath.IsAtOrUnder(destination, source))
            {
                throw new IOException("A directory cannot be moved into itself.");
            }

            RenameDirectory(directories, destination, replaceExisting);
            return;
        }

        var file = View.FindFile(source)
            ?? throw new FileNotFoundException($"'{source}' does not exist in the pool.");

        RenameFile(file, destination, replaceExisting);
    }

    private void RenameFile(PoolLocation file, string destination, bool replaceExisting)
    {
        if (!replaceExisting && View.Exists(destination))
        {
            throw new IOException($"'{destination}' already exists.");
        }

        var target = HostPathIn(file.Part, destination);
        var parent = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(parent))
        {
            GuardInsidePart(file.Part, parent);
            Directory.CreateDirectory(parent);
        }

        GuardInsidePart(file.Part, file.HostPath);
        GuardInsidePart(file.Part, target);

        if (replaceExisting)
        {
            // Clear the name everywhere first: the union must not be left with two winners.
            foreach (var existing in View.FindAllFiles(destination))
            {
                GuardInsidePart(existing.Part, existing.HostPath);
                File.Delete(existing.HostPath);
            }
        }

        File.Move(file.HostPath, target, overwrite: replaceExisting);
    }

    private void RenameDirectory(IReadOnlyList<PoolLocation> sources, string destination, bool replaceExisting)
    {
        if (!replaceExisting && View.Exists(destination))
        {
            throw new IOException($"'{destination}' already exists.");
        }

        foreach (var source in sources)
        {
            var target = HostPathIn(source.Part, destination);
            GuardInsidePart(source.Part, source.HostPath);
            GuardInsidePart(source.Part, target);

            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            if (Directory.Exists(target))
            {
                // Another part already carries the destination name: merge into it instead of
                // failing, so a per-part rename cannot leave the union half-renamed.
                MergeDirectoryInto(source.Part, source.HostPath, target);
                continue;
            }

            Directory.Move(source.HostPath, target);
        }
    }

    private void MergeDirectoryInto(PoolPart part, string sourceRoot, string targetRoot)
    {
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, directory);
            Directory.CreateDirectory(Path.Combine(targetRoot, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, file);
            var target = Path.Combine(targetRoot, relative);
            GuardInsidePart(part, target);
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.Move(file, target, overwrite: true);
        }

        Directory.Delete(sourceRoot, recursive: true);
    }

    /// <summary>
    /// The host path a pool path would have on a given part. Never dereferences anything: callers
    /// still guard before writing.
    /// </summary>
    public static string HostPathIn(PoolPart part, string poolPath) =>
        PoolPath.ToHostPath(part.RequireRootPath(), poolPath);

    /// <summary>Hard boundary: refuse any host path that is not inside the part folder.</summary>
    public static void GuardInsidePart(PoolPart part, string hostPath)
    {
        var root = part.RequireRootPath();
        if (!PoolPartLayout.IsInsidePart(root, hostPath))
        {
            throw new UnauthorizedAccessException(
                $"Refusing to touch '{hostPath}': it is outside pool part '{root}'.");
        }
    }

    private static void ValidatePath(string poolPath)
    {
        foreach (var component in PoolPath.Split(poolPath))
        {
            if (!PoolPath.IsValidName(component))
            {
                throw new ArgumentException($"'{component}' is not a valid name.", nameof(poolPath));
            }
        }
    }
}
