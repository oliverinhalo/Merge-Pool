using Xunit;

namespace MergePool.Update.Tests;

public sealed class UpgradeCoordinatorTests
{
    [Fact]
    public async Task A_healthy_upgrade_drains_stops_swaps_starts_and_checks()
    {
        using var install = new FakeInstall("1.0.0", "1.1.0");
        install.Link.PointAt("1.0.0");

        var result = await install.Coordinator().UpgradeAsync("1.1.0", CancellationToken.None);

        Assert.Equal(UpgradeOutcome.Succeeded, result.Outcome);
        Assert.Equal("1.1.0", result.ActiveVersion);
        Assert.Equal("1.0.0", result.PreviousVersion);
        Assert.Equal("1.1.0", install.Link.ReadCurrentVersion());

        Assert.Equal(1, install.Engine.DrainCount);
        Assert.Equal(1, install.Service.StopCount);
        Assert.Equal(1, install.Service.StartCount);
        Assert.Equal(1, install.Engine.HealthCheckCount);
    }

    [Fact]
    public async Task The_previous_version_is_left_on_disk_so_a_rollback_stays_possible()
    {
        using var install = new FakeInstall("1.0.0", "1.1.0");
        install.Link.PointAt("1.0.0");

        await install.Coordinator().UpgradeAsync("1.1.0", CancellationToken.None);

        Assert.True(install.Layout.VersionExists("1.0.0"));
        Assert.True(install.Layout.VersionExists("1.1.0"));
    }

    [Fact]
    public async Task An_unhealthy_new_version_is_rolled_back_and_verified()
    {
        using var install = new FakeInstall("1.0.0", "1.1.0");
        install.Link.PointAt("1.0.0");

        // The new version comes up but reports a problem; the old one is healthy.
        install.Engine.QueueHealth(
            new EngineHealth(false, ["Pool 'Media' is not mounted at P:."], "1.1.0"),
            new EngineHealth(true, [], "1.0.0"));

        var result = await install.Coordinator().UpgradeAsync("1.1.0", CancellationToken.None);

        Assert.Equal(UpgradeOutcome.RolledBack, result.Outcome);
        Assert.Equal("1.0.0", result.ActiveVersion);
        Assert.Equal("1.0.0", install.Link.ReadCurrentVersion());
        Assert.Contains("not mounted", result.Error, StringComparison.Ordinal);

        // Stopped and started twice: once for the upgrade, once to put the old version back.
        Assert.Equal(2, install.Service.StopCount);
        Assert.Equal(2, install.Service.StartCount);
    }

    [Fact]
    public async Task A_service_that_will_not_start_rolls_back()
    {
        using var install = new FakeInstall("1.0.0", "1.1.0");
        install.Link.PointAt("1.0.0");
        install.Service.StartFailure = new InvalidOperationException("The service did not start.");

        var result = await install.Coordinator().UpgradeAsync("1.1.0", CancellationToken.None);

        Assert.Equal(UpgradeOutcome.RolledBack, result.Outcome);
        Assert.Equal("1.0.0", install.Link.ReadCurrentVersion());
    }

    [Fact]
    public async Task A_service_that_never_answers_rolls_back()
    {
        using var install = new FakeInstall("1.0.0", "1.1.0");
        install.Link.PointAt("1.0.0");

        install.Engine.QueueHealth(null, new EngineHealth(true, [], "1.0.0"));

        var result = await install.Coordinator().UpgradeAsync("1.1.0", CancellationToken.None);

        Assert.Equal(UpgradeOutcome.RolledBack, result.Outcome);
        Assert.Equal("1.0.0", result.ActiveVersion);
        Assert.Contains("health check", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_failed_rollback_is_reported_as_needing_attention()
    {
        using var install = new FakeInstall("1.0.0", "1.1.0");
        install.Link.PointAt("1.0.0");

        install.Engine.QueueHealth(new EngineHealth(false, ["broken"], "1.1.0"), null);

        var result = await install.Coordinator().UpgradeAsync("1.1.0", CancellationToken.None);

        Assert.Equal(UpgradeOutcome.Failed, result.Outcome);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task A_first_install_with_nothing_to_roll_back_to_fails_clearly()
    {
        using var install = new FakeInstall("1.0.0");
        install.Engine.QueueHealth(new EngineHealth(false, ["broken"], "1.0.0"));

        var result = await install.Coordinator().UpgradeAsync("1.0.0", CancellationToken.None);

        Assert.Equal(UpgradeOutcome.Failed, result.Outcome);
        Assert.Contains(result.Log, line => line.Contains("no previous version", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Upgrading_to_the_version_already_running_does_nothing()
    {
        using var install = new FakeInstall("1.0.0");
        install.Link.PointAt("1.0.0");

        var result = await install.Coordinator().UpgradeAsync("1.0.0", CancellationToken.None);

        Assert.Equal(UpgradeOutcome.AlreadyCurrent, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.Equal(0, install.Service.StopCount);
    }

    [Fact]
    public async Task An_uninstalled_target_version_is_refused_before_anything_is_touched()
    {
        using var install = new FakeInstall("1.0.0");
        install.Link.PointAt("1.0.0");

        var result = await install.Coordinator().UpgradeAsync("9.9.9", CancellationToken.None);

        Assert.Equal(UpgradeOutcome.Failed, result.Outcome);
        Assert.Equal("1.0.0", install.Link.ReadCurrentVersion());
        Assert.Equal(0, install.Service.StopCount);
        Assert.Equal(0, install.Engine.DrainCount);
    }

    [Fact]
    public async Task An_unreachable_engine_does_not_block_the_upgrade_by_default()
    {
        using var install = new FakeInstall("1.0.0", "1.1.0");
        install.Link.PointAt("1.0.0");
        install.Engine.DrainSucceeds = false;

        var result = await install.Coordinator().UpgradeAsync("1.1.0", CancellationToken.None);

        Assert.Equal(UpgradeOutcome.Succeeded, result.Outcome);
        Assert.Contains(result.Log, line => line.Contains("not reachable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Refusing_to_upgrade_without_a_clean_drain_is_configurable()
    {
        using var install = new FakeInstall("1.0.0", "1.1.0");
        install.Link.PointAt("1.0.0");
        install.Engine.DrainSucceeds = false;

        var coordinator = install.Coordinator(new UpgradeOptions { AllowDrainFailure = false });
        var result = await coordinator.UpgradeAsync("1.1.0", CancellationToken.None);

        Assert.Equal(UpgradeOutcome.Failed, result.Outcome);
        Assert.Equal("1.0.0", install.Link.ReadCurrentVersion());
        Assert.Equal(0, install.Service.StopCount);
    }

    [Fact]
    public async Task Old_versions_are_pruned_but_the_rollback_target_is_kept()
    {
        using var install = new FakeInstall("1.0.0", "1.1.0", "1.2.0", "1.3.0");
        install.Link.PointAt("1.2.0");

        await install.Coordinator(new UpgradeOptions { KeepVersions = 2 })
            .UpgradeAsync("1.3.0", CancellationToken.None);

        Assert.True(install.Layout.VersionExists("1.3.0"));
        Assert.True(install.Layout.VersionExists("1.2.0"));
        Assert.False(install.Layout.VersionExists("1.0.0"));
    }
}

public sealed class InstallLayoutTests
{
    [Fact]
    public void Installed_versions_come_back_newest_first()
    {
        using var install = new FakeInstall("1.0.0", "1.10.0", "1.2.0");

        Assert.Equal(["1.10.0", "1.2.0", "1.0.0"], install.Layout.InstalledVersions());
    }

    [Fact]
    public void The_previous_version_is_the_newest_that_is_not_the_active_one()
    {
        using var install = new FakeInstall("1.0.0", "1.1.0", "1.2.0");

        Assert.Equal("1.2.0", install.Layout.PreviousVersion("1.1.0"));
        Assert.Equal("1.1.0", install.Layout.PreviousVersion("1.2.0"));
    }

    [Fact]
    public void An_empty_install_has_no_versions()
    {
        using var install = new FakeInstall();

        Assert.Empty(install.Layout.InstalledVersions());
        Assert.Null(install.Layout.PreviousVersion("1.0.0"));
    }

    [Fact]
    public void Pruning_always_keeps_the_active_version_even_if_it_is_old()
    {
        using var install = new FakeInstall("1.0.0", "2.0.0", "3.0.0", "4.0.0");

        install.Layout.PruneOldVersions("1.0.0", keep: 1);

        Assert.True(install.Layout.VersionExists("1.0.0"));
        Assert.True(install.Layout.VersionExists("4.0.0"));
        Assert.False(install.Layout.VersionExists("2.0.0"));
    }

    [Fact]
    public void Version_paths_sit_side_by_side_under_the_install_root()
    {
        using var install = new FakeInstall("1.0.0");

        Assert.Equal(
            Path.Combine(install.Root, "versions", "1.0.0"),
            install.Layout.VersionPath("1.0.0"));

        Assert.Equal(Path.Combine(install.Root, "current"), install.Layout.CurrentPath);
    }
}
