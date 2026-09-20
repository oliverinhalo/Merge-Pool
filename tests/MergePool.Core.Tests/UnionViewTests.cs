using MergePool.Core.Model;
using Xunit;

namespace MergePool.Core.Tests;

public sealed class UnionViewTests
{
    [Fact]
    public void Listing_merges_every_part()
    {
        using var pool = new TempPool();
        var (_, a) = pool.AddDrive("A");
        var (_, b) = pool.AddDrive("B");

        pool.WriteInto(a, "one.txt", "1");
        pool.WriteInto(b, "two.txt", "2");

        Assert.Equal(["one.txt", "two.txt"], pool.View.EnumerateNames(""));
    }

    [Fact]
    public void A_folder_spanning_drives_lists_as_one()
    {
        using var pool = new TempPool();
        var (_, a) = pool.AddDrive("A");
        var (_, b) = pool.AddDrive("B");

        pool.WriteInto(a, "Movies/alpha.mkv", "a");
        pool.WriteInto(b, "Movies/beta.mkv", "b");

        Assert.Equal(["alpha.mkv", "beta.mkv"], pool.View.EnumerateNames("Movies"));
    }

    [Fact]
    public void Missing_drive_degrades_instead_of_failing()
    {
        using var pool = new TempPool();
        var (volumeA, a) = pool.AddDrive("A");
        var (_, b) = pool.AddDrive("B");

        pool.WriteInto(a, "gone.txt", "a");
        pool.WriteInto(b, "still-here.txt", "b");

        pool.Volumes.SetPresent(volumeA, present: false);

        Assert.Equal(PoolHealth.Degraded, pool.Current.Health);
        Assert.Equal(["still-here.txt"], pool.View.EnumerateNames(""));
        Assert.False(pool.View.FileExists("gone.txt"));
    }

    [Fact]
    public void Drive_rejoins_automatically_when_it_comes_back()
    {
        using var pool = new TempPool();
        var (volumeA, a) = pool.AddDrive("A");
        pool.AddDrive("B");
        pool.WriteInto(a, "gone.txt", "a");

        pool.Volumes.SetPresent(volumeA, present: false);
        Assert.False(pool.View.FileExists("gone.txt"));

        pool.Volumes.SetPresent(volumeA, present: true);

        Assert.Equal(PoolHealth.Healthy, pool.Current.Health);
        Assert.True(pool.View.FileExists("gone.txt"));
    }

    [Fact]
    public void Duplicate_file_resolves_to_the_newest_write()
    {
        using var pool = new TempPool();
        var (_, a) = pool.AddDrive("A");
        var (_, b) = pool.AddDrive("B");

        pool.WriteInto(a, "dup.txt", "old", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        pool.WriteInto(b, "dup.txt", "new", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var found = pool.View.FindFile("dup.txt");

        Assert.NotNull(found);
        Assert.Equal(b, found.Part.PartId);
        Assert.Equal("new", File.ReadAllText(found.HostPath));
        Assert.Equal(2, pool.View.FindAllFiles("dup.txt").Count);
        Assert.Single(pool.View.EnumerateNames(""));
    }

    [Fact]
    public void A_directory_shadows_a_file_of_the_same_name()
    {
        using var pool = new TempPool();
        var (_, a) = pool.AddDrive("A");
        var (_, b) = pool.AddDrive("B");

        pool.WriteInto(a, "thing", "file");
        Directory.CreateDirectory(Path.Combine(pool.PartRoot(b), "thing"));

        var entries = pool.View.EnumerateEntries("");
        var entry = Assert.Single(entries);
        Assert.True(entry.IsDirectory);
    }

    [Fact]
    public void Pool_part_bookkeeping_is_hidden_from_the_pool()
    {
        using var pool = new TempPool();
        pool.AddDrive("A");

        Assert.Empty(pool.View.EnumerateNames(""));
        Assert.True(File.Exists(Path.Combine(pool.PartRoot(pool.Current.Parts[0].PartId), "poolpart.json")));
    }

    [Fact]
    public void Enumerating_a_missing_directory_throws()
    {
        using var pool = new TempPool();
        pool.AddDrive("A");

        Assert.Throws<DirectoryNotFoundException>(() => pool.View.EnumerateEntries("nope"));
    }

    [Fact]
    public void Capacity_is_the_sum_of_online_drives()
    {
        using var pool = new TempPool();
        var (volumeA, _) = pool.AddDrive("A", totalBytes: 100, freeBytes: 40);
        pool.AddDrive("B", totalBytes: 200, freeBytes: 60);

        Assert.Equal(300, pool.Current.TotalBytes);
        Assert.Equal(100, pool.Current.FreeBytes);

        pool.Volumes.SetPresent(volumeA, present: false);

        Assert.Equal(200, pool.Current.TotalBytes);
        Assert.Equal(60, pool.Current.FreeBytes);
    }

    [Fact]
    public void Pool_with_no_online_drives_reports_offline()
    {
        using var pool = new TempPool();
        var (volumeA, _) = pool.AddDrive("A");
        pool.Volumes.SetPresent(volumeA, present: false);

        Assert.Equal(PoolHealth.Offline, pool.Current.Health);
    }
}
