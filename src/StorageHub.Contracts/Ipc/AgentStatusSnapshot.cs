using System.Text.Json.Serialization;

namespace StorageHub.Contracts.Ipc;

[JsonConverter(typeof(JsonStringEnumConverter<AgentLifecycleState>))]
public enum AgentLifecycleState
{
    Starting,
    Ready,
    Degraded,
    Stopping,
    Faulted
}

public sealed record AgentStatusSnapshot(
    Guid AgentInstanceId,
    AgentLifecycleState State,
    DateTimeOffset ObservedAtUtc,
    int ActiveTransfers,
    int ActiveSyncRuns,
    string? Detail);

public sealed record AgentStatusRequest;

public sealed record IpcErrorResponse(string Code, string Message);
