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

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; set; } = [];
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

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; set; } = [];
}
