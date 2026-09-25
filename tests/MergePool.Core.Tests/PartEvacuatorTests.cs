using MergePool.Core.Model;
using MergePool.Core.Rebalance;
using Xunit;

namespace MergePool.Core.Tests;

public sealed class PartEvacuatorTests : IDisposable
{
    private readonly TempPool _pool = new();

    [Fact]
    public void Everything_on_the_drive_is_planned_onto_the_others()
    {
        var (_, leaving) = _pool.AddDrive("Leaving", totalBytes: 1000, freeBytes: 500);
        _pool.AddDrive("Staying", totalBytes: 1000, freeBytes: 900);

        _pool.WriteInto(leaving, "Movies\\film.mkv", new string('x', 40));
        _pool.WriteInto(leaving, "notes.txt", "hello");

        var plan = new PartEvacuator().Plan(_pool.Current, leaving);

        Assert.True(plan.Fits);
        Assert.Equal(2, plan.Items.Count);
        Assert.Equal(45, plan.TotalBytes);
        Assert.All(plan.Items, item => Assert.NotEqual(leaving, item.ToPartId));
    }

    [Fact]
    public void Files_that_will_not_fit_are_reported_instead_of_half_moved()
    {
        var (_, leaving) = _pool.AddDrive("Leaving", totalBytes: 1000, freeBytes: 500);
        _pool.AddDrive("Staying", totalBytes: 1000, freeBytes: 10);

        _pool.WriteInto(leaving, "big.bin", new string('x', 400));

        var plan = new PartEvacuator().Plan(_pool.Current, leaving);

        Assert.False(plan.Fits);
        Assert.Empty(plan.Items);
        Assert.Single(plan.WithoutRoom);
        Assert.Equal(10, plan.DestinationFreeBytes);
    }

    [Fact]
    public void Running_the_plan_moves_the_files_and_keeps_their_contents()
    {
        var (_, leaving) = _pool.AddDrive("Leaving");
        var (_, staying) = _pool.AddDrive("Staying");

        _pool.WriteInto(leaving, "Movies\\film.mkv", "irreplaceable");

        var evacuator = new PartEvacuator();
        var result = evacuator.Run(_pool, evacuator.Plan(_pool.Current, leaving));

        Assert.True(result.IsComplete);
        Assert.Equal(1, result.MovedCount);

        var moved = Path.Combine(_pool.PartRoot(staying), "Movies", "film.mkv");
        Assert.Equal("irreplaceable", File.ReadAllText(moved));
        Assert.False(File.Exists(Path.Combine(_pool.PartRoot(leaving), "Movies", "film.mkv")));
    }

    [Fact]
    public void A_file_another_process_has_open_is_left_alone_and_reported()
    {
        var (_, leaving) = _pool.AddDrive("Leaving");
        _pool.AddDrive("Staying");

        var open = _pool.WriteInto(leaving, "locked.bin", "busy");
        _pool.WriteInto(leaving, "free.bin", "movable");

        var evacuator = new PartEvacuator();
        var plan = evacuator.Plan(_pool.Current, leaving);

        EvacuationResult result;
        using (var _ = new FileStream(open, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = evacuator.Run(_pool, plan);
        }

        Assert.False(result.IsComplete);
        Assert.Equal(1, result.MovedCount);
        Assert.Single(result.SkippedInUse);

        // The locked file is untouched, exactly where it was.
        Assert.Equal("busy", File.ReadAllText(open));
    }

    [Fact]
    public void An_emptied_part_folder_is_removed_but_never_one_with_content_left()
    {
        var (_, leaving) = _pool.AddDrive("Leaving");
        var partRoot = _pool.PartRoot(leaving);

        _pool.WriteInto(leaving, "Movies\\film.mkv", "content");
        Assert.False(PartEvacuator.TryRemoveEmptyPart(partRoot));
        Assert.True(Directory.Exists(partRoot));
        Assert.True(File.Exists(Path.Combine(partRoot, "Movies", "film.mkv")));

        File.Delete(Path.Combine(partRoot, "Movies", "film.mkv"));
        Assert.True(PartEvacuator.TryRemoveEmptyPart(partRoot));
        Assert.False(Directory.Exists(partRoot));
    }

    [Fact]
    public void Removing_an_empty_part_leaves_the_rest_of_the_drive_alone()
    {
        var (_, leaving) = _pool.AddDrive("Leaving");
        var partRoot = _pool.PartRoot(leaving);
        var driveRoot = Path.GetDirectoryName(partRoot)!;

        // Content on the drive but outside the pool part. Nothing here is the pool's to touch.
        File.WriteAllText(Path.Combine(driveRoot, "not-ours.txt"), "mine");

        Assert.True(PartEvacuator.TryRemoveEmptyPart(partRoot));

        Assert.False(Directory.Exists(partRoot));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(driveRoot, "not-ours.txt")));
    }

    [Fact]
    public void Largest_files_are_placed_first_so_a_tight_fit_still_works()
    {
        var (_, leaving) = _pool.AddDrive("Leaving", totalBytes: 1000, freeBytes: 500);
        _pool.AddDrive("Small", totalBytes: 1000, freeBytes: 30);
        _pool.AddDrive("Large", totalBytes: 1000, freeBytes: 100);

        // Placing the small file first could put it on Large and leave the big one homeless.
        _pool.WriteInto(leaving, "big.bin", new string('x', 90));
        _pool.WriteInto(leaving, "small.bin", new string('x', 20));

        var plan = new PartEvacuator().Plan(_pool.Current, leaving);

        Assert.True(plan.Fits);
        Assert.Equal(2, plan.Items.Count);
    }

    public void Dispose() => _pool.Dispose();
}
