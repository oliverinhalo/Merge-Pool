namespace MergePool.Update;

/// <summary>
/// Points the <c>current</c> link at a version. On Windows this is a directory junction, which is
/// swapped rather than copied so the change is effectively atomic.
/// </summary>
public interface ICurrentLinkManager
{
    /// <summary>The version the link currently resolves to, or <c>null</c> when there is no link.</summary>
    string? ReadCurrentVersion();

    /// <summary>Repoints the link. Must leave the old version's files untouched.</summary>
    void PointAt(string version);
}

public enum ServiceState
{
    NotInstalled = 0,
    Stopped = 1,
    Running = 2,
    Transitioning = 3,
}

public interface IServiceControl
{
    ServiceState GetState();

    Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken);

    Task StartAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed record EngineHealth(bool Healthy, IReadOnlyList<string> Problems, string Version);

/// <summary>The upgrade's view of the running engine: drain before, health check after.</summary>
public interface IEngineControl
{
    /// <summary>Unmounts every pool and stops accepting work. Returns false if the engine is unreachable.</summary>
    Task<bool> DrainAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Mounts everything again and reports whether the pools came back.</summary>
    Task<EngineHealth?> ResumeAndCheckAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

public enum UpgradeOutcome
{
    Succeeded = 0,

    /// <summary>The new version failed its health check and the previous one was put back.</summary>
    RolledBack = 1,

    /// <summary>The upgrade failed and the rollback failed too: the machine needs a human.</summary>
    Failed = 2,

    /// <summary>Nothing to do: the requested version is already active.</summary>
    AlreadyCurrent = 3,
}

public sealed record UpgradeResult
{
    public required UpgradeOutcome Outcome { get; init; }

    public required string TargetVersion { get; init; }

    public string? PreviousVersion { get; init; }

    /// <summary>The version actually running when the upgrade finished.</summary>
    public string? ActiveVersion { get; init; }

    public IReadOnlyList<string> Log { get; init; } = Array.Empty<string>();

    public string? Error { get; init; }

    public bool IsSuccess => Outcome is UpgradeOutcome.Succeeded or UpgradeOutcome.AlreadyCurrent;
}
