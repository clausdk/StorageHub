using System.Security.Cryptography;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Agent.Ipc;

public interface IAgentSecretIpcCommandHandler
{
    bool CanHandle(string messageType);

    ValueTask<AgentSecretIpcCommandResponse> HandleAsync(
        SecretIpcRequestEnvelope request,
        CancellationToken cancellationToken = default);
}

public sealed record AgentSecretIpcCommandResponse
{
    public AgentSecretIpcCommandResponse(string messageType, SecretVaultResponse payload)
    {
        if (string.IsNullOrWhiteSpace(messageType) ||
            !messageType.StartsWith(IpcProtocol.SecretMessageTypePrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("A secret response message type is required.", nameof(messageType));
        }

        MessageType = messageType;
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
    }

    public string MessageType { get; }

    public SecretVaultResponse Payload { get; }
}

/// <summary>Owns one request at a time on the dedicated secret-only pipe.</summary>
public sealed class AgentSecretIpcRequestHandler
{
    private readonly IAgentSecretIpcCommandHandler _commandHandler;

    public AgentSecretIpcRequestHandler(IAgentSecretIpcCommandHandler commandHandler) =>
        _commandHandler = commandHandler ?? throw new ArgumentNullException(nameof(commandHandler));

    public async Task HandleSessionAsync(
        NamedPipeIpcSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        long responseSequence = 0;
        while (!cancellationToken.IsCancellationRequested && session.IsConnected)
        {
            SecretIpcRequestEnvelope request;
            try
            {
                request = await session.ReceiveSecretAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }

            AgentSecretIpcCommandResponse commandResponse;
            var operation = request.Payload?.Operation ?? default;
            try
            {
                commandResponse = request.Payload is not null && _commandHandler.CanHandle(request.MessageType)
                    ? await _commandHandler.HandleAsync(request, cancellationToken).ConfigureAwait(false)
                    : Error(
                        operation,
                        request.Payload is null
                            ? "secret.ipc.payload.invalid"
                            : "secret.ipc.message.unsupported",
                        request.Payload is null
                            ? StorageIpcFailureCategory.Validation
                            : StorageIpcFailureCategory.Unsupported,
                        request.Payload is null
                            ? "The secret request payload was invalid."
                            : "The requested secret operation is not supported.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                commandResponse = Error(
                    operation,
                    "secret.ipc.command.failed",
                    StorageIpcFailureCategory.Unavailable,
                    "The requested secret operation could not be completed.");
            }
            finally
            {
                if (request.Payload?.SecretMaterial is { } material)
                {
                    CryptographicOperations.ZeroMemory(material);
                }
            }

            await session.SendSecretAsync(
                new SecretIpcResponseEnvelope(
                    commandResponse.MessageType,
                    request.RequestId,
                    checked(++responseSequence),
                    commandResponse.Payload),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static AgentSecretIpcCommandResponse Error(
        SecretVaultOperation operation,
        string code,
        StorageIpcFailureCategory category,
        string message) => new(
        SecretVaultIpcMessageTypes.ErrorResponse,
        new SecretVaultResponse(
            SecretVaultIpcContract.CurrentVersion,
            operation,
            Succeeded: false,
            Failure: new StorageIpcFailure(code, category, message, IsTransient: false)));
}
