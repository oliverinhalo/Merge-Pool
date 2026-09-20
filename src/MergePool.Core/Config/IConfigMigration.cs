using System.Text.Json.Nodes;

namespace MergePool.Core.Config;

/// <summary>
/// One forward step of the config schema. A migration edits the raw JSON tree, so properties it
/// does not know about survive untouched.
/// </summary>
public interface IConfigMigration
{
    int FromVersion { get; }

    int ToVersion { get; }

    /// <summary>Transforms the document in place and returns it.</summary>
    JsonObject Migrate(JsonObject document);
}

/// <summary>
/// Brings pre-versioning documents up to v1: stamps the schema version and normalizes drive
/// references that were stored as drive letters into volume GUIDs where possible.
/// </summary>
public sealed class MigrationV0ToV1 : IConfigMigration
{
    public int FromVersion => ConfigSchema.UnversionedVersion;

    public int ToVersion => 1;

    public JsonObject Migrate(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        document[ConfigSchema.SchemaVersionProperty] = 1;

        if (document["pools"] is not JsonArray pools)
        {
            return document;
        }

        foreach (var pool in pools.OfType<JsonObject>())
        {
            if (pool["drives"] is not JsonArray drives)
            {
                continue;
            }

            foreach (var drive in drives.OfType<JsonObject>())
            {
                // v0 stored "driveLetter": "D:". Keep it for display, and leave volumeId empty so
                // the engine re-discovers the volume by scanning for the part folder.
                if (drive["driveLetter"] is { } letter && drive["lastKnownLetter"] is null)
                {
                    drive["lastKnownLetter"] = letter.DeepClone();
                }

                drive["volumeId"] ??= string.Empty;
            }
        }

        return document;
    }
}
