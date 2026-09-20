using MergePool.Ipc.Client;
using MergePool.Ipc.Protocol;
using MergePool.Ipc.Server;
using MergePool.Ipc.Transport;
using Xunit;

namespace MergePool.Ipc.Tests;

/// <summary>Server and client talking over a real named pipe, as the service and UI do.</summary>
public sealed class IpcRoundTripTests : IAsyncLifetime
{
    private readonly string _pipeName = "mergepool-test-" + Guid.NewGuid().ToString("N");
    private IpcServer? _server;

    private sealed class EchoHandler : IIpcMethodHandler
    {
        public IReadOnlyList<string> Methods { get; } = ["echo", "boom", "slow"];

        public Task<IpcResponse> HandleAsync(IpcCall call, CancellationToken cancellationToken) =>
            call.Request.Method switch
            {
                "echo" => Task.FromResult(IpcResponse.Success(call.Request.Id, call.Payload?.DeepClone())),
                "boom" => throw new InvalidOperationException("handler exploded"),
                _ => Task.FromResult(IpcResponse.Success(call.Request.Id)),
            };
    }

    private IpcClient NewClient() => new(new IpcClientOptions
    {
        PipeName = _pipeName,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        RequestTimeout = TimeSpan.FromSeconds(30),
    });

    public Task InitializeAsync()
    {
        var router = new IpcRouter().Register(new EchoHandler());
        _server = new IpcServer(
            new IpcServerOptions { PipeName = _pipeName, ServiceVersion = "1.2.3" },
            router);
        _server.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    [Fact]
    public async Task The_handshake_reports_the_version_and_capabilities()
    {
        await using var client = NewClient();
        var hello = await client.ConnectAsync(CancellationToken.None);

        Assert.Equal("1.2.3", hello.ServiceVersion);
        Assert.Equal(ProtocolVersion.Current, hello.ProtocolVersion);
        Assert.Equal(ProtocolVersion.MinimumSupported, hello.MinProtocolVersion);
        Assert.Contains(Capabilities.Pools, hello.Capabilities);
        Assert.True(client.Supports(Capabilities.Metrics));
        Assert.False(client.Supports("capability-from-the-future"));
    }

    [Fact]
    public async Task A_call_round_trips_its_payload()
    {
        await using var client = NewClient();
        await client.ConnectAsync(CancellationToken.None);

        var response = await client.InvokeRawAsync(
            "echo",
            new HelloRequest { ClientName = "unit-test" },
            CancellationToken.None);

        var echoed = IpcFraming.FromNode<HelloRequest>(response.Payload);
        Assert.Equal("unit-test", echoed.ClientName);
    }

    [Fact]
    public async Task Several_calls_share_one_connection()
    {
        await using var client = NewClient();
        await client.ConnectAsync(CancellationToken.None);

        for (var i = 0; i < 5; i++)
        {
            var response = await client.InvokeRawAsync(
                "echo",
                new HelloRequest { ClientVersion = i.ToString() },
                CancellationToken.None);

            Assert.Equal(i.ToString(), IpcFraming.FromNode<HelloRequest>(response.Payload).ClientVersion);
        }
    }

    [Fact]
    public async Task An_unknown_method_is_a_structured_error_not_a_dropped_connection()
    {
        await using var client = NewClient();
        await client.ConnectAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<IpcException>(
            () => client.InvokeRawAsync("method.from.a.newer.ui", null, CancellationToken.None));

        Assert.Equal(IpcErrorCodes.MethodNotSupported, exception.Code);

        // The connection survives, so the UI can carry on with the methods that do exist.
        var response = await client.InvokeRawAsync("echo", null, CancellationToken.None);
        Assert.True(response.Ok);
    }

    [Fact]
    public async Task A_handler_that_throws_becomes_an_internal_error()
    {
        await using var client = NewClient();
        await client.ConnectAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<IpcException>(
            () => client.InvokeRawAsync("boom", null, CancellationToken.None));

        Assert.Equal(IpcErrorCodes.Internal, exception.Code);
        Assert.Contains("handler exploded", exception.Error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_capability_gated_call_returns_null_when_unsupported()
    {
        await using var client = NewClient();
        await client.ConnectAsync(CancellationToken.None);

        var result = await client.InvokeIfSupportedAsync<HelloResult>(
            "capability-that-does-not-exist",
            "echo",
            null,
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task A_method_call_before_the_handshake_is_refused()
    {
        using var pipe = new System.IO.Pipes.NamedPipeClientStream(
            ".", _pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);

        await pipe.ConnectAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

        var request = new IpcRequest { Method = "echo", Id = "1" };
        await IpcFraming.WriteFrameAsync(pipe, IpcFraming.Serialize(request), CancellationToken.None);

        var frame = await IpcFraming.ReadFrameAsync(pipe, CancellationToken.None);
        var response = IpcFraming.Deserialize<IpcResponse>(frame!)!;

        Assert.False(response.Ok);
        Assert.Equal(IpcErrorCodes.HandshakeRequired, response.Error!.Code);
    }

    [Fact]
    public async Task A_client_that_needs_a_future_protocol_is_told_so()
    {
        using var pipe = new System.IO.Pipes.NamedPipeClientStream(
            ".", _pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);

        await pipe.ConnectAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

        var hello = new HelloRequest
        {
            MinProtocolVersion = ProtocolVersion.Current + 1,
            MaxProtocolVersion = ProtocolVersion.Current + 2,
        };

        var request = new IpcRequest { Method = Methods.Hello, Id = "1", Payload = IpcFraming.ToNode(hello) };
        await IpcFraming.WriteFrameAsync(pipe, IpcFraming.Serialize(request), CancellationToken.None);

        var frame = await IpcFraming.ReadFrameAsync(pipe, CancellationToken.None);
        var response = IpcFraming.Deserialize<IpcResponse>(frame!)!;

        Assert.False(response.Ok);
        Assert.Equal(IpcErrorCodes.ProtocolNotSupported, response.Error!.Code);
    }

    [Fact]
    public async Task Malformed_json_is_answered_rather_than_dropping_the_connection()
    {
        using var pipe = new System.IO.Pipes.NamedPipeClientStream(
            ".", _pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);

        await pipe.ConnectAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        await IpcFraming.WriteFrameAsync(pipe, "{not json", CancellationToken.None);

        var frame = await IpcFraming.ReadFrameAsync(pipe, CancellationToken.None);
        var response = IpcFraming.Deserialize<IpcResponse>(frame!)!;

        Assert.Equal(IpcErrorCodes.InvalidRequest, response.Error!.Code);
    }

    [Fact]
    public async Task Several_clients_are_served_at_once()
    {
        await using var first = NewClient();
        await using var second = NewClient();

        var hellos = await Task.WhenAll(
            first.ConnectAsync(CancellationToken.None),
            second.ConnectAsync(CancellationToken.None));

        Assert.All(hellos, hello => Assert.Equal(ProtocolVersion.Current, hello.ProtocolVersion));

        var responses = await Task.WhenAll(
            first.InvokeRawAsync("echo", null, CancellationToken.None),
            second.InvokeRawAsync("echo", null, CancellationToken.None));

        Assert.All(responses, response => Assert.True(response.Ok));
    }

    [Fact]
    public async Task An_old_ui_talking_to_a_newer_service_keeps_working()
    {
        // The service announces a capability the old UI knows nothing about, and the old UI's
        // requests carry only the fields it knows. Neither side breaks.
        using var pipe = new System.IO.Pipes.NamedPipeClientStream(
            ".", _pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);

        await pipe.ConnectAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

        // A minimal handshake, exactly as a much older build would have written it.
        const string legacyHello = """
        {"id":"1","method":"hello","protocolVersion":1,"payload":{"clientName":"old-ui","minProtocolVersion":1,"maxProtocolVersion":1}}
        """;

        await IpcFraming.WriteFrameAsync(pipe, legacyHello, CancellationToken.None);
        var frame = await IpcFraming.ReadFrameAsync(pipe, CancellationToken.None);
        var response = IpcFraming.Deserialize<IpcResponse>(frame!)!;

        Assert.True(response.Ok);
        Assert.Equal(1, IpcFraming.FromNode<HelloResult>(response.Payload).ProtocolVersion);

        const string legacyCall = """{"id":"2","method":"echo","protocolVersion":1}""";
        await IpcFraming.WriteFrameAsync(pipe, legacyCall, CancellationToken.None);

        var second = IpcFraming.Deserialize<IpcResponse>(
            (await IpcFraming.ReadFrameAsync(pipe, CancellationToken.None))!)!;

        Assert.True(second.Ok);
    }
}
