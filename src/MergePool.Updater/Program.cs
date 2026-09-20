using System.Runtime.Versioning;
using MergePool.Update;
using MergePool.Updater;

[assembly: SupportedOSPlatform("windows")]

// Usage: MergePool.Updater --install-root <path> --version <version> [--service <name>] [--pipe <name>]
var arguments = ParseArguments(args);

if (!arguments.TryGetValue("version", out var targetVersion) || string.IsNullOrWhiteSpace(targetVersion))
{
    Console.Error.WriteLine("Usage: MergePool.Updater --version <version> [--install-root <path>] [--service <name>] [--pipe <name>]");
    return 2;
}

var installRoot = arguments.GetValueOrDefault("install-root")
                  ?? Path.GetDirectoryName(Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)))
                  ?? AppContext.BaseDirectory;

var layout = new InstallLayout(installRoot);
var coordinator = new UpgradeCoordinator(
    layout,
    new WindowsCurrentLinkManager(layout),
    new WindowsServiceControl(arguments.GetValueOrDefault("service") ?? "MergePool"),
    new IpcEngineControl(arguments.GetValueOrDefault("pipe") ?? "MergePool.Engine"));

Console.WriteLine($"Upgrading {layout} to {targetVersion}.");

var result = await coordinator.UpgradeAsync(targetVersion, CancellationToken.None);

foreach (var line in result.Log)
{
    Console.WriteLine(line);
}

switch (result.Outcome)
{
    case UpgradeOutcome.Succeeded:
        Console.WriteLine($"MergePool {result.ActiveVersion} is running.");
        return 0;

    case UpgradeOutcome.AlreadyCurrent:
        Console.WriteLine($"MergePool {result.ActiveVersion} was already running.");
        return 0;

    case UpgradeOutcome.RolledBack:
        Console.Error.WriteLine(
            $"The upgrade to {result.TargetVersion} failed ({result.Error}). "
            + $"MergePool {result.ActiveVersion} was put back and your pools are serving again.");
        return 1;

    default:
        Console.Error.WriteLine(
            $"The upgrade to {result.TargetVersion} failed and could not be rolled back: {result.Error}. "
            + $"Currently active: {result.ActiveVersion ?? "none"}.");
        return 3;
}

static Dictionary<string, string> ParseArguments(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].StartsWith("--", StringComparison.Ordinal))
        {
            result[args[i][2..]] = args[i + 1];
        }
    }

    return result;
}
