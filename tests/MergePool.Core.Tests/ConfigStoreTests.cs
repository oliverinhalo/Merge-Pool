using System.Text.Json;
using MergePool.Core.Config;
using Xunit;

namespace MergePool.Core.Tests;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mergepool-config", Guid.NewGuid().ToString("N"));

    private string ConfigPath => Path.Combine(_dir, "config.json");

    public ConfigStoreTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void Missing_file_yields_defaults_at_the_current_schema()
    {
        var result = new ConfigStore(ConfigPath).Load();

        Assert.True(result.CreatedFromDefaults);
        Assert.False(result.Migrated);
        Assert.Equal(ConfigSchema.CurrentVersion, result.Config.SchemaVersion);
    }

    [Fact]
    public void Save_then_load_round_trips()
    {
        var store = new ConfigStore(ConfigPath);
        var config = new MergePoolConfig();
        config.Pools.Add(new PoolDefinition
        {
            Name = "Media",
            MountPoint = "P:",
            Drives =
            [
                new PoolDriveDefinition { VolumeId = "{3f2504e0-4f89-41d3-9a0c-0305e82c3301}", PartId = Guid.NewGuid() },
            ],
        });

        store.Save(config);
        var loaded = store.Load().Config;

        Assert.Equal("Media", loaded.Pools[0].Name);
        Assert.Equal("P:", loaded.Pools[0].MountPoint);
        Assert.Single(loaded.Pools[0].Drives);
    }

    [Fact]
    public void Unknown_fields_survive_a_load_and_save_cycle()
    {
        File.WriteAllText(ConfigPath, """
        {
          "schemaVersion": 1,
          "pools": [
            { "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301", "name": "P", "futureKnob": 42 }
          ],
          "placement": { "freeSpaceWeight": 2.0, "unknownTuning": { "nested": true } },
          "somethingFromANewerBuild": ["a", "b"]
        }
        """);

        var store = new ConfigStore(ConfigPath);
        var loaded = store.Load().Config;
        store.Save(loaded);

        using var document = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        var root = document.RootElement;

        Assert.Equal(2, root.GetProperty("somethingFromANewerBuild").GetArrayLength());
        Assert.Equal(42, root.GetProperty("pools")[0].GetProperty("futureKnob").GetInt32());
        Assert.True(root.GetProperty("placement").GetProperty("unknownTuning").GetProperty("nested").GetBoolean());
        Assert.Equal(2.0, root.GetProperty("placement").GetProperty("freeSpaceWeight").GetDouble());
    }

    [Fact]
    public void A_newer_document_keeps_its_own_schema_version()
    {
        File.WriteAllText(ConfigPath, """{ "schemaVersion": 99, "pools": [] }""");

        var result = new ConfigStore(ConfigPath).Load();

        Assert.False(result.Migrated);
        Assert.Equal(99, result.OriginalVersion);
        Assert.Equal(99, result.Config.SchemaVersion);
    }

    [Fact]
    public void Unversioned_document_is_migrated_after_a_backup()
    {
        var original = """
        {
          "pools": [
            {
              "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
              "name": "Legacy",
              "drives": [ { "driveLetter": "D:", "partId": "11111111-1111-1111-1111-111111111111" } ]
            }
          ]
        }
        """;
        File.WriteAllText(ConfigPath, original);

        var result = new ConfigStore(ConfigPath).Load();

        Assert.True(result.Migrated);
        Assert.Equal(0, result.OriginalVersion);
        Assert.Equal(ConfigSchema.CurrentVersion, result.Config.SchemaVersion);
        Assert.Equal("D:", result.Config.Pools[0].Drives[0].LastKnownLetter);

        Assert.NotNull(result.BackupPath);
        Assert.Equal(original, File.ReadAllText(result.BackupPath));
    }

    [Fact]
    public void Migration_is_persisted_so_it_only_runs_once()
    {
        File.WriteAllText(ConfigPath, """{ "pools": [] }""");

        var store = new ConfigStore(ConfigPath);
        Assert.True(store.Load().Migrated);
        Assert.False(store.Load().Migrated);
    }

    [Fact]
    public void Missing_migration_is_an_error_rather_than_silent_data_loss()
    {
        File.WriteAllText(ConfigPath, """{ "schemaVersion": 0, "pools": [] }""");

        var store = new ConfigStore(ConfigPath, migrations: []);

        Assert.Throws<InvalidDataException>(() => store.Load());
    }

    [Fact]
    public void Save_leaves_no_temp_file_behind()
    {
        var store = new ConfigStore(ConfigPath);
        store.Save(new MergePoolConfig());

        Assert.False(File.Exists(ConfigPath + ".tmp"));
        Assert.True(File.Exists(ConfigPath));
    }

    [Fact]
    public void Non_object_config_is_rejected()
    {
        File.WriteAllText(ConfigPath, "[1,2,3]");
        Assert.Throws<InvalidDataException>(() => new ConfigStore(ConfigPath).Load());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
