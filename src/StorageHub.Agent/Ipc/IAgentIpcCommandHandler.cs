using System.Text.Json;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Agent.Ipc;

/// <summary>
/// Extends the agent's normal, non-secret IPC surface. Implementations must never return
/// secret-bearing payloads and should expose only stable, user-safe failures.
/// </summary>
public interface IAgentIpcCommandHandler
{
    bool CanHandle(string messageType);

    ValueTask<AgentIpcCommandResponse> HandleAsync(
        IpcEnvelope request,
        CancellationToken cancellationToken = default);
}

/// <summary>A response whose request identity and sequence remain controlled by the IPC host.</summary>
public sealed record AgentIpcCommandResponse
{
    private AgentIpcCommandResponse(string messageType, JsonElement payload)
    {
        if (string.IsNullOrWhiteSpace(messageType) ||
            messageType.StartsWith(IpcProtocol.SecretMessageTypePrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A normal, non-secret response message type is required.", nameof(messageType));
        }

        MessageType = messageType;
        Payload = payload;
    }

    public string MessageType { get; }

    public JsonElement Payload { get; }

    public static AgentIpcCommandResponse Create<TPayload>(string messageType, TPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new AgentIpcCommandResponse(messageType, JsonSerializer.SerializeToElement(payload));
    }

    public static AgentIpcCommandResponse Error(string code, string message) => Create(
        IpcProtocol.ErrorResponseMessageType,
        new IpcErrorResponse(code, message));

    internal IpcEnvelope ToEnvelope(Guid requestId, long sequence) =>
        new(MessageType, requestId, sequence, Payload);
}
