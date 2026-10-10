using MergePool.Core.Config;
using MergePool.Core.Duplication;
using PoolPathUtil = MergePool.Core.Paths.PoolPath;
using Xunit;

namespace MergePool.Core.Tests;

/// <summary>
/// Duplication is the one feature here whose whole purpose is surviving a lost drive, so these are
/// mostly about the ways copies could quietly fail to be copies.
/// </summary>
public sealed class DuplicationPlannerTests
{
    /// <summary>Pool paths are canonical with a backslash separator, whichever way they came in.</summary>
    private static string P(string poolPath) => PoolPathUtil.Normalize(poolPath);

    private static DuplicationRule Rule(string folder, int copies = 2) => new()
    {
        Folder = folder,
        Copies = copies,
        Mode = DuplicationMode.Copies,
    };

    [Fact]
    public void A_file_with_one_copy_gets_another_on_a_different_drive()
    {
        using var pool = new TempPool();
        var a = pool.AddDrive("a").Part;
        var b = pool.AddDrive("b").Part;

        pool.WriteInto(a, "Photos/one.jpg", "content");

        var plan = new DuplicationPlanner(pool, Headroom.None).Plan([Rule("Photos")]);

        var action = Assert.Single(plan.Actions);
        Assert.Equal(DuplicationActionKind.AddCopy, action.Kind);
        Assert.Equal(P("Photos/one.jpg"), action.PoolPath);
        Assert.Equal(a, action.SourcePartId);
        Assert.Equal(b, action.TargetPartId);
    }

    [Fact]
    public void A_file_that_already_has_its_copies_is_left_alone()
    {
        using var pool = new TempPool();
        var a = pool.AddDrive("a").Part;
        var b = pool.AddDrive("b").Part;

        var stamp = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        pool.WriteInto(a, "Photos/one.jpg", "content", stamp);
        pool.WriteInto(b, "Photos/one.jpg", "content", stamp);

        var plan = new DuplicationPlanner(pool, Headroom.None).Plan([Rule("Photos")]);

        Assert.True(plan.IsEmpty);
        var status = Assert.Single(plan.Rules);
        Assert.Equal(1, status.Files);
        Assert.Equal(1, status.ProtectedFiles);
        Assert.True(status.IsSatisfied);
    }

    [Fact]
    public void Two_copies_are_never_put_on_one_drive()
    {
        using var pool = new TempPool();
        var a = pool.AddDrive("a").Part;

        pool.WriteInto(a, "Photos/one.jpg", "content");

        // One drive cannot hold two copies, so there is nothing to do and something to say.
        var plan = new DuplicationPlanner(pool, Headroom.None).Plan([Rule("Photos")]);

        Assert.True(plan.IsEmpty);
        var shortfall = Assert.Single(plan.Shortfalls);
        Assert.Equal(P("Photos/one.jpg"), shortfall.PoolPath);
        Assert.Equal(1, shortfall.Copies);
        Assert.Contains("1 drive", shortfall.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_drive_means_every_drive()
    {
        using var pool = new TempPool();
        var a = pool.AddDrive("a").Part;
        pool.AddDrive("b");
        pool.AddDrive("c");

        pool.WriteInto(a, "Photos/one.jpg", "content");

        var plan = new DuplicationPlanner(pool, Headroom.None)
            .Plan([new DuplicationRule { Folder = "Photos", Mode = DuplicationMode.AllDrives }]);

        Assert.Equal(2, plan.CopiesToAdd);
        Assert.Equal(3, Assert.Single(plan.Rules).WantedCopies);
    }

    [Fact]
    public void More_copies_than_there_are_drives_asks_for_what_it_can_get()
    {
        using var pool = new TempPool();
        var a = pool.AddDrive("a").Part;
        pool.AddDrive("b");

        pool.WriteInto(a, "Photos/one.jpg", "content");

        var plan = new DuplicationPlanner(pool, Headroom.None).Plan([Rule("Photos", copies: 5)]);

        var status = Assert.Single(plan.Rules);
        Assert.Equal(5, status.RequestedCopies);
        Assert.Equal(2, status.WantedCopies);
        Assert.True(status.NeedsMoreDrives);
        Assert.Equal(1, plan.CopiesToAdd);

        // Asking for five on two drives is not met, and says so rather than looking satisfied.
        Assert.Single(plan.Shortfalls);
    }

    [Fact]
    public void A_rule_on_the_pool_root_covers_everything_in_it()
    {
        using var pool = new TempPool();
        var a = pool.AddDrive("a").Part;
        pool.AddDrive("b");

        pool.WriteInto(a, "one.txt", "content");
        pool.WriteInto(a, "Photos/two.jpg", "content");
        pool.WriteInto(a, "Deep/Deeper/three.bin", "content");

        var plan = new DuplicationPlanner(pool, Headroom.None).Plan([Rule(string.Empty)]);

        Assert.Equal(3, plan.CopiesToAdd);
    }

    [Fact]
    public void A_folder_rule_does_not_reach_outside_its_folder()
    {
        using var pool = new TempPool();
        var a = pool.AddDrive("a").Part;
        pool.AddDrive("b");

        pool.WriteInto(a, "Photos/inside.jpg", "content");
        pool.WriteInto(a, "Films/outside.mkv", "content");
        pool.WriteInto(a, "PhotosElsewhere/not-inside.jpg", "content");

        var plan = new DuplicationPlanner(pool, Headroom.None).Plan([Rule("Photos")]);

        var action = Assert.Single(plan.Actions);
        Assert.Equal(P("Photos/inside.jpg"), action.PoolPath);
    }

    [Fact]
    public void A_copy_that_no_longer_matches_is_written_again_from_the_newest()
    {
        using var pool = new TempPool();
        var a = pool.AddDrive("a").Part;
        var b = pool.AddDrive("b").Part;

        pool.WriteInto(a, "Photos/one.jpg", "new content", new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        pool.WriteInto(b, "Photos/one.jpg", "old", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var plan = new DuplicationPlanner(pool, Headroom.None).Plan([Rule("Photos")]);

        var action = Assert.Single(plan.Actions);
        Assert.Equal(DuplicationActionKind.RefreshCopy, action.Kind);
        Assert.Equal(a, action.SourcePartId);
        Assert.Equal(b, action.TargetPartId);

        // It is not counted as protected while a copy is out of date.
        Assert.Equal(0, Assert.Single(plan.Rules).ProtectedFiles);
    }

    [Fact]
    public void Copies_matching_in_size_and_time_are_not_rewritten_every_pass()
    {
        using var pool = new TempPool();
        var a = pool.AddDrive("a").Part;
        var b = pool.AddDrive("b").Part;

        // A second apart: FAT-family drives keep last-write times to two seconds, and an exact
        // comparison would copy every file on every pass for a pool with one in it.
        pool.WriteInto(a, "Photos/one.jpg", "content", new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc));
        pool.WriteInto(b, "Photos/one.jpg", "content", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.True(new DuplicationPlanner(pool, Headroom.None).Plan([Rule("Photos")]).IsEmpty);
    }

    [Fact]
    public void Content_verification_catches_a_copy_that_differs_without_looking_different()
    {
        using var pool = new TempPool();
        var a = pool.AddDrive("a").Part;
        var b = pool.AddDrive("b").Part;

        var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        pool.WriteInto(a, "Photos/one.jpg", "AAAA", stamp);
        pool.WriteInto(b, "Photos/one.jpg", "BBBB", stamp);

        // Same length, same timestamp, different bytes: only reading them finds it.
        Assert.True(new DuplicationPlanner(pool, Headroom.None).Plan([Rule("Photos")]).IsEmpty);

        var verified = new DuplicationPlanner(pool, Headroom.None)
            .Plan([new DuplicationRule { Folder = "Photos", Copies = 2, VerifyByContent = true }]);

        Assert.Equal(DuplicationActionKind.RefreshCopy, Assert.Single(verified.Actions).Kind);
    }

    [Fact]
    public void A_drive_the_rule_excludes_is_not_given_copies()
    {
        using var pool = new TempPool();
        var a = pool.AddDrive("a").Part;
        var b = pool.AddDrive("b").Part;
        var c = pool.AddDrive("c").Part;

        pool.WriteInto(a, "Photos/one.jpg", "content");

        var plan = new DuplicationPlanner(pool, Headroom.None).Plan(
        [
            new DuplicationRule
            {
                Folder = "Photos",
                Mode = DuplicationMode.AllDrives,
                OnlyOnParts = [a, b],
            },
        ]);

        var action = Assert.Single(plan.Actions);
        Assert.Equal(b, action.TargetPartId);
        Assert.NotEqual(c, action.TargetPartId);
    }

    [Fact]
    public void Surplus_copies_are_removed_down_to_the_rule_but_never_past_it()
    {
        using var pool = new TempPool();
        var a = pool.AddDrive("a").Part;
        var b = pool.AddDrive("b").Part;
        var c = pool.AddDrive("c").Part;

        var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        pool.WriteInto(a, "Photos/one.jpg", "content", stamp);
        pool.WriteInto(b, "Photos/one.jpg", "content", stamp);
        pool.WriteInto(c, "Photos/one.jpg", "content", stamp);

        var plan = new DuplicationPlanner(pool, Headroom.None).Plan([Rule("Photos", copies: 2)]);

        var removal = Assert.Single(plan.Actions);
        Assert.Equal(DuplicationActionKind.RemoveCopy, removal.Kind);

        // Two of the three survive, which is what the rule asked for.
        Assert.Equal(1, plan.CopiesToRemove);
    }

    [Fact]
    public void Surplus_copies_can_be_left_alone_entirely()
    {
        using var pool = new TempPool();
        var a = pool.AddDrive("a").Part;
        var b = pool.AddDrive("b").Part;
        var c = pool.AddDrive("c").Part;

        var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        pool.WriteInto(a, "Photos/one.jpg", "content", stamp);
        pool.WriteInto(b, "Photos/one.jpg", "content", stamp);
        pool.WriteInto(c, "Photos/one.jpg", "content", stamp);

        var plan = new DuplicationPlanner(pool, Headroom.None).Plan(
        [
            new DuplicationRule { Folder = "Photos", Copies = 2, Surplus = SurplusCopies.Keep },
        ]);

        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void The_last_copy_is_never_a_surplus_copy()
    {
        using var pool = new TempPool();
        var a = pool.AddDrive("a").Part;

        pool.WriteInto(a, "Photos/one.jpg", "content");

        // One drive, one copy, a rule it cannot meet: nothing may be deleted to tidy that up.
        var plan = new DuplicationPlanner(pool, Headroom.None).Plan([Rule("Photos")]);

        Assert.Equal(0, plan.CopiesToRemove);
        Assert.True(File.Exists(Path.Combine(pool.PartRoot(a), "Photos", "one.jpg")));
    }

    [Fact]
    public void A_disabled_rule_plans_nothing()
    {
        using var pool = new TempPool();
        var a = pool.AddDrive("a").Part;
        pool.AddDrive("b");

        pool.WriteInto(a, "Photos/one.jpg", "content");

        var plan = new DuplicationPlanner(pool, Headroom.None)
            .Plan([new DuplicationRule { Folder = "Photos", Enabled = false }]);

        Assert.True(plan.IsEmpty);
        Assert.False(Assert.Single(plan.Rules).Enabled);
    }

    [Fact]
    public void A_drive_without_room_is_not_promised_a_copy()
    {
        using var pool = new TempPool();
        var a = pool.AddDrive("a", freeBytes: 500_000_000).Part;
        pool.AddDrive("b", totalBytes: 1000, freeBytes: 0);

        pool.WriteInto(a, "Photos/one.jpg", "content");

        var plan = new DuplicationPlanner(pool, Headroom.None).Plan([Rule("Photos")]);

        Assert.Equal(0, plan.CopiesToAdd);
        Assert.Contains("room", Assert.Single(plan.Shortfalls).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void MergePools_own_bookkeeping_is_not_pool_content()
    {
        using var pool = new TempPool();
        var a = pool.AddDrive("a").Part;
        pool.AddDrive("b");

        // The marker file sits at the part root and is written by MergePool itself.
        Assert.True(File.Exists(Path.Combine(pool.PartRoot(a), "poolpart.json")));

        var plan = new DuplicationPlanner(pool, Headroom.None).Plan([new DuplicationRule { Folder = string.Empty }]);

        Assert.True(plan.IsEmpty);
        Assert.Equal(0, Assert.Single(plan.Rules).Files);
    }

    private static class Headroom
    {
        /// <summary>The fake drives in these tests are small; headroom would swallow them.</summary>
        public static DuplicationOptions None { get; } = new() { CopyHeadroomBytes = 0 };
    }
}

/// <summary>Running a plan: the copies have to actually land, whole, and without being noticed.</summary>
public sealed class DuplicatorTests
{
    private static DuplicationOptions Fast { get; } = new() { CopyHeadroomBytes = 0, MegabytesPerSecond = 1000 };

    private static DuplicationPlan PlanFor(TempPool pool, params DuplicationRule[] rules) =>
        new DuplicationPlanner(pool, Fast).Plan(rules);

    [Fact]
    public async Task A_copy_lands_on_the_other_drive_with_the_same_bytes_and_times()
    {
        using var pool = new TempPool();
        var a = pool.AddDrive("a").Part;
        var b = pool.AddDrive("b").Part;

        var stamp = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        pool.WriteInto(a, "Photos/one.jpg", "the only copy", stamp);

        var result = await new Duplicator(pool, Fast)
            .RunAsync(PlanFor(pool, new DuplicationRule { Folder = "Photos" }), CancellationToken.None);

        Assert.Equal(1, result.CopiesAdded);
        Assert.Empty(result.Problems);

        var copy = Path.Combine(pool.PartRoot(b), "Photos", "one.jpg");
        Assert.Equal("the only copy", File.ReadAllText(copy));

        // The copy keeps the original's last-write time, so it is not read as a newer version of
        // the file it is a copy of — and so the next pass does not copy it all over again.
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(copy));
        Assert.True(PlanFor(pool, new DuplicationRule { Folder = "Photos" }).IsEmpty);
    }

    [Fact]
    public async Task The_pool_never_shows_two_entries_for_a_duplicated_file()
    {
        using var pool = new TempPool();
        var a = pool.AddDrive("a").Part;
        pool.AddDrive("b");

        pool.WriteInto(a, "Photos/one.jpg", "content");

        await new Duplicator(pool, Fast)
            .RunAsync(PlanFor(pool, new DuplicationRule { Folder = "Photos" }), CancellationToken.None);

        var names = pool.View.EnumerateNames("Photos");
        Assert.Equal(["one.jpg"], names);
    }

    [Fact]
    public async Task The_work_folder_a_copy_passes_through_is_not_part_of_the_pool()
    {
        using var pool = new TempPool();
        var a = pool.AddDrive("a").Part;
        var b = pool.AddDrive("b").Part;

        pool.WriteInto(a, "one.txt", "content");

        await new Duplicator(pool, Fast)
            .RunAsync(PlanFor(pool, new DuplicationRule { Folder = string.Empty }), CancellationToken.None);

        // It exists on the disk, and the pool does not have it.
        Assert.True(Directory.Exists(Path.Combine(pool.PartRoot(b), ".mergepool-work")));
        Assert.DoesNotContain(".mergepool-work", pool.View.EnumerateNames(string.Empty));
    }

    [Fact]
    public async Task A_stale_copy_is_brought_back_in_line_with_the_newest()
    {
        using var pool = new TempPool();
        var a = pool.AddDrive("a").Part;
        var b = pool.AddDrive("b").Part;

        pool.WriteInto(a, "Photos/one.jpg", "the new content", new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        pool.WriteInto(b, "Photos/one.jpg", "old", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var result = await new Duplicator(pool, Fast)
            .RunAsync(PlanFor(pool, new DuplicationRule { Folder = "Photos" }), CancellationToken.None);

        Assert.Equal(1, result.CopiesRefreshed);
        Assert.Equal("the new content", File.ReadAllText(Path.Combine(pool.PartRoot(b), "Photos", "one.jpg")));
    }

    [Fact]
    public async Task A_surplus_copy_is_deleted_and_the_file_is_still_in_the_pool()
    {
        using var pool = new TempPool();
        var a = pool.AddDrive("a").Part;
        var b = pool.AddDrive("b").Part;
        var c = pool.AddDrive("c").Part;

        var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        pool.WriteInto(a, "Photos/one.jpg", "content", stamp);
        pool.WriteInto(b, "Photos/one.jpg", "content", stamp);
        pool.WriteInto(c, "Photos/one.jpg", "content", stamp);

        var result = await new Duplicator(pool, Fast)
            .RunAsync(PlanFor(pool, new DuplicationRule { Folder = "Photos", Copies = 2 }), CancellationToken.None);

        Assert.Equal(1, result.CopiesRemoved);
        Assert.Equal(2, pool.View.FindAllFiles("Photos/one.jpg").Count);
        Assert.True(pool.View.FileExists("Photos/one.jpg"));
    }

    [Fact]
    public async Task A_file_that_vanished_since_the_plan_was_made_is_passed_over()
    {
        using var pool = new TempPool();
        var a = pool.AddDrive("a").Part;
        pool.AddDrive("b");

        var host = pool.WriteInto(a, "Photos/one.jpg", "content");
        var plan = PlanFor(pool, new DuplicationRule { Folder = "Photos" });

        File.Delete(host);

        var result = await new Duplicator(pool, Fast).RunAsync(plan, CancellationToken.None);

        Assert.Equal(0, result.CopiesAdded);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public async Task Pausing_stops_the_run_where_it_is()
    {
        using var pool = new TempPool();
        var a = pool.AddDrive("a").Part;
        pool.AddDrive("b");

        pool.WriteInto(a, "Photos/one.jpg", "content");

        var duplicator = new Duplicator(pool, Fast);
        duplicator.Pause();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            duplicator.RunAsync(PlanFor(pool, new DuplicationRule { Folder = "Photos" }), cancellation.Token));

        Assert.True(duplicator.IsPaused);
    }

    [Fact]
    public async Task Every_copy_follows_a_rename()
    {
        using var pool = new TempPool();
        var a = pool.AddDrive("a").Part;
        var b = pool.AddDrive("b").Part;

        pool.WriteInto(a, "Photos/one.jpg", "content");

        await new Duplicator(pool, Fast)
            .RunAsync(PlanFor(pool, new DuplicationRule { Folder = "Photos" }), CancellationToken.None);

        pool.Operations.Rename("Photos/one.jpg", "Photos/renamed.jpg");

        // Moving only the copy the union reads would leave the other under the old name, and the
        // old name would simply reappear in the pool.
        Assert.Equal(["renamed.jpg"], pool.View.EnumerateNames("Photos"));
        Assert.Equal(2, pool.View.FindAllFiles("Photos/renamed.jpg").Count);
        Assert.False(File.Exists(Path.Combine(pool.PartRoot(a), "Photos", "one.jpg")));
        Assert.False(File.Exists(Path.Combine(pool.PartRoot(b), "Photos", "one.jpg")));
    }

    [Fact]
    public async Task Deleting_a_duplicated_file_deletes_every_copy()
    {
        using var pool = new TempPool();
        var a = pool.AddDrive("a").Part;
        var b = pool.AddDrive("b").Part;

        pool.WriteInto(a, "Photos/one.jpg", "content");

        await new Duplicator(pool, Fast)
            .RunAsync(PlanFor(pool, new DuplicationRule { Folder = "Photos" }), CancellationToken.None);

        pool.Operations.DeleteFile("Photos/one.jpg");

        Assert.False(File.Exists(Path.Combine(pool.PartRoot(a), "Photos", "one.jpg")));
        Assert.False(File.Exists(Path.Combine(pool.PartRoot(b), "Photos", "one.jpg")));
        Assert.False(pool.View.FileExists("Photos/one.jpg"));
    }

    [Fact]
    public async Task A_lost_drive_leaves_the_other_copy_readable()
    {
        using var pool = new TempPool();
        var a = pool.AddDrive("a").Part;
        var b = pool.AddDrive("b").Part;

        pool.WriteInto(a, "Photos/one.jpg", "irreplaceable");

        await new Duplicator(pool, Fast)
            .RunAsync(PlanFor(pool, new DuplicationRule { Folder = "Photos" }), CancellationToken.None);

        // The whole point of the feature, as a test: take a drive away and the file is still there.
        pool.Volumes.SetPresent(pool.Current.FindPart(a)!.Volume, present: false);

        Assert.True(pool.View.FileExists("Photos/one.jpg"));
        Assert.Equal("irreplaceable", File.ReadAllText(pool.View.FindFile("Photos/one.jpg")!.HostPath));
        Assert.Equal(b, pool.View.FindFile("Photos/one.jpg")!.Part.PartId);
    }
}
