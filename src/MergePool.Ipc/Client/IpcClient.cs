using System.IO.Pipes;
using System.Reflection;
using MergePool.Ipc.Protocol;
using MergePool.Ipc.Transport;

namespace MergePool.Ipc.Client;

public sealed record IpcClientOptions
{
    public required string PipeName { get; init; }

    public string ServerName { get; init; } = ".";

    public string ClientName { get; init; } = "MergePool.Ui";

    public string ClientVersion { get; init; } =
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// The UI's side of the pipe. It performs the handshake on connect and remembers what the service
/// said it can do, so the UI disables features rather than failing calls when it is newer than the
/// service it is talking to.
/// </summary>
public sealed class IpcClient(IpcClientOptions options) : IAsyncDisposable
{
    private readonly IpcClientOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private NamedPipeClientStream? _pipe;

    public HelloResult? Handshake { get; private set; }

    public bool IsConnected => _pipe?.IsConnected == true;

    /// <summary>Protocol version in use, or 0 when not connected.</summary>
    public int ProtocolVersion => Handshake?.ProtocolVersion ?? 0;

    /// <summary>True when the service announced this capability. Use it to gate UI features.</summary>
    public bool Supports(string capability) =>
        Handshake?.Capabilities.Contains(capability, StringComparer.Ordinal) == true;

    public async Task<HelloResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pipe is { IsConnected: true } && Handshake is not null)
            {
                return Handshake;
            }

            await DisposePipeAsync().ConfigureAwait(false);

            var pipe = new NamedPipeClientStream(
                _options.ServerName,
                _options.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ConnectTimeout);

            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            _pipe = pipe;

            var hello = new HelloRequest
            {
                ClientName = _options.ClientName,
                ClientVersion = _options.ClientVersion,
                MinProtocolVersion = Protocol.ProtocolVersion.MinimumSupported,
                MaxProtocolVersion = Protocol.ProtocolVersion.Current,
            };

            var response = await SendAsync(Methods.Hello, hello, cancellationToken).ConfigureAwait(false);
            Handshake = IpcFraming.FromNode<HelloResult>(response.Payload);
            return Handshake;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>Calls a method and deserializes the result. Throws <see cref="IpcException"/> on error.</summary>
    public async Task<TResult> InvokeAsync<TResult>(
        string method,
        object? payload = null,
        CancellationToken cancellationToken = default)
        where TResult : new()
    {
        var response = await InvokeRawAsync(method, payload, cancellationToken).ConfigureAwait(false);
        return IpcFraming.FromNode<TResult>(response.Payload);
    }

    public async Task<IpcResponse> InvokeRawAsync(
        string method,
        object? payload = null,
        CancellationToken cancellationToken = default)
    {
        if (Handshake is null || _pipe is not { IsConnected: true })
        {
            await ConnectAsync(cancellationToken).ConfigureAwait(false);
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SendAsync(method, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>Calls a method only if the service announced the capability behind it.</summary>
    public async Task<TResult?> InvokeIfSupportedAsync<TResult>(
        string capability,
        string method,
        object? payload = null,
        CancellationToken cancellationToken = default)
        where TResult : class, new()
    {
        if (Handshake is null)
        {
            await ConnectAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!Supports(capability))
        {
            return null;
        }

        try
        {
            return await InvokeAsync<TResult>(method, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (IpcException exception) when (exception.Code == IpcErrorCodes.MethodNotSupported)
        {
            // An older service than the capability list suggested: treat it as unavailable.
            return null;
        }
    }

    private async Task<IpcResponse> SendAsync(string method, object? payload, CancellationToken cancellationToken)
    {
        var pipe = _pipe ?? throw new InvalidOperationException("Not connected.");

        var request = new IpcRequest
        {
            Method = method,
            ProtocolVersion = Handshake?.ProtocolVersion ?? Protocol.ProtocolVersion.Current,
            Payload = payload is null ? null : IpcFraming.ToNode(payload),
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);

        await IpcFraming.WriteFrameAsync(pipe, IpcFraming.Serialize(request), timeout.Token).ConfigureAwait(false);

        var frame = await IpcFraming.ReadFrameAsync(pipe, timeout.Token).ConfigureAwait(false)
            ?? throw new IOException("The service closed the connection.");

        var response = IpcFraming.Deserialize<IpcResponse>(frame)
            ?? throw new InvalidDataException("The service sent an empty response.");

        if (!response.Ok)
        {
            throw new IpcException(response.Error ?? new IpcError { Message = "Unknown error." });
        }

        return response;
    }

    private async ValueTask DisposePipeAsync()
    {
        if (_pipe is null)
        {
            return;
        }

        await _pipe.DisposeAsync().ConfigureAwait(false);
        _pipe = null;
        Handshake = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisposePipeAsync().ConfigureAwait(false);
        _mutex.Dispose();
    }
}
