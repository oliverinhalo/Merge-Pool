using System.Text.Json;
using System.Text.Json.Serialization;

namespace MergePool.Core.Config;

/// <summary>
/// Root of the on-disk configuration. Every node carries <see cref="JsonExtensionDataAttribute"/>
/// so a config written by a newer build round-trips through an older one without losing fields.
/// </summary>
public sealed class MergePoolConfig
{
    /// <summary>The schema version this document was written with. See <see cref="ConfigSchema"/>.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = ConfigSchema.CurrentVersion;

    [JsonPropertyName("pools")]
    public List<PoolDefinition> Pools { get; set; } = [];

    [JsonPropertyName("placement")]
    public PlacementOptions Placement { get; set; } = new();

    [JsonPropertyName("service")]
    public ServiceOptions Service { get; set; } = new();

    [JsonPropertyName("updates")]
    public UpdateOptions Updates { get; set; } = new();

    [JsonPropertyName("ui")]
    public UiOptions Ui { get; set; } = new();

    [JsonPropertyName("web")]
    public WebOptions Web { get; set; } = new();

    [JsonPropertyName("duplication")]
    public DuplicationSettings Duplication { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; set; } = [];
}

public sealed class PoolDefinition
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonPropertyName("name")]
    public string Name { get; set; } = "Pool";

    /// <summary>Where the pool is mounted, e.g. <c>P:</c> or a directory path.</summary>
    [JsonPropertyName("mountPoint")]
    public string MountPoint { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("drives")]
    public List<PoolDriveDefinition> Drives { get; set; } = [];

    /// <summary>
    /// Folders inside this pool that are kept on more than one drive. Empty means no folder is
    /// duplicated, which is the behaviour of every build before this field existed.
    /// </summary>
    [JsonPropertyName("duplication")]
    public List<DuplicationRule> Duplication { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; set; } = [];
}

/// <summary>How many copies a duplicated folder keeps.</summary>
public enum DuplicationMode
{
    /// <summary>A fixed number of copies, each on a different drive.</summary>
    Copies = 0,

    /// <summary>A copy on every drive in the pool, however many that turns out to be.</summary>
    AllDrives = 1,
}

/// <summary>What to do with copies that a rule no longer calls for.</summary>
public enum SurplusCopies
{
    /// <summary>Delete them, so the rule is what is on the disks.</summary>
    Remove = 0,

    /// <summary>Leave them alone. Nothing is ever deleted to satisfy a rule.</summary>
    Keep = 1,
}

/// <summary>
/// One protected folder: a path inside the pool whose files are kept on several drives, so losing a
/// drive does not lose them.
/// </summary>
/// <remarks>
/// <para>A rule covers a folder and everything under it. The pool root (<c>/</c>) is a valid folder,
/// which is how a whole pool is protected.</para>
/// <para>Copies are whole files on different drives — never pieces of a file, and never two copies on
/// one drive, because two copies on one disk is one disk failure away from none.</para>
/// </remarks>
public sealed class DuplicationRule
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The folder inside the pool, as a pool path: <c>Photos</c>, <c>Photos/2026</c>, or empty for
    /// the whole pool.
    /// </summary>
    [JsonPropertyName("folder")]
    public string Folder { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("mode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DuplicationMode Mode { get; set; } = DuplicationMode.Copies;

    /// <summary>
    /// Copies to keep when <see cref="Mode"/> is <see cref="DuplicationMode.Copies"/>. Two is the
    /// least that means anything; three survives losing two drives at once.
    /// </summary>
    [JsonPropertyName("copies")]
    public int Copies { get; set; } = DefaultCopies;

    /// <summary>
    /// Drives allowed to hold copies, by part id. Empty means any drive in the pool, which is what
    /// almost everyone wants; naming drives is for keeping copies off a disk you do not trust.
    /// </summary>
    [JsonPropertyName("onlyOnParts")]
    public List<Guid> OnlyOnParts { get; set; } = [];

    /// <summary>
    /// Compare copies by content, not just by size and last-write time. Catches a copy that has
    /// rotted on the disk, at the cost of reading every byte of every copy on each pass.
    /// </summary>
    [JsonPropertyName("verifyByContent")]
    public bool VerifyByContent { get; set; }

    /// <summary>What happens to copies the rule no longer calls for.</summary>
    [JsonPropertyName("surplus")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SurplusCopies Surplus { get; set; } = SurplusCopies.Remove;

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; set; } = [];

    public const int MinimumCopies = 2;

    public const int DefaultCopies = 2;

    /// <summary>
    /// The copies this rule asks for. Always at least two — a rule asking for one copy is not
    /// duplication — and, for a rule covering every drive, however many drives that is.
    /// </summary>
    public int RequestedCopies(int eligibleDrives) => Mode is DuplicationMode.AllDrives
        ? Math.Max(0, eligibleDrives)
        : Math.Max(MinimumCopies, Copies);

    /// <summary>
    /// The copies that can actually exist: never more than there are drives, because two copies on
    /// one drive is one drive failure away from none. Below <see cref="RequestedCopies"/> means the
    /// pool cannot keep this rule, which is reported rather than quietly rounded down.
    /// </summary>
    public int AchievableCopies(int eligibleDrives) =>
        Math.Min(RequestedCopies(eligibleDrives), Math.Max(0, eligibleDrives));

    /// <summary>Whether this rule governs a pool path. A rule covers its folder and everything under it.</summary>
    public bool Covers(string poolPath) => Paths.PoolPath.IsAtOrUnder(poolPath, Paths.PoolPath.Normalize(Folder));
}

public sealed class PoolDriveDefinition
{
    /// <summary>Volume GUID path. Never a drive letter: letters move between boots.</summary>
    [JsonPropertyName("volumeId")]
    public string VolumeId { get; set; } = string.Empty;

    /// <summary>Identifies the <c>.PoolPart-{GUID}</c> folder on that volume.</summary>
    [JsonPropertyName("partId")]
    public Guid PartId { get; set; }

    /// <summary>Last known drive letter. Display only; never used for identity.</summary>
    [JsonPropertyName("lastKnownLetter")]
    public string? LastKnownLetter { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Operator override for the drive's speed factor. <c>null</c> means measure it.</summary>
    [JsonPropertyName("speedFactorOverride")]
    public double? SpeedFactorOverride { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; set; } = [];
}

/// <summary>Tunables for placement scoring and throttle detection. All configurable at runtime.</summary>
public sealed class PlacementOptions
{
    /// <summary>Exponent on free space in the placement score. 1.0 = strictly proportional.</summary>
    [JsonPropertyName("freeSpaceWeight")]
    public double FreeSpaceWeight { get; set; } = 1.0;

    /// <summary>Exponent on the speed factor in the placement score.</summary>
    [JsonPropertyName("speedWeight")]
    public double SpeedWeight { get; set; } = 1.0;

    /// <summary>A drive with less than this much free space is skipped for new files.</summary>
    [JsonPropertyName("minFreeBytes")]
    public long MinFreeBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Headroom kept free beyond the file's own size when choosing a drive.</summary>
    [JsonPropertyName("writeHeadroomBytes")]
    public long WriteHeadroomBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>EWMA smoothing factor for throughput/latency samples (0..1, higher = more reactive).</summary>
    [JsonPropertyName("ewmaAlpha")]
    public double EwmaAlpha { get; set; } = 0.25;

    /// <summary>Slower smoothing used for the per-drive baseline the throttle detector compares against.</summary>
    [JsonPropertyName("baselineAlpha")]
    public double BaselineAlpha { get; set; } = 0.02;

    /// <summary>Throughput below this fraction of the drive's own baseline means throttled.</summary>
    [JsonPropertyName("throttleEnterRatio")]
    public double ThrottleEnterRatio { get; set; } = 0.45;

    /// <summary>Throughput must climb back above this fraction of baseline to clear (hysteresis).</summary>
    [JsonPropertyName("throttleExitRatio")]
    public double ThrottleExitRatio { get; set; } = 0.75;

    /// <summary>Latency above this multiple of baseline latency also counts as throttled.</summary>
    [JsonPropertyName("latencyThrottleRatio")]
    public double LatencyThrottleRatio { get; set; } = 3.0;

    /// <summary>Minimum time a drive stays parked once throttled.</summary>
    [JsonPropertyName("throttleCooldownSeconds")]
    public double ThrottleCooldownSeconds { get; set; } = 60;

    /// <summary>Consecutive bad samples required before parking a drive.</summary>
    [JsonPropertyName("throttleEnterSamples")]
    public int ThrottleEnterSamples { get; set; } = 3;

    /// <summary>Consecutive good samples required before un-parking a drive.</summary>
    [JsonPropertyName("throttleExitSamples")]
    public int ThrottleExitSamples { get; set; } = 3;

    /// <summary>Throughput treated as a speed factor of 1.0 when converting samples to scores.</summary>
    [JsonPropertyName("referenceThroughputBytesPerSecond")]
    public double ReferenceThroughputBytesPerSecond { get; set; } = 120d * 1024 * 1024;

    /// <summary>Clamp on the measured speed factor, so one odd sample cannot dominate placement.</summary>
    [JsonPropertyName("minSpeedFactor")]
    public double MinSpeedFactor { get; set; } = 0.05;

    [JsonPropertyName("maxSpeedFactor")]
    public double MaxSpeedFactor { get; set; } = 8.0;

    /// <summary>Samples needed before a drive's baseline is trusted enough to call it throttled.</summary>
    [JsonPropertyName("minSamplesForThrottleDetection")]
    public int MinSamplesForThrottleDetection { get; set; } = 5;

    /// <summary>Writes smaller than this only feed latency: they say nothing about throughput.</summary>
    [JsonPropertyName("minThroughputSampleBytes")]
    public long MinThroughputSampleBytes { get; set; } = 64 * 1024;

    /// <summary>How fast the baseline follows a sample that is better than it.</summary>
    [JsonPropertyName("baselineRiseAlpha")]
    public double BaselineRiseAlpha { get; set; } = 0.3;

    /// <summary>How long a drive may sit idle before the light probe measures it.</summary>
    [JsonPropertyName("idleProbeIntervalSeconds")]
    public double IdleProbeIntervalSeconds { get; set; } = 120;

    /// <summary>Size of the idle probe write. Kept small so it costs nothing on an SMR drive.</summary>
    [JsonPropertyName("idleProbeBytes")]
    public int IdleProbeBytes { get; set; } = 1 << 20;

    [JsonPropertyName("rebalance")]
    public RebalanceOptions Rebalance { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; set; } = [];
}

/// <summary>
/// How duplication behaves across every pool. Which folders are duplicated, and how many copies
/// they keep, is per pool — see <see cref="DuplicationRule"/>.
/// </summary>
public sealed class DuplicationSettings
{
    /// <summary>
    /// Off means no copies are made or removed. The rules stay configured, so turning this back on
    /// picks up where it left off rather than starting from nothing.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Cap on copy throughput, so duplication never competes with what you are doing.</summary>
    [JsonPropertyName("maxBytesPerSecond")]
    public long MaxBytesPerSecond { get; set; } = 32L * 1024 * 1024;

    /// <summary>How often the protected folders are checked over.</summary>
    [JsonPropertyName("scanIntervalMinutes")]
    public double ScanIntervalMinutes { get; set; } = 15;

    /// <summary>Free space left on a drive after a copy lands on it.</summary>
    [JsonPropertyName("copyHeadroomBytes")]
    public long CopyHeadroomBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>Hold off while the pool is serving real IO, and carry on when it goes quiet.</summary>
    [JsonPropertyName("pauseWhileBusy")]
    public bool PauseWhileBusy { get; set; } = true;

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; set; } = [];

    public int MegabytesPerSecond => (int)Math.Max(1, MaxBytesPerSecond / (1024 * 1024));

    public TimeSpan ScanInterval => TimeSpan.FromMinutes(Math.Clamp(ScanIntervalMinutes, 1, 24 * 60));
}

public sealed class RebalanceOptions
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Rebalancing starts when the fullest and emptiest drive differ by more than this.</summary>
    [JsonPropertyName("triggerImbalance")]
    public double TriggerImbalance { get; set; } = 0.15;

    /// <summary>Stop once the imbalance is back under this (hysteresis against churn).</summary>
    [JsonPropertyName("targetImbalance")]
    public double TargetImbalance { get; set; } = 0.05;

    /// <summary>Cap on background copy rate so rebalancing never competes with real IO.</summary>
    [JsonPropertyName("maxBytesPerSecond")]
    public long MaxBytesPerSecond { get; set; } = 32L * 1024 * 1024;

    /// <summary>Largest file the rebalancer will move in one pass.</summary>
    [JsonPropertyName("maxFileBytes")]
    public long MaxFileBytes { get; set; } = 64L * 1024 * 1024 * 1024;

    [JsonPropertyName("pauseWhileBusy")]
    public bool PauseWhileBusy { get; set; } = true;

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; set; } = [];
}

/// <summary>
/// How MergePool keeps itself up to date. An update only ever adds a new version directory and
/// repoints which one is active; pool data and this configuration are never touched by one.
/// </summary>
public sealed class UpdateOptions
{
    /// <summary>Check the release feed on a timer.</summary>
    [JsonPropertyName("automaticChecks")]
    public bool AutomaticChecks { get; set; } = true;

    /// <summary>Install a newer version as soon as it is found, without being asked.</summary>
    [JsonPropertyName("automaticInstall")]
    public bool AutomaticInstall { get; set; } = true;

    [JsonPropertyName("checkIntervalHours")]
    public double CheckIntervalHours { get; set; } = 6;

    /// <summary>Accept pre-releases. Off: a working machine should get stable builds.</summary>
    [JsonPropertyName("includePrereleases")]
    public bool IncludePrereleases { get; set; }

    [JsonPropertyName("repositoryOwner")]
    public string RepositoryOwner { get; set; } = "oliverinhalo";

    [JsonPropertyName("repositoryName")]
    public string RepositoryName { get; set; } = "Merge-Pool";

    /// <summary>Recorded so a restart does not immediately re-check.</summary>
    [JsonPropertyName("lastCheckedUtc")]
    public DateTimeOffset? LastCheckedUtc { get; set; }

    /// <summary>Set after a version fails to install, so it is not retried in a loop.</summary>
    [JsonPropertyName("skipVersion")]
    public string? SkipVersion { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; set; } = [];
}

/// <summary>Who can reach the web interface.</summary>
public enum WebAccessScope
{
    /// <summary>Only this computer, over the loopback address. Nothing on the network can connect.</summary>
    ThisComputer = 0,

    /// <summary>Any machine that can reach this one. Needs a firewall rule and the access token.</summary>
    Network = 1,
}

/// <summary>
/// The optional web interface: the same controls as the window, reachable from a browser on the
/// network.
/// </summary>
/// <remarks>
/// Off by default, and bound to this computer only until that is explicitly changed — MergePool can
/// move and delete nothing, but it can unmount a pool and move files between drives, so it is not
/// something to put on a network by accident. Every request must carry the access token.
/// </remarks>
public sealed class WebOptions
{
    /// <summary>Lowest port that is not in the well-known range.</summary>
    public const int MinimumPort = 1024;

    public const int MaximumPort = 65535;

    public const int DefaultPort = 8787;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("port")]
    public int Port { get; set; } = DefaultPort;

    /// <summary>One of <see cref="WebAccessScope"/>. Stored as a name so the file stays readable.</summary>
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = nameof(WebAccessScope.ThisComputer);

    /// <summary>
    /// Shared secret every request must present. Generated when the interface is first turned on;
    /// changing it signs every browser out.
    /// </summary>
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>
    /// Whether the browser may change anything. Off means it can look but not mount, unmount,
    /// rebalance or touch drives.
    /// </summary>
    [JsonPropertyName("allowChanges")]
    public bool AllowChanges { get; set; } = true;

    /// <summary>Open the port in Windows Firewall while the interface is on.</summary>
    [JsonPropertyName("manageFirewallRule")]
    public bool ManageFirewallRule { get; set; } = true;

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; set; } = [];

    public WebAccessScope AccessScope =>
        Enum.TryParse<WebAccessScope>(Scope, ignoreCase: true, out var scope)
            ? scope
            : WebAccessScope.ThisComputer;

    /// <summary>A port outside the usable range, or one Windows reserves, is refused rather than bound.</summary>
    public static bool IsUsablePort(int port) => port is >= MinimumPort and <= MaximumPort;
}

/// <summary>Preferences that belong to the window rather than the engine.</summary>
public sealed class UiOptions
{
    /// <summary>Close the window to the notification area instead of exiting.</summary>
    [JsonPropertyName("closeToTray")]
    public bool CloseToTray { get; set; } = true;

    /// <summary>Start hidden in the notification area when launched at sign-in.</summary>
    [JsonPropertyName("startMinimised")]
    public bool StartMinimised { get; set; } = true;

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "System";

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; set; } = [];
}

public sealed class ServiceOptions
{
    /// <summary>Named pipe the UI connects to. Part of the compatibility contract.</summary>
    [JsonPropertyName("pipeName")]
    public string PipeName { get; set; } = "MergePool.Engine";

    [JsonPropertyName("logLevel")]
    public string LogLevel { get; set; } = "Information";

    /// <summary>
    /// Remount a pool whose file system has stopped answering, rather than leaving it wedged.
    /// </summary>
    /// <remarks>
    /// On by default, because the alternative is what it replaced: a mount that answers nothing,
    /// processes stuck in an uninterruptible wait that cannot be killed, and a reboot as the only
    /// way out. A remount breaks open handles on the pool, which is disruptive — but a mount that
    /// has stalled has already broken them, and this at least lets the applications using it
    /// reconnect and carry on. Pool data is never involved either way.
    /// </remarks>
    [JsonPropertyName("recoverStalledMounts")]
    public bool RecoverStalledMounts { get; set; } = true;

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; set; } = [];
}
