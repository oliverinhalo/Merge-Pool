using System.IO;
using MergePool.Ipc.Client;
using MergePool.Ipc.Protocol;

namespace MergePool.Ui.Services;

/// <summary>
/// The UI's link to the service. It reconnects on its own, because the service is restarted by
/// every upgrade and the UI must simply carry on afterwards.
/// </summary>
public sealed class ServiceConnection : IAsyncDisposable
{
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly string _pipeName;

    private IpcClient? _client;

    public ServiceConnection(string pipeName = "MergePool.Engine") => _pipeName = pipeName;

    public HelloResult? Handshake { get; private set; }

    public bool IsConnected => _client?.IsConnected == true;

    /// <summary>Last connection problem, for the UI to show instead of failing silently.</summary>
    public string? LastError { get; private set; }

    public bool Supports(string capability) => _client?.Supports(capability) == true;

    public async Task<bool> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (IsConnected)
        {
            return true;
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected)
            {
                return true;
            }

            if (_client is not null)
            {
                await _client.DisposeAsync().ConfigureAwait(false);
            }

            _client = new IpcClient(new IpcClientOptions
            {
                PipeName = _pipeName,
                ClientName = "MergePool.Ui",
                ConnectTimeout = TimeSpan.FromSeconds(5),
            });

            Handshake = await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            LastError = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            LastError = "The MergePool service is not running or cannot be reached.";
            Handshake = null;
            return false;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// Calls the service, reconnecting once if the connection died. Returns <c>null</c> when the
    /// service is unreachable, so callers show stale data rather than crashing.
    /// </summary>
    public async Task<TResult?> TryInvokeAsync<TResult>(
        string method,
        object? payload = null,
        CancellationToken cancellationToken = default)
        where TResult : class, new()
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            try
            {
                return await _client!.InvokeAsync<TResult>(method, payload, cancellationToken).ConfigureAwait(false);
            }
            catch (IpcException exception)
            {
                LastError = string.IsNullOrWhiteSpace(exception.Error.Detail)
                    ? exception.Message
                    : $"{exception.Message} ({exception.Error.Detail})";
                return null;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or ObjectDisposedException)
            {
                // The service went away, most likely an upgrade. Drop the client and try once more.
                LastError = "Lost the connection to the MergePool service; reconnecting.";
                await ResetAsync().ConfigureAwait(false);
            }
        }

        return null;
    }

    /// <summary>Like <see cref="TryInvokeAsync{T}"/> but surfaces the service's error to the caller.</summary>
    public async Task<TResult> InvokeAsync<TResult>(
        string method,
        object? payload = null,
        CancellationToken cancellationToken = default)
        where TResult : class, new()
    {
        if (!await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new IOException(LastError ?? "The MergePool service is not reachable.");
        }

        return await _client!.InvokeAsync<TResult>(method, payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task ResetAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }

        Handshake = null;
    }

    public async ValueTask DisposeAsync()
    {
        await ResetAsync().ConfigureAwait(false);
        _mutex.Dispose();
    }
}
