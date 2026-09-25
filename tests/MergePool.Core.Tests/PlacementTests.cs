using MergePool.Core.Config;
using MergePool.Core.Model;
using MergePool.Core.Placement;
using MergePool.Core.Volumes;
using Xunit;

namespace MergePool.Core.Tests;

public sealed class PlacementTests
{
    private static PoolPart Part(
        string name,
        long free,
        double speed = 1.0,
        bool throttled = false,
        PoolPartState state = PoolPartState.Online) => new()
    {
        PartId = DeterministicId(name),
        Volume = VolumeId.FromGuid(DeterministicId(name)),
        RootPath = "/parts/" + name,
        State = state,
        TotalBytes = free * 2,
        FreeBytes = free,
        SpeedFactor = speed,
        IsThrottled = throttled,
        Label = name,
    };

    private static Guid DeterministicId(string name)
    {
        Span<byte> bytes = stackalloc byte[16];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(name[i % name.Length] + i);
        }

        return new Guid(bytes);
    }

    private static PoolSnapshot Snapshot(params PoolPart[] parts) =>
        new() { PoolId = Guid.Empty, Parts = parts };

    private static PlacementOptions Options() => new() { MinFreeBytes = 0, WriteHeadroomBytes = 0 };

    [Fact]
    public void Picks_the_highest_free_times_speed_score()
    {
        var placement = new FreeSpaceSpeedPlacement(Options());
        var snapshot = Snapshot(Part("a", free: 1000, speed: 1.0), Part("b", free: 400, speed: 4.0));

        var decision = placement.Select(snapshot, new PlacementRequest { PoolPath = "x" });

        Assert.Equal(DeterministicId("b"), decision!.PartId);
    }

    [Fact]
    public void Weights_are_configurable()
    {
        var options = Options();
        options.SpeedWeight = 0; // ignore speed entirely
        var placement = new FreeSpaceSpeedPlacement(options);
        var snapshot = Snapshot(Part("a", free: 1000, speed: 1.0), Part("b", free: 400, speed: 4.0));

        var decision = placement.Select(snapshot, new PlacementRequest { PoolPath = "x" });

        Assert.Equal(DeterministicId("a"), decision!.PartId);
    }

    [Fact]
    public void Throttled_drives_are_skipped_for_new_writes()
    {
        var placement = new FreeSpaceSpeedPlacement(Options());
        var snapshot = Snapshot(Part("a", free: 10_000, throttled: true), Part("b", free: 100));

        var decision = placement.Select(snapshot, new PlacementRequest { PoolPath = "x" });

        Assert.Equal(DeterministicId("b"), decision!.PartId);
        Assert.False(decision.AllCandidatesThrottled);
    }

    [Fact]
    public void When_every_drive_is_throttled_the_best_one_is_still_used()
    {
        var placement = new FreeSpaceSpeedPlacement(Options());
        var snapshot = Snapshot(Part("a", free: 10_000, throttled: true), Part("b", free: 100, throttled: true));

        var decision = placement.Select(snapshot, new PlacementRequest { PoolPath = "x" });

        Assert.Equal(DeterministicId("a"), decision!.PartId);
        Assert.True(decision.AllCandidatesThrottled);
    }

    [Fact]
    public void Offline_and_read_only_drives_never_take_new_files()
    {
        var placement = new FreeSpaceSpeedPlacement(Options());
        var snapshot = Snapshot(
            Part("offline", free: 10_000, state: PoolPartState.Offline),
            Part("readonly", free: 10_000, state: PoolPartState.ReadOnly),
            Part("ok", free: 10));

        var decision = placement.Select(snapshot, new PlacementRequest { PoolPath = "x" });

        Assert.Equal(DeterministicId("ok"), decision!.PartId);
    }

    [Fact]
    public void A_file_that_still_fits_stays_where_it_is()
    {
        var placement = new FreeSpaceSpeedPlacement(Options());
        var snapshot = Snapshot(Part("a", free: 1_000_000), Part("b", free: 500));

        var decision = placement.Select(snapshot, new PlacementRequest
        {
            PoolPath = "x",
            EstimatedSize = 100,
            PreferredPart = DeterministicId("b"),
        });

        Assert.Equal(DeterministicId("b"), decision!.PartId);
        Assert.Equal("stay-on-current-drive", decision.Reason);
    }

    [Fact]
    public void A_file_that_outgrows_its_drive_is_placed_elsewhere()
    {
        var placement = new FreeSpaceSpeedPlacement(Options());
        var snapshot = Snapshot(Part("a", free: 1_000_000), Part("b", free: 500));

        var decision = placement.Select(snapshot, new PlacementRequest
        {
            PoolPath = "x",
            EstimatedSize = 10_000,
            PreferredPart = DeterministicId("b"),
        });

        Assert.Equal(DeterministicId("a"), decision!.PartId);
    }

    [Fact]
    public void Excluded_drives_are_not_considered()
    {
        var placement = new FreeSpaceSpeedPlacement(Options());
        var snapshot = Snapshot(Part("a", free: 10_000), Part("b", free: 100));

        var decision = placement.Select(snapshot, new PlacementRequest
        {
            PoolPath = "x",
            ExcludedParts = [DeterministicId("a")],
        });

        Assert.Equal(DeterministicId("b"), decision!.PartId);
    }

    [Fact]
    public void Reserved_free_space_is_respected()
    {
        var options = Options();
        options.MinFreeBytes = 1000;
        var placement = new FreeSpaceSpeedPlacement(options);
        var snapshot = Snapshot(Part("a", free: 1500));

        Assert.NotNull(placement.Select(snapshot, new PlacementRequest { PoolPath = "x", EstimatedSize = 400 }));
        Assert.Null(placement.Select(snapshot, new PlacementRequest { PoolPath = "x", EstimatedSize = 600 }));
    }

    [Fact]
    public void A_live_speed_source_overrides_the_static_factor()
    {
        var source = new StubSpeedSource
        {
            Factors = { [DeterministicId("a")] = 0.1, [DeterministicId("b")] = 10.0 },
        };
        var placement = new FreeSpaceSpeedPlacement(Options(), source);
        var snapshot = Snapshot(Part("a", free: 1000, speed: 10.0), Part("b", free: 900, speed: 0.1));

        var decision = placement.Select(snapshot, new PlacementRequest { PoolPath = "x" });

        Assert.Equal(DeterministicId("b"), decision!.PartId);
    }

    [Fact]
    public void An_empty_pool_places_nothing()
    {
        var placement = new FreeSpaceSpeedPlacement(Options());
        Assert.Null(placement.Select(Snapshot(), new PlacementRequest { PoolPath = "x" }));
    }

    private sealed class StubSpeedSource : ISpeedFactorSource
    {
        public Dictionary<Guid, double> Factors { get; } = [];

        public HashSet<Guid> Throttled { get; } = [];

        public double GetSpeedFactor(Guid partId) => Factors.TryGetValue(partId, out var value) ? value : 1.0;

        public bool IsThrottled(Guid partId) => Throttled.Contains(partId);
    }
}
