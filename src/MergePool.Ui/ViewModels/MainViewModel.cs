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
    private bool _isConnected;
    private PoolViewModel? _selectedPool;
    private PoolPartViewModel? _selectedPart;

    public MainViewModel(ServiceConnection? service = null)
    {
        _service = service ?? new ServiceConnection();

        Updates = new UpdateViewModel(_service, () => _shutdown.Token);
        Settings = new SettingsViewModel(_service, () => _shutdown.Token);

        CreatePoolCommand = new AsyncCommand(CreatePoolAsync, () => CanCreatePool);
        RefreshCommand = new AsyncCommand(() => RefreshAsync(_shutdown.Token));
        MountCommand = new AsyncCommand(MountSelectedPoolAsync, () => SelectedPool is { IsMounted: false });
        UnmountCommand = new AsyncCommand(UnmountSelectedPoolAsync, () => SelectedPool is { IsMounted: true });
        RemovePoolCommand = new AsyncCommand(RemoveSelectedPoolAsync, () => SelectedPool is not null);
        RebalanceCommand = new AsyncCommand(RebalanceAsync, () => SelectedPool is not null);
        AddDrivesCommand = new AsyncCommand(AddDrivesToSelectedPoolAsync, () => CanAddDrives);
        DetachDriveCommand = new AsyncCommand(() => RemoveDriveAsync(moveFilesOff: false), () => CanRemoveDrive);
        EvacuateDriveCommand = new AsyncCommand(() => RemoveDriveAsync(moveFilesOff: true), () => CanRemoveDrive);
        OpenPoolCommand = new AsyncCommand(OpenSelectedPoolAsync, () => SelectedPool is { IsMounted: true });

        _timer = new DispatcherTimer { Interval = RefreshInterval };
        _timer.Tick += async (_, _) => await RefreshAsync(_shutdown.Token).ConfigureAwait(true);
    }

    public ObservableCollection<DriveViewModel> Drives { get; } = [];

    public ObservableCollection<PoolViewModel> Pools { get; } = [];

    /// <summary>Drive letters that are free for a new pool.</summary>
    public ObservableCollection<string> AvailableMountLetters { get; } = [];

    public UpdateViewModel Updates { get; }

    public SettingsViewModel Settings { get; }

    public AsyncCommand CreatePoolCommand { get; }

    public AsyncCommand RefreshCommand { get; }

    public AsyncCommand MountCommand { get; }

    public AsyncCommand UnmountCommand { get; }

    public AsyncCommand RemovePoolCommand { get; }

    public AsyncCommand RebalanceCommand { get; }

    public AsyncCommand AddDrivesCommand { get; }

    /// <summary>Takes a drive out and leaves its files on it.</summary>
    public AsyncCommand DetachDriveCommand { get; }

    /// <summary>Moves a drive's files onto the others first, then takes it out.</summary>
    public AsyncCommand EvacuateDriveCommand { get; }

    public AsyncCommand OpenPoolCommand { get; }

    /// <summary>Set by the window so a removal can be confirmed before anything happens.</summary>
    public Func<string, string, string, bool>? ConfirmAction { get; set; }

    /// <summary>Set by the window to open the pool in Explorer.</summary>
    public Action<string>? OpenInExplorer { get; set; }

    /// <summary>Raised whenever the headline state changes, so the tray tooltip can follow it.</summary>
    public event EventHandler<string>? StatusChanged;

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

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (Set(ref _isConnected, value))
            {
                Raise(nameof(ConnectionText));
            }
        }
    }

    public string ConnectionText => IsConnected ? "Service running" : "Service not reachable";

    public PoolViewModel? SelectedPool
    {
        get => _selectedPool;
        set
        {
            if (Set(ref _selectedPool, value))
            {
                SelectedPart = value?.Parts.FirstOrDefault();
                RaiseCommandStates();
            }
        }
    }

    /// <summary>The drive selected inside the pool, which the remove buttons act on.</summary>
    public PoolPartViewModel? SelectedPart
    {
        get => _selectedPart;
        set
        {
            if (Set(ref _selectedPart, value))
            {
                Raise(nameof(CanRemoveDrive));
                DetachDriveCommand.RaiseCanExecuteChanged();
                EvacuateDriveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanCreatePool =>
        !string.IsNullOrWhiteSpace(PoolName)
        && !string.IsNullOrWhiteSpace(SelectedMountLetter)
        && Drives.Any(d => d.IsSelected);

    /// <summary>
    /// Growing a pool needs a pool to grow and a drive to add. A service too old to know the
    /// method is feature-detected rather than called and failed.
    /// </summary>
    public bool CanAddDrives =>
        SelectedPool is not null
        && Drives.Any(d => d.IsSelected)
        && _service.Supports(Capabilities.PoolEdit);

    /// <summary>A pool must keep at least one drive, so the last one cannot be removed this way.</summary>
    public bool CanRemoveDrive =>
        SelectedPool is { } pool
        && SelectedPart is not null
        && pool.Parts.Count > 1
        && _service.Supports(Capabilities.DriveRemoval);

    /// <summary>Label on the add button, so it names the pool the drives would join.</summary>
    public string AddDrivesLabel => SelectedPool is null
        ? "Add to pool"
        : string.Create(CultureInfo.CurrentCulture, $"Add to '{SelectedPool.Name}'");

    public async Task StartAsync()
    {
        await RefreshAsync(_shutdown.Token).ConfigureAwait(true);
        await Settings.LoadAsync().ConfigureAwait(true);
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
            IsConnected = false;
            AnnounceStatus();
            return;
        }

        IsConnected = true;
        ServiceVersion = status.Draining
            ? status.ServiceVersion + " (updating)"
            : status.ServiceVersion;

        ErrorMessage = status.WinFspInstalled
            ? null
            : "WinFsp is not installed, so pools cannot be mounted. Reinstall MergePool to add it.";

        await RefreshDrivesAsync(cancellationToken).ConfigureAwait(true);
        await RefreshPoolsAsync(cancellationToken).ConfigureAwait(true);
        await RefreshUpdatesAsync(cancellationToken).ConfigureAwait(true);
        AnnounceStatus();
    }

    private async Task RefreshUpdatesAsync(CancellationToken cancellationToken)
    {
        if (!_service.Supports(Capabilities.AutoUpdate))
        {
            return;
        }

        var status = await _service.TryInvokeAsync<UpdateStatusResult>(
            Methods.UpdateStatus, null, cancellationToken).ConfigureAwait(true);

        Updates.Apply(status);
    }

    private async Task RefreshDrivesAsync(CancellationToken cancellationToken)
    {
        var drives = await _service.TryInvokeAsync<DriveListResult>(
            Methods.DrivesList, null, cancellationToken).ConfigureAwait(true);

        if (drives is null)
        {
            // Say so. An empty list with no message reads as "this machine has no drives".
            ErrorMessage = _service.LastError ?? "MergePool could not read the drives on this machine.";
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
                        Raise(nameof(CanAddDrives));
                        CreatePoolCommand.RaiseCanExecuteChanged();
                        AddDrivesCommand.RaiseCanExecuteChanged();
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
            ErrorMessage = _service.LastError ?? "MergePool could not read the pools.";
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

        // The selected drive can disappear underneath us when a pool changes.
        if (SelectedPool is { } pool && (SelectedPart is null || pool.Parts.All(p => p.PartId != SelectedPart.PartId)))
        {
            SelectedPart = pool.Parts.FirstOrDefault();
        }

        RaiseCommandStates();
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

            ClearDriveSelection();
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

    private async Task AddDrivesToSelectedPoolAsync()
    {
        if (SelectedPool is not { } pool)
        {
            return;
        }

        var selected = Drives.Where(d => d.IsSelected).Select(d => d.VolumeId).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = string.Create(
            CultureInfo.CurrentCulture,
            $"Adding {selected.Count} drive(s) to '{pool.Name}'…");

        try
        {
            var updated = await _service.InvokeAsync<PoolDto>(
                Methods.PoolAddDrives,
                new AddDrivesRequest
                {
                    PoolId = pool.PoolId,
                    VolumeIds = selected,
                    AdoptExistingContent = AdoptExistingContent,
                },
                _shutdown.Token).ConfigureAwait(true);

            StatusMessage = string.Create(
                CultureInfo.CurrentCulture,
                $"'{updated.Name}' now spans {updated.Parts.Count} drives. No remount was needed.");

            ClearDriveSelection();
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

    /// <summary>
    /// Takes a drive out of the selected pool. Both routes are confirmed first, because one of them
    /// moves data and the other changes what the pool can see.
    /// </summary>
    private async Task RemoveDriveAsync(bool moveFilesOff)
    {
        if (SelectedPool is not { } pool || SelectedPart is not { } part)
        {
            return;
        }

        if (!await ConfirmRemovalAsync(pool, part, moveFilesOff).ConfigureAwait(true))
        {
            return;
        }

        IsBusy = true;
        StatusMessage = moveFilesOff
            ? string.Create(
                CultureInfo.CurrentCulture,
                $"Moving {PoolPartViewModel.Format(part.PoolBytes)} off {part.DriveLetter} onto the other drives…")
            : string.Create(CultureInfo.CurrentCulture, $"Removing {part.DriveLetter} from '{pool.Name}'…");

        try
        {
            var result = await _service.InvokeAsync<DriveRemovalResultDto>(
                Methods.PoolRemoveDrive,
                new RemoveDriveRequest
                {
                    PoolId = pool.PoolId,
                    VolumeId = part.VolumeId,
                    MoveFilesOff = moveFilesOff,
                },
                _shutdown.Token).ConfigureAwait(true);

            StatusMessage = result.Message;

            if (!result.Removed)
            {
                ErrorMessage = result.Message;
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

    private async Task<bool> ConfirmRemovalAsync(PoolViewModel pool, PoolPartViewModel part, bool moveFilesOff)
    {
        if (ConfirmAction is not { } confirm)
        {
            return true;
        }

        if (!moveFilesOff)
        {
            return confirm(
                "Remove this drive from the pool?",
                $"{part.DriveLetter} {part.Label} leaves '{pool.Name}'.\n\n"
                + $"Nothing is deleted or moved. The {PoolPartViewModel.Format(part.PoolBytes)} it holds stay on the drive "
                + "in its pool part folder, as ordinary files — they just stop appearing in the pool.",
                "Remove drive");
        }

        // The plan is worth showing before a move: it says whether it can even finish.
        var plan = await _service.TryInvokeAsync<DriveRemovalPlanResult>(
            Methods.PoolPlanDriveRemoval,
            new RemoveDriveRequest { PoolId = pool.PoolId, VolumeId = part.VolumeId, MoveFilesOff = true },
            _shutdown.Token).ConfigureAwait(true);

        if (plan is null)
        {
            ErrorMessage = _service.LastError ?? "MergePool could not work out what would have to move.";
            return false;
        }

        if (!plan.Fits)
        {
            ErrorMessage = string.Create(
                CultureInfo.CurrentCulture,
                $"{part.DriveLetter} holds {PoolPartViewModel.Format(plan.TotalBytes)}, and the pool's other drives have only "
                + $"{PoolPartViewModel.Format(plan.DestinationFreeBytes)} free. Free up space, or remove the drive and leave its files on it.");

            return false;
        }

        return confirm(
            "Move this drive's files off, then remove it?",
            string.Create(
                CultureInfo.CurrentCulture,
                $"{plan.FileCount:N0} file(s), {PoolPartViewModel.Format(plan.TotalBytes)}, move from {part.DriveLetter} onto the pool's other drives.\n\n"
                + "Each file is copied whole and only then removed from this drive, so nothing is ever half-moved. "
                + "Files open in another program are left alone and the drive stays in the pool if any are.\n\n"
                + "This can take a while."),
            "Move files and remove");
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

    private async Task RemoveSelectedPoolAsync()
    {
        if (SelectedPool is not { } pool)
        {
            return;
        }

        if (ConfirmAction is { } confirm && !confirm(
                $"Remove the pool '{pool.Name}'?",
                $"{pool.MountPoint} disappears, but nothing on your drives is deleted.\n\n"
                + "Every pooled file stays on the drive it is already on, in its pool part folder, in the same folders "
                + "you see in the pool.",
                "Remove pool"))
        {
            return;
        }

        await RunPoolCommandAsync(
            Methods.PoolRemove,
            // Pool parts stay on the drives: removing a pool must never delete anything.
            p => new RemovePoolRequest { PoolId = p.PoolId, DeletePoolParts = false },
            "Removing the pool (the files stay on the drives)…").ConfigureAwait(true);
    }

    private Task RebalanceAsync() =>
        RunPoolCommandAsync(
            Methods.RebalanceStart,
            pool => new PoolReference { PoolId = pool.PoolId },
            "Rebalancing in the background…");

    private Task OpenSelectedPoolAsync()
    {
        if (SelectedPool is { IsMounted: true } pool)
        {
            OpenInExplorer?.Invoke(pool.MountPoint);
        }

        return Task.CompletedTask;
    }

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

    private void ClearDriveSelection()
    {
        foreach (var drive in Drives)
        {
            drive.IsSelected = false;
        }
    }

    private void RaiseCommandStates()
    {
        Raise(nameof(CanAddDrives));
        Raise(nameof(CanRemoveDrive));
        Raise(nameof(AddDrivesLabel));

        MountCommand.RaiseCanExecuteChanged();
        UnmountCommand.RaiseCanExecuteChanged();
        RemovePoolCommand.RaiseCanExecuteChanged();
        RebalanceCommand.RaiseCanExecuteChanged();
        AddDrivesCommand.RaiseCanExecuteChanged();
        DetachDriveCommand.RaiseCanExecuteChanged();
        EvacuateDriveCommand.RaiseCanExecuteChanged();
        OpenPoolCommand.RaiseCanExecuteChanged();
    }

    /// <summary>One line describing the whole system, for the tray tooltip.</summary>
    private void AnnounceStatus()
    {
        var summary = !IsConnected
            ? "MergePool — service not reachable"
            : Pools.Count == 0
                ? "MergePool — no pools yet"
                : string.Create(
                    CultureInfo.CurrentCulture,
                    $"MergePool — {Pools.Count} pool(s), {Pools.Count(p => p.Health == "Degraded")} degraded");

        StatusChanged?.Invoke(this, summary);
    }

    public async ValueTask DisposeAsync()
    {
        _timer.Stop();
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _service.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
