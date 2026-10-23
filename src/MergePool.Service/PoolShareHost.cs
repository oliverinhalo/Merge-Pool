using System.Runtime.Versioning;
using MergePool.Core.Sharing;
using MergePool.Engine;
using Microsoft.Extensions.Logging;

namespace MergePool.Service;

/// <summary>
/// Keeps the machine's SMB shares in step with the pools that are mounted.
/// </summary>
/// <remarks>
/// <para>Applied on every tick, like the web interface and its firewall rule, rather than hooked
/// onto mount and unmount. Making reality match the configuration repeatedly is what makes this
/// survive the cases a hand-written watchdog script trips over: a service restart, an update, a
/// pool remounted by the I/O watchdog, a drive letter that changed. There is nothing to recover
/// because nothing is remembered — each pass simply asks what should be shared and what is.</para>
/// <para>Nothing here is clever about access rights. The accounts are re-granted as part of
/// creating the share and otherwise left alone: MergePool is not the only thing that may have
/// opinions about a share's permissions, and quietly reverting someone's change would be worse
/// than not noticing it.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PoolShareHost(PoolEngine engine, ISmbShareManager shares, ILogger logger)
{
    private readonly PoolEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    private readonly ISmbShareManager _shares = shares ?? throw new ArgumentNullException(nameof(shares));

    /// <summary>Names we created, so a share of ours is removed when its pool stops wanting it.</summary>
    private readonly HashSet<string> _ours = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<Guid, PoolShareStatus> _status = [];
    private readonly object _gate = new();

    private static readonly IReadOnlySet<string> EmptyNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private bool? _fileSharingEnabled;
    private bool _warnedUnreadable;

    /// <summary>What each pool's share is doing. Read by the UI over the pipe.</summary>
    public IReadOnlyDictionary<Guid, PoolShareStatus> Status
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<Guid, PoolShareStatus>(_status);
            }
        }
    }

    /// <summary>Brings the machine's shares in line with the pools. Cheap when nothing has changed.</summary>
    public void Apply()
    {
        var desired = new List<DesiredShare>();
        var problems = new Dictionary<Guid, string>();
        var mountPoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pool in _engine.Pools)
        {
            var options = pool.Definition.Share;
            var mountPoint = _engine.GetMountPoint(pool.PoolId) ?? pool.Definition.MountPoint;

            if (!string.IsNullOrWhiteSpace(mountPoint))
            {
                mountPoints.Add(ShareReconciler.Normalise(mountPoint));
            }

            if (!options.Enabled)
            {
                continue;
            }

            if (options.Problem(pool.Definition.Name) is { } problem)
            {
                problems[pool.PoolId] = problem;
                continue;
            }

            if (!_engine.IsMounted(pool.PoolId) || string.IsNullOrWhiteSpace(mountPoint))
            {
                // An unmounted pool has nothing to share. The share goes away with the mount and
                // comes back with it, which is the whole point of doing this here.
                problems[pool.PoolId] = "the pool is not mounted";
                continue;
            }

            desired.Add(new DesiredShare
            {
                PoolId = pool.PoolId,
                PoolName = pool.Definition.Name,
                ShareName = options.ShareNameFor(pool.Definition.Name)!,
                Path = ShareReconciler.Normalise(mountPoint),
                ReadOnly = options.ReadOnly,
                AllowedAccounts = [.. options.AllowedAccounts],
            });
        }

        // Nothing to do and nothing of ours on the machine: do not even ask, so a pool with sharing
        // switched off costs one comparison a tick rather than a PowerShell launch.
        if (desired.Count == 0 && _ours.Count == 0)
        {
            Publish(desired, problems, created: EmptyNames);
            return;
        }

        var existing = _shares.List();
        if (existing is null)
        {
            if (!_warnedUnreadable)
            {
                _warnedUnreadable = true;
                logger.LogWarning(
                    "The machine's SMB shares could not be read, so MergePool cannot manage pool shares. "
                    + "This usually means the Server service is not running.");
            }

            Publish(desired, problems, created: EmptyNames, unreadable: true);
            return;
        }

        _warnedUnreadable = false;

        var actions = ShareReconciler.Plan(desired, existing, _ours, mountPoints);
        var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var share in desired.Where(d => existing.Any(e =>
                     string.Equals(e.ShareName, d.ShareName, StringComparison.OrdinalIgnoreCase))))
        {
            // Already shared from the right place by a previous pass or a previous run.
            created.Add(share.ShareName);
            _ours.Add(share.ShareName);
        }

        foreach (var action in actions)
        {
            if (action.Kind is ShareActionKind.Remove)
            {
                var failure = _shares.Remove(action.ShareName);
                if (failure is null)
                {
                    _ours.Remove(action.ShareName);
                    created.Remove(action.ShareName);
                    logger.LogInformation(
                        "Removed SMB share '{Share}': {Reason}.", action.ShareName, action.Reason);
                }
                else
                {
                    logger.LogWarning(
                        "Could not remove SMB share '{Share}': {Failure}", action.ShareName, failure);
                }

                continue;
            }

            if (action.Share is not { } share)
            {
                continue;
            }

            var problem = _shares.Create(share);
            if (problem is null)
            {
                _ours.Add(share.ShareName);
                created.Add(share.ShareName);

                logger.LogInformation(
                    "Shared pool '{Pool}' as \\\\{Machine}\\{Share} ({Access}) for {Accounts}.",
                    share.PoolName,
                    Environment.MachineName,
                    share.ShareName,
                    share.ReadOnly ? "read-only" : "read-write",
                    string.Join(", ", share.AllowedAccounts));
            }
            else
            {
                problems[share.PoolId] = problem;
                logger.LogWarning(
                    "Could not share pool '{Pool}' as '{Share}': {Problem}",
                    share.PoolName,
                    share.ShareName,
                    problem);
            }
        }

        // Asked once rather than every tick: it changes when someone changes a firewall setting,
        // which is not something to launch PowerShell over fifteen times a minute.
        if (desired.Count > 0 && _fileSharingEnabled is null && _shares is WindowsSmbShares windows)
        {
            _fileSharingEnabled = windows.FileSharingLooksEnabled();

            if (_fileSharingEnabled is false)
            {
                logger.LogWarning(
                    "A pool is shared over SMB, but Windows' File and Printer Sharing rules are off, so "
                    + "nothing on the network can reach it. Turn them on for the private network profile.");
            }
        }

        Publish(desired, problems, created);
    }

    /// <summary>Removes every share MergePool created. Called as the service stops.</summary>
    public void RemoveAll()
    {
        foreach (var name in _ours.ToArray())
        {
            if (_shares.Remove(name) is null)
            {
                _ours.Remove(name);
                logger.LogInformation("Removed SMB share '{Share}' as the service stopped.", name);
            }
        }
    }

    private void Publish(
        IReadOnlyList<DesiredShare> desired,
        IReadOnlyDictionary<Guid, string> problems,
        IReadOnlySet<string> created,
        bool unreadable = false)
    {
        lock (_gate)
        {
            _status.Clear();

            foreach (var pool in _engine.Pools)
            {
                var options = pool.Definition.Share;
                var want = desired.FirstOrDefault(d => d.PoolId == pool.PoolId);
                var isShared = want is not null && created.Contains(want.ShareName);

                _status[pool.PoolId] = new PoolShareStatus
                {
                    PoolId = pool.PoolId,
                    Enabled = options.Enabled,
                    IsShared = isShared,
                    UncPath = want is null
                        ? null
                        : $"\\\\{Environment.MachineName}\\{want.ShareName}",
                    Problem = problems.TryGetValue(pool.PoolId, out var problem)
                        ? problem
                        : unreadable && options.Enabled
                            ? "the machine's SMB shares could not be read; is the Server service running?"
                            : null,
                    FileSharingEnabled = _fileSharingEnabled,
                };
            }
        }
    }
}
