using System.Globalization;
using System.IO;
using MergePool.Ipc.Protocol;
using MergePool.Ui.Services;

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

    public UpdateViewModel(ServiceConnection service, Func<CancellationToken> token)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _token = token ?? throw new ArgumentNullException(nameof(token));

        CheckCommand = new AsyncCommand(CheckAsync, () => !_busy && IsSupported);
        InstallCommand = new AsyncCommand(InstallAsync, () => !_busy && UpdateAvailable);
    }

    public AsyncCommand CheckCommand { get; }

    public AsyncCommand InstallCommand { get; }

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

        CheckCommand.RaiseCanExecuteChanged();
        InstallCommand.RaiseCanExecuteChanged();

        if (UpdateAvailable && !wasAvailable)
        {
            NewVersionFound?.Invoke(this, AvailableVersion ?? string.Empty);
        }
    }

    /// <summary>Raised the first time a version is seen, so the tray can say so once.</summary>
    public event EventHandler<string>? NewVersionFound;

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
