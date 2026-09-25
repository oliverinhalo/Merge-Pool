using System.IO.Pipes;
using System.Text.Json.Nodes;
using MergePool.Ipc.Protocol;
using MergePool.Ipc.Transport;

namespace MergePool.Ipc.Server;

/// <summary>One request, already parsed, with the connection's negotiated protocol version.</summary>
public sealed record IpcCall
{
    public required IpcRequest Request { get; init; }

    public required int NegotiatedVersion { get; init; }

    public JsonNode? Payload => Request.Payload;

    public T PayloadAs<T>()
        where T : new() => IpcFraming.FromNode<T>(Request.Payload);
}

/// <summary>Handles the service side of a method call.</summary>
public interface IIpcMethodHandler
{
    /// <summary>Methods this handler answers, and the capabilities it implies.</summary>
    IReadOnlyList<string> Methods { get; }

    Task<IpcResponse> HandleAsync(IpcCall call, CancellationToken cancellationToken);
}

/// <summary>
/// Routes calls to handlers. An unknown method is answered with a structured error rather than a
/// dropped connection, so a newer UI talking to an older service degrades instead of breaking.
/// </summary>
public sealed class IpcRouter
{
    private readonly Dictionary<string, IIpcMethodHandler> _handlers = new(StringComparer.Ordinal);

    public IpcRouter Register(IIpcMethodHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        foreach (var method in handler.Methods)
        {
            _handlers[method] = handler;
        }

        return this;
    }

    public IReadOnlyCollection<string> KnownMethods => _handlers.Keys;

    public async Task<IpcResponse> DispatchAsync(IpcCall call, CancellationToken cancellationToken)
    {
        if (!_handlers.TryGetValue(call.Request.Method, out var handler))
        {
            return IpcResponse.Failure(
                call.Request.Id,
                IpcErrorCodes.MethodNotSupported,
                $"This service does not support '{call.Request.Method}'.",
                $"Supported methods: {string.Join(", ", _handlers.Keys.Order(StringComparer.Ordinal))}");
        }

        try
        {
            return await handler.HandleAsync(call, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return IpcResponse.Failure(
                call.Request.Id,
                IpcErrorCodes.Internal,
                "The service failed to handle the request.",
                exception.Message);
        }
    }
}

public sealed record IpcServerOptions
{
    public required string PipeName { get; init; }

    public string ServiceVersion { get; init; } = "0.0.0";

    public int MaxConcurrentClients { get; init; } = 8;

    public IReadOnlyList<string> Capabilities { get; init; } = Protocol.Capabilities.All;

    /// <summary>
    /// Creates the pipe. The Windows service supplies one that applies a security descriptor;
    /// leaving it null uses the default, which is what tests want.
    /// </summary>
    public Func<string, int, NamedPipeServerStream>? PipeFactory { get; init; }

    /// <summary>Reported in the handshake so a UI can tell the user an upgrade is in progress.</summary>
    public Func<bool>? IsDraining { get; init; }
}

/// <summary>
/// Named-pipe server. Each connection starts with a handshake that fixes the protocol version for
/// the rest of that connection, so a long-lived UI is never surprised by a mid-stream change.
/// </summary>
public sealed class IpcServer(IpcServerOptions options, IpcRouter router) : IAsyncDisposable
{
    private readonly IpcServerOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly IpcRouter _router = router ?? throw new ArgumentNullException(nameof(router));
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Task> _listeners = [];

    public string PipeName => _options.PipeName;

    public bool IsRunning { get; private set; }

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        IsRunning = true;
        for (var i = 0; i < Math.Max(1, _options.MaxConcurrentClients); i++)
        {
            _listeners.Add(Task.Run(() => ListenLoopAsync(_stopping.Token)));
        }
    }

    public async Task StopAsync()
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;
        await _stopping.CancelAsync().ConfigureAwait(false);

        try
        {
            await Task.WhenAll(_listeners).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected while shutting down.
        }
        catch (IOException)
        {
            // A client dropping as we close is not a failure.
        }

        _listeners.Clear();
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                await ServeAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // Broken pipe: drop this connection and accept the next one.
            }
            catch (InvalidDataException)
            {
                // A peer sent a frame we could not parse; the connection is not salvageable.
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        var instances = Math.Max(1, _options.MaxConcurrentClients);
        if (_options.PipeFactory is { } factory)
        {
            return factory(_options.PipeName, instances);
        }

        return new NamedPipeServerStream(
            _options.PipeName,
            PipeDirection.InOut,
            instances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
    }

    /// <summary>Serves one connection until the client disconnects.</summary>
    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        int? negotiated = null;

        while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
        {
            var frame = await IpcFraming.ReadFrameAsync(pipe, cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                return;
            }

            IpcRequest? request;
            try
            {
                request = IpcFraming.Deserialize<IpcRequest>(frame);
            }
            catch (System.Text.Json.JsonException exception)
            {
                await Respond(
                    pipe,
                    IpcResponse.Failure(string.Empty, IpcErrorCodes.InvalidRequest, "Malformed request.", exception.Message),
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (request is null || string.IsNullOrEmpty(request.Method))
            {
                await Respond(
                    pipe,
                    IpcResponse.Failure(request?.Id ?? string.Empty, IpcErrorCodes.InvalidRequest, "A method is required."),
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            var response = request.Method == Methods.Hello
                ? Handshake(request, out negotiated)
                : negotiated is null
                    ? IpcResponse.Failure(
                        request.Id,
                        IpcErrorCodes.HandshakeRequired,
                        "Send 'hello' before any other method.")
                    : await _router.DispatchAsync(
                        new IpcCall { Request = request, NegotiatedVersion = negotiated.Value },
                        cancellationToken).ConfigureAwait(false);

            await Respond(pipe, response, cancellationToken).ConfigureAwait(false);
        }
    }

    private IpcResponse Handshake(IpcRequest request, out int? negotiated)
    {
        var hello = IpcFraming.FromNode<HelloRequest>(request.Payload);
        var version = ProtocolVersion.Negotiate(hello.MinProtocolVersion, hello.MaxProtocolVersion);

        if (version is null)
        {
            negotiated = null;
            return IpcResponse.Failure(
                request.Id,
                IpcErrorCodes.ProtocolNotSupported,
                $"This service speaks protocol {ProtocolVersion.MinimumSupported}-{ProtocolVersion.Current}; "
                + $"the client offered {hello.MinProtocolVersion}-{hello.MaxProtocolVersion}.");
        }

        negotiated = version;

        var result = new HelloResult
        {
            ServiceVersion = _options.ServiceVersion,
            ProtocolVersion = version.Value,
            MinProtocolVersion = ProtocolVersion.MinimumSupported,
            MaxProtocolVersion = ProtocolVersion.Current,
            Capabilities = [.. _options.Capabilities],
            Draining = _options.IsDraining?.Invoke() ?? false,
        };

        return IpcResponse.Success(request.Id, IpcFraming.ToNode(result));
    }

    private static Task Respond(Stream stream, IpcResponse response, CancellationToken cancellationToken) =>
        IpcFraming.WriteFrameAsync(stream, IpcFraming.Serialize(response), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }
}
