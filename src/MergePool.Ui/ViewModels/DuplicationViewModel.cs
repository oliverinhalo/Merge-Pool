using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using MergePool.Core.Config;
using MergePool.Ipc.Protocol;
using MergePool.Ui.Services;

namespace MergePool.Ui.ViewModels;

/// <summary>One protected folder, as the window shows it.</summary>
public sealed class ProtectedFolderViewModel(DuplicationRuleDto rule) : ViewModelBase
{
    public DuplicationRuleDto Rule { get; } = rule;

    public Guid Id => Rule.Id;

    public string Folder => string.IsNullOrEmpty(Rule.Folder) ? "Everything in this pool" : Rule.Folder;

    public bool IsEnabled => Rule.Enabled;

    public string Copies => Rule.Mode.Equals("AllDrives", StringComparison.OrdinalIgnoreCase)
        ? "A copy on every drive"
        : string.Create(CultureInfo.CurrentCulture, $"{Rule.RequestedCopies} copies");

    /// <summary>The line that answers "is my folder safe yet".</summary>
    public string State
    {
        get
        {
            if (!Rule.Enabled)
            {
                return "Paused. The copies already made are left alone.";
            }

            if (Rule.Files == 0)
            {
                return "Nothing in this folder yet.";
            }

            if (Rule.Satisfied)
            {
                return string.Create(
                    CultureInfo.CurrentCulture,
                    $"All {Rule.Files:N0} file(s) have {Rule.RequestedCopies} copies. {Format(Rule.Bytes)} protected.");
            }

            if (Rule.PossibleCopies < Rule.RequestedCopies)
            {
                return string.Create(
                    CultureInfo.CurrentCulture,
                    $"This pool has {Rule.DrivesAvailable} drive(s), so {Rule.RequestedCopies} copies is not possible. Add a drive.");
            }

            return string.Create(
                CultureInfo.CurrentCulture,
                $"{Rule.ProtectedFiles:N0} of {Rule.Files:N0} file(s) protected. {Format(Rule.PendingBytes)} still to copy.");
        }
    }

    public bool HasProblems => Rule.Problems.Count > 0;

    public string Problems => string.Join(Environment.NewLine, Rule.Problems);

    /// <summary>Green only when the folder is as safe as it was asked to be.</summary>
    public bool IsSatisfied => Rule.Enabled && Rule.Satisfied;

    public string Extras
    {
        get
        {
            var parts = new List<string>(3);

            if (Rule.OnlyOnParts.Count > 0)
            {
                parts.Add(string.Create(CultureInfo.CurrentCulture, $"only on {Rule.OnlyOnParts.Count} chosen drive(s)"));
            }

            if (Rule.VerifyByContent)
            {
                parts.Add("copies checked byte for byte");
            }

            if (!Rule.Surplus.Equals("Remove", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add("extra copies kept");
            }

            return parts.Count == 0 ? string.Empty : string.Join(", ", parts);
        }
    }

    private static string Format(long bytes) => bytes switch
    {
        >= 1L << 40 => string.Create(CultureInfo.CurrentCulture, $"{bytes / (double)(1L << 40):0.##} TB"),
        >= 1L << 30 => string.Create(CultureInfo.CurrentCulture, $"{bytes / (double)(1L << 30):0.##} GB"),
        >= 1L << 20 => string.Create(CultureInfo.CurrentCulture, $"{bytes / (double)(1L << 20):0.##} MB"),
        >= 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1024.0:0.##} KB"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{bytes} B"),
    };
}

/// <summary>
/// Protected folders: which folders inside a pool are kept on more than one drive, how many copies
/// each keeps, and whether they have them yet.
/// </summary>
public sealed class DuplicationViewModel : ViewModelBase
{
    private readonly ServiceConnection _service;
    private readonly Func<CancellationToken> _token;

    private DuplicationResult? _state;
    private Guid _poolId;
    private string _newFolder = string.Empty;
    private int _newCopies = DuplicationRule.DefaultCopies;
    private bool _newEveryDrive;
    private bool _newVerifyByContent;
    private bool _newKeepSurplus;
    private string? _message;
    private bool _busy;
    private ProtectedFolderViewModel? _selected;

    public DuplicationViewModel(ServiceConnection service, Func<CancellationToken> token)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _token = token ?? throw new ArgumentNullException(nameof(token));

        AddCommand = new AsyncCommand(AddAsync, () => !_busy && CanAdd);
        ProtectEverythingCommand = new AsyncCommand(ProtectEverythingAsync, () => !_busy && HasPool);
        RunNowCommand = new AsyncCommand(RunNowAsync, () => !_busy && HasPool);
        RemoveCommand = new AsyncCommand(RemoveSelectedAsync, () => !_busy && Selected is not null);
        TogglePauseCommand = new AsyncCommand(ToggleSelectedAsync, () => !_busy && Selected is not null);
    }

    public AsyncCommand AddCommand { get; }

    public AsyncCommand ProtectEverythingCommand { get; }

    public AsyncCommand RunNowCommand { get; }

    public AsyncCommand RemoveCommand { get; }

    public AsyncCommand TogglePauseCommand { get; }

    /// <summary>Set by the window, which owns asking the user things.</summary>
    public Func<string, string, string, bool>? ConfirmAction { get; set; }

    public ObservableCollection<ProtectedFolderViewModel> Folders { get; } = [];

    public bool IsSupported => _service.Supports(Capabilities.Duplication);

    public bool HasPool => _poolId != Guid.Empty;

    public ProtectedFolderViewModel? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                RemoveCommand.RaiseCanExecuteChanged();
                TogglePauseCommand.RaiseCanExecuteChanged();
                Raise(nameof(PauseLabel));
            }
        }
    }

    public string PauseLabel => Selected?.IsEnabled == false ? "Resume" : "Pause";

    // ---- The new rule being described ----

    /// <summary>A folder inside the pool, as the user types it: <c>Photos</c>, <c>Photos\2026</c>.</summary>
    public string NewFolder
    {
        get => _newFolder;
        set
        {
            if (Set(ref _newFolder, value))
            {
                Raise(nameof(CanAdd));
                AddCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public int NewCopies
    {
        get => _newCopies;
        set
        {
            if (Set(ref _newCopies, Math.Clamp(value, DuplicationRule.MinimumCopies, 16)))
            {
                Raise(nameof(CopiesText));
            }
        }
    }

    /// <summary>A copy on every drive, however many drives the pool turns out to have.</summary>
    public bool NewEveryDrive
    {
        get => _newEveryDrive;
        set
        {
            if (Set(ref _newEveryDrive, value))
            {
                Raise(nameof(CopiesText));
            }
        }
    }

    public bool NewVerifyByContent
    {
        get => _newVerifyByContent;
        set => Set(ref _newVerifyByContent, value);
    }

    /// <summary>Leave copies a rule no longer calls for, rather than deleting them.</summary>
    public bool NewKeepSurplus
    {
        get => _newKeepSurplus;
        set => Set(ref _newKeepSurplus, value);
    }

    public string CopiesText => NewEveryDrive
        ? string.Create(CultureInfo.CurrentCulture, $"A copy on every drive in the pool. Uses the most space and survives the most failures.")
        : string.Create(CultureInfo.CurrentCulture, $"{NewCopies} copies, each on a different drive. Survives losing {NewCopies - 1} drive(s) at once.");

    public bool CanAdd => HasPool && !string.IsNullOrWhiteSpace(NewFolder);

    // ---- Pool-wide state ----

    public bool Enabled
    {
        get => _state?.Enabled != false;
        set => _ = ApplyOptionsAsync(new DuplicationOptionsDto { Enabled = value });
    }

    public string Headline
    {
        get
        {
            if (!IsSupported)
            {
                return "This copy of the MergePool service is too old to protect folders. Update MergePool.";
            }

            if (_state is null)
            {
                return "Loading…";
            }

            if (!_state.Enabled)
            {
                return "Protecting folders is switched off. Nothing is being copied or removed.";
            }

            if (Folders.Count == 0)
            {
                return "No folder in this pool is protected yet.";
            }

            if (_state.CopiesToAdd == 0 && _state.CopiesToRefresh == 0 && _state.CopiesToRemove == 0)
            {
                return "Every protected folder has the copies it asks for.";
            }

            return string.Create(
                CultureInfo.CurrentCulture,
                $"{_state.CopiesToAdd:N0} copy(ies) to make, {_state.CopiesToRefresh:N0} to bring up to date, {_state.CopiesToRemove:N0} to remove.");
        }
    }

    public string? Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public void SetPool(Guid poolId)
    {
        if (_poolId == poolId)
        {
            return;
        }

        _poolId = poolId;
        _state = null;
        Folders.Clear();
        Selected = null;
        RaiseAll();
    }

    public void Apply(DuplicationResult? state)
    {
        _state = state;

        var selectedId = Selected?.Id;
        Folders.Clear();

        foreach (var rule in state?.Rules ?? [])
        {
            Folders.Add(new ProtectedFolderViewModel(rule));
        }

        Selected = Folders.FirstOrDefault(f => f.Id == selectedId) ?? Folders.FirstOrDefault();
        RaiseAll();
    }

    private void RaiseAll()
    {
        Raise(nameof(IsSupported));
        Raise(nameof(HasPool));
        Raise(nameof(Enabled));
        Raise(nameof(Headline));
        Raise(nameof(CanAdd));

        AddCommand.RaiseCanExecuteChanged();
        ProtectEverythingCommand.RaiseCanExecuteChanged();
        RunNowCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
        TogglePauseCommand.RaiseCanExecuteChanged();
    }

    private Task AddAsync() => SetRuleAsync(new DuplicationRuleDto
    {
        Folder = NewFolder.Trim(),
        Enabled = true,
        Mode = NewEveryDrive ? "AllDrives" : "Copies",
        Copies = NewCopies,
        VerifyByContent = NewVerifyByContent,
        Surplus = NewKeepSurplus ? "Keep" : "Remove",
    });

    private Task ProtectEverythingAsync() => SetRuleAsync(new DuplicationRuleDto
    {
        Folder = string.Empty,
        Enabled = true,
        Mode = NewEveryDrive ? "AllDrives" : "Copies",
        Copies = NewCopies,
        VerifyByContent = NewVerifyByContent,
        Surplus = NewKeepSurplus ? "Keep" : "Remove",
    });

    private async Task SetRuleAsync(DuplicationRuleDto rule)
    {
        _busy = true;

        try
        {
            var state = await _service.TryInvokeAsync<DuplicationResult>(
                Methods.DuplicationSetRule,
                new DuplicationRuleRequest { PoolId = _poolId, Rule = rule },
                _token()).ConfigureAwait(true);

            if (state is null)
            {
                Message = _service.LastError ?? "MergePool could not protect that folder.";
                return;
            }

            Apply(state);
            NewFolder = string.Empty;

            Message = string.IsNullOrEmpty(rule.Folder)
                ? "Everything in this pool is now protected. Copies are made in the background."
                : $"'{rule.Folder}' is now protected. Copies are made in the background.";
        }
        catch (Exception exception) when (exception is IpcException or IOException)
        {
            Message = exception.Message;
        }
        finally
        {
            _busy = false;
            RaiseAll();
        }
    }

    private async Task RemoveSelectedAsync()
    {
        if (Selected is not { } folder)
        {
            return;
        }

        var confirmed = ConfirmAction?.Invoke(
            "Stop protecting this folder?",
            $"'{folder.Folder}' will stop being kept on several drives.\n\n"
            + "The copies already on your drives are left exactly where they are — nothing is deleted. "
            + "New and changed files in it simply stop being copied.",
            "Stop protecting it") ?? true;

        if (!confirmed)
        {
            return;
        }

        _busy = true;

        try
        {
            var state = await _service.TryInvokeAsync<DuplicationResult>(
                Methods.DuplicationRemoveRule,
                new DuplicationRuleRequest { PoolId = _poolId, RuleId = folder.Id },
                _token()).ConfigureAwait(true);

            if (state is null)
            {
                Message = _service.LastError ?? "MergePool could not change that folder.";
                return;
            }

            Apply(state);
            Message = $"'{folder.Folder}' is no longer protected. Nothing was deleted.";
        }
        catch (Exception exception) when (exception is IpcException or IOException)
        {
            Message = exception.Message;
        }
        finally
        {
            _busy = false;
            RaiseAll();
        }
    }

    private Task ToggleSelectedAsync()
    {
        if (Selected is not { } folder)
        {
            return Task.CompletedTask;
        }

        var rule = folder.Rule;
        return SetRuleAsync(new DuplicationRuleDto
        {
            Id = rule.Id,
            Folder = rule.Folder,
            Enabled = !rule.Enabled,
            Mode = rule.Mode,
            Copies = rule.Copies,
            OnlyOnParts = rule.OnlyOnParts,
            VerifyByContent = rule.VerifyByContent,
            Surplus = rule.Surplus,
        });
    }

    private async Task RunNowAsync()
    {
        _busy = true;
        Message = "Checking the protected folders…";

        try
        {
            var result = await _service.TryInvokeAsync<DuplicationRunResultDto>(
                Methods.DuplicationRunNow,
                new DuplicationRuleRequest { PoolId = _poolId },
                _token()).ConfigureAwait(true);

            Message = result is null
                ? _service.LastError ?? "MergePool could not run that now."
                : result.CopiesAdded + result.CopiesRefreshed + result.CopiesRemoved == 0
                    ? "Nothing needed doing."
                    : string.Create(
                        CultureInfo.CurrentCulture,
                        $"Made {result.CopiesAdded:N0}, brought {result.CopiesRefreshed:N0} up to date, removed {result.CopiesRemoved:N0}.");
        }
        catch (Exception exception) when (exception is IpcException or IOException)
        {
            Message = exception.Message;
        }
        finally
        {
            _busy = false;
            RaiseAll();
        }
    }

    private async Task ApplyOptionsAsync(DuplicationOptionsDto options)
    {
        _busy = true;

        try
        {
            await _service.TryInvokeAsync<DuplicationResult>(
                Methods.DuplicationSetOptions, options, _token()).ConfigureAwait(true);

            if (options.Enabled is { } enabled)
            {
                Message = enabled
                    ? "Protecting folders is on."
                    : "Protecting folders is off. The copies already made are left alone.";
            }
        }
        catch (Exception exception) when (exception is IpcException or IOException)
        {
            Message = exception.Message;
        }
        finally
        {
            _busy = false;
            RaiseAll();
        }
    }
}
