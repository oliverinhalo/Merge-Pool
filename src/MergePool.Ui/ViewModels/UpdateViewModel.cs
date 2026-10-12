using System.Globalization;
using System.IO;
using System.Reflection;
using MergePool.Core.Update;
using MergePool.Ipc.Protocol;
using MergePool.Ui.Services;
using MergePool.Ui.ViewModels.Settings;

namespace MergePool.Ui.ViewModels;

/// <summary>
/// The update panel and the banner above the window. Everything here is the service's doing: the
/// window only asks and reports, because the service is the only thing that can restart itself.
/// </summary>
public sealed class UpdateViewModel : ViewModelBase
{
    private readonly ServiceConnection _service;
    private readonly Func<CancellationToken> _token;

    private UpdateStatusResult? _status;
    private string? _message;
    private bool _busy;
    private bool _announcedStale;
    private bool _showAdvanced;

    public UpdateViewModel(ServiceConnection service, Func<CancellationToken> token)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _token = token ?? throw new ArgumentNullException(nameof(token));

        Advanced = BuildAdvanced();
        Advanced.ApplyAdvanced(advanced: false);

        CheckCommand = new AsyncCommand(CheckAsync, () => !_busy && IsSupported);
        InstallCommand = new AsyncCommand(InstallAsync, () => !_busy && UpdateAvailable);
        RestartWindowCommand = new AsyncCommand(RestartWindowAsync, () => WindowIsStale);
    }

    public AsyncCommand CheckCommand { get; }

    /// <summary>
    /// How updating behaves, beyond the two checkboxes: how often the release feed is looked at,
    /// and whether pre-releases count. Shown only when Advanced is on, because the defaults are
    /// what a machine that is being relied on should be running.
    /// </summary>
    public SettingGroup Advanced { get; }

    /// <summary>Set from the window's Advanced toggle.</summary>
    public bool ShowAdvanced
    {
        get => _showAdvanced;
        set
        {
            if (Set(ref _showAdvanced, value))
            {
                Advanced.ApplyAdvanced(value);
            }
        }
    }

    public AsyncCommand InstallCommand { get; }

    /// <summary>Closes this window and opens the version the service has moved on to.</summary>
    public AsyncCommand RestartWindowCommand { get; }

    /// <summary>
    /// Set by the application: opens the given executable and closes this window. Separate from the
    /// view model so that deciding to restart can be tested without starting a process.
    /// </summary>
    public Func<string, bool>? RelaunchWindow { get; set; }

    public bool IsSupported => _service.Supports(Capabilities.AutoUpdate);

    public string InstalledVersion => _status?.InstalledVersion ?? "—";

    public string? AvailableVersion => _status?.AvailableVersion;

    public bool UpdateAvailable => _status?.UpdateAvailable == true;

    public bool IsInstalling => _status?.Stage is "Downloading" or "Staged" or "Applying";

    public double Progress => _status?.Progress ?? 0;

    public string? ReleaseNotes => _status?.ReleaseNotes;

    public string? ReleaseUrl => _status?.ReleaseUrl;

    /// <summary>Only shown when something is genuinely wrong, not merely "no update right now".</summary>
    public string? Problem => _status?.Stage is "Failed" ? _status.LastError : null;

    public bool AutomaticChecks
    {
        get => _status?.AutomaticChecks ?? true;
        set => _ = ApplySettingAsync(new UpdateSettingsDto { AutomaticChecks = value });
    }

    public bool AutomaticInstall
    {
        get => _status?.AutomaticInstall ?? true;
        set => _ = ApplySettingAsync(new UpdateSettingsDto { AutomaticInstall = value });
    }

    /// <summary>
    /// Whether the window reopens itself once the service has moved to a new version, rather than
    /// waiting to be asked. On means an update needs nothing from you at all.
    /// </summary>
    public bool SeamlessRestart
    {
        get => StartupRegistration.SeamlessRestartEnabled();
        set
        {
            if (StartupRegistration.SetSeamlessRestart(value))
            {
                Message = value
                    ? "MergePool will reopen itself on the new version after an update."
                    : "MergePool will ask before reopening itself after an update.";
            }
            else
            {
                Message = "Windows would not let MergePool save that setting.";
            }

            Raise();
        }
    }

    /// <summary>Whether the window itself opens at sign-in. The service always does.</summary>
    public bool StartWithWindows
    {
        get => StartupRegistration.IsEnabled();
        set
        {
            if (StartupRegistration.SetEnabled(value))
            {
                Message = value
                    ? "MergePool's window will open in the notification area when you sign in."
                    : "MergePool's window will no longer open at sign-in. Your pools stay mounted either way.";
            }
            else
            {
                Message = "Windows would not let MergePool change its start-up setting.";
            }

            Raise();
        }
    }

    public string? Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public string Headline => Banner();

    public string LastChecked => _status?.LastCheckedUtc is { } checkedUtc
        ? string.Create(CultureInfo.CurrentCulture, $"Last checked {checkedUtc.ToLocalTime():g}")
        : "Not checked yet";

    public string InstalledVersionsText => _status is null || _status.InstalledVersions.Count == 0
        ? "—"
        : string.Join(", ", _status.InstalledVersions);

    /// <summary>The version of MergePool this window itself was built as.</summary>
    public string WindowVersion { get; } =
        typeof(UpdateViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            is { } informational && informational.Length > 0
            ? informational.Split('+')[0]
            : typeof(UpdateViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// The service has restarted onto a newer version than this window is running. An update
    /// replaces neither: it adds a version and moves the link, so whatever was already open goes on
    /// running the version it started as until it is opened again.
    /// </summary>
    public bool WindowIsStale => RestartTarget is not null;

    public string StaleHeadline => string.Create(
        CultureInfo.CurrentCulture,
        $"MergePool {InstalledVersion} is installed and running. This window is still {WindowVersion} — reopen it to catch up.");

    /// <summary>Where a stale window has to restart from to become the installed version.</summary>
    private string? RestartTarget
    {
        get
        {
            if (!Version.TryParse(_status?.InstalledVersion, out var service)
                || !Version.TryParse(WindowVersion, out var window)
                || service <= window)
            {
                return null;
            }

            try
            {
                return VersionHandoff.RestartTargetForVersion(Environment.ProcessPath, _status?.InstalledVersion);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private Task RestartWindowAsync()
    {
        if (RestartTarget is { } target && RelaunchWindow?.Invoke(target) != true)
        {
            Message = "MergePool could not reopen itself. Close this window and open MergePool again.";
        }

        return Task.CompletedTask;
    }

    public void Apply(UpdateStatusResult? status)
    {
        var wasAvailable = UpdateAvailable;
        _status = status;

        Raise(nameof(IsSupported));
        Raise(nameof(InstalledVersion));
        Raise(nameof(AvailableVersion));
        Raise(nameof(UpdateAvailable));
        Raise(nameof(IsInstalling));
        Raise(nameof(Progress));
        Raise(nameof(ReleaseNotes));
        Raise(nameof(ReleaseUrl));
        Raise(nameof(Problem));
        Raise(nameof(AutomaticChecks));
        Raise(nameof(AutomaticInstall));
        Raise(nameof(Headline));
        Raise(nameof(LastChecked));
        Raise(nameof(InstalledVersionsText));
        Raise(nameof(WindowIsStale));
        Raise(nameof(StaleHeadline));

        if (status is not null)
        {
            _checkInterval.Load(status.CheckIntervalHours);
            _prereleases.Load(status.IncludePrereleases);
            Advanced.Refresh();
        }

        CheckCommand.RaiseCanExecuteChanged();
        InstallCommand.RaiseCanExecuteChanged();
        RestartWindowCommand.RaiseCanExecuteChanged();

        if (UpdateAvailable && !wasAvailable)
        {
            NewVersionFound?.Invoke(this, AvailableVersion ?? string.Empty);
        }

        // Announced once. The banner stays either way; this is for reopening without being asked.
        if (!_announcedStale && RestartTarget is { } target)
        {
            _announcedStale = true;
            WindowLeftBehind?.Invoke(this, target);
        }
    }

    /// <summary>Raised the first time a version is seen, so the tray can say so once.</summary>
    public event EventHandler<string>? NewVersionFound;

    /// <summary>
    /// Raised once when the service has moved to a newer version than this window. Carries the
    /// executable to reopen, so the application does not have to work it out again.
    /// </summary>
    public event EventHandler<string>? WindowLeftBehind;

    private string Banner()
    {
        if (_status is null)
        {
            return "Checking for updates…";
        }

        return _status.Stage switch
        {
            "Downloading" => string.Create(
                CultureInfo.CurrentCulture,
                $"Downloading MergePool {_status.AvailableVersion} — {_status.Progress:P0}"),
            "Staged" => $"MergePool {_status.AvailableVersion} is ready to install.",
            "Applying" => $"Installing MergePool {_status.AvailableVersion}. The service restarts in a moment; your pools come straight back.",
            "Unavailable" => "This copy of MergePool cannot update itself.",
            _ when _status.UpdateAvailable => string.Create(
                CultureInfo.CurrentCulture,
                $"MergePool {_status.AvailableVersion} is available. You have {_status.InstalledVersion}."),
            _ => $"MergePool {_status.InstalledVersion} is up to date.",
        };
    }

    private async Task CheckAsync()
    {
        _busy = true;
        Message = "Checking for updates…";

        try
        {
            var status = await _service
                .TryInvokeAsync<UpdateStatusResult>(Methods.UpdateCheck, null, _token())
                .ConfigureAwait(true);

            Apply(status);
            Message = status is null
                ? _service.LastError
                : status.UpdateAvailable
                    ? $"MergePool {status.AvailableVersion} is available."
                    : $"You are on the newest version ({status.InstalledVersion}).";
        }
        finally
        {
            _busy = false;
            CheckCommand.RaiseCanExecuteChanged();
            InstallCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task InstallAsync()
    {
        _busy = true;
        Message = "Downloading the update…";

        try
        {
            var status = await _service
                .InvokeAsync<UpdateStatusResult>(Methods.UpdateApply, null, _token())
                .ConfigureAwait(true);

            Apply(status);
            Message = status.Stage switch
            {
                "Applying" or "Staged" =>
                    "Installing. The service restarts on the new version and the pools come back on their own — "
                    + "nothing on your drives is touched.",
                "Failed" => status.LastError,
                _ => Message,
            };
        }
        catch (Exception exception) when (exception is IpcException or IOException)
        {
            Message = exception.Message;
        }
        finally
        {
            _busy = false;
            CheckCommand.RaiseCanExecuteChanged();
            InstallCommand.RaiseCanExecuteChanged();
        }
    }

    private NumericSetting _checkInterval = null!;

    private ToggleSetting _prereleases = null!;

    private SettingGroup BuildAdvanced()
    {
        _checkInterval = new NumericSetting(
            "How often it looks for a new version",
            "How long MergePool waits between checks of the release feed. A check is one small "
            + "request; it downloads nothing unless there is a newer version and you have asked for "
            + "it to be installed.",
            "More often means you get a fix sooner. Less often means next to no network traffic — "
            + "and you can always click Check now, whatever this says.",
            minimum: 1,
            maximum: 336,
            step: 1,
            defaultValue: 6,
            SettingFormats.Hours,
            value => string.Create(
                CultureInfo.CurrentCulture,
                $"The release feed is checked every {SettingFormats.Hours(value)}."),
            value => _ = ApplySettingAsync(new UpdateSettingsDto { CheckIntervalHours = value }),
            advanced: true);

        _prereleases = new ToggleSetting(
            "Offer pre-release versions",
            "Whether a version marked as a pre-release counts as an update. These are builds put "
            + "out for testing before a release is called finished.",
            "On, you see fixes and new work first, and you are the one finding out what is wrong "
            + "with them. Off, MergePool only ever moves to a finished release — which is what a "
            + "machine holding data you care about should be doing.",
            defaultValue: false,
            "On: pre-releases are installed like any other version.",
            "Off: only finished releases are offered.",
            value => _ = ApplySettingAsync(new UpdateSettingsDto { IncludePrereleases = value }),
            advanced: true);

        return new SettingGroup(
            "Updating",
            "How MergePool looks for new versions of itself.",
            advanced: true,
            _checkInterval,
            _prereleases);
    }

    private async Task ApplySettingAsync(UpdateSettingsDto settings)
    {
        var status = await _service
            .TryInvokeAsync<UpdateStatusResult>(Methods.UpdateSetOptions, settings, _token())
            .ConfigureAwait(true);

        if (status is not null)
        {
            Apply(status);
        }
    }
}
