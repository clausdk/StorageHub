using System.Text.Json;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Agent.Ipc;

/// <summary>Handles the recovery-safe control surface exposed by the background agent.</summary>
public sealed class AgentIpcRequestHandler
{
    private readonly Func<AgentStatusSnapshot> _statusProvider;
    private readonly IAgentIpcCommandHandler? _commandHandler;

    public AgentIpcRequestHandler(
        Func<AgentStatusSnapshot> statusProvider,
        IAgentIpcCommandHandler? commandHandler = null)
    {
        _statusProvider = statusProvider ?? throw new ArgumentNullException(nameof(statusProvider));
        _commandHandler = commandHandler;
    }

    public async Task HandleSessionAsync(
        NamedPipeIpcSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        long responseSequence = 0;
        while (!cancellationToken.IsCancellationRequested && session.IsConnected)
        {
            AgentIpcCommandResponse? commandResponse = null;
            IpcEnvelope request;
            try
            {
                request = await session.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }

            IpcEnvelope response;
            if (string.Equals(
                request.MessageType,
                IpcProtocol.AgentStatusRequestMessageType,
                StringComparison.Ordinal))
            {
                try
                {
                    _ = request.DeserializePayload<AgentStatusRequest>();
                    response = IpcEnvelope.Create(
                        IpcProtocol.AgentStatusResponseMessageType,
                        request.RequestId,
                        checked(++responseSequence),
                        _statusProvider());
                }
                catch (Exception error) when (
                    error is JsonException or InvalidOperationException or NotSupportedException)
                {
                    response = CreateError(
                        request.RequestId,
                        checked(++responseSequence),
                        "ipc.payload.invalid",
                        "The request payload was invalid.");
                }
                catch (Exception)
                {
                    response = CreateError(
                        request.RequestId,
                        checked(++responseSequence),
                        "ipc.status.unavailable",
                        "Agent status is temporarily unavailable.");
                }
            }
            else if (_commandHandler is not null)
            {
                try
                {
                    if (!_commandHandler.CanHandle(request.MessageType))
                    {
                        response = CreateUnsupported(request.RequestId, checked(++responseSequence));
                    }
                    else
                    {
                        commandResponse = await _commandHandler
                            .HandleAsync(request, cancellationToken)
                            .ConfigureAwait(false);
                        response = commandResponse.ToEnvelope(
                            request.RequestId,
                            checked(++responseSequence));
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error) when (
                    error is JsonException or InvalidDataException or InvalidOperationException or NotSupportedException)
                {
                    response = CreateError(
                        request.RequestId,
                        checked(++responseSequence),
                        "ipc.payload.invalid",
                        "The request payload was invalid.");
                }
                catch (Exception)
                {
                    response = CreateError(
                        request.RequestId,
                        checked(++responseSequence),
                        "ipc.command.failed",
                        "The requested operation could not be completed.");
                }
            }
            else
            {
                response = CreateUnsupported(request.RequestId, checked(++responseSequence));
            }

            await session.SendAsync(response, cancellationToken).ConfigureAwait(false);
            commandResponse?.NotifyResponseSent();
        }
    }

    private static IpcEnvelope CreateError(
        Guid requestId,
        long sequence,
        string code,
        string message) => IpcEnvelope.Create(
        IpcProtocol.ErrorResponseMessageType,
        requestId,
        sequence,
        new IpcErrorResponse(code, message));

    private static IpcEnvelope CreateUnsupported(Guid requestId, long sequence) => CreateError(
        requestId,
        sequence,
        "ipc.message.unsupported",
        "The requested IPC operation is not supported by this agent version.");
}
