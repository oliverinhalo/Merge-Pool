using System.Text.Json;
using System.Text.Json.Serialization;

namespace MergePool.Core.Model;

/// <summary>
/// Advisory metadata written into each pool part. The pooled files themselves are the source of
/// truth; this only helps a rebuilt config find its parts again.
/// </summary>
public sealed class PoolPartMarker
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("partId")]
    public Guid PartId { get; set; }

    [JsonPropertyName("poolId")]
    public Guid PoolId { get; set; }

    [JsonPropertyName("poolName")]
    public string PoolName { get; set; } = string.Empty;

    [JsonPropertyName("createdUtc")]
    public DateTimeOffset CreatedUtc { get; set; }

    [JsonPropertyName("createdByVersion")]
    public string CreatedByVersion { get; set; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; set; } = [];
}
