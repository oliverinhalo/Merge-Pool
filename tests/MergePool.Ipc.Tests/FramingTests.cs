using System.Text;
using System.Text.Json;
using MergePool.Ipc.Protocol;
using MergePool.Ipc.Transport;
using Xunit;

namespace MergePool.Ipc.Tests;

public sealed class FramingTests
{
    [Fact]
    public async Task Frames_round_trip()
    {
        using var stream = new MemoryStream();
        await IpcFraming.WriteFrameAsync(stream, """{"a":1}""", CancellationToken.None);
        await IpcFraming.WriteFrameAsync(stream, """{"b":2}""", CancellationToken.None);
        stream.Position = 0;

        Assert.Equal("""{"a":1}""", await IpcFraming.ReadFrameAsync(stream, CancellationToken.None));
        Assert.Equal("""{"b":2}""", await IpcFraming.ReadFrameAsync(stream, CancellationToken.None));
        Assert.Null(await IpcFraming.ReadFrameAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Non_ascii_survives_the_wire()
    {
        const string payload = """{"label":"Sicherungslaufwerk – 日本語"}""";

        using var stream = new MemoryStream();
        await IpcFraming.WriteFrameAsync(stream, payload, CancellationToken.None);
        stream.Position = 0;

        Assert.Equal(payload, await IpcFraming.ReadFrameAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task A_truncated_frame_is_an_error()
    {
        using var stream = new MemoryStream();
        await IpcFraming.WriteFrameAsync(stream, """{"a":1}""", CancellationToken.None);

        var bytes = stream.ToArray();
        using var truncated = new MemoryStream(bytes[..^2]);

        await Assert.ThrowsAsync<EndOfStreamException>(
            () => IpcFraming.ReadFrameAsync(truncated, CancellationToken.None));
    }

    [Fact]
    public async Task An_absurd_frame_length_is_rejected()
    {
        using var stream = new MemoryStream([0xFF, 0xFF, 0xFF, 0x7F]);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => IpcFraming.ReadFrameAsync(stream, CancellationToken.None));
    }

    [Fact]
    public void Unknown_fields_survive_a_round_trip_through_an_older_build()
    {
        // A response from a newer service, carrying a field this build has never heard of.
        const string json = """
        {"id":"abc","ok":true,"payload":{"x":1},"futureField":{"nested":[1,2]}}
        """;

        var response = IpcFraming.Deserialize<IpcResponse>(json)!;
        var again = IpcFraming.Serialize(response);

        using var document = JsonDocument.Parse(again);
        Assert.Equal(2, document.RootElement.GetProperty("futureField").GetProperty("nested").GetArrayLength());
        Assert.True(response.Ok);
    }

    [Fact]
    public void Unknown_contract_fields_survive_too()
    {
        const string json = """{"serviceVersion":"9.9.9","protocolVersion":1,"somethingNew":true}""";

        var hello = IpcFraming.Deserialize<HelloResult>(json)!;
        var again = IpcFraming.Serialize(hello);

        Assert.Contains("somethingNew", again, StringComparison.Ordinal);
        Assert.Equal("9.9.9", hello.ServiceVersion);
    }

    [Fact]
    public async Task An_oversized_message_is_refused_before_it_is_sent()
    {
        var huge = new string('x', IpcFraming.MaxFrameBytes + 16);
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => IpcFraming.WriteFrameAsync(stream, huge, CancellationToken.None));

        Assert.Equal(0, stream.Length);
        Assert.Equal(IpcFraming.MaxFrameBytes + 16, Encoding.UTF8.GetByteCount(huge));
    }
}
