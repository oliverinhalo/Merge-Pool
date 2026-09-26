using System.Globalization;
using System.IO;
using MergePool.Core.Config;
using MergePool.Ipc.Protocol;
using MergePool.Ui.Services;

namespace MergePool.Ui.ViewModels;

/// <summary>
/// The web interface settings: whether it is on, which port it answers on, who may reach it, and
/// the token a browser has to present.
/// </summary>
public sealed class WebInterfaceViewModel : ViewModelBase
{
    private readonly ServiceConnection _service;
    private readonly Func<CancellationToken> _token;

    private WebInterfaceResult? _state;
    private string _portText = WebOptions.DefaultPort.ToString(CultureInfo.InvariantCulture);
    private string? _message;
    private bool _tokenVisible;
    private bool _busy;

    public WebInterfaceViewModel(ServiceConnection service, Func<CancellationToken> token)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _token = token ?? throw new ArgumentNullException(nameof(token));

        ApplyPortCommand = new AsyncCommand(ApplyPortAsync, () => !_busy && PortIsValid);
        RegenerateTokenCommand = new AsyncCommand(RegenerateTokenAsync, () => !_busy);
        CopyTokenCommand = new AsyncCommand(CopyTokenAsync, () => !string.IsNullOrEmpty(AccessToken));
        CopyUrlCommand = new AsyncCommand(CopyUrlAsync, () => !string.IsNullOrEmpty(Url));
        OpenCommand = new AsyncCommand(OpenAsync, () => !string.IsNullOrEmpty(Url));
        OpenWebPageCommand = new AsyncCommand(OpenWebPageAsync, () => !_busy && IsSupported);
    }

    public AsyncCommand ApplyPortCommand { get; }

    public AsyncCommand RegenerateTokenCommand { get; }

    public AsyncCommand CopyTokenCommand { get; }

    public AsyncCommand CopyUrlCommand { get; }

    public AsyncCommand OpenCommand { get; }

    /// <summary>
    /// Turns the web interface on if it is off and opens it in a browser, signed in. One action,
    /// because "show me the web page" is one thought — and it is what the notification area offers.
    /// </summary>
    public AsyncCommand OpenWebPageCommand { get; }

    /// <summary>Set by the window: copying and opening a browser are its business, not the model's.</summary>
    public Action<string>? CopyToClipboard { get; set; }

    public Action<string>? OpenInBrowser { get; set; }

    public bool IsSupported => _service.Supports(Capabilities.WebInterface);

    public bool Enabled
    {
        get => _state?.Enabled == true;
        set => _ = ApplyAsync(new WebInterfaceSettingsDto { Enabled = value });
    }

    /// <summary>Typed rather than picked: any port from 1024 to 65535 is fair game.</summary>
    public string PortText
    {
        get => _portText;
        set
        {
            if (Set(ref _portText, value))
            {
                Raise(nameof(PortIsValid));
                Raise(nameof(PortHint));
                ApplyPortCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool PortIsValid =>
        int.TryParse(PortText, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
        && WebOptions.IsUsablePort(port);

    public string PortHint => PortIsValid
        ? string.Create(
            CultureInfo.CurrentCulture,
            $"Any port from {WebOptions.MinimumPort} to {WebOptions.MaximumPort}. {WebOptions.DefaultPort} is the default.")
        : string.Create(
            CultureInfo.CurrentCulture,
            $"Enter a port between {WebOptions.MinimumPort} and {WebOptions.MaximumPort}.");

    /// <summary>False means this computer only; true means anything that can reach this machine.</summary>
    public bool ReachableFromNetwork
    {
        get => string.Equals(_state?.Scope, "Network", StringComparison.OrdinalIgnoreCase);
        set => _ = ApplyAsync(new WebInterfaceSettingsDto { Scope = value ? "Network" : "ThisComputer" });
    }

    public bool AllowChanges
    {
        get => _state?.AllowChanges != false;
        set => _ = ApplyAsync(new WebInterfaceSettingsDto { AllowChanges = value });
    }

    public bool ManageFirewallRule
    {
        get => _state?.ManageFirewallRule != false;
        set => _ = ApplyAsync(new WebInterfaceSettingsDto { ManageFirewallRule = value });
    }

    public string AccessToken => _state?.AccessToken ?? string.Empty;

    /// <summary>The token is masked until asked for, so it is not on screen during a screen share.</summary>
    public bool IsTokenVisible
    {
        get => _tokenVisible;
        set
        {
            if (Set(ref _tokenVisible, value))
            {
                Raise(nameof(DisplayedToken));
            }
        }
    }

    public string DisplayedToken => string.IsNullOrEmpty(AccessToken)
        ? "—"
        : IsTokenVisible ? AccessToken : new string('•', Math.Min(AccessToken.Length, 32));

    public string? Url => _state?.Url;

    public string StateText
    {
        get
        {
            if (_state is null)
            {
                return "Loading…";
            }

            if (!_state.Enabled)
            {
                return "Off. Nothing is listening.";
            }

            return _state.State switch
            {
                "Listening" => string.Create(
                    CultureInfo.CurrentCulture,
                    $"Listening at {_state.Url}"),
                "Failed" => _state.Error ?? "The port could not be opened.",
                _ => "Starting…",
            };
        }
    }

    public bool HasProblem => _state is { Enabled: true, State: "Failed" };

    public bool IsListening => _state is { State: "Listening" };

    public string TrafficText => _state is null || _state.RequestCount == 0
        ? "No requests yet."
        : string.Create(
            CultureInfo.CurrentCulture,
            $"{_state.RequestCount:N0} request(s) served, {_state.RejectedCount:N0} refused for a bad token.");

    /// <summary>The honest caveat: this is plain HTTP, and it is worth saying so on screen.</summary>
    public string SecurityNote => ReachableFromNetwork
        ? "Anyone on your network who has the token can control MergePool. The connection is not "
          + "encrypted, so use this on a network you trust, not on shared or public Wi-Fi."
        : "Only this computer can connect. Nothing on your network can reach it.";

    public string? Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public void Apply(WebInterfaceResult? state)
    {
        _state = state;

        if (state is not null && !_busy)
        {
            var port = state.Port.ToString(CultureInfo.InvariantCulture);
            if (!string.Equals(_portText, port, StringComparison.Ordinal))
            {
                _portText = port;
                Raise(nameof(PortText));
                Raise(nameof(PortIsValid));
                Raise(nameof(PortHint));
            }
        }

        Raise(nameof(IsSupported));
        Raise(nameof(Enabled));
        Raise(nameof(ReachableFromNetwork));
        Raise(nameof(AllowChanges));
        Raise(nameof(ManageFirewallRule));
        Raise(nameof(AccessToken));
        Raise(nameof(DisplayedToken));
        Raise(nameof(Url));
        Raise(nameof(StateText));
        Raise(nameof(HasProblem));
        Raise(nameof(IsListening));
        Raise(nameof(TrafficText));
        Raise(nameof(SecurityNote));

        CopyTokenCommand.RaiseCanExecuteChanged();
        CopyUrlCommand.RaiseCanExecuteChanged();
        OpenCommand.RaiseCanExecuteChanged();
        OpenWebPageCommand.RaiseCanExecuteChanged();
    }

    private Task ApplyPortAsync() =>
        ApplyAsync(new WebInterfaceSettingsDto
        {
            Port = int.Parse(PortText, NumberStyles.None, CultureInfo.InvariantCulture),
        });

    private async Task ApplyAsync(WebInterfaceSettingsDto settings)
    {
        _busy = true;

        try
        {
            var state = await _service
                .TryInvokeAsync<WebInterfaceResult>(Methods.WebSet, settings, _token())
                .ConfigureAwait(true);

            if (state is null)
            {
                Message = _service.LastError ?? "MergePool could not change the web interface.";
                return;
            }

            Apply(state);

            Message = state switch
            {
                { Enabled: false } => "The web interface is off.",
                { State: "Listening" } => string.Create(
                    CultureInfo.CurrentCulture,
                    $"Ready at {state.Url} — sign in with the access token below."),
                { State: "Failed" } => state.Error,
                _ => "Starting the web interface…",
            };
        }
        finally
        {
            _busy = false;
            ApplyPortCommand.RaiseCanExecuteChanged();
            RegenerateTokenCommand.RaiseCanExecuteChanged();
            OpenWebPageCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task RegenerateTokenAsync()
    {
        _busy = true;

        try
        {
            var state = await _service
                .TryInvokeAsync<WebInterfaceResult>(Methods.WebRegenerateToken, null, _token())
                .ConfigureAwait(true);

            if (state is null)
            {
                Message = _service.LastError ?? "MergePool could not change the access token.";
                return;
            }

            Apply(state);
            IsTokenVisible = true;
            Message = "New access token. Any browser using the old one has been signed out.";
        }
        catch (Exception exception) when (exception is IpcException or IOException)
        {
            Message = exception.Message;
        }
        finally
        {
            _busy = false;
            RegenerateTokenCommand.RaiseCanExecuteChanged();
        }
    }

    private Task CopyTokenAsync()
    {
        CopyToClipboard?.Invoke(AccessToken);
        Message = "Access token copied.";
        return Task.CompletedTask;
    }

    private Task CopyUrlAsync()
    {
        if (Url is { } url)
        {
            CopyToClipboard?.Invoke(url);
            Message = "Address copied.";
        }

        return Task.CompletedTask;
    }

    private Task OpenAsync()
    {
        if (Url is { } url)
        {
            OpenInBrowser?.Invoke(SignedIn(url));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Turns the interface on if it is not already listening, then opens it. Returns once the
    /// browser has been asked to open, or once there is a reason in <see cref="Message"/> why not.
    /// </summary>
    public async Task OpenWebPageAsync()
    {
        if (!IsSupported)
        {
            Message = "This copy of the MergePool service is too old to serve a web page. Update MergePool.";
            return;
        }

        if (_state is not { Enabled: true, State: "Listening" })
        {
            // Whatever port is already configured, unless nothing usable is — then the default.
            var port = PortIsValid
                ? int.Parse(PortText, NumberStyles.None, CultureInfo.InvariantCulture)
                : WebOptions.DefaultPort;

            await ApplyAsync(new WebInterfaceSettingsDto { Enabled = true, Port = port }).ConfigureAwait(true);
        }

        if (Url is { } url && IsListening)
        {
            OpenInBrowser?.Invoke(SignedIn(url));
            Message = string.Create(CultureInfo.CurrentCulture, $"Opening {url} in your browser.");
            return;
        }

        Message ??= "MergePool could not open the web page.";
    }

    /// <summary>
    /// The address with the token in the fragment, so the browser arrives signed in. A fragment is
    /// never sent to the server, so the token is not in any request or log on the way there, and the
    /// page takes it out of the address bar as soon as it has it.
    /// </summary>
    private string SignedIn(string url) => string.IsNullOrEmpty(AccessToken)
        ? url
        : url + "#token=" + Uri.EscapeDataString(AccessToken);
}
