using MergePool.Core.Model;
using MergePool.Core.Union;
using Xunit;

namespace MergePool.Core.Tests;

public sealed class PoolOperationsTests
{
    [Fact]
    public void CreateDirectory_mirrors_onto_every_drive()
    {
        using var pool = new TempPool();
        var (_, a) = pool.AddDrive("A");
        var (_, b) = pool.AddDrive("B");

        pool.Operations.CreateDirectory("Movies/2024");

        Assert.True(Directory.Exists(Path.Combine(pool.PartRoot(a), "Movies", "2024")));
        Assert.True(Directory.Exists(Path.Combine(pool.PartRoot(b), "Movies", "2024")));
    }

    [Fact]
    public void New_file_lands_whole_on_the_best_scoring_drive()
    {
        using var pool = new TempPool();
        pool.AddDrive("Small", totalBytes: 1000, freeBytes: 100);
        var (_, big) = pool.AddDrive("Big", totalBytes: 10_000, freeBytes: 9_000);

        var location = pool.Operations.PrepareNewFile("video.mkv", estimatedSize: 50);
        File.WriteAllText(location.HostPath, "payload");

        Assert.Equal(big, location.Part.PartId);
        Assert.Single(pool.View.FindAllFiles("video.mkv"));
    }

    [Fact]
    public void Speed_factor_can_outweigh_free_space()
    {
        using var pool = new TempPool();
        pool.AddDrive("SlowRoomy", totalBytes: 10_000, freeBytes: 4_000, speedFactor: 0.2);
        var (_, fast) = pool.AddDrive("FastTighter", totalBytes: 10_000, freeBytes: 3_000, speedFactor: 1.0);

        var location = pool.Operations.PrepareNewFile("clip.mkv", estimatedSize: 10);

        Assert.Equal(fast, location.Part.PartId);
    }

    [Fact]
    public void Placement_skips_a_drive_that_cannot_fit_the_file()
    {
        using var pool = new TempPool();
        pool.AddDrive("Tiny", totalBytes: 1000, freeBytes: 10, speedFactor: 100);
        var (_, roomy) = pool.AddDrive("Roomy", totalBytes: 1000, freeBytes: 900);

        var location = pool.Operations.PrepareNewFile("big.bin", estimatedSize: 500);

        Assert.Equal(roomy, location.Part.PartId);
    }

    [Fact]
    public void Placement_fails_when_no_drive_fits()
    {
        using var pool = new TempPool();
        pool.AddDrive("A", totalBytes: 1000, freeBytes: 10);

        Assert.Throws<IOException>(() => pool.Operations.PrepareNewFile("huge.bin", estimatedSize: 10_000));
    }

    [Fact]
    public void Rename_keeps_the_file_on_its_own_drive()
    {
        using var pool = new TempPool();
        var (_, a) = pool.AddDrive("A", freeBytes: 100);
        pool.AddDrive("B", freeBytes: 900_000);

        pool.WriteInto(a, "Downloads/file.bin", "data");
        pool.Operations.CreateDirectory("Archive");

        pool.Operations.Rename("Downloads/file.bin", "Archive/file.bin");

        var moved = pool.View.FindFile("Archive/file.bin");
        Assert.NotNull(moved);
        Assert.Equal(a, moved.Part.PartId);
        Assert.False(pool.View.FileExists("Downloads/file.bin"));
        Assert.Equal("data", File.ReadAllText(moved.HostPath));
    }

    [Fact]
    public void Rename_of_a_folder_applies_to_every_drive_holding_it()
    {
        using var pool = new TempPool();
        var (_, a) = pool.AddDrive("A");
        var (_, b) = pool.AddDrive("B");

        pool.WriteInto(a, "Old/one.txt", "1");
        pool.WriteInto(b, "Old/two.txt", "2");

        pool.Operations.Rename("Old", "New");

        Assert.Equal(["one.txt", "two.txt"], pool.View.EnumerateNames("New"));
        Assert.False(pool.View.DirectoryExists("Old"));
    }

    [Fact]
    public void Rename_onto_an_existing_name_requires_replace()
    {
        using var pool = new TempPool();
        var (_, a) = pool.AddDrive("A");
        pool.WriteInto(a, "one.txt", "1");
        pool.WriteInto(a, "two.txt", "2");

        Assert.Throws<IOException>(() => pool.Operations.Rename("one.txt", "two.txt"));

        pool.Operations.Rename("one.txt", "two.txt", replaceExisting: true);
        Assert.Equal("1", File.ReadAllText(pool.View.FindFile("two.txt")!.HostPath));
    }

    [Fact]
    public void Replace_clears_duplicates_on_other_drives()
    {
        using var pool = new TempPool();
        var (_, a) = pool.AddDrive("A");
        var (_, b) = pool.AddDrive("B");

        pool.WriteInto(a, "source.txt", "source");
        pool.WriteInto(a, "target.txt", "stale-a");
        pool.WriteInto(b, "target.txt", "stale-b");

        pool.Operations.Rename("source.txt", "target.txt", replaceExisting: true);

        var all = pool.View.FindAllFiles("target.txt");
        Assert.Single(all);
        Assert.Equal("source", File.ReadAllText(all[0].HostPath));
    }

    [Fact]
    public void A_folder_cannot_be_moved_into_itself()
    {
        using var pool = new TempPool();
        pool.AddDrive("A");
        pool.Operations.CreateDirectory("Media");

        Assert.Throws<IOException>(() => pool.Operations.Rename("Media", "Media/Inner"));
    }

    [Fact]
    public void DeleteFile_clears_it_from_every_drive()
    {
        using var pool = new TempPool();
        var (_, a) = pool.AddDrive("A");
        var (_, b) = pool.AddDrive("B");

        pool.WriteInto(a, "dup.txt", "a");
        pool.WriteInto(b, "dup.txt", "b");

        pool.Operations.DeleteFile("dup.txt");

        Assert.Empty(pool.View.FindAllFiles("dup.txt"));
    }

    [Fact]
    public void DeleteDirectory_refuses_a_non_empty_folder_unless_recursive()
    {
        using var pool = new TempPool();
        var (_, a) = pool.AddDrive("A");
        pool.AddDrive("B");
        pool.WriteInto(a, "Stuff/file.txt", "x");

        Assert.Throws<IOException>(() => pool.Operations.DeleteDirectory("Stuff"));

        pool.Operations.DeleteDirectory("Stuff", recursive: true);
        Assert.False(pool.View.DirectoryExists("Stuff"));
    }

    [Fact]
    public void The_pool_root_cannot_be_deleted_or_renamed()
    {
        using var pool = new TempPool();
        pool.AddDrive("A");

        Assert.Throws<IOException>(() => pool.Operations.DeleteDirectory(""));
        Assert.Throws<IOException>(() => pool.Operations.Rename("", "x"));
    }

    [Fact]
    public void Nothing_outside_the_pool_part_can_be_touched()
    {
        using var pool = new TempPool();
        var (_, a) = pool.AddDrive("A");
        var part = pool.Current.FindPart(a)!;

        var outside = Path.Combine(pool.Root, "A", "user-data.txt");
        File.WriteAllText(outside, "precious");

        Assert.Throws<UnauthorizedAccessException>(() => PoolOperations.GuardInsidePart(part, outside));
        Assert.Equal("precious", File.ReadAllText(outside));
    }

    [Fact]
    public void Invalid_names_are_rejected_before_any_host_path_is_built()
    {
        using var pool = new TempPool();
        pool.AddDrive("A");

        Assert.Throws<ArgumentException>(() => pool.Operations.CreateDirectory("a|b"));
        Assert.Throws<ArgumentException>(() => pool.Operations.PrepareNewFile("a/../b.txt"));
    }

    [Fact]
    public void Creating_a_folder_over_an_existing_file_fails()
    {
        using var pool = new TempPool();
        var (_, a) = pool.AddDrive("A");
        pool.WriteInto(a, "thing", "file");

        Assert.Throws<IOException>(() => pool.Operations.CreateDirectory("thing"));
    }

    [Fact]
    public void Operations_still_work_while_a_drive_is_missing()
    {
        using var pool = new TempPool();
        var (volumeA, _) = pool.AddDrive("A");
        var (_, b) = pool.AddDrive("B");

        pool.Volumes.SetPresent(volumeA, present: false);
        var location = pool.Operations.PrepareNewFile("while-degraded.txt", estimatedSize: 1);
        File.WriteAllText(location.HostPath, "ok");

        Assert.Equal(b, location.Part.PartId);
        Assert.Equal(PoolHealth.Degraded, pool.Current.Health);
    }
}
