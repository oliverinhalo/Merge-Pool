using MergePool.Core.Paths;
using MergePool.Core.Volumes;

namespace MergePool.Core.Model;

public sealed record AdoptionItem
{
    public required string Name { get; init; }

    public required string HostPath { get; init; }

    public required bool IsDirectory { get; init; }

    public long Bytes { get; init; }
}

public sealed record AdoptionPlan
{
    public required IReadOnlyList<AdoptionItem> Items { get; init; }

    public required IReadOnlyList<string> Skipped { get; init; }

    public long TotalBytes => Items.Sum(static i => i.Bytes);
}

public sealed record AdoptionResult
{
    public required int MovedCount { get; init; }

    public required long MovedBytes { get; init; }

    public required IReadOnlyList<string> Failed { get; init; }
}

/// <summary>
/// Optional "adopt existing content" step: moves what is already on a drive into that drive's pool
/// part. The move is always within the same volume, so it is a rename — no copying, no data risk —
/// and nothing ever leaves the drive it was on.
/// </summary>
public sealed class PoolAdopter
{
    /// <summary>
    /// Names never adopted: OS state, recycle bins, other pool parts. Adoption is opt-in and this
    /// list is the backstop.
    /// </summary>
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$RECYCLE.BIN",
        "System Volume Information",
        "Recovery",
        "$WinREAgent",
        "Windows",
        "Windows.old",
        "Program Files",
        "Program Files (x86)",
        "ProgramData",
        "Users",
        "Boot",
        "EFI",
        "PerfLogs",
        "bootmgr",
        "BOOTNXT",
        "pagefile.sys",
        "hiberfil.sys",
        "swapfile.sys",
        "DumpStack.log",
        "DumpStack.log.tmp",
    };

    /// <summary>Lists what would be adopted from a volume root, and what is being left alone.</summary>
    public AdoptionPlan Plan(string volumeRoot, Guid partId, bool includeHidden = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(volumeRoot);

        var items = new List<AdoptionItem>();
        var skipped = new List<string>();
        var partFolder = PoolPartLayout.FolderName(partId);

        foreach (var entry in new DirectoryInfo(volumeRoot).EnumerateFileSystemInfos())
        {
            var name = entry.Name;

            if (PoolPath.Comparer.Equals(name, partFolder)
                || PoolPartLayout.TryParseFolderName(name, out _)
                || ProtectedNames.Contains(name))
            {
                skipped.Add(name);
                continue;
            }

            var attributes = entry.Attributes;
            if (!includeHidden && (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
            {
                skipped.Add(name);
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                // Junctions and symlinks would change meaning once moved. Leave them.
                skipped.Add(name);
                continue;
            }

            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            items.Add(new AdoptionItem
            {
                Name = name,
                HostPath = entry.FullName,
                IsDirectory = isDirectory,
                Bytes = isDirectory ? MeasureDirectory(entry.FullName) : ((FileInfo)entry).Length,
            });
        }

        return new AdoptionPlan { Items = items, Skipped = skipped };
    }

    /// <summary>Executes the plan as same-volume renames into the pool part.</summary>
    public AdoptionResult Adopt(
        string volumeRoot,
        Guid partId,
        AdoptionPlan plan,
        IProgress<AdoptionItem>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(volumeRoot);
        ArgumentNullException.ThrowIfNull(plan);

        var partRoot = PoolPartLayout.RootPathFor(volumeRoot, partId);
        Directory.CreateDirectory(partRoot);

        var failed = new List<string>();
        var moved = 0;
        long movedBytes = 0;

        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var target = PoolPath.ToHostPath(partRoot, item.Name);
            if (!PoolPartLayout.IsInsidePart(partRoot, target))
            {
                failed.Add(item.Name);
                continue;
            }

            try
            {
                if (item.IsDirectory)
                {
                    if (Directory.Exists(target))
                    {
                        failed.Add(item.Name);
                        continue;
                    }

                    Directory.Move(item.HostPath, target);
                }
                else
                {
                    if (File.Exists(target))
                    {
                        failed.Add(item.Name);
                        continue;
                    }

                    File.Move(item.HostPath, target);
                }

                moved++;
                movedBytes += item.Bytes;
                progress?.Report(item);
            }
            catch (IOException)
            {
                failed.Add(item.Name);
            }
            catch (UnauthorizedAccessException)
            {
                failed.Add(item.Name);
            }
        }

        return new AdoptionResult { MovedCount = moved, MovedBytes = movedBytes, Failed = failed };
    }

    private static long MeasureDirectory(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(static f => f.Length);
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
