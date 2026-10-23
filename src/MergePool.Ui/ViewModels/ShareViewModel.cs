using System.Collections.ObjectModel;
using System.Globalization;
using MergePool.Ipc.Protocol;
using MergePool.Ui.Services;

namespace MergePool.Ui.ViewModels;

/// <summary>
/// Sharing the selected pool on the network over SMB, which is how containers reach it.
/// </summary>
/// <remarks>
/// <para>Docker Desktop cannot bind-mount a MergePool drive — there is no block device behind a
/// WinFsp volume for its Linux VM to recognise — so a container reaches the pool over SMB or not at
/// all. Doing that by hand means creating the share, and then putting it back every time the pool
/// remounts. The service keeps it in step instead.</para>
/// <para>Nothing here shares anything until an account has been named. The window says so rather
/// than offering a switch that silently does nothing.</para>
/// </remarks>
public sealed class ShareViewModel : ViewModelBase
{
    private readonly ServiceConnection _service;
    private readonly Func<CancellationToken> _token;

    private PoolShareDto? _state;
    private Guid _poolId;
    private string _newAccount = string.Empty;
    private string? _selectedAccount;
    private string? _message;
    private bool _busy;

    public ShareViewModel(ServiceConnection service, Func<CancellationToken> token)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _token = token ?? throw new ArgumentNullException(nameof(token));

        AddAccountCommand = new AsyncCommand(AddAccountAsync, () => !_busy && CanAddAccount);
        RemoveAccountCommand = new AsyncCommand(RemoveAccountAsync, () => !_busy && SelectedAccount is not null);
        CopyDockerCommand = new AsyncCommand(CopyDockerAsync, () => DockerCommand is { Length: > 0 });
        CopyPathCommand = new AsyncCommand(CopyPathAsync, () => UncPath is { Length: > 0 });
    }

    /// <summary>Set by the window, so the view model never touches the clipboard itself.</summary>
    public Action<string>? CopyToClipboard { get; set; }

    public AsyncCommand AddAccountCommand { get; }

    public AsyncCommand RemoveAccountCommand { get; }

    public AsyncCommand CopyDockerCommand { get; }

    public AsyncCommand CopyPathCommand { get; }

    public ObservableCollection<string> Accounts { get; } = [];

    public bool IsSupported => _service.Supports(Capabilities.NetworkShare);

    public bool HasPool => _poolId != Guid.Empty;

    public bool Enabled
    {
        get => _state?.Enabled ?? false;
        set => _ = ApplyAsync(new PoolShareRequest { PoolId = _poolId, Enabled = value });
    }

    public bool ReadOnly
    {
        get => _state?.ReadOnly ?? false;
        set => _ = ApplyAsync(new PoolShareRequest { PoolId = _poolId, ReadOnly = value });
    }

    public string ShareName
    {
        get => _state?.Name ?? string.Empty;
        set => _ = ApplyAsync(new PoolShareRequest { PoolId = _poolId, Name = value });
    }

    public string NewAccount
    {
        get => _newAccount;
        set
        {
            if (Set(ref _newAccount, value))
            {
                Raise(nameof(CanAddAccount));
                AddAccountCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (Set(ref _selectedAccount, value))
            {
                RemoveAccountCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanAddAccount => HasPool && !string.IsNullOrWhiteSpace(NewAccount);

    public string? UncPath => _state?.UncPath;

    public string? DockerCommand => _state?.DockerVolumeCommand;

    public string? Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    /// <summary>What the share is doing, in a sentence, including why it is not doing it.</summary>
    public string StateText
    {
        get
        {
            if (!IsSupported)
            {
                return "This copy of the MergePool service is too old to share a pool. Update MergePool.";
            }

            if (!HasPool)
            {
                return "Select a pool on the left to share it.";
            }

            if (_state is null)
            {
                return "Loading…";
            }

            if (!_state.Enabled)
            {
                return "Not shared. Turn this on to reach the pool from another machine, or from a "
                    + "container that cannot use the drive letter.";
            }

            if (_state.Problem is { Length: > 0 } problem)
            {
                return $"Not shared yet: {problem}";
            }

            if (!_state.IsShared)
            {
                return "Setting the share up…";
            }

            return string.Create(
                CultureInfo.CurrentCulture,
                $"Shared as {_state.UncPath} ({(_state.ReadOnly ? "read-only" : "read and write")}).");
        }
    }

    /// <summary>A warning when the share exists but Windows will not let anything reach it.</summary>
    public string? FirewallNote => _state is { IsShared: true, FileSharingEnabled: false }
        ? "Windows' File and Printer Sharing rules are off, so nothing on the network can reach this "
          + "share even though it exists. Turn them on for your private network profile. MergePool "
          + "does not change them for you: opening port 445 to a network is your decision, not its."
        : null;

    public bool IsShared => _state?.IsShared ?? false;

    public void SetPool(Guid poolId)
    {
        if (_poolId == poolId)
        {
            return;
        }

        _poolId = poolId;
        _state = null;
        Accounts.Clear();
        RaiseAll();
    }

    public async Task LoadAsync()
    {
        if (!HasPool || !IsSupported)
        {
            return;
        }

        var state = await _service
            .TryInvokeAsync<PoolShareDto>(Methods.ShareGet, new PoolReference { PoolId = _poolId }, _token())
            .ConfigureAwait(true);

        if (state is not null)
        {
            Apply(state);
        }
    }

    private void Apply(PoolShareDto state)
    {
        _state = state;

        var selected = SelectedAccount;
        Accounts.Clear();
        foreach (var account in state.AllowedAccounts)
        {
            Accounts.Add(account);
        }

        SelectedAccount = Accounts.Contains(selected ?? string.Empty) ? selected : Accounts.FirstOrDefault();
        RaiseAll();
    }

    private Task AddAccountAsync()
    {
        var accounts = Accounts.ToList();
        accounts.Add(NewAccount.Trim());
        NewAccount = string.Empty;

        return ApplyAsync(new PoolShareRequest { PoolId = _poolId, AllowedAccounts = accounts });
    }

    private Task RemoveAccountAsync()
    {
        if (SelectedAccount is not { } account)
        {
            return Task.CompletedTask;
        }

        var accounts = Accounts.Where(a => !string.Equals(a, account, StringComparison.OrdinalIgnoreCase)).ToList();

        return ApplyAsync(new PoolShareRequest { PoolId = _poolId, AllowedAccounts = accounts });
    }

    private Task CopyDockerAsync()
    {
        if (DockerCommand is { Length: > 0 } command)
        {
            CopyToClipboard?.Invoke(command);
            Message = "Copied. Replace USER and PASSWORD with the account above before running it.";
        }

        return Task.CompletedTask;
    }

    private Task CopyPathAsync()
    {
        if (UncPath is { Length: > 0 } path)
        {
            CopyToClipboard?.Invoke(path);
            Message = "Copied.";
        }

        return Task.CompletedTask;
    }

    private async Task ApplyAsync(PoolShareRequest request)
    {
        if (!HasPool)
        {
            return;
        }

        _busy = true;
        RaiseCommands();

        try
        {
            var state = await _service
                .TryInvokeAsync<PoolShareDto>(Methods.ShareSet, request, _token())
                .ConfigureAwait(true);

            if (state is null)
            {
                Message = _service.LastError ?? "That could not be saved.";
                return;
            }

            Apply(state);

            // The service creates and removes the share on its own schedule, so what the window
            // shows a moment after a change is the setting, not yet the share.
            Message = state is { Enabled: true, IsShared: false, Problem: null }
                ? "Saved. The share appears within a few seconds."
                : "Saved.";
        }
        finally
        {
            _busy = false;
            RaiseCommands();
        }
    }

    private void RaiseAll()
    {
        Raise(nameof(IsSupported));
        Raise(nameof(HasPool));
        Raise(nameof(Enabled));
        Raise(nameof(ReadOnly));
        Raise(nameof(ShareName));
        Raise(nameof(UncPath));
        Raise(nameof(DockerCommand));
        Raise(nameof(StateText));
        Raise(nameof(FirewallNote));
        Raise(nameof(IsShared));
        Raise(nameof(CanAddAccount));
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        AddAccountCommand.RaiseCanExecuteChanged();
        RemoveAccountCommand.RaiseCanExecuteChanged();
        CopyDockerCommand.RaiseCanExecuteChanged();
        CopyPathCommand.RaiseCanExecuteChanged();
    }
}
