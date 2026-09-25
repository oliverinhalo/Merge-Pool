using MergePool.Core.Config;
using MergePool.Core.Placement;
using MergePool.Core.Rebalance;
using Xunit;

namespace MergePool.Core.Tests;

public sealed class RebalancerTests
{
    private static RebalanceOptions Options() => new()
    {
        Enabled = true,
        TriggerImbalance = 0.15,
        TargetImbalance = 0.05,
        MaxBytesPerSecond = 0, // no rate limit in tests
        MaxFileBytes = long.MaxValue,
    };

    [Fact]
    public void A_balanced_pool_plans_nothing()
    {
        using var pool = new TempPool();
        pool.AddDrive("A", totalBytes: 1000, freeBytes: 500);
        pool.AddDrive("B", totalBytes: 1000, freeBytes: 500);

        var plan = new Rebalancer(pool, Options()).Plan();

        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.ImbalanceBefore, 6);
    }

    [Fact]
    public void A_disabled_rebalancer_plans_nothing()
    {
        using var pool = new TempPool();
        var (_, a) = pool.AddDrive("A", totalBytes: 1000, freeBytes: 50);
        pool.AddDrive("B", totalBytes: 1000, freeBytes: 950);
        pool.WriteInto(a, "big.bin", new string('x', 200));

        var options = Options();
        options.Enabled = false;

        Assert.True(new Rebalancer(pool, options).Plan().IsEmpty);
    }

    [Fact]
    public void Files_are_planned_off_the_fullest_drive_largest_first()
    {
        using var pool = new TempPool();
        var (_, full) = pool.AddDrive("Full", totalBytes: 1000, freeBytes: 100);
        var (_, empty) = pool.AddDrive("Empty", totalBytes: 1000, freeBytes: 900);

        pool.WriteInto(full, "small.bin", new string('s', 50));
        pool.WriteInto(full, "large.bin", new string('l', 300));

        var plan = new Rebalancer(pool, Options()).Plan();

        Assert.NotEmpty(plan.Moves);
        Assert.Equal("large.bin", plan.Moves[0].PoolPath);
        Assert.Equal(full, plan.Moves[0].FromPartId);
        Assert.Equal(empty, plan.Moves[0].ToPartId);
        Assert.True(plan.ImbalanceAfter < plan.ImbalanceBefore);
    }

    [Fact]
    public void Running_a_plan_moves_the_file_whole_to_the_other_drive()
    {
        using var pool = new TempPool();
        var (_, full) = pool.AddDrive("Full", totalBytes: 1000, freeBytes: 100);
        var (_, empty) = pool.AddDrive("Empty", totalBytes: 1000, freeBytes: 900);
        pool.WriteInto(full, "Media/movie.mkv", new string('m', 300));

        var rebalancer = new Rebalancer(pool, Options());
        var result = rebalancer.Run(rebalancer.Plan());

        Assert.Equal(1, result.MovedCount);
        Assert.Equal(300, result.MovedBytes);

        var located = pool.View.FindAllFiles("Media/movie.mkv");
        Assert.Single(located);
        Assert.Equal(empty, located[0].Part.PartId);
        Assert.Equal(new string('m', 300), File.ReadAllText(located[0].HostPath));
        Assert.False(File.Exists(Path.Combine(pool.PartRoot(full), "Media", "movie.mkv")));
    }

    [Fact]
    public void A_plan_takes_several_files_and_never_the_same_one_twice()
    {
        using var pool = new TempPool();
        var (_, full) = pool.AddDrive("Full", totalBytes: 1000, freeBytes: 20);
        pool.AddDrive("Empty", totalBytes: 1000, freeBytes: 980);

        pool.WriteInto(full, "a.bin", new string('a', 300));
        pool.WriteInto(full, "b.bin", new string('b', 200));
        pool.WriteInto(full, "c.bin", new string('c', 100));

        var plan = new Rebalancer(pool, Options()).Plan();

        Assert.True(plan.Moves.Count > 1);
        Assert.Equal(plan.Moves.Count, plan.Moves.Select(m => m.PoolPath).Distinct().Count());

        // Largest first, so the imbalance closes in as few moves as possible.
        Assert.Equal("a.bin", plan.Moves[0].PoolPath);
        Assert.True(plan.Moves[0].Bytes >= plan.Moves[1].Bytes);
    }

    [Fact]
    public void Planning_stops_once_the_source_drive_has_nothing_left_to_give()
    {
        using var pool = new TempPool();
        var (_, full) = pool.AddDrive("Full", totalBytes: 1000, freeBytes: 10);
        pool.AddDrive("Empty", totalBytes: 1000, freeBytes: 990);
        pool.WriteInto(full, "only.bin", new string('o', 50));

        var plan = new Rebalancer(pool, Options()).Plan();

        // One movable file means one move, however far off balance the pool is.
        Assert.Single(plan.Moves);
    }

    [Fact]
    public void A_file_in_use_is_skipped_rather_than_moved()
    {
        using var pool = new TempPool();
        var (_, full) = pool.AddDrive("Full", totalBytes: 1000, freeBytes: 100);
        pool.AddDrive("Empty", totalBytes: 1000, freeBytes: 900);
        var host = pool.WriteInto(full, "busy.bin", new string('b', 300));

        var rebalancer = new Rebalancer(pool, Options());
        var plan = rebalancer.Plan();

        using (var _ = new FileStream(host, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = rebalancer.Run(plan);

            Assert.Equal(0, result.MovedCount);
            Assert.Equal(1, result.SkippedInUse);
        }

        Assert.True(File.Exists(host));
    }

    [Fact]
    public void The_pool_part_marker_is_never_rebalanced()
    {
        using var pool = new TempPool();
        pool.AddDrive("Full", totalBytes: 1000, freeBytes: 10);
        pool.AddDrive("Empty", totalBytes: 1000, freeBytes: 990);

        // The only file on the full drive is the part's own bookkeeping file.
        var plan = new Rebalancer(pool, Options()).Plan();

        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void A_throttled_destination_is_skipped()
    {
        using var pool = new TempPool();
        var (_, full) = pool.AddDrive("Full", totalBytes: 1000, freeBytes: 100);
        var (_, empty) = pool.AddDrive("Empty", totalBytes: 1000, freeBytes: 900);
        pool.WriteInto(full, "file.bin", new string('f', 300));

        var speeds = new StubSpeedSource();
        speeds.Throttled.Add(empty);

        var rebalancer = new Rebalancer(pool, Options(), relocator: null, speedSource: speeds);
        var result = rebalancer.Run(rebalancer.Plan());

        Assert.Equal(0, result.MovedCount);
        Assert.Equal(1, result.SkippedInUse);
    }

    [Fact]
    public void A_cancelled_run_stops_and_reports_it()
    {
        using var pool = new TempPool();
        var (_, full) = pool.AddDrive("Full", totalBytes: 1000, freeBytes: 100);
        pool.AddDrive("Empty", totalBytes: 1000, freeBytes: 900);
        pool.WriteInto(full, "a.bin", new string('a', 200));

        var rebalancer = new Rebalancer(pool, Options());
        var plan = rebalancer.Plan();

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = rebalancer.Run(plan, cancellation.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(0, result.MovedCount);
    }

    [Fact]
    public void A_paused_rebalancer_does_not_move_anything_until_resumed()
    {
        using var pool = new TempPool();
        var (_, full) = pool.AddDrive("Full", totalBytes: 1000, freeBytes: 100);
        pool.AddDrive("Empty", totalBytes: 1000, freeBytes: 900);
        pool.WriteInto(full, "a.bin", new string('a', 200));

        var rebalancer = new Rebalancer(pool, Options());
        var plan = rebalancer.Plan();
        rebalancer.Pause();
        Assert.True(rebalancer.IsPaused);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var result = rebalancer.Run(plan, cancellation.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(0, result.MovedCount);

        rebalancer.Resume();
        Assert.False(rebalancer.IsPaused);
        Assert.Equal(1, rebalancer.Run(plan).MovedCount);
    }

    [Fact]
    public void A_missing_drive_makes_the_plan_skip_rather_than_fail()
    {
        using var pool = new TempPool();
        var (fullVolume, full) = pool.AddDrive("Full", totalBytes: 1000, freeBytes: 100);
        pool.AddDrive("Empty", totalBytes: 1000, freeBytes: 900);
        pool.WriteInto(full, "a.bin", new string('a', 200));

        var rebalancer = new Rebalancer(pool, Options());
        var plan = rebalancer.Plan();

        pool.Volumes.SetPresent(fullVolume, present: false);
        var result = rebalancer.Run(plan);

        Assert.Equal(0, result.MovedCount);
        Assert.Equal(1, result.SkippedInUse);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public void Files_larger_than_the_cap_are_left_alone()
    {
        using var pool = new TempPool();
        var (_, full) = pool.AddDrive("Full", totalBytes: 1000, freeBytes: 100);
        pool.AddDrive("Empty", totalBytes: 1000, freeBytes: 900);
        pool.WriteInto(full, "huge.bin", new string('h', 300));

        var options = Options();
        options.MaxFileBytes = 100;

        Assert.True(new Rebalancer(pool, options).Plan().IsEmpty);
    }

    private sealed class StubSpeedSource : ISpeedFactorSource
    {
        public HashSet<Guid> Throttled { get; } = [];

        public double GetSpeedFactor(Guid partId) => 1.0;

        public bool IsThrottled(Guid partId) => Throttled.Contains(partId);
    }
}
