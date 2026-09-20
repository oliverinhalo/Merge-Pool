using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MergePool.Core.Config;

public sealed record ConfigLoadResult
{
    public required MergePoolConfig Config { get; init; }

    /// <summary>Schema version of the document as found on disk.</summary>
    public required int OriginalVersion { get; init; }

    public required bool Migrated { get; init; }

    /// <summary>Path of the pre-migration backup, when one was taken.</summary>
    public string? BackupPath { get; init; }

    /// <summary>True when no file existed and defaults were returned.</summary>
    public required bool CreatedFromDefaults { get; init; }
}

/// <summary>
/// Reads and writes <c>config.json</c>. Guarantees, in order: never lose a field we do not
/// understand, never migrate without a backup, never leave a torn file behind.
/// </summary>
public sealed class ConfigStore
{
    private readonly string _path;
    private readonly IReadOnlyList<IConfigMigration> _migrations;
    private readonly TimeProvider _timeProvider;
    private readonly object _writeLock = new();

    public ConfigStore(string path, IEnumerable<IConfigMigration>? migrations = null, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = System.IO.Path.GetFullPath(path);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _migrations = (migrations ?? DefaultMigrations()).OrderBy(static m => m.FromVersion).ToArray();
    }

    public string FilePath => _path;

    public static IReadOnlyList<IConfigMigration> DefaultMigrations() => [new MigrationV0ToV1()];

    /// <summary>Default location: <c>%ProgramData%\MergePool\config.json</c>, version independent.</summary>
    public static string DefaultConfigPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrEmpty(root))
        {
            root = AppContext.BaseDirectory;
        }

        return System.IO.Path.Combine(root, "MergePool", "config.json");
    }

    public ConfigLoadResult Load()
    {
        if (!File.Exists(_path))
        {
            return new ConfigLoadResult
            {
                Config = new MergePoolConfig(),
                OriginalVersion = ConfigSchema.CurrentVersion,
                Migrated = false,
                CreatedFromDefaults = true,
            };
        }

        var text = File.ReadAllText(_path, Encoding.UTF8);
        var node = JsonNode.Parse(text, ConfigSchema.NodeOptions, ConfigSchema.DocumentOptions);
        if (node is not JsonObject document)
        {
            throw new InvalidDataException($"'{_path}' is not a JSON object.");
        }

        var originalVersion = ReadVersion(document);
        string? backupPath = null;
        var migrated = false;

        if (originalVersion < ConfigSchema.CurrentVersion)
        {
            backupPath = WriteBackup(text, originalVersion);
            document = ApplyMigrations(document, originalVersion);
            migrated = true;
        }

        var config = document.Deserialize<MergePoolConfig>(ConfigSchema.SerializerOptions)
                     ?? throw new InvalidDataException($"'{_path}' deserialized to null.");

        // A document from the future keeps its own version so the newer build still recognizes it.
        config.SchemaVersion = Math.Max(originalVersion, ConfigSchema.CurrentVersion);

        if (migrated)
        {
            Save(config);
        }

        return new ConfigLoadResult
        {
            Config = config,
            OriginalVersion = originalVersion,
            Migrated = migrated,
            BackupPath = backupPath,
            CreatedFromDefaults = false,
        };
    }

    /// <summary>Writes atomically: temp file in the same directory, flushed, then replaced into place.</summary>
    public void Save(MergePoolConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.SchemaVersion < ConfigSchema.CurrentVersion)
        {
            config.SchemaVersion = ConfigSchema.CurrentVersion;
        }

        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(config, ConfigSchema.SerializerOptions);

        lock (_writeLock)
        {
            var temp = _path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, _path, overwrite: true);
        }
    }

    internal static int ReadVersion(JsonObject document)
    {
        if (document.TryGetPropertyValue(ConfigSchema.SchemaVersionProperty, out var value)
            && value is JsonValue jsonValue
            && jsonValue.TryGetValue<int>(out var version))
        {
            return version;
        }

        return ConfigSchema.UnversionedVersion;
    }

    private JsonObject ApplyMigrations(JsonObject document, int fromVersion)
    {
        var version = fromVersion;
        while (version < ConfigSchema.CurrentVersion)
        {
            var migration = _migrations.FirstOrDefault(m => m.FromVersion == version)
                ?? throw new InvalidDataException(
                    $"No migration from config schema v{version}; cannot upgrade '{_path}'.");

            document = migration.Migrate(document);
            if (migration.ToVersion <= version)
            {
                throw new InvalidOperationException(
                    $"Migration {migration.GetType().Name} does not advance the schema version.");
            }

            version = migration.ToVersion;
            document[ConfigSchema.SchemaVersionProperty] = version;
        }

        return document;
    }

    private string WriteBackup(string originalText, int version)
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var stamp = _timeProvider.GetUtcNow().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var backupPath = string.Create(
            CultureInfo.InvariantCulture,
            $"{_path}.v{version}.{stamp}.bak");

        var suffix = 1;
        while (File.Exists(backupPath))
        {
            backupPath = string.Create(
                CultureInfo.InvariantCulture,
                $"{_path}.v{version}.{stamp}.{suffix++}.bak");
        }

        File.WriteAllText(backupPath, originalText, Encoding.UTF8);
        return backupPath;
    }
}
