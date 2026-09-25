using System.Globalization;
using MergePool.Ipc.Protocol;
using MergePool.Ui.Services;

namespace MergePool.Ui.ViewModels;

/// <summary>
/// The tunables, as things a person can reason about rather than raw numbers: how much MergePool
/// leans on free space against speed, when it decides a drive has gone slow, and how hard the
/// background rebalancer is allowed to work.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly ServiceConnection _service;
    private readonly Func<CancellationToken> _token;

    private PlacementSettingsDto _settings = new();
    private bool _loading;
    private string? _message;

    public SettingsViewModel(ServiceConnection service, Func<CancellationToken> token)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _token = token ?? throw new ArgumentNullException(nameof(token));

        SaveCommand = new AsyncCommand(SaveAsync);
        ResetCommand = new AsyncCommand(ResetAsync);
    }

    public AsyncCommand SaveCommand { get; }

    public AsyncCommand ResetCommand { get; }

    public string? Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    /// <summary>0 = place purely by free space, 1 = lean hard on the faster drive.</summary>
    public double SpeedBias
    {
        get => Math.Clamp(_settings.SpeedWeight ?? 1.0, 0, 3) / 3;
        set
        {
            _settings.SpeedWeight = Math.Round(Math.Clamp(value, 0, 1) * 3, 2);
            Raise();
            Raise(nameof(SpeedBiasText));
        }
    }

    public string SpeedBiasText => (_settings.SpeedWeight ?? 1.0) switch
    {
        < 0.35 => "Fill the emptiest drive, whatever its speed",
        < 0.85 => "Mostly by free space, a little by speed",
        < 1.6 => "Balanced: free space and speed count equally",
        < 2.4 => "Prefer the faster drive unless it is much fuller",
        _ => "Strongly prefer the fastest drive",
    };

    /// <summary>Free space kept clear on every drive, in gigabytes.</summary>
    public double MinFreeGigabytes
    {
        get => Math.Round((_settings.MinFreeBytes ?? 0) / (double)(1024 * 1024 * 1024), 1);
        set
        {
            _settings.MinFreeBytes = (long)Math.Round(Math.Clamp(value, 0, 200) * 1024 * 1024 * 1024);
            Raise();
            Raise(nameof(MinFreeText));
        }
    }

    public string MinFreeText => string.Create(
        CultureInfo.CurrentCulture,
        $"New files avoid a drive with less than {MinFreeGigabytes:N1} GB free.");

    /// <summary>How far below its own normal speed a drive has to fall before it is parked.</summary>
    public double ThrottleSensitivity
    {
        get => 1 - Math.Clamp(_settings.ThrottleEnterRatio ?? 0.45, 0.1, 0.9);
        set
        {
            var ratio = Math.Round(1 - Math.Clamp(value, 0.1, 0.9), 2);
            _settings.ThrottleEnterRatio = ratio;

            // The exit ratio has to stay above the enter ratio or the drive would flap in and out.
            _settings.ThrottleExitRatio = Math.Round(Math.Min(0.95, ratio + 0.3), 2);
            Raise();
            Raise(nameof(ThrottleText));
        }
    }

    public string ThrottleText => string.Create(
        CultureInfo.CurrentCulture,
        $"A drive is parked once it writes below {_settings.ThrottleEnterRatio ?? 0.45:P0} of its own normal speed, "
        + $"and comes back at {_settings.ThrottleExitRatio ?? 0.75:P0}.");

    public bool RebalanceEnabled
    {
        get => _settings.RebalanceEnabled ?? true;
        set
        {
            _settings.RebalanceEnabled = value;
            Raise();
        }
    }

    /// <summary>Cap on the background rebalancer, in megabytes a second.</summary>
    public double RebalanceMegabytesPerSecond
    {
        get => Math.Round((_settings.RebalanceMaxBytesPerSecond ?? 33_554_432) / (double)(1024 * 1024));
        set
        {
            _settings.RebalanceMaxBytesPerSecond = (long)Math.Round(Math.Clamp(value, 1, 500) * 1024 * 1024);
            Raise();
            Raise(nameof(RebalanceRateText));
        }
    }

    public string RebalanceRateText => string.Create(
        CultureInfo.CurrentCulture,
        $"Rebalancing moves at most {RebalanceMegabytesPerSecond:N0} MB/s, so it stays out of the way of real work.");

    public async Task LoadAsync()
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        try
        {
            var settings = await _service
                .TryInvokeAsync<PlacementSettingsDto>(Methods.ConfigGet, null, _token())
                .ConfigureAwait(true);

            if (settings is not null)
            {
                _settings = settings;
                RaiseAll();
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task SaveAsync()
    {
        var settings = await _service
            .TryInvokeAsync<PlacementSettingsDto>(Methods.ConfigSetPlacement, _settings, _token())
            .ConfigureAwait(true);

        if (settings is null)
        {
            Message = _service.LastError ?? "The settings could not be saved.";
            return;
        }

        _settings = settings;
        RaiseAll();
        Message = "Saved. The change applies to the next file placed; nothing already in the pool moves.";
    }

    private async Task ResetAsync()
    {
        _settings = new PlacementSettingsDto
        {
            SpeedWeight = 1.0,
            FreeSpaceWeight = 1.0,
            MinFreeBytes = 2L * 1024 * 1024 * 1024,
            ThrottleEnterRatio = 0.45,
            ThrottleExitRatio = 0.75,
            RebalanceEnabled = true,
            RebalanceMaxBytesPerSecond = 32L * 1024 * 1024,
        };

        RaiseAll();
        await SaveAsync().ConfigureAwait(true);
        Message = "Back to the defaults.";
    }

    private void RaiseAll()
    {
        Raise(nameof(SpeedBias));
        Raise(nameof(SpeedBiasText));
        Raise(nameof(MinFreeGigabytes));
        Raise(nameof(MinFreeText));
        Raise(nameof(ThrottleSensitivity));
        Raise(nameof(ThrottleText));
        Raise(nameof(RebalanceEnabled));
        Raise(nameof(RebalanceMegabytesPerSecond));
        Raise(nameof(RebalanceRateText));
    }
}
