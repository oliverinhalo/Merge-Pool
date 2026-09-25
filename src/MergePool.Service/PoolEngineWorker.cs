using System.Runtime.Versioning;
using MergePool.Engine;
using MergePool.Engine.Ipc;
using MergePool.Ipc.Protocol;
using MergePool.Ipc.Server;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MergePool.Service;

/// <summary>
/// The service body: brings pools up, serves the UI over the named pipe, and runs background
/// upkeep (drive rescans, idle probes, throttle recovery, rebalancing).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PoolEngineWorker(
    PoolEngine engine,
    UpdateHost updates,
    ILogger<PoolEngineWorker> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);

    private IpcServer? _server;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (engine.ConfigWasMigrated)
        {
            logger.LogInformation(
                "Configuration was migrated to the current schema. Backup: {Backup}", engine.ConfigBackupPath);
        }

        var router = new IpcRouter().Register(new EngineIpcHandler(engine, updates.Service));

        _server = new IpcServer(
            new IpcServerOptions
            {
                PipeName = engine.Config.Service.PipeName,
                ServiceVersion = engine.Version,
                PipeFactory = PipeSecurityFactory.Create,
                IsDraining = () => engine.IsDraining,
                Capabilities = Capabilities.All,
            },
            router);

        _server.Start();
        logger.LogInformation(
            "MergePool {Version} listening on pipe {Pipe} (protocol {Min}-{Max}).",
            engine.Version,
            engine.Config.Service.PipeName,
            ProtocolVersion.MinimumSupported,
            ProtocolVersion.Current);

        engine.MountAll();

        foreach (var problem in engine.CheckHealth())
        {
            logger.LogWarning("Health: {Problem}", problem);
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                engine.Tick(stoppingToken);
                RunRebalancePass(stoppingToken);
                await RunUpdateCheckAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Background upkeep hit an IO error; continuing.");
            }
        }
    }

    /// <summary>
    /// Keeping itself current. The updater decides whether it is time, so this is nearly free on
    /// most ticks, and it is skipped entirely while draining: an upgrade is already under way then.
    /// </summary>
    private async Task RunUpdateCheckAsync(CancellationToken stoppingToken)
    {
        if (updates.Service is not { } updater || engine.IsDraining)
        {
            return;
        }

        var before = updater.State.Stage;

        await updater.TickAsync(
            engine.Config.Updates,
            options => engine.UpdateUpdateOptions(existing =>
            {
                existing.LastCheckedUtc = options.LastCheckedUtc;
                existing.SkipVersion = options.SkipVersion;
            }),
            stoppingToken).ConfigureAwait(false);

        var state = updater.State;
        if (state.Stage == before)
        {
            return;
        }

        if (state.UpdateAvailable)
        {
            logger.LogInformation(
                "Update available: {Version} (installed {Installed}). Stage: {Stage}.",
                state.Available!.Version,
                state.InstalledVersion,
                state.Stage);
        }

        if (state.LastError is { } error)
        {
            logger.LogWarning("Update check: {Error}", error);
        }
    }

    /// <summary>Rebalancing is opportunistic: one pool per tick, and never while draining.</summary>
    private void RunRebalancePass(CancellationToken stoppingToken)
    {
        if (engine.IsDraining || !engine.Config.Placement.Rebalance.Enabled)
        {
            return;
        }

        foreach (var pool in engine.Pools)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            var plan = pool.Rebalancer.Plan();
            if (plan.IsEmpty)
            {
                continue;
            }

            logger.LogInformation(
                "Rebalancing pool {Pool}: {Count} file(s), {Bytes} bytes, imbalance {Before:P1} -> {After:P1}.",
                pool.Definition.Name,
                plan.Moves.Count,
                plan.TotalBytes,
                plan.ImbalanceBefore,
                plan.ImbalanceAfter);

            var result = pool.Rebalancer.Run(plan, stoppingToken);
            logger.LogInformation(
                "Rebalance finished: moved {Moved}, skipped {Skipped}, failed {Failed}.",
                result.MovedCount,
                result.SkippedInUse,
                result.Failed);

            return;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Draining pools for shutdown.");
        engine.Drain();

        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
