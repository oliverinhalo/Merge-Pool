using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using MergePool.Ipc.Protocol;
using MergePool.Ui.Services;

namespace MergePool.Ui.ViewModels;

/// <summary>
/// The whole window. It polls the service so the drive and pool status stay live, and it never
/// blocks on a service that is restarting: an upgrade should look like a brief "reconnecting".
/// </summary>
public sealed class MainViewModel : ViewModelBase, IAsyncDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    private readonly ServiceConnection _service;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _shutdown = new();

    private string _poolName = "Pool";
    private string _selectedMountLetter = "P";
    private bool _adoptExistingContent;
    private string? _statusMessage;
    private string? _errorMessage;
    private bool _isBusy;
    private string _serviceVersion = "not connected";

    public MainViewModel(ServiceConnection? service = null)
    {
        _service = service ?? new ServiceConnection();

        CreatePoolCommand = new AsyncCommand(CreatePoolAsync, () => CanCreatePool);
        RefreshCommand = new AsyncCommand(() => RefreshAsync(_shutdown.Token));
        MountCommand = new AsyncCommand(MountSelectedPoolAsync, () => SelectedPool is { IsMounted: false });
        UnmountCommand = new AsyncCommand(UnmountSelectedPoolAsync, () => SelectedPool is { IsMounted: true });
        RemovePoolCommand = new AsyncCommand(RemoveSelectedPoolAsync, () => SelectedPool is not null);
        RebalanceCommand = new AsyncCommand(RebalanceAsync, () => SelectedPool is not null);

        _timer = new DispatcherTimer { Interval = RefreshInterval };
        _timer.Tick += async (_, _) => await RefreshAsync(_shutdown.Token).ConfigureAwait(true);
    }

    public ObservableCollection<DriveViewModel> Drives { get; } = [];

    public ObservableCollection<PoolViewModel> Pools { get; } = [];

    /// <summary>Drive letters that are free for a new pool.</summary>
    public ObservableCollection<string> AvailableMountLetters { get; } = [];

    public AsyncCommand CreatePoolCommand { get; }

    public AsyncCommand RefreshCommand { get; }

    public AsyncCommand MountCommand { get; }

    public AsyncCommand UnmountCommand { get; }

    public AsyncCommand RemovePoolCommand { get; }

    public AsyncCommand RebalanceCommand { get; }

    public string PoolName
    {
        get => _poolName;
        set
        {
            if (Set(ref _poolName, value))
            {
                CreatePoolCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SelectedMountLetter
    {
        get => _selectedMountLetter;
        set
        {
            if (Set(ref _selectedMountLetter, value))
            {
                CreatePoolCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Opt-in: move what is already on each drive into its pool part (a same-volume rename).</summary>
    public bool AdoptExistingContent
    {
        get => _adoptExistingContent;
        set => Set(ref _adoptExistingContent, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (Set(ref _errorMessage, value))
            {
                Raise(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public string ServiceVersion
    {
        get => _serviceVersion;
        private set => Set(ref _serviceVersion, value);
    }

    private PoolViewModel? _selectedPool;

    public PoolViewModel? SelectedPool
    {
        get => _selectedPool;
        set
        {
            if (Set(ref _selectedPool, value))
            {
                MountCommand.RaiseCanExecuteChanged();
                UnmountCommand.RaiseCanExecuteChanged();
                RemovePoolCommand.RaiseCanExecuteChanged();
                RebalanceCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanCreatePool =>
        !string.IsNullOrWhiteSpace(PoolName)
        && !string.IsNullOrWhiteSpace(SelectedMountLetter)
        && Drives.Any(d => d.IsSelected);

    public async Task StartAsync()
    {
        await RefreshAsync(_shutdown.Token).ConfigureAwait(true);
        _timer.Start();
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var status = await _service.TryInvokeAsync<ServiceStatusResult>(
            Methods.ServiceStatus, null, cancellationToken).ConfigureAwait(true);

        if (status is null)
        {
            ErrorMessage = _service.LastError ?? "The MergePool service is not reachable.";
            ServiceVersion = "not connected";
            return;
        }

        ServiceVersion = status.Draining
            ? status.ServiceVersion + " (updating)"
            : status.ServiceVersion;

        ErrorMessage = status.WinFspInstalled
            ? null
            : "WinFsp is not installed, so pools cannot be mounted. Reinstall MergePool to add it.";

        await RefreshDrivesAsync(cancellationToken).ConfigureAwait(true);
        await RefreshPoolsAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task RefreshDrivesAsync(CancellationToken cancellationToken)
    {
        var drives = await _service.TryInvokeAsync<DriveListResult>(
            Methods.DrivesList, null, cancellationToken).ConfigureAwait(true);

        if (drives is null)
        {
            return;
        }

        foreach (var dto in drives.Drives)
        {
            var existing = Drives.FirstOrDefault(d =>
                string.Equals(d.VolumeId, dto.VolumeId, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                var drive = new DriveViewModel(dto);
                drive.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(DriveViewModel.IsSelected))
                    {
                        Raise(nameof(CanCreatePool));
                        CreatePoolCommand.RaiseCanExecuteChanged();
                    }
                };

                Drives.Add(drive);
            }
            else
            {
                existing.Update(dto);
            }
        }

        for (var i = Drives.Count - 1; i >= 0; i--)
        {
            if (drives.Drives.All(d => !string.Equals(d.VolumeId, Drives[i].VolumeId, StringComparison.OrdinalIgnoreCase)))
            {
                Drives.RemoveAt(i);
            }
        }

        UpdateAvailableMountLetters(drives.Drives);
    }

    private async Task RefreshPoolsAsync(CancellationToken cancellationToken)
    {
        var pools = await _service.TryInvokeAsync<PoolListResult>(
            Methods.PoolsList, null, cancellationToken).ConfigureAwait(true);

        if (pools is null)
        {
            return;
        }

        foreach (var dto in pools.Pools)
        {
            var existing = Pools.FirstOrDefault(p => p.PoolId == dto.PoolId);
            if (existing is null)
            {
                Pools.Add(new PoolViewModel(dto));
            }
            else
            {
                existing.Update(dto);
            }
        }

        for (var i = Pools.Count - 1; i >= 0; i--)
        {
            if (pools.Pools.All(p => p.PoolId != Pools[i].PoolId))
            {
                Pools.RemoveAt(i);
            }
        }

        SelectedPool ??= Pools.FirstOrDefault();

        MountCommand.RaiseCanExecuteChanged();
        UnmountCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Offers letters that no physical drive is using.</summary>
    private void UpdateAvailableMountLetters(IReadOnlyList<DriveDto> drives)
    {
        var taken = drives
            .Where(d => !string.IsNullOrEmpty(d.DriveLetter))
            .Select(d => d.DriveLetter![0])
            .ToHashSet();

        var free = Enumerable
            .Range('D', 'Z' - 'D' + 1)
            .Select(c => (char)c)
            .Where(c => !taken.Contains(c))
            .Select(c => c.ToString(CultureInfo.InvariantCulture))
            .ToArray();

        if (AvailableMountLetters.SequenceEqual(free, StringComparer.Ordinal))
        {
            return;
        }

        AvailableMountLetters.Clear();
        foreach (var letter in free)
        {
            AvailableMountLetters.Add(letter);
        }

        if (!AvailableMountLetters.Contains(SelectedMountLetter) && AvailableMountLetters.Count > 0)
        {
            SelectedMountLetter = AvailableMountLetters[0];
        }
    }

    private async Task CreatePoolAsync()
    {
        var selected = Drives.Where(d => d.IsSelected).Select(d => d.VolumeId).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Creating the pool…";

        try
        {
            var pool = await _service.InvokeAsync<PoolDto>(
                Methods.PoolCreate,
                new CreatePoolRequest
                {
                    Name = PoolName,
                    MountPoint = SelectedMountLetter + ":",
                    VolumeIds = selected,
                    AdoptExistingContent = AdoptExistingContent,
                    MountImmediately = true,
                },
                _shutdown.Token).ConfigureAwait(true);

            StatusMessage = string.Create(
                CultureInfo.CurrentCulture,
                $"Pool '{pool.Name}' is mounted at {pool.MountPoint}.");

            foreach (var drive in Drives)
            {
                drive.IsSelected = false;
            }

            await RefreshAsync(_shutdown.Token).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IpcException or IOException)
        {
            ErrorMessage = exception.Message;
            StatusMessage = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task MountSelectedPoolAsync() =>
        RunPoolCommandAsync(
            Methods.PoolMount,
            pool => new MountRequest { PoolId = pool.PoolId },
            "Mounting…");

    private Task UnmountSelectedPoolAsync() =>
        RunPoolCommandAsync(
            Methods.PoolUnmount,
            pool => new PoolReference { PoolId = pool.PoolId },
            "Unmounting…");

    private Task RemoveSelectedPoolAsync() =>
        RunPoolCommandAsync(
            Methods.PoolRemove,
            // Pool parts stay on the drives: removing a pool must never delete anything.
            pool => new RemovePoolRequest { PoolId = pool.PoolId, DeletePoolParts = false },
            "Removing the pool (the files stay on the drives)…");

    private Task RebalanceAsync() =>
        RunPoolCommandAsync(
            Methods.RebalanceStart,
            pool => new PoolReference { PoolId = pool.PoolId },
            "Rebalancing in the background…");

    private async Task RunPoolCommandAsync(string method, Func<PoolViewModel, object> payload, string busyMessage)
    {
        if (SelectedPool is not { } pool)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = busyMessage;

        try
        {
            await _service.InvokeAsync<PoolDto>(method, payload(pool), _shutdown.Token).ConfigureAwait(true);
            await RefreshAsync(_shutdown.Token).ConfigureAwait(true);
            StatusMessage = null;
        }
        catch (Exception exception) when (exception is IpcException or IOException)
        {
            ErrorMessage = exception.Message;
            StatusMessage = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _timer.Stop();
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _service.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
