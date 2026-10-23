using MergePool.Core.Sharing;
using Xunit;

namespace MergePool.Core.Tests;

/// <summary>
/// Deciding which SMB shares should exist. The dangerous half is removal: a share MergePool did
/// not create, pointing somewhere that is not a pool, must survive whatever its name is.
/// </summary>
public sealed class ShareReconcilerTests
{
    private static readonly IReadOnlySet<string> Nothing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlySet<string> Names(params string[] names) =>
        new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    private static DesiredShare Want(string name = "Media", string path = "P:\\") => new()
    {
        PoolId = Guid.NewGuid(),
        PoolName = "Media",
        ShareName = name,
        Path = path,
        ReadOnly = false,
        AllowedAccounts = ["MACHINE\\poolsvc"],
    };

    [Fact]
    public void A_mounted_pool_that_wants_a_share_gets_one()
    {
        var actions = ShareReconciler.Plan([Want()], [], Nothing, Names("P:\\"));

        var action = Assert.Single(actions);
        Assert.Equal(ShareActionKind.Create, action.Kind);
        Assert.Equal("Media", action.ShareName);
    }

    [Fact]
    public void A_share_that_is_already_right_is_left_alone()
    {
        var existing = new[] { new ExistingShare { ShareName = "Media", Path = "P:\\" } };

        Assert.Empty(ShareReconciler.Plan([Want()], existing, Names("Media"), Names("P:\\")));
    }

    [Theory]
    [InlineData("P:", "P:\\")]
    [InlineData("P:\\", "P:")]
    public void A_mount_point_spelled_either_way_is_the_same_share(string wanted, string existing)
    {
        // The configuration says "P:" and the SMB server reports "P:\". Treating those as different
        // would remove and recreate the share on every pass.
        var shares = new[] { new ExistingShare { ShareName = "Media", Path = existing } };

        Assert.Empty(ShareReconciler.Plan([Want(path: wanted)], shares, Names("Media"), Names(wanted)));
    }

    [Fact]
    public void A_share_whose_pool_is_no_longer_mounted_is_removed()
    {
        var existing = new[] { new ExistingShare { ShareName = "Media", Path = "P:\\" } };

        var action = Assert.Single(ShareReconciler.Plan([], existing, Names("Media"), Names("P:\\")));

        Assert.Equal(ShareActionKind.Remove, action.Kind);
        Assert.Equal("Media", action.ShareName);
    }

    [Fact]
    public void Somebody_elses_share_is_never_removed()
    {
        // The whole safety property. A share we did not create, pointing somewhere that is not one
        // of our mounts, is not ours to touch however much its name looks like one of ours.
        var existing = new[]
        {
            new ExistingShare { ShareName = "Media", Path = "D:\\SomeoneElsesMedia" },
            new ExistingShare { ShareName = "Backups", Path = "E:\\Backups" },
        };

        Assert.Empty(ShareReconciler.Plan([], existing, Nothing, Names("P:\\")));
    }

    [Fact]
    public void A_share_of_ours_pointing_at_the_wrong_place_is_moved()
    {
        // A drive letter changed under a share we created: remove the stale one, make the new one.
        var existing = new[] { new ExistingShare { ShareName = "Media", Path = "Q:\\" } };

        var actions = ShareReconciler.Plan([Want(path: "P:\\")], existing, Names("Media"), Names("P:\\", "Q:\\"));

        Assert.Equal(2, actions.Count);
        Assert.Equal(ShareActionKind.Remove, actions[0].Kind);
        Assert.Equal(ShareActionKind.Create, actions[1].Kind);
        Assert.Equal("P:\\", actions[1].Share!.Path);
    }

    [Fact]
    public void A_share_someone_else_owns_under_a_name_we_want_is_reported_not_hijacked()
    {
        // We want "Media"; something that is not ours already has that name. Taking it would be
        // removing a share we know nothing about, so nothing happens and the pool stays unshared.
        var existing = new[] { new ExistingShare { ShareName = "Media", Path = "D:\\NotOurs" } };

        Assert.Empty(ShareReconciler.Plan([Want()], existing, Nothing, Names("P:\\")));
    }

    [Fact]
    public void A_share_pointing_at_one_of_our_mounts_is_ours_even_after_a_restart()
    {
        // Nothing was created in this session — the service has just started — but the share points
        // at a pool of ours, so it is ours to clean up when the pool stops wanting it.
        var existing = new[] { new ExistingShare { ShareName = "Media", Path = "P:\\" } };

        var action = Assert.Single(ShareReconciler.Plan([], existing, Nothing, Names("P:\\")));

        Assert.Equal(ShareActionKind.Remove, action.Kind);
    }
}

/// <summary>What a share may be called, and when MergePool refuses to make one at all.</summary>
public sealed class PoolShareOptionsTests
{
    [Theory]
    [InlineData("Media", "Media")]
    [InlineData("  Media  ", "Media")]
    [InlineData("Media (2026)", "Media 2026")]
    [InlineData("My/Pool", "My-Pool")]
    [InlineData("a:b|c", "a-b-c")]
    [InlineData("...", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void A_share_name_is_reduced_to_something_windows_will_take(string input, string? expected) =>
        Assert.Equal(expected, PoolShareOptions.Sanitise(input));

    [Fact]
    public void A_name_too_long_for_a_share_is_cut_to_fit() =>
        Assert.Equal(80, PoolShareOptions.Sanitise(new string('a', 200))!.Length);

    [Fact]
    public void The_pool_name_is_used_when_no_share_name_is_given()
    {
        var options = new PoolShareOptions { Enabled = true, AllowedAccounts = ["poolsvc"] };

        Assert.Equal("Media", options.ShareNameFor("Media"));
        Assert.True(options.IsUsable("Media"));
    }

    [Fact]
    public void Sharing_is_off_until_someone_says_who_may_connect()
    {
        // Deliberate: a pool holds everything its owner has, and a share that defaulted to being
        // reachable — by anyone, or at all — would be a worse bug than any this release fixes.
        var fresh = new PoolShareOptions();

        Assert.False(fresh.Enabled);
        Assert.False(fresh.IsUsable("Media"));
        Assert.Null(fresh.Problem("Media"));

        var enabled = new PoolShareOptions { Enabled = true };

        Assert.False(enabled.IsUsable("Media"));
        Assert.Contains("No account", enabled.Problem("Media"));
    }

    [Fact]
    public void A_name_with_nothing_usable_in_it_is_refused_rather_than_guessed_at()
    {
        var options = new PoolShareOptions
        {
            Enabled = true,
            Name = "///",
            AllowedAccounts = ["poolsvc"],
        };

        Assert.Null(options.ShareNameFor("***"));
        Assert.Contains("share name", options.Problem("***"));
    }
}
