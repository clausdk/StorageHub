using StorageHub.Contracts.Ipc;

namespace StorageHub.Agent.Ipc;

/// <summary>
/// Owns the authenticated, current-user-only control commands used by the desktop and installer.
/// Shutdown begins only after the acknowledgement has been written to the client.
/// </summary>
public sealed class AgentControlIpcCommandService : IAgentIpcCommandHandler
{
    private const string InvalidRequestCode = "agent.control.request.invalid";
    private readonly Action _requestShutdown;
    private readonly int _processId;

    public AgentControlIpcCommandService(Action requestShutdown, int processId)
    {
        _requestShutdown = requestShutdown ?? throw new ArgumentNullException(nameof(requestShutdown));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);

        _processId = processId;
    }

    public bool CanHandle(string messageType) =>
        string.Equals(messageType, AgentControlIpcMessageTypes.ShutdownRequest, StringComparison.Ordinal);

    public ValueTask<AgentIpcCommandResponse> HandleAsync(
        IpcEnvelope request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanHandle(request.MessageType))
        {
            throw new NotSupportedException("The agent control command is not supported.");
        }

        var payload = request.DeserializePayload<AgentShutdownRequest>();
        if (!payload.HasValidBounds)
        {
            return ValueTask.FromResult(AgentIpcCommandResponse.Error(
                InvalidRequestCode,
                "The agent control request is invalid."));
        }

        return ValueTask.FromResult(AgentIpcCommandResponse.CreateWithPostSendAction(
            AgentControlIpcMessageTypes.ShutdownResponse,
            new AgentShutdownResponse(
                AgentControlIpcContract.CurrentVersion,
                Accepted: true,
                _processId),
            _requestShutdown));
    }
}
