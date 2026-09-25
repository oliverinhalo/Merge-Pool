using System.Globalization;
using MergePool.Core.Config;
using MergePool.Update;

namespace MergePool.Engine;

public enum UpdateStage
{
    Idle = 0,
    Checking = 1,
    Downloading = 2,

    /// <summary>Downloaded and unpacked into its own version directory, waiting to be made current.</summary>
    Staged = 3,

    /// <summary>The handoff has been launched: the service is about to be restarted on the new version.</summary>
    Applying = 4,
    Failed = 5,

    /// <summary>The release feed is unreachable, or this build has no way to apply an update.</summary>
    Unavailable = 6,
}

public sealed record UpdateState
{
    public required string InstalledVersion { get; init; }

    public ReleaseInfo? Available { get; init; }

    public UpdateStage Stage { get; init; } = UpdateStage.Idle;

    public double Progress { get; init; }

    public DateTimeOffset? LastCheckedUtc { get; init; }

    public string? LastError { get; init; }

    public IReadOnlyList<string> InstalledVersions { get; init; } = Array.Empty<string>();

    public bool UpdateAvailable => Available is not null && Available.IsNewerThan(InstalledVersion);
}

/// <summary>
/// Hands a staged version over to something that can restart the service. The service cannot stop
/// itself and carry on running, so the swap is done by a separate process.
/// </summary>
public interface IUpgradeLauncher
{
    /// <summary>
    /// Starts the updater for a staged version and returns without waiting: the service is about to
    /// be stopped by the process just launched.
    /// </summary>
    bool Launch(StagedRelease release, string installRoot, string serviceName, string pipeName);
}

/// <summary>
/// Keeps MergePool up to date on its own: checks the release feed on a timer, downloads a newer
/// version into its own directory, and hands off to the updater to make it current.
/// </summary>
/// <remarks>
/// An update never removes data. The only things it writes are a new <c>versions\{version}</c>
/// directory and, at the very end, the <c>current</c> link. Pool parts, the files inside them and
/// <c>config.json</c> are not touched at any point, and a version that fails its health check is
/// rolled back by <see cref="UpgradeCoordinator"/> to the one that was working.
/// </remarks>
public sealed class AutoUpdateService(
    InstallLayout layout,
    IReleaseFeed feed,
    ReleaseStager stager,
    IUpgradeLauncher launcher,
    string installedVersion,
    TimeProvider? timeProvider = null)
{
    private readonly InstallLayout _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    private readonly IReleaseFeed _feed = feed ?? throw new ArgumentNullException(nameof(feed));
    private readonly ReleaseStager _stager = stager ?? throw new ArgumentNullException(nameof(stager));
    private readonly IUpgradeLauncher _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly object _gate = new();

    private UpdateState _state = new() { InstalledVersion = installedVersion };
    private StagedRelease? _staged;

    public string ServiceName { get; init; } = "MergePool";

    public string PipeName { get; init; } = "MergePool.Engine";

    public UpdateState State
    {
        get
        {
            lock (_gate)
            {
                return _state with { InstalledVersions = _layout.InstalledVersions() };
            }
        }
    }

    /// <summary>
    /// One pass of the timer: checks if it is time, checks the feed, and installs when told to.
    /// Never throws — an unreachable feed is reported through <see cref="State"/>.
    /// </summary>
    public async Task TickAsync(
        UpdateOptions options,
        Action<UpdateOptions> persist,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(persist);

        if (!options.AutomaticChecks || !IsDue(options))
        {
            return;
        }

        var release = await CheckAsync(options, cancellationToken).ConfigureAwait(false);

        options.LastCheckedUtc = _timeProvider.GetUtcNow();
        persist(options);

        if (release is null || !options.AutomaticInstall)
        {
            return;
        }

        if (string.Equals(release.Version, options.SkipVersion, StringComparison.OrdinalIgnoreCase))
        {
            // A version that already failed to install is not retried on every tick.
            return;
        }

        var applied = await ApplyAsync(cancellationToken).ConfigureAwait(false);
        if (!applied)
        {
            options.SkipVersion = release.Version;
            persist(options);
        }
    }

    /// <summary>Asks the feed what the newest release is. Returns it only when it is newer than this build.</summary>
    public async Task<ReleaseInfo?> CheckAsync(UpdateOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Update(state => state with { Stage = UpdateStage.Checking, LastError = null });

            var latest = await _feed.GetLatestAsync(cancellationToken).ConfigureAwait(false);
            var installed = State.InstalledVersion;
            var newer = latest is not null && latest.IsNewerThan(installed) ? latest : null;

            Update(state => state with
            {
                Available = newer,
                Stage = UpdateStage.Idle,
                Progress = 0,
                LastCheckedUtc = _timeProvider.GetUtcNow(),
            });

            return newer;
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException
            or InvalidDataException
            or System.Text.Json.JsonException
            or NotSupportedException)
        {
            Update(state => state with
            {
                Stage = UpdateStage.Unavailable,
                LastError = $"Could not reach the update feed: {exception.Message}",
                LastCheckedUtc = _timeProvider.GetUtcNow(),
            });

            return null;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// Downloads the known release, unpacks it beside the running version and launches the handoff.
    /// Returns false and records why if any step fails; the running version keeps serving either way.
    /// </summary>
    public async Task<bool> ApplyAsync(CancellationToken cancellationToken)
    {
        var release = State.Available;
        if (release is null)
        {
            Update(state => state with { LastError = "There is no newer version to install." });
            return false;
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Update(state => state with { Stage = UpdateStage.Downloading, Progress = 0, LastError = null });

            var progress = new Progress<double>(fraction =>
                Update(state => state with { Progress = fraction }));

            var staged = await _stager.StageAsync(release, progress, cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                _staged = staged;
            }

            Update(state => state with { Stage = UpdateStage.Staged, Progress = 1 });

            // From here the running process is about to be stopped by the updater it launches. The
            // files it is running from are left in place, so a failed swap rolls straight back.
            if (!_launcher.Launch(staged, _layout.InstallRoot, ServiceName, PipeName))
            {
                Update(state => state with
                {
                    Stage = UpdateStage.Failed,
                    LastError = string.Create(
                        CultureInfo.InvariantCulture,
                        $"Version {staged.Version} is downloaded and ready, but the updater could not be started. "
                        + $"It can be finished by running \"{staged.UpdaterPath}\" --version {staged.Version}."),
                });

                return false;
            }

            Update(state => state with { Stage = UpdateStage.Applying });
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException
            or IOException
            or InvalidDataException
            or UnauthorizedAccessException)
        {
            Update(state => state with
            {
                Stage = UpdateStage.Failed,
                LastError = $"The update could not be installed: {exception.Message}",
            });

            return false;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private bool IsDue(UpdateOptions options)
    {
        if (options.LastCheckedUtc is not { } last)
        {
            return true;
        }

        var interval = TimeSpan.FromHours(Math.Max(0.25, options.CheckIntervalHours));
        return _timeProvider.GetUtcNow() - last >= interval;
    }

    private void Update(Func<UpdateState, UpdateState> change)
    {
        lock (_gate)
        {
            _state = change(_state);
        }
    }
}
