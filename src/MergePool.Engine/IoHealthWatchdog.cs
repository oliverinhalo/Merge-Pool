using MergePool.Core.Config;
using MergePool.Core.Health;

namespace MergePool.Engine;

/// <summary>What the watchdog did, or decided not to do, on one check.</summary>
public sealed record WatchdogOutcome
{
    public required Guid PoolId { get; init; }

    public required string PoolName { get; init; }

    public required IoHealthState State { get; init; }

    /// <summary>Whether the pool was remounted to get it answering again.</summary>
    public bool Recovered { get; init; }

    /// <summary>Why nothing was done, when a stalled pool was left alone.</summary>
    public string? Declined { get; init; }

    public string? Detail { get; init; }
}

/// <summary>
/// Watches each pool's I/O health and remounts a pool that has stopped answering.
/// </summary>
/// <remarks>
/// <para>This is the recovery path for the failure that has no other way out. A file system
/// callback that blocks for ever leaves threads stuck inside the kernel's I/O path; the processes
/// waiting on them are in an uninterruptible wait and cannot be killed, not even with a SIGKILL
/// from WSL, and nothing short of a reboot clears it. The pool's own locks and waits are bounded
/// now, so this should not happen for any reason MergePool controls — but a drive or a driver can
/// still stop answering, and "needs a reboot" is not an acceptable answer for a service holding
/// someone's photo library.</para>
/// <para>Remounting is disruptive: handles open on the pool break, and anything mid-write sees an
/// error. That is why it is a last resort, several conditions deep:</para>
/// <list type="bullet">
/// <item>the pool reports <see cref="IoHealthState.Stalled"/> — a run of operations that ran out of
/// their bounded wait, not one slow one;</item>
/// <item>it has been stalled for longer than <see cref="StalledFor"/>, so a burst of slowness
/// under load is not enough;</item>
/// <item>no remount in the last <see cref="Cooldown"/>, so a pool that cannot be fixed this way is
/// not remounted in a loop;</item>
/// <item>the service is not draining, because an upgrade is already taking the mount down.</item>
/// </list>
/// <para>Every decision is reported, including the ones not to act, so the log says what the
/// watchdog saw rather than only what it did.</para>
/// <para>One instance, kept for the life of the service. The cooldown is instance state, so a
/// watchdog created per tick would have no cooldown at all and would remount a pool it cannot fix
/// every time it looked.</para>
/// </remarks>
public sealed class IoHealthWatchdog(
    PoolEngine engine,
    Func<ServiceOptions> options,
    TimeProvider? timeProvider = null)
{
    /// <summary>How long a pool must have been stalled before remounting it.</summary>
    public static readonly TimeSpan StalledFor = TimeSpan.FromSeconds(90);

    /// <summary>The least time between two recoveries of the same pool.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);

    private readonly PoolEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    private readonly Func<ServiceOptions> _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<Guid, DateTimeOffset> _lastRecovery = [];

    /// <summary>
    /// Checks every mounted pool and recovers the ones that have stopped answering. Returns what it
    /// found, for the caller to log.
    /// </summary>
    public IReadOnlyList<WatchdogOutcome> Check()
    {
        var outcomes = new List<WatchdogOutcome>();

        foreach (var (poolId, name, health) in _engine.IoHealth)
        {
            var state = health.State;

            if (state is not IoHealthState.Stalled)
            {
                continue;
            }

            var outcome = Consider(poolId, name, health);
            outcomes.Add(outcome);
        }

        return outcomes;
    }

    private WatchdogOutcome Consider(Guid poolId, string name, IoHealthMonitor health)
    {
        var detail = health.Describe();

        if (health.StalledFor is not { } stalled || stalled < StalledFor)
        {
            return new WatchdogOutcome
            {
                PoolId = poolId,
                PoolName = name,
                State = IoHealthState.Stalled,
                Declined = "not stalled for long enough to be sure",
                Detail = detail,
            };
        }

        if (_engine.IsDraining)
        {
            return new WatchdogOutcome
            {
                PoolId = poolId,
                PoolName = name,
                State = IoHealthState.Stalled,
                Declined = "the service is draining, so the mount is coming down anyway",
                Detail = detail,
            };
        }

        if (!_options().RecoverStalledMounts)
        {
            return new WatchdogOutcome
            {
                PoolId = poolId,
                PoolName = name,
                State = IoHealthState.Stalled,
                Declined = "automatic recovery is switched off in the configuration",
                Detail = detail,
            };
        }

        var now = _timeProvider.GetUtcNow();
        if (_lastRecovery.TryGetValue(poolId, out var last) && now - last < Cooldown)
        {
            return new WatchdogOutcome
            {
                PoolId = poolId,
                PoolName = name,
                State = IoHealthState.Stalled,
                Declined = "it was already remounted recently; remounting in a loop fixes nothing",
                Detail = detail,
            };
        }

        _lastRecovery[poolId] = now;

        try
        {
            // Unmount and mount rather than anything cleverer: it is the one action that returns
            // WinFsp's dispatcher state to a known good one, and the pool's files are untouched by
            // it. Every byte stays where it is; only the drive letter goes away and come back.
            _engine.Remount(poolId);

            health.Reset();

            return new WatchdogOutcome
            {
                PoolId = poolId,
                PoolName = name,
                State = IoHealthState.Stalled,
                Recovered = true,
                Detail = detail,
            };
        }
        catch (Exception exception) when (exception is IOException
            or InvalidOperationException
            or UnauthorizedAccessException)
        {
            return new WatchdogOutcome
            {
                PoolId = poolId,
                PoolName = name,
                State = IoHealthState.Stalled,
                Declined = $"remounting failed: {exception.Message}",
                Detail = detail,
            };
        }
    }
}
