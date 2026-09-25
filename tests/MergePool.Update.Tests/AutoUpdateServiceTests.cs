using System.IO.Compression;
using System.Net;
using MergePool.Core.Config;
using MergePool.Engine;
using MergePool.Update;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MergePool.Update.Tests;

/// <summary>Records what it was asked to launch instead of restarting anything.</summary>
internal sealed class FakeLauncher : IUpgradeLauncher
{
    public List<StagedRelease> Launched { get; } = [];

    public bool Succeeds { get; set; } = true;

    public bool Launch(StagedRelease release, string installRoot, string serviceName, string pipeName)
    {
        Launched.Add(release);
        return Succeeds;
    }
}

internal sealed class FakeFeed(ReleaseInfo? release = null) : IReleaseFeed
{
    public int Calls { get; private set; }

    public ReleaseInfo? Release { get; set; } = release;

    public Exception? Throws { get; set; }

    public Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Throws is not null ? Task.FromException<ReleaseInfo?>(Throws) : Task.FromResult(Release);
    }
}

public sealed class AutoUpdateServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mergepool-autoupdate", Guid.NewGuid().ToString("N"));

    private readonly InstallLayout _layout;
    private readonly FakeTimeProvider _clock = new();

    public AutoUpdateServiceTests()
    {
        Directory.CreateDirectory(_root);
        _layout = new InstallLayout(Path.Combine(_root, "install"));

        // The version that is already running, so an update has something to replace.
        var running = _layout.VersionPath("0.2.0");
        Directory.CreateDirectory(running);
        File.WriteAllText(Path.Combine(running, "MergePool.Service.exe"), "the running service");
    }

    private byte[] Package()
    {
        var staging = Path.Combine(_root, "staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "MergePool.Service.exe"), "new service");
        File.WriteAllText(Path.Combine(staging, "MergePool.Updater.exe"), "new updater");

        var zip = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        ZipFile.CreateFromDirectory(staging, zip);
        var bytes = File.ReadAllBytes(zip);

        Directory.Delete(staging, recursive: true);
        File.Delete(zip);
        return bytes;
    }

    private AutoUpdateService Build(FakeFeed feed, FakeLauncher launcher, byte[]? payload = null)
    {
        var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload ?? Package()),
        }));

        return new AutoUpdateService(
            _layout,
            feed,
            new ReleaseStager(_layout, http),
            launcher,
            installedVersion: "0.2.0",
            _clock);
    }

    private static ReleaseInfo Release(string version = "0.3.0") => new()
    {
        Version = version,
        PackageUrl = new Uri("https://example.invalid/package.zip"),
        Notes = "Notes",
    };

    [Fact]
    public async Task A_newer_release_is_reported_as_available()
    {
        var service = Build(new FakeFeed(Release()), new FakeLauncher());

        var found = await service.CheckAsync(new UpdateOptions(), CancellationToken.None);

        Assert.NotNull(found);
        Assert.True(service.State.UpdateAvailable);
        Assert.Equal("0.3.0", service.State.Available!.Version);
    }

    [Fact]
    public async Task The_version_already_running_is_not_offered_as_an_update()
    {
        var service = Build(new FakeFeed(Release("0.2.0")), new FakeLauncher());

        Assert.Null(await service.CheckAsync(new UpdateOptions(), CancellationToken.None));
        Assert.False(service.State.UpdateAvailable);
    }

    [Fact]
    public async Task An_unreachable_feed_is_reported_rather_than_thrown()
    {
        var feed = new FakeFeed(Release()) { Throws = new HttpRequestException("no route to host") };
        var service = Build(feed, new FakeLauncher());

        Assert.Null(await service.CheckAsync(new UpdateOptions(), CancellationToken.None));

        Assert.Equal(UpdateStage.Unavailable, service.State.Stage);
        Assert.Contains("no route to host", service.State.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Applying_stages_the_version_and_hands_off_without_touching_the_running_one()
    {
        var launcher = new FakeLauncher();
        var service = Build(new FakeFeed(Release()), launcher);

        await service.CheckAsync(new UpdateOptions(), CancellationToken.None);
        Assert.True(await service.ApplyAsync(CancellationToken.None));

        var staged = Assert.Single(launcher.Launched);
        Assert.Equal("0.3.0", staged.Version);
        Assert.True(File.Exists(Path.Combine(_layout.VersionPath("0.3.0"), "MergePool.Service.exe")));
        Assert.Equal(UpdateStage.Applying, service.State.Stage);

        // The version currently serving is not written to at any point.
        Assert.Equal(
            "the running service",
            File.ReadAllText(Path.Combine(_layout.VersionPath("0.2.0"), "MergePool.Service.exe")));
    }

    [Fact]
    public async Task A_handoff_that_cannot_start_says_how_to_finish_it_by_hand()
    {
        var launcher = new FakeLauncher { Succeeds = false };
        var service = Build(new FakeFeed(Release()), launcher);

        await service.CheckAsync(new UpdateOptions(), CancellationToken.None);

        Assert.False(await service.ApplyAsync(CancellationToken.None));
        Assert.Equal(UpdateStage.Failed, service.State.Stage);
        Assert.Contains("--version 0.3.0", service.State.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Applying_with_nothing_to_install_is_refused()
    {
        var service = Build(new FakeFeed(), new FakeLauncher());

        Assert.False(await service.ApplyAsync(CancellationToken.None));
        Assert.DoesNotContain(_layout.InstalledVersions(), version => version != "0.2.0");
    }

    [Fact]
    public async Task The_timer_only_checks_once_per_interval()
    {
        var feed = new FakeFeed(Release());
        var service = Build(feed, new FakeLauncher());
        var options = new UpdateOptions { AutomaticInstall = false, CheckIntervalHours = 6 };

        await service.TickAsync(options, _ => { }, CancellationToken.None);
        Assert.Equal(1, feed.Calls);

        _clock.Advance(TimeSpan.FromHours(1));
        await service.TickAsync(options, _ => { }, CancellationToken.None);
        Assert.Equal(1, feed.Calls);

        _clock.Advance(TimeSpan.FromHours(6));
        await service.TickAsync(options, _ => { }, CancellationToken.None);
        Assert.Equal(2, feed.Calls);
    }

    [Fact]
    public async Task Automatic_checks_can_be_turned_off_entirely()
    {
        var feed = new FakeFeed(Release());
        var service = Build(feed, new FakeLauncher());

        await service.TickAsync(new UpdateOptions { AutomaticChecks = false }, _ => { }, CancellationToken.None);

        Assert.Equal(0, feed.Calls);
    }

    [Fact]
    public async Task Checking_without_installing_finds_the_update_but_leaves_it_alone()
    {
        var launcher = new FakeLauncher();
        var service = Build(new FakeFeed(Release()), launcher);

        await service.TickAsync(
            new UpdateOptions { AutomaticInstall = false }, _ => { }, CancellationToken.None);

        Assert.True(service.State.UpdateAvailable);
        Assert.Empty(launcher.Launched);
        Assert.False(Directory.Exists(_layout.VersionPath("0.3.0")));
    }

    [Fact]
    public async Task A_version_that_fails_to_install_is_not_retried_on_every_tick()
    {
        var feed = new FakeFeed(Release());
        var launcher = new FakeLauncher { Succeeds = false };
        var service = Build(feed, launcher);

        var options = new UpdateOptions();
        var saved = new List<string?>();

        await service.TickAsync(options, o => saved.Add(o.SkipVersion), CancellationToken.None);
        Assert.Single(launcher.Launched);
        Assert.Contains("0.3.0", saved);

        _clock.Advance(TimeSpan.FromDays(1));
        await service.TickAsync(options, o => saved.Add(o.SkipVersion), CancellationToken.None);

        // Checked again, but not installed again.
        Assert.Equal(2, feed.Calls);
        Assert.Single(launcher.Launched);
    }

    [Fact]
    public async Task The_last_check_time_is_recorded_so_a_restart_does_not_re_check_immediately()
    {
        var service = Build(new FakeFeed(Release()), new FakeLauncher());
        var options = new UpdateOptions { AutomaticInstall = false };
        var persisted = 0;

        await service.TickAsync(options, _ => persisted++, CancellationToken.None);

        Assert.Equal(_clock.GetUtcNow(), options.LastCheckedUtc);
        Assert.True(persisted > 0);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
