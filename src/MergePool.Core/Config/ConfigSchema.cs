using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MergePool.Core.Config;

/// <summary>Schema version constants and the serializer settings used for every config read/write.</summary>
public static class ConfigSchema
{
    /// <summary>
    /// Bump this only together with a new <see cref="IConfigMigration"/>. Migrations are
    /// forward-only: an older build reads a newer document through its extension data rather than
    /// rewriting it.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>Documents from before versioning was introduced report this.</summary>
    public const int UnversionedVersion = 0;

    public const string SchemaVersionProperty = "schemaVersion";

    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static JsonNodeOptions NodeOptions { get; } = new() { PropertyNameCaseInsensitive = true };

    public static JsonDocumentOptions DocumentOptions { get; } = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
