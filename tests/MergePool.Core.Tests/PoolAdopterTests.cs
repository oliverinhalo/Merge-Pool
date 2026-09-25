using MergePool.Core.Model;
using Xunit;

namespace MergePool.Core.Tests;

public sealed class PoolAdopterTests : IDisposable
{
    private readonly string _volume = Path.Combine(Path.GetTempPath(), "mergepool-adopt", Guid.NewGuid().ToString("N"));
    private readonly Guid _partId = Guid.NewGuid();

    public PoolAdopterTests() => Directory.CreateDirectory(_volume);

    [Fact]
    public void Plan_skips_system_folders_and_other_pool_parts()
    {
        Directory.CreateDirectory(Path.Combine(_volume, "Movies"));
        Directory.CreateDirectory(Path.Combine(_volume, "$RECYCLE.BIN"));
        Directory.CreateDirectory(Path.Combine(_volume, "System Volume Information"));
        Directory.CreateDirectory(Path.Combine(_volume, PoolPartLayout.FolderName(Guid.NewGuid())));
        File.WriteAllText(Path.Combine(_volume, "notes.txt"), "hello");

        var plan = new PoolAdopter().Plan(_volume, _partId);

        Assert.Equal(["Movies", "notes.txt"], plan.Items.Select(i => i.Name).OrderBy(n => n).ToArray());
        Assert.Contains("$RECYCLE.BIN", plan.Skipped);
        Assert.Contains("System Volume Information", plan.Skipped);
    }

    [Fact]
    public void Adopt_moves_content_into_the_pool_part_on_the_same_volume()
    {
        Directory.CreateDirectory(Path.Combine(_volume, "Movies"));
        File.WriteAllText(Path.Combine(_volume, "Movies", "a.mkv"), "movie");
        File.WriteAllText(Path.Combine(_volume, "notes.txt"), "hello");

        var adopter = new PoolAdopter();
        var plan = adopter.Plan(_volume, _partId);
        var result = adopter.Adopt(_volume, _partId, plan);

        var partRoot = PoolPartLayout.RootPathFor(_volume, _partId);
        Assert.Equal(2, result.MovedCount);
        Assert.Empty(result.Failed);
        Assert.Equal("movie", File.ReadAllText(Path.Combine(partRoot, "Movies", "a.mkv")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(partRoot, "notes.txt")));
        Assert.False(File.Exists(Path.Combine(_volume, "notes.txt")));
    }

    [Fact]
    public void Adopt_reports_total_bytes_without_moving_protected_content()
    {
        File.WriteAllText(Path.Combine(_volume, "a.bin"), new string('x', 100));
        Directory.CreateDirectory(Path.Combine(_volume, "Windows"));
        File.WriteAllText(Path.Combine(_volume, "Windows", "untouched.txt"), "os");

        var adopter = new PoolAdopter();
        var plan = adopter.Plan(_volume, _partId);
        adopter.Adopt(_volume, _partId, plan);

        Assert.Equal(100, plan.TotalBytes);
        Assert.True(File.Exists(Path.Combine(_volume, "Windows", "untouched.txt")));
    }

    [Fact]
    public void Adopt_does_not_overwrite_an_existing_entry()
    {
        var partRoot = PoolPartLayout.RootPathFor(_volume, _partId);
        Directory.CreateDirectory(partRoot);
        File.WriteAllText(Path.Combine(partRoot, "clash.txt"), "already-pooled");
        File.WriteAllText(Path.Combine(_volume, "clash.txt"), "loose");

        var adopter = new PoolAdopter();
        var result = adopter.Adopt(_volume, _partId, adopter.Plan(_volume, _partId));

        Assert.Equal(["clash.txt"], result.Failed);
        Assert.Equal("already-pooled", File.ReadAllText(Path.Combine(partRoot, "clash.txt")));
        Assert.Equal("loose", File.ReadAllText(Path.Combine(_volume, "clash.txt")));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_volume, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
