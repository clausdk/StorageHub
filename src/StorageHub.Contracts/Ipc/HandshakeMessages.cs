namespace StorageHub.Contracts.Ipc;

public sealed record HelloRequest(
    ProtocolVersion ProtocolVersion,
    string ClientName,
    string ClientVersion,
    Guid ClientInstanceId);

public sealed record HelloResponse(
    ProtocolVersion ProtocolVersion,
    bool Accepted,
    string AgentVersion,
    Guid AgentInstanceId,
    string? RejectionReason = null);
