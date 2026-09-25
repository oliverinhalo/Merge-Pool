namespace MergePool.Ipc.Protocol;

/// <summary>
/// The UI/service wire contract. An update must never break a running setup, so the rules are:
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The service accepts every version from <see cref="MinimumSupported"/> to
/// <see cref="Current"/>, so an old UI keeps working against a new service.</item>
/// <item>A version is only bumped for an additive change. Anything removed or re-shaped raises
/// <see cref="MinimumSupported"/>, which is a breaking change and a major release.</item>
/// <item>Both sides ignore fields they do not recognize and round-trip them where they can, so a
/// new service may send more than an old UI expects.</item>
/// <item>New functionality is announced through <see cref="Capabilities"/> rather than inferred
/// from the version number, so a UI can feature-detect instead of guessing.</item>
/// </list>
/// </remarks>
public static class ProtocolVersion
{
    public const int Current = 4;

    public const int MinimumSupported = 1;

    public static bool IsSupported(int version) => version is >= MinimumSupported and <= Current;

    /// <summary>Picks the highest version both sides can speak, or <c>null</c> when there is none.</summary>
    public static int? Negotiate(int clientMinimum, int clientMaximum)
    {
        var low = Math.Max(clientMinimum, MinimumSupported);
        var high = Math.Min(clientMaximum, Current);
        return high >= low ? high : null;
    }
}

/// <summary>Named features a service build offers. A UI checks for these rather than for a version.</summary>
public static class Capabilities
{
    public const string Pools = "pools";
    public const string Metrics = "metrics";
    public const string Rebalance = "rebalance";
    public const string Adoption = "adoption";
    /// <summary>Changing the drives of a pool that already exists (protocol 2).</summary>
    public const string PoolEdit = "poolEdit";
    /// <summary>Per-drive and per-pool usage measured inside the pool parts (protocol 3).</summary>
    public const string Usage = "usage";
    /// <summary>Taking a drive out of a pool, optionally moving its content off first (protocol 3).</summary>
    public const string DriveRemoval = "driveRemoval";
    /// <summary>Checking for and applying updates from the release feed (protocol 3).</summary>
    public const string AutoUpdate = "autoUpdate";
    /// <summary>The web interface: a port to serve it on, and who may reach it (protocol 4).</summary>
    public const string WebInterface = "webInterface";
    public const string Config = "config";
    public const string Upgrade = "upgrade";

    public static IReadOnlyList<string> All { get; } =
    [
        Pools,
        Metrics,
        Rebalance,
        Adoption,
        PoolEdit,
        Usage,
        DriveRemoval,
        AutoUpdate,
        WebInterface,
        Config,
        Upgrade,
    ];
}

/// <summary>Method names on the wire. Names are permanent: a method is deprecated, never reused.</summary>
public static class Methods
{
    public const string Hello = "hello";
    public const string ServiceStatus = "service.status";
    public const string DrivesList = "drives.list";
    public const string PoolsList = "pools.list";
    public const string PoolCreate = "pool.create";
    public const string PoolRemove = "pool.remove";
    public const string PoolAddDrives = "pool.addDrives";
    public const string PoolRemoveDrive = "pool.removeDrive";
    public const string PoolPlanDriveRemoval = "pool.planDriveRemoval";
    public const string PoolMount = "pool.mount";
    public const string PoolUnmount = "pool.unmount";
    public const string PoolStatus = "pool.status";
    public const string MetricsGet = "metrics.get";
    public const string RebalanceStart = "rebalance.start";
    public const string RebalancePause = "rebalance.pause";
    public const string RebalanceResume = "rebalance.resume";
    public const string RebalanceStop = "rebalance.stop";
    public const string RebalanceStatus = "rebalance.status";
    public const string ConfigGet = "config.get";
    public const string ConfigSetPlacement = "config.setPlacement";
    public const string AdoptionPlan = "adoption.plan";
    public const string AdoptionRun = "adoption.run";
    public const string UpgradeDrain = "upgrade.drain";
    public const string UpgradeResume = "upgrade.resume";
    public const string UpdateStatus = "update.status";
    public const string UpdateCheck = "update.check";
    public const string UpdateApply = "update.apply";
    public const string UpdateSetOptions = "update.setOptions";
    public const string WebGet = "web.get";
    public const string WebSet = "web.set";
    public const string WebRegenerateToken = "web.regenerateToken";
    public const string HealthCheck = "health.check";
}

/// <summary>Stable error codes. A UI branches on these, never on the message text.</summary>
public static class IpcErrorCodes
{
    public const string MethodNotSupported = "method_not_supported";
    public const string ProtocolNotSupported = "protocol_not_supported";
    public const string HandshakeRequired = "handshake_required";
    public const string InvalidRequest = "invalid_request";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string AccessDenied = "access_denied";
    public const string Internal = "internal_error";
    public const string Unavailable = "unavailable";
}
