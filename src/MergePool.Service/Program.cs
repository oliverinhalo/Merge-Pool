using System.Runtime.Versioning;
using MergePool.Core.Config;
using MergePool.Core.Volumes;
using MergePool.Engine;
using MergePool.Fs.WinFsp;
using MergePool.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

[assembly: SupportedOSPlatform("windows")]

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
