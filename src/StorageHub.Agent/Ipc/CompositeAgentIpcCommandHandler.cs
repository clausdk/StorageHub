using StorageHub.Contracts.Ipc;

namespace StorageHub.Agent.Ipc;

/// <summary>Routes normal IPC commands across independently testable feature handlers.</summary>
public sealed class CompositeAgentIpcCommandHandler : IAgentIpcCommandHandler
{
    private readonly IAgentIpcCommandHandler[] _handlers;

    public CompositeAgentIpcCommandHandler(params IAgentIpcCommandHandler[] handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        if (handlers.Length == 0 || handlers.Any(handler => handler is null))
        {
            throw new ArgumentException("At least one non-null IPC command handler is required.", nameof(handlers));
        }

        _handlers = [.. handlers];
    }

    public bool CanHandle(string messageType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        return _handlers.Any(handler => handler.CanHandle(messageType));
    }

    public ValueTask<AgentIpcCommandResponse> HandleAsync(
        IpcEnvelope request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var matches = _handlers.Where(handler => handler.CanHandle(request.MessageType)).Take(2).ToArray();
        return matches.Length switch
        {
            1 => matches[0].HandleAsync(request, cancellationToken),
            0 => throw new NotSupportedException("No IPC command handler owns this message type."),
            _ => throw new InvalidOperationException("More than one IPC command handler owns this message type.")
        };
    }
}
