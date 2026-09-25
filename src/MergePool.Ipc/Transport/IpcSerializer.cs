using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MergePool.Ipc.Transport;

/// <summary>
/// Length-prefixed UTF-8 JSON frames. Simple on purpose: the wire format is part of the
/// compatibility contract, so it must stay readable and trivially parseable by any build.
/// </summary>
public static class IpcFraming
{
    /// <summary>Guards against a malformed or hostile peer claiming an enormous frame.</summary>
    public const int MaxFrameBytes = 16 * 1024 * 1024;

    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.General)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static async Task WriteFrameAsync(Stream stream, string json, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        if (payload.Length > MaxFrameBytes)
        {
            throw new InvalidOperationException($"Message of {payload.Length} bytes exceeds the frame limit.");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one frame, or <c>null</c> when the peer closed the connection cleanly.</summary>
    public static async Task<string?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        if (!await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 0 or > MaxFrameBytes)
        {
            throw new InvalidDataException($"Frame length {length} is out of range.");
        }

        if (length == 0)
        {
            return string.Empty;
        }

        var payload = new byte[length];
        if (!await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            throw new EndOfStreamException("The connection ended mid-frame.");
        }

        return Encoding.UTF8.GetString(payload);
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return offset != 0
                    ? throw new EndOfStreamException("The connection ended mid-frame.")
                    : false;
            }

            offset += read;
        }

        return true;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static JsonNode? ToNode<T>(T value) =>
        value is null ? null : JsonSerializer.SerializeToNode(value, Options);

    /// <summary>Reads a payload, returning defaults when the peer sent nothing.</summary>
    public static T FromNode<T>(JsonNode? node)
        where T : new() =>
        node is null ? new T() : node.Deserialize<T>(Options) ?? new T();
}
