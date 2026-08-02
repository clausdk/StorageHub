using System.Text.Json;

namespace StorageHub.Contracts.Ipc;

/// <summary>A transport-neutral, sequenced IPC message envelope.</summary>
public sealed record IpcEnvelope(
    string MessageType,
    Guid RequestId,
    long Sequence,
    JsonElement Payload)
{
    public static IpcEnvelope Create<TPayload>(
        string messageType,
        Guid requestId,
        long sequence,
        TPayload payload,
        JsonSerializerOptions? serializerOptions = null)
    {
        if (string.IsNullOrWhiteSpace(messageType))
        {
            throw new ArgumentException("A message type is required.", nameof(messageType));
        }

        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty request ID is required.", nameof(requestId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        ArgumentNullException.ThrowIfNull(payload);
        return new IpcEnvelope(
            messageType.Trim(),
            requestId,
            sequence,
            JsonSerializer.SerializeToElement(payload, serializerOptions));
    }

    public TPayload DeserializePayload<TPayload>(JsonSerializerOptions? serializerOptions = null) =>
        Payload.Deserialize<TPayload>(serializerOptions)
        ?? throw new JsonException($"The '{MessageType}' payload deserialized to null.");
}
