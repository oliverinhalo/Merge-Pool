using System.Text.Json;
using System.Text.Json.Serialization;

namespace MergePool.Ipc.Protocol;

/// <summary>
/// Data carried over the wire. Every type keeps an extension-data bag so a newer peer's extra
/// fields survive a round trip through an older one.
/// </summary>
public abstract class IpcContract
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; set; } = [];
}

public sealed class HelloRequest : IpcContract
{
    [JsonPropertyName("clientName")]
    public string ClientName { get; set; } = "MergePool";

    [JsonPropertyName("clientVersion")]
    public string ClientVersion { get; set; } = "0.0.0";

    [JsonPropertyName("minProtocolVersion")]
    public int MinProtocolVersion { get; set; } = ProtocolVersion.MinimumSupported;

    [JsonPropertyName("maxProtocolVersion")]
    public int MaxProtocolVersion { get; set; } = ProtocolVersion.Current;
}

public sealed class HelloResult : IpcContract
{
    [JsonPropertyName("serviceVersion")]
    public string ServiceVersion { get; set; } = "0.0.0";

    /// <summary>The version both sides will use for the rest of the connection.</summary>
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; }

    [JsonPropertyName("minProtocolVersion")]
    public int MinProtocolVersion { get; set; }

    [JsonPropertyName("maxProtocolVersion")]
    public int MaxProtocolVersion { get; set; }

    /// <summary>Named features this build offers. Clients feature-detect on these.</summary>
    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; set; } = [];

    /// <summary>Set while the service is draining for an upgrade.</summary>
    [JsonPropertyName("draining")]
    public bool Draining { get; set; }
}

public sealed class DriveDto : IpcContract
{
    [JsonPropertyName("volumeId")]
    public string VolumeId { get; set; } = string.Empty;

    [JsonPropertyName("driveLetter")]
    public string? DriveLetter { get; set; }

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("fileSystem")]
    public string FileSystem { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "Unknown";

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("freeBytes")]
    public long FreeBytes { get; set; }

    [JsonPropertyName("isReady")]
    public bool IsReady { get; set; }

    /// <summary>True when the drive can be added to a pool.</summary>
    [JsonPropertyName("isPoolable")]
    public bool IsPoolable { get; set; }

    /// <summary>Set when the drive already carries a pool part.</summary>
    [JsonPropertyName("poolId")]
    public Guid? PoolId { get; set; }
}

public sealed class PoolPartDto : IpcContract
{
    [JsonPropertyName("partId")]
    public Guid PartId { get; set; }

    [JsonPropertyName("volumeId")]
    public string VolumeId { get; set; } = string.Empty;

    [JsonPropertyName("driveLetter")]
    public string? DriveLetter { get; set; }

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>One of Offline, Online, ReadOnly.</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "Offline";

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("freeBytes")]
    public long FreeBytes { get; set; }

    [JsonPropertyName("isThrottled")]
    public bool IsThrottled { get; set; }

    [JsonPropertyName("throttleReason")]
    public string ThrottleReason { get; set; } = "None";

    [JsonPropertyName("speedFactor")]
    public double SpeedFactor { get; set; } = 1.0;

    [JsonPropertyName("throughputBytesPerSecond")]
    public double ThroughputBytesPerSecond { get; set; }

    [JsonPropertyName("baselineBytesPerSecond")]
    public double BaselineBytesPerSecond { get; set; }

    [JsonPropertyName("latencyMilliseconds")]
    public double LatencyMilliseconds { get; set; }
}

public sealed class PoolDto : IpcContract
{
    [JsonPropertyName("poolId")]
    public Guid PoolId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("mountPoint")]
    public string MountPoint { get; set; } = string.Empty;

    [JsonPropertyName("isMounted")]
    public bool IsMounted { get; set; }

    /// <summary>One of Healthy, Degraded, Offline.</summary>
    [JsonPropertyName("health")]
    public string Health { get; set; } = "Offline";

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("freeBytes")]
    public long FreeBytes { get; set; }

    [JsonPropertyName("parts")]
    public List<PoolPartDto> Parts { get; set; } = [];
}

public sealed class PoolListResult : IpcContract
{
    [JsonPropertyName("pools")]
    public List<PoolDto> Pools { get; set; } = [];
}

public sealed class DriveListResult : IpcContract
{
    [JsonPropertyName("drives")]
    public List<DriveDto> Drives { get; set; } = [];
}

public sealed class CreatePoolRequest : IpcContract
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "Pool";

    [JsonPropertyName("mountPoint")]
    public string MountPoint { get; set; } = string.Empty;

    /// <summary>Volume GUIDs of the drives to pool.</summary>
    [JsonPropertyName("volumeIds")]
    public List<string> VolumeIds { get; set; } = [];

    /// <summary>Move what is already on each drive into its pool part (same-volume rename).</summary>
    [JsonPropertyName("adoptExistingContent")]
    public bool AdoptExistingContent { get; set; }

    [JsonPropertyName("mountImmediately")]
    public bool MountImmediately { get; set; } = true;
}

public sealed class PoolReference : IpcContract
{
    [JsonPropertyName("poolId")]
    public Guid PoolId { get; set; }
}

public sealed class RemovePoolRequest : IpcContract
{
    [JsonPropertyName("poolId")]
    public Guid PoolId { get; set; }

    /// <summary>
    /// Pool parts are left on the drives by default: removing a pool must never delete data.
    /// </summary>
    [JsonPropertyName("deletePoolParts")]
    public bool DeletePoolParts { get; set; }
}

public sealed class ServiceStatusResult : IpcContract
{
    [JsonPropertyName("serviceVersion")]
    public string ServiceVersion { get; set; } = "0.0.0";

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; }

    [JsonPropertyName("uptimeSeconds")]
    public double UptimeSeconds { get; set; }

    [JsonPropertyName("draining")]
    public bool Draining { get; set; }

    [JsonPropertyName("poolCount")]
    public int PoolCount { get; set; }

    [JsonPropertyName("mountedCount")]
    public int MountedCount { get; set; }

    [JsonPropertyName("winFspInstalled")]
    public bool WinFspInstalled { get; set; }

    [JsonPropertyName("winFspVersion")]
    public string? WinFspVersion { get; set; }
}

public sealed class MetricsResult : IpcContract
{
    [JsonPropertyName("drives")]
    public List<PoolPartDto> Drives { get; set; } = [];
}

public sealed class RebalanceStatusResult : IpcContract
{
    [JsonPropertyName("running")]
    public bool Running { get; set; }

    [JsonPropertyName("paused")]
    public bool Paused { get; set; }

    [JsonPropertyName("movedCount")]
    public int MovedCount { get; set; }

    [JsonPropertyName("movedBytes")]
    public long MovedBytes { get; set; }

    [JsonPropertyName("plannedCount")]
    public int PlannedCount { get; set; }

    [JsonPropertyName("plannedBytes")]
    public long PlannedBytes { get; set; }

    [JsonPropertyName("imbalance")]
    public double Imbalance { get; set; }

    [JsonPropertyName("currentFile")]
    public string? CurrentFile { get; set; }
}

public sealed class AdoptionPlanRequest : IpcContract
{
    [JsonPropertyName("volumeId")]
    public string VolumeId { get; set; } = string.Empty;

    [JsonPropertyName("poolId")]
    public Guid PoolId { get; set; }
}

public sealed class AdoptionPlanResult : IpcContract
{
    [JsonPropertyName("items")]
    public List<AdoptionItemDto> Items { get; set; } = [];

    [JsonPropertyName("skipped")]
    public List<string> Skipped { get; set; } = [];

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }
}

public sealed class AdoptionItemDto : IpcContract
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("isDirectory")]
    public bool IsDirectory { get; set; }

    [JsonPropertyName("bytes")]
    public long Bytes { get; set; }
}

public sealed class AdoptionRunResult : IpcContract
{
    [JsonPropertyName("movedCount")]
    public int MovedCount { get; set; }

    [JsonPropertyName("movedBytes")]
    public long MovedBytes { get; set; }

    [JsonPropertyName("failed")]
    public List<string> Failed { get; set; } = [];
}

public sealed class HealthCheckResult : IpcContract
{
    [JsonPropertyName("healthy")]
    public bool Healthy { get; set; }

    [JsonPropertyName("problems")]
    public List<string> Problems { get; set; } = [];
}

public sealed class MountRequest : IpcContract
{
    [JsonPropertyName("poolId")]
    public Guid PoolId { get; set; }

    /// <summary>Overrides the configured mount point for this mount only.</summary>
    [JsonPropertyName("mountPoint")]
    public string? MountPoint { get; set; }
}

/// <summary>Placement tuning the UI can change at runtime. Absent fields are left as they are.</summary>
public sealed class PlacementSettingsDto : IpcContract
{
    [JsonPropertyName("freeSpaceWeight")]
    public double? FreeSpaceWeight { get; set; }

    [JsonPropertyName("speedWeight")]
    public double? SpeedWeight { get; set; }

    [JsonPropertyName("minFreeBytes")]
    public long? MinFreeBytes { get; set; }

    [JsonPropertyName("throttleEnterRatio")]
    public double? ThrottleEnterRatio { get; set; }

    [JsonPropertyName("throttleExitRatio")]
    public double? ThrottleExitRatio { get; set; }

    [JsonPropertyName("throttleCooldownSeconds")]
    public double? ThrottleCooldownSeconds { get; set; }

    [JsonPropertyName("rebalanceEnabled")]
    public bool? RebalanceEnabled { get; set; }

    [JsonPropertyName("rebalanceMaxBytesPerSecond")]
    public long? RebalanceMaxBytesPerSecond { get; set; }
}
