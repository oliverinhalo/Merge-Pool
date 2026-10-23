using MergePool.Core.Config;
using MergePool.Core.Model;
using MergePool.Core.Union;
using PoolPathUtil = MergePool.Core.Paths.PoolPath;

namespace MergePool.Core.Duplication;

/// <summary>
/// Works out, for every file under a protected folder, which drives should hold a copy and which
/// already do.
/// </summary>
/// <remarks>
/// <para>The rules it keeps to, in order of importance:</para>
/// <list type="bullet">
/// <item>Two copies on one drive is not two copies. A file's copies are always on different
/// drives, and a drive is never asked to hold a second one.</item>
/// <item>A copy is a whole file. Nothing here splits anything.</item>
/// <item>The newest copy is the truth, by the same rule the union view reads with — newest
/// last-write wins, ties broken by the lower part id. So what a reader sees is what gets copied.</item>
/// <item>Never delete the last copy of anything. Surplus removal is capped so the rule's own copy
/// count always survives, and a rule may be told never to delete at all.</item>
/// </list>
/// </remarks>
public sealed class DuplicationPlanner(IPoolTopology topology, DuplicationOptions? options = null)
{
    private readonly IPoolTopology _topology = topology ?? throw new ArgumentNullException(nameof(topology));
    private readonly DuplicationOptions _options = options ?? new DuplicationOptions();

    public UnionView View { get; } = new UnionView(topology);

    public DuplicationPlan Plan(IEnumerable<DuplicationRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var snapshot = _topology.Current;
        var actions = new List<DuplicationAction>();
        var statuses = new List<DuplicationRuleStatus>();

        // Free space is projected as copies are planned, so a plan cannot promise a drive more than
        // it has — and so two rules cannot both spend the same gigabyte.
        var projectedFree = snapshot.OnlineParts.ToDictionary(p => p.PartId, p => p.FreeBytes);

        foreach (var rule in rules)
        {
            statuses.Add(PlanRule(rule, snapshot, projectedFree, actions));
        }

        return new DuplicationPlan { Actions = actions, Rules = statuses };
    }

    private DuplicationRuleStatus PlanRule(
        DuplicationRule rule,
        PoolSnapshot snapshot,
        Dictionary<Guid, long> projectedFree,
        List<DuplicationAction> actions)
    {
        var folder = PoolPathUtil.Normalize(rule.Folder);
        var eligible = EligibleParts(rule, snapshot);
        var requested = rule.RequestedCopies(eligible.Count);
        var wanted = rule.AchievableCopies(eligible.Count);

        var files = 0;
        var protectedFiles = 0;
        var bytes = 0L;
        var pending = 0L;
        var shortfalls = new List<DuplicationShortfall>();

        if (!rule.Enabled)
        {
            return new DuplicationRuleStatus
            {
                RuleId = rule.Id,
                Folder = folder,
                Enabled = false,
                RequestedCopies = requested,
                WantedCopies = wanted,
                Files = 0,
                ProtectedFiles = 0,
                Bytes = 0,
                PendingBytes = 0,
                DrivesAvailable = eligible.Count,
            };
        }

        foreach (var poolPath in FilesUnder(folder))
        {
            var copies = View.FindAllFiles(poolPath);
            if (copies.Count == 0)
            {
                continue;
            }

            files++;

            var newest = Newest(copies);
            var length = Length(newest.HostPath);
            bytes += length;

            // Copies on a drive the rule does not allow do not count towards the rule, but they are
            // still copies: they are only ever removed, never relied on.
            var counted = copies
                .Where(c => eligible.Any(p => p.PartId == c.Part.PartId))
                .ToArray();

            var stale = counted
                .Where(c => c.Part.PartId != newest.Part.PartId && !Matches(newest.HostPath, c.HostPath, rule))
                .ToArray();

            foreach (var copy in stale)
            {
                actions.Add(new DuplicationAction
                {
                    Kind = DuplicationActionKind.RefreshCopy,
                    RuleId = rule.Id,
                    PoolPath = poolPath,
                    SourceHostPath = newest.HostPath,
                    SourcePartId = newest.Part.PartId,
                    TargetHostPath = copy.HostPath,
                    TargetPartId = copy.Part.PartId,
                    Bytes = length,
                });

                pending += length;
            }

            var holders = counted.Select(c => c.Part.PartId).ToHashSet();
            var missing = wanted - holders.Count;

            if (missing > 0)
            {
                var chosen = ChooseTargets(eligible, holders, length, missing, projectedFree);

                foreach (var part in chosen)
                {
                    var target = PoolOperations.HostPathIn(part, poolPath);
                    actions.Add(new DuplicationAction
                    {
                        Kind = DuplicationActionKind.AddCopy,
                        RuleId = rule.Id,
                        PoolPath = poolPath,
                        SourceHostPath = newest.HostPath,
                        SourcePartId = newest.Part.PartId,
                        TargetHostPath = target,
                        TargetPartId = part.PartId,
                        Bytes = length,
                    });

                    projectedFree[part.PartId] = projectedFree.GetValueOrDefault(part.PartId) - length;
                    holders.Add(part.PartId);
                    pending += length;
                }

                if (chosen.Count < missing)
                {
                    shortfalls.Add(Short(rule, poolPath, holders.Count, requested, eligible.Count,
                        "no other drive has room for another copy"));
                }
            }

            // Asked for more copies than the pool has drives to put them on. Clamping the target
            // would make this look met; it is not, and the only honest thing is to say so.
            if (holders.Count < requested && eligible.Count < requested)
            {
                shortfalls.Add(Short(rule, poolPath, holders.Count, requested, eligible.Count,
                    $"the pool has {eligible.Count} drive(s) this rule can use"));
            }

            if (holders.Count >= requested && stale.Length == 0)
            {
                protectedFiles++;
            }

            // Removing a copy is the one thing in here that destroys data, so it is only planned
            // from a view that accounts for every drive. With a drive unreadable, the copies we can
            // see are an undercount of the copies that exist — and "there are more copies than the
            // rule asks for" computed from an undercount is how you delete the wrong one.
            if (rule.Surplus is SurplusCopies.Remove && _topology.Current.IsComplete)
            {
                foreach (var action in PlanSurplus(rule, poolPath, copies, newest, wanted, eligible, length))
                {
                    actions.Add(action);
                }
            }
        }

        return new DuplicationRuleStatus
        {
            RuleId = rule.Id,
            Folder = folder,
            Enabled = true,
            RequestedCopies = requested,
            WantedCopies = wanted,
            Files = files,
            ProtectedFiles = protectedFiles,
            Bytes = bytes,
            PendingBytes = pending,
            DrivesAvailable = eligible.Count,
            Shortfalls = shortfalls,
        };
    }

    private static DuplicationShortfall Short(
        DuplicationRule rule,
        string poolPath,
        int copies,
        int wanted,
        int drives,
        string reason) => new()
        {
            RuleId = rule.Id,
            PoolPath = poolPath,
            Copies = copies,
            WantedCopies = wanted,
            Drives = drives,
            Reason = reason,
        };

    /// <summary>
    /// Copies beyond what the rule asks for: first the ones on drives it is not allowed to use,
    /// then the ones on the fullest drives. The newest copy is never a candidate, and removal stops
    /// while fewer than the wanted number would be left.
    /// </summary>
    private static IEnumerable<DuplicationAction> PlanSurplus(
        DuplicationRule rule,
        string poolPath,
        IReadOnlyList<PoolLocation> copies,
        PoolLocation newest,
        int wanted,
        IReadOnlyList<PoolPart> eligible,
        long length)
    {
        var removable = copies
            .Where(c => c.Part.PartId != newest.Part.PartId)
            .OrderByDescending(c => eligible.All(p => p.PartId != c.Part.PartId))
            .ThenBy(c => c.Part.FreeBytes)
            .ToArray();

        var remaining = copies.Count;

        foreach (var copy in removable)
        {
            if (remaining <= Math.Max(wanted, 1))
            {
                break;
            }

            remaining--;

            yield return new DuplicationAction
            {
                Kind = DuplicationActionKind.RemoveCopy,
                RuleId = rule.Id,
                PoolPath = poolPath,
                TargetHostPath = copy.HostPath,
                TargetPartId = copy.Part.PartId,
                Bytes = length,
            };
        }
    }

    /// <summary>
    /// Drives that may hold a copy: online, allowed by the rule, and — for a drive that does not
    /// hold one yet — able to take new files at all.
    /// </summary>
    private static List<PoolPart> EligibleParts(DuplicationRule rule, PoolSnapshot snapshot)
    {
        var allowed = rule.OnlyOnParts.Count == 0
            ? null
            : rule.OnlyOnParts.ToHashSet();

        return snapshot.OnlineParts
            .Where(p => allowed is null || allowed.Contains(p.PartId))
            .ToList();
    }

    /// <summary>
    /// Where new copies go: the drive with the most room first, weighted by how fast it is, and
    /// only if the copy fits with the configured headroom left over.
    /// </summary>
    private List<PoolPart> ChooseTargets(
        IReadOnlyList<PoolPart> eligible,
        IReadOnlySet<Guid> holders,
        long length,
        int wanted,
        Dictionary<Guid, long> projectedFree)
    {
        return eligible
            .Where(p => !holders.Contains(p.PartId))
            .Where(p => p.AcceptsNewFiles)
            .Where(p => projectedFree.GetValueOrDefault(p.PartId) - length >= _options.CopyHeadroomBytes)
            .OrderByDescending(p => projectedFree.GetValueOrDefault(p.PartId) * Math.Max(0.1, p.SpeedFactor))
            .Take(wanted)
            .ToList();
    }

    /// <summary>The newest copy, by the same rule the union view reads with.</summary>
    private static PoolLocation Newest(IReadOnlyList<PoolLocation> copies)
    {
        var winner = copies[0];
        var winnerWrite = LastWrite(winner.HostPath);

        for (var i = 1; i < copies.Count; i++)
        {
            var write = LastWrite(copies[i].HostPath);
            if (write > winnerWrite
                || (write == winnerWrite && copies[i].Part.PartId.CompareTo(winner.Part.PartId) < 0))
            {
                winner = copies[i];
                winnerWrite = write;
            }
        }

        return winner;
    }

    /// <summary>
    /// Whether a copy still matches the newest one. Size and last-write time is what a file manager
    /// would compare; content hashing is opt-in because it means reading every byte of every copy.
    /// </summary>
    private static bool Matches(string newestPath, string copyPath, DuplicationRule rule)
    {
        var newest = new FileInfo(newestPath);
        var copy = new FileInfo(copyPath);

        if (!newest.Exists || !copy.Exists || newest.Length != copy.Length)
        {
            return false;
        }

        // A second of slack: FAT-family filesystems store last-write times to two seconds, so an
        // exact comparison would copy every file on every pass for a pool with one on it.
        if ((newest.LastWriteTimeUtc - copy.LastWriteTimeUtc).Duration() > TimeSpan.FromSeconds(2))
        {
            return false;
        }

        return !rule.VerifyByContent || FileDigest.Same(newestPath, copyPath);
    }

    private IEnumerable<string> FilesUnder(string folder)
    {
        var seen = new HashSet<string>(PoolPathUtil.Comparer);

        foreach (var part in _topology.Current.OnlineParts)
        {
            var root = PoolPathUtil.ToHostPath(part.RequireRootPath(), folder);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in SafeFiles(root))
            {
                var relative = Path.GetRelativePath(part.RequireRootPath(), file);
                var poolPath = PoolPathUtil.Normalize(relative);

                if (IsInternal(poolPath))
                {
                    continue;
                }

                if (seen.Add(poolPath))
                {
                    yield return poolPath;
                }
            }
        }
    }

    /// <summary>MergePool's own bookkeeping is not pool content and is never duplicated.</summary>
    private static bool IsInternal(string poolPath)
    {
        var first = PoolPathUtil.Split(poolPath).FirstOrDefault();
        return first is not null && PoolPartLayout.IsInternalRootName(first);
    }

    /// <summary>A removable drive can vanish mid-walk; the pool simply runs degraded.</summary>
    private static IEnumerable<string> SafeFiles(string root)
    {
        IEnumerator<string> enumerator;

        try
        {
            enumerator = Directory
                .EnumerateFiles(root, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                })
                .GetEnumerator();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        yield break;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    yield break;
                }

                yield return enumerator.Current;
            }
        }
    }

    private static long Length(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? info.Length : 0;
    }

    private static DateTime LastWrite(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
    }
}

/// <summary>Tunables for duplication that are not per-rule.</summary>
public sealed class DuplicationOptions
{
    /// <summary>Free space left on a drive after a copy lands on it.</summary>
    public long CopyHeadroomBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Cap on how fast copies are written, so duplication never fights real IO.</summary>
    public int MegabytesPerSecond { get; init; } = 40;
}
