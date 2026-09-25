using System.Runtime.Versioning;
using MergePool.Core.Config;
using MergePool.Core.Volumes;
using MergePool.Engine;
using MergePool.Update;
using MergePool.Fs.WinFsp;
using MergePool.Service;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

[assembly: SupportedOSPlatform("windows")]

const string UpdateHttpClientName = "MergePool.Updates";

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = "MergePool");
builder.Logging.AddEventLog(settings => settings.SourceName = "MergePool");

builder.Services.AddSingleton<IVolumeProvider, WindowsVolumeProvider>();
builder.Services.AddSingleton<WinFspMountService>();
builder.Services.AddSingleton<IPoolMountService>(sp => sp.GetRequiredService<WinFspMountService>());

builder.Services.AddSingleton(_ => new ConfigStore(ResolveConfigPath(args)));

builder.Services.AddSingleton(sp => new PoolEngine(new PoolEngineOptions
{
    ConfigStore = sp.GetRequiredService<ConfigStore>(),
    VolumeProvider = sp.GetRequiredService<IVolumeProvider>(),
    MountService = sp.GetRequiredService<IPoolMountService>(),
    SecurityCopier = new WindowsFileSecurityCopier(),
}));

// Updating itself. The install root is the folder holding versions\ and current, which is two
// levels above the running binaries; without one (a plain dotnet run) the engine simply reports that
// it cannot update itself rather than failing to start.
builder.Services.AddSingleton<ProcessUpgradeLauncher>();
builder.Services.AddSingleton<IUpgradeLauncher>(sp => sp.GetRequiredService<ProcessUpgradeLauncher>());
builder.Services.AddHttpClient(UpdateHttpClientName, client =>
{
    client.Timeout = TimeSpan.FromMinutes(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("MergePool-Updater");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
});

builder.Services.AddSingleton(sp =>
{
    var installRoot = ManagedInstall.FindRoot();
    if (installRoot is null)
    {
        return new UpdateHost(null);
    }

    var engine = sp.GetRequiredService<PoolEngine>();
    var updates = engine.Config.Updates;
    var layout = new InstallLayout(installRoot);
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(UpdateHttpClientName);

    return new UpdateHost(new AutoUpdateService(
        layout,
        new GitHubReleaseFeed(http, new ReleaseFeedOptions
        {
            Owner = updates.RepositoryOwner,
            Repository = updates.RepositoryName,
            IncludePrereleases = updates.IncludePrereleases,
        }),
        new ReleaseStager(layout, http),
        sp.GetRequiredService<IUpgradeLauncher>(),
        engine.Version)
    {
        ServiceName = "MergePool",
        PipeName = engine.Config.Service.PipeName,
    });
});

builder.Services.AddHostedService<PoolEngineWorker>();

var host = builder.Build();
await host.RunAsync();

/// <summary>
/// The config lives outside the versioned install directory so an upgrade never disturbs it.
/// A --config switch is supported for side-by-side testing.
/// </summary>
static string ResolveConfigPath(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], "--config", StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    return ConfigStore.DefaultConfigPath();
}
