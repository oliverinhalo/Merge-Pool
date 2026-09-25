using MergePool.Core.Model;
using MergePool.Core.Volumes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MergePool.Core.Tests;

public sealed class PartUsageMeterTests : IDisposable
{
    private readonly TempPool _pool = new();

    [Fact]
    public void A_part_reports_what_the_pool_holds_not_what_the_drive_holds()
    {
        var (_, part) = _pool.AddDrive("One", totalBytes: 1000, freeBytes: 400);
        var partRoot = _pool.PartRoot(part);

        File.WriteAllBytes(Path.Combine(partRoot, "pooled.bin"), new byte[120]);

        var meter = new PartUsageMeter();
        var usage = meter.Measure(_pool.Current.FindPart(part)!);

        // 600 bytes of the drive are used, but only 120 of those are the pool's.
        Assert.Equal(120, usage.Bytes);
        Assert.Equal(1, usage.FileCount);
        Assert.True(usage.IsKnown);
    }

    [Fact]
    public void The_part_marker_is_not_counted_as_pooled_content()
    {
        var (_, part) = _pool.AddDrive("One");
        var meter = new PartUsageMeter();

        // The part folder holds only its own marker file.
        var usage = meter.Measure(_pool.Current.FindPart(part)!);

        Assert.Equal(0, usage.Bytes);
        Assert.Equal(0, usage.FileCount);
    }

    [Fact]
    public void Nested_files_are_included()
    {
        var (_, part) = _pool.AddDrive("One");
        var partRoot = _pool.PartRoot(part);
        Directory.CreateDirectory(Path.Combine(partRoot, "Movies", "2026"));
        File.WriteAllBytes(Path.Combine(partRoot, "Movies", "2026", "film.mkv"), new byte[64]);
        File.WriteAllBytes(Path.Combine(partRoot, "Movies", "poster.jpg"), new byte[16]);

        var usage = new PartUsageMeter().Measure(_pool.Current.FindPart(part)!);

        Assert.Equal(80, usage.Bytes);
        Assert.Equal(2, usage.FileCount);
    }

    [Fact]
    public void A_measurement_is_reused_until_it_expires()
    {
        var (_, part) = _pool.AddDrive("One");
        var partRoot = _pool.PartRoot(part);
        File.WriteAllBytes(Path.Combine(partRoot, "first.bin"), new byte[10]);

        var clock = new FakeTimeProvider();
        var meter = new PartUsageMeter(TimeSpan.FromSeconds(60), clock);

        meter.Refresh(_pool.Current);
        Assert.Equal(10, meter.Get(part).Bytes);

        File.WriteAllBytes(Path.Combine(partRoot, "second.bin"), new byte[90]);

        // Within the TTL the cached figure stands: walking a part is not free.
        clock.Advance(TimeSpan.FromSeconds(30));
        meter.Refresh(_pool.Current);
        Assert.Equal(10, meter.Get(part).Bytes);

        clock.Advance(TimeSpan.FromSeconds(31));
        meter.Refresh(_pool.Current);
        Assert.Equal(100, meter.Get(part).Bytes);
    }

    [Fact]
    public void Invalidating_a_part_forces_the_next_refresh_to_re_measure()
    {
        var (_, part) = _pool.AddDrive("One");
        var partRoot = _pool.PartRoot(part);
        File.WriteAllBytes(Path.Combine(partRoot, "first.bin"), new byte[10]);

        var clock = new FakeTimeProvider();
        var meter = new PartUsageMeter(TimeSpan.FromHours(1), clock);
        meter.Refresh(_pool.Current);

        File.WriteAllBytes(Path.Combine(partRoot, "second.bin"), new byte[5]);
        meter.Invalidate(part);
        meter.Refresh(_pool.Current);

        Assert.Equal(15, meter.Get(part).Bytes);
    }

    [Fact]
    public void An_unmeasured_part_reads_as_unknown_rather_than_empty()
    {
        var (_, part) = _pool.AddDrive("One");

        var usage = new PartUsageMeter().Get(part);

        Assert.False(usage.IsKnown);
        Assert.Equal(0, usage.Bytes);
    }

    [Fact]
    public void The_pool_capacity_is_its_own_content_plus_free_space()
    {
        var (_, first) = _pool.AddDrive("One", totalBytes: 1000, freeBytes: 200);
        var (_, second) = _pool.AddDrive("Two", totalBytes: 1000, freeBytes: 900);

        File.WriteAllBytes(Path.Combine(_pool.PartRoot(first), "a.bin"), new byte[300]);
        File.WriteAllBytes(Path.Combine(_pool.PartRoot(second), "b.bin"), new byte[50]);

        var meter = new PartUsageMeter();
        var manager = new PoolPartManager(_pool.Volumes, timeProvider: null, usageMeter: meter);
        meter.Refresh(manager.Resolve(_pool.Definition));

        var snapshot = manager.Resolve(_pool.Definition);

        Assert.True(snapshot.IsUsageMeasured);
        Assert.Equal(350, snapshot.PoolUsedBytes);

        // Drive One holds 800 bytes but only 300 of them are pooled, so the pool's share of that
        // drive is 300 + 200 free. Two contributes 50 + 900.
        Assert.Equal(1450, snapshot.PoolCapacityBytes);
        Assert.Equal(2000, snapshot.TotalBytes);

        // 500 on One and 50 on Two belong to something other than the pool.
        Assert.Equal(550, snapshot.ForeignBytes);
    }

    public void Dispose() => _pool.Dispose();
}
