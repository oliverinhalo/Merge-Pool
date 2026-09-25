using System.Runtime.Versioning;
using MergePool.Core.Config;
using MergePool.Engine;
using MergePool.Engine.Ipc;
using MergePool.Ipc.Protocol;
using MergePool.Ipc.Server;
using MergePool.Web;
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
    private WebControlServer? _web;
    private WindowsFirewallRule? _firewall;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (engine.ConfigWasMigrated)
        {
            logger.LogInformation(
                "Configuration was migrated to the current schema. Backup: {Backup}", engine.ConfigBackupPath);
        }

        // The web interface lives here rather than in the window: it has to keep serving whether or
        // not anyone is signed in, and only the service's account can bind a port for the network.
        engine.NewWebToken = WebAccessToken.Generate;
        _firewall = new WindowsFirewallRule(logger);
        _web = new WebControlServer(
            new WebApi(engine, updates.Service),
            () => engine.Config.Web,
            (message, exception) =>
            {
                if (exception is null)
                {
                    logger.LogInformation("Web interface: {Message}", message);
                }
                else
                {
                    logger.LogWarning(exception, "Web interface: {Message}", message);
                }
            });

        ApplyWebInterface();

        var router = new IpcRouter().Register(new EngineIpcHandler(engine, updates.Service, DescribeWeb));

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

                // Cheap when nothing changed: this only acts when the port, the scope or the on/off
                // switch is not what the server is currently doing.
                ApplyWebInterface();

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

    /// <summary>
    /// Brings the web server and the firewall rule in line with the settings. The rule is only ever
    /// open while the interface is both on and set to be reachable from the network.
    /// </summary>
    private void ApplyWebInterface()
    {
        if (_web is null)
        {
            return;
        }

        _web.Apply();

        var options = engine.Config.Web;
        var status = _web.Status;

        var wantsRule = options.ManageFirewallRule
            && options.Enabled
            && options.AccessScope is WebAccessScope.Network
            && status.State is WebServerState.Listening;

        _firewall?.Apply(wantsRule ? options.Port : null);
    }

    /// <summary>What the UI is told about the web interface, over the pipe.</summary>
    private WebInterfaceResult DescribeWeb()
    {
        var status = _web?.Status;

        return new WebInterfaceResult
        {
            State = (status?.State ?? WebServerState.Stopped).ToString(),
            Url = status?.Url,
            Error = status?.Error,
            RequestCount = status?.RequestCount ?? 0,
            RejectedCount = status?.RejectedCount ?? 0,
        };
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

        // The port closes with the service, and the firewall rule goes with it: an open port that
        // nothing is listening on is still a door.
        _web?.Dispose();
        _web = null;
        _firewall?.Remove();
        _firewall = null;

        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
