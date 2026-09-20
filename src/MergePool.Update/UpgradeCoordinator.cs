using System.Globalization;

namespace MergePool.Update;

public sealed record UpgradeOptions
{
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan HealthCheckTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Old versions kept on disk after a successful upgrade, so a rollback stays possible.</summary>
    public int KeepVersions { get; init; } = 2;

    /// <summary>Ignore an unreachable engine when draining (e.g. the service is already stopped).</summary>
    public bool AllowDrainFailure { get; init; } = true;
}

/// <summary>
/// Runs an upgrade so that a running setup is never left broken: drain, stop, swap the
/// <c>current</c> link, start, health check — and on any failure put the previous version back and
/// verify that it came up.
/// </summary>
/// <remarks>
/// The on-disk pool data and the configuration are never touched here. The only thing an upgrade
/// changes is which version directory <c>current</c> points at, which is why rolling back is just
/// pointing it back again.
/// </remarks>
public sealed class UpgradeCoordinator(
    InstallLayout layout,
    ICurrentLinkManager link,
    IServiceControl service,
    IEngineControl engine,
    UpgradeOptions? options = null)
{
    private readonly InstallLayout _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    private readonly ICurrentLinkManager _link = link ?? throw new ArgumentNullException(nameof(link));
    private readonly IServiceControl _service = service ?? throw new ArgumentNullException(nameof(service));
    private readonly IEngineControl _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    private readonly UpgradeOptions _options = options ?? new UpgradeOptions();

    public async Task<UpgradeResult> UpgradeAsync(string targetVersion, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetVersion);

        var log = new List<string>();
        var previous = _link.ReadCurrentVersion();

        if (string.Equals(previous, targetVersion, StringComparison.OrdinalIgnoreCase))
        {
            return new UpgradeResult
            {
                Outcome = UpgradeOutcome.AlreadyCurrent,
                TargetVersion = targetVersion,
                PreviousVersion = previous,
                ActiveVersion = previous,
                Log = [Line($"Version {targetVersion} is already active.")],
            };
        }

        if (!_layout.VersionExists(targetVersion))
        {
            return Failure(
                targetVersion,
                previous,
                previous,
                log,
                $"Version {targetVersion} is not installed under {_layout.VersionsRoot}.");
        }

        try
        {
            // 1. Drain: unmount the pools cleanly so nothing is mid-write when the service stops.
            log.Add(Line("Draining pools."));
            var drained = await _engine.DrainAsync(_options.DrainTimeout, cancellationToken).ConfigureAwait(false);
            if (!drained)
            {
                if (!_options.AllowDrainFailure)
                {
                    return Failure(targetVersion, previous, previous, log, "The engine could not be drained.");
                }

                log.Add(Line("The engine was not reachable; continuing (it is probably already stopped)."));
            }

            // 2. Stop the service so the version directory it is running from is released.
            log.Add(Line("Stopping the service."));
            await _service.StopAsync(_options.StopTimeout, cancellationToken).ConfigureAwait(false);

            // 3. Swap. This is the whole upgrade: one link, repointed.
            log.Add(Line($"Pointing 'current' at {targetVersion}."));
            _link.PointAt(targetVersion);

            // 4. Start and 5. check.
            log.Add(Line("Starting the service."));
            await _service.StartAsync(_options.StartTimeout, cancellationToken).ConfigureAwait(false);

            var health = await _engine.ResumeAndCheckAsync(_options.HealthCheckTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (health is { Healthy: true })
            {
                log.Add(Line($"Version {targetVersion} is healthy."));

                var pruned = _layout.PruneOldVersions(targetVersion, _options.KeepVersions);
                if (pruned.Count > 0)
                {
                    log.Add(Line($"Removed old versions: {string.Join(", ", pruned)}."));
                }

                return new UpgradeResult
                {
                    Outcome = UpgradeOutcome.Succeeded,
                    TargetVersion = targetVersion,
                    PreviousVersion = previous,
                    ActiveVersion = targetVersion,
                    Log = log,
                };
            }

            var reason = health is null
                ? "the service did not answer the health check"
                : string.Join("; ", health.Problems);

            log.Add(Line($"Version {targetVersion} is not healthy: {reason}."));
            return await RollBackAsync(targetVersion, previous, log, reason, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            log.Add(Line($"The upgrade failed: {exception.Message}"));
            return await RollBackAsync(targetVersion, previous, log, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Puts a known-good version back and confirms it is actually serving again.</summary>
    public async Task<UpgradeResult> RollBackAsync(
        string failedVersion,
        string? previousVersion,
        List<string> log,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(previousVersion) || !_layout.VersionExists(previousVersion))
        {
            log.Add(Line("There is no previous version to roll back to."));
            return Failure(failedVersion, previousVersion, _link.ReadCurrentVersion(), log, reason);
        }

        try
        {
            log.Add(Line($"Rolling back to {previousVersion}."));

            await _service.StopAsync(_options.StopTimeout, cancellationToken).ConfigureAwait(false);
            _link.PointAt(previousVersion);
            await _service.StartAsync(_options.StartTimeout, cancellationToken).ConfigureAwait(false);

            var health = await _engine.ResumeAndCheckAsync(_options.HealthCheckTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (health is { Healthy: true })
            {
                log.Add(Line($"Rolled back to {previousVersion}; the pools are serving again."));
                return new UpgradeResult
                {
                    Outcome = UpgradeOutcome.RolledBack,
                    TargetVersion = failedVersion,
                    PreviousVersion = previousVersion,
                    ActiveVersion = previousVersion,
                    Log = log,
                    Error = reason,
                };
            }

            log.Add(Line("The rollback started but the pools did not come back."));
            return Failure(failedVersion, previousVersion, previousVersion, log, reason);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            log.Add(Line($"The rollback failed: {exception.Message}"));
            return Failure(failedVersion, previousVersion, _link.ReadCurrentVersion(), log, reason);
        }
    }

    private static UpgradeResult Failure(
        string target,
        string? previous,
        string? active,
        List<string> log,
        string error) => new()
    {
        Outcome = UpgradeOutcome.Failed,
        TargetVersion = target,
        PreviousVersion = previous,
        ActiveVersion = active,
        Log = log,
        Error = error,
    };

    private static string Line(string message) =>
        string.Create(CultureInfo.InvariantCulture, $"[{DateTimeOffset.UtcNow:HH:mm:ss}] {message}");
}
