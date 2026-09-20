using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MergePool.Ipc.Protocol;

/// <summary>A request from the UI to the service.</summary>
public sealed class IpcRequest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    /// <summary>Protocol version this request is written against, fixed by the handshake.</summary>
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = Protocol.ProtocolVersion.Current;

    [JsonPropertyName("payload")]
    public JsonNode? Payload { get; set; }

    /// <summary>Anything a newer client sent that this build does not know about.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; set; } = [];
}

public sealed class IpcResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("payload")]
    public JsonNode? Payload { get; set; }

    [JsonPropertyName("error")]
    public IpcError? Error { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; set; } = [];

    public static IpcResponse Success(string id, JsonNode? payload = null) =>
        new() { Id = id, Ok = true, Payload = payload };

    public static IpcResponse Failure(string id, string code, string message, string? detail = null) =>
        new()
        {
            Id = id,
            Ok = false,
            Error = new IpcError { Code = code, Message = message, Detail = detail },
        };
}

public sealed class IpcError
{
    /// <summary>One of <see cref="IpcErrorCodes"/>. Clients branch on this, not on the message.</summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = IpcErrorCodes.Internal;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; set; } = [];
}

/// <summary>Raised on the client when the service answers with an error.</summary>
public sealed class IpcException(IpcError error) : Exception(error.Message)
{
    public IpcError Error { get; } = error;

    public string Code => Error.Code;
}
