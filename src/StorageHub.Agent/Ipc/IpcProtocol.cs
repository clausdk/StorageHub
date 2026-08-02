using StorageHub.Contracts.Ipc;

namespace StorageHub.Agent.Ipc;

public static class IpcProtocol
{
    public const string HelloRequestMessageType = "hello.request";

    public const string HelloResponseMessageType = "hello.response";

    public const string SecretMessageTypePrefix = "secret.";

    public const string AgentStatusRequestMessageType = "agent.status.request";

    public const string AgentStatusResponseMessageType = "agent.status.response";

    public const string ConnectionListRequestMessageType = StorageIpcMessageTypes.ConnectionListRequest;

    public const string ConnectionListResponseMessageType = StorageIpcMessageTypes.ConnectionListResponse;

    public const string ConnectionTestRequestMessageType = StorageIpcMessageTypes.ConnectionTestRequest;

    public const string ConnectionTestResponseMessageType = StorageIpcMessageTypes.ConnectionTestResponse;

    public const string StorageListRequestMessageType = StorageIpcMessageTypes.StorageListRequest;

    public const string StorageListResponseMessageType = StorageIpcMessageTypes.StorageListResponse;

    public const string ErrorResponseMessageType = "error.response";
}

internal static class IpcProtocolValidation
{
    public static void ValidateNormalEnvelope(IpcEnvelope envelope, long previousSequence)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (string.IsNullOrWhiteSpace(envelope.MessageType))
        {
            throw new InvalidDataException("An IPC message type is required.");
        }

        if (envelope.MessageType.StartsWith(IpcProtocol.SecretMessageTypePrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The '{envelope.MessageType}' message is secret-bearing and cannot use the normal IPC channel.");
        }

        if (envelope.RequestId == Guid.Empty)
        {
            throw new InvalidDataException("An IPC request ID must not be empty.");
        }

        if (envelope.Sequence <= previousSequence)
        {
            throw new InvalidDataException(
                $"IPC sequence {envelope.Sequence} must be greater than {previousSequence}.");
        }
    }

    public static void ValidateSecretEnvelope(
        string messageType,
        Guid requestId,
        long sequence,
        long previousSequence)
    {
        if (string.IsNullOrWhiteSpace(messageType) ||
            !messageType.StartsWith(IpcProtocol.SecretMessageTypePrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A secret IPC message type is required on the secret channel.");
        }

        if (requestId == Guid.Empty)
        {
            throw new InvalidDataException("A secret IPC request ID must not be empty.");
        }

        if (sequence <= previousSequence)
        {
            throw new InvalidDataException(
                $"Secret IPC sequence {sequence} must be greater than {previousSequence}.");
        }
    }

    public static void ValidateHandshakeEnvelope(IpcEnvelope envelope, string expectedMessageType)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!string.Equals(envelope.MessageType, expectedMessageType, StringComparison.Ordinal))
        {
            throw new IpcProtocolNegotiationException(
                $"Expected '{expectedMessageType}' but received '{envelope.MessageType}'.");
        }

        if (envelope.RequestId == Guid.Empty || envelope.Sequence != 0)
        {
            throw new IpcProtocolNegotiationException(
                "The handshake requires a non-empty request ID and sequence zero.");
        }
    }

    public static void ValidatePipeName(string pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("A named-pipe name is required.", nameof(pipeName));
        }

        if (pipeName.Length > 180)
        {
            throw new ArgumentException("The named-pipe name cannot exceed 180 characters.", nameof(pipeName));
        }

        foreach (var character in pipeName)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
            {
                throw new ArgumentException(
                    "The named-pipe name may contain only ASCII letters, digits, dots, dashes, and underscores.",
                    nameof(pipeName));
            }
        }
    }

    public static void ValidateIdentity(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty identity value is required.", parameterName);
        }

        if (value.Length > 256)
        {
            throw new ArgumentException("Identity values cannot exceed 256 characters.", parameterName);
        }
    }

    public static void ValidatePositiveTimeout(TimeSpan value, string parameterName)
        => ValidatePositiveTimeout(value, parameterName, TimeSpan.FromMinutes(1));

    /// <summary>
    /// Validates a bounded request or established-session timeout. Storage operations may be
    /// longer than connection and handshake attempts, but remain capped to limit stuck clients.
    /// </summary>
    public static void ValidatePositiveOperationTimeout(TimeSpan value, string parameterName)
        => ValidatePositiveTimeout(value, parameterName, TimeSpan.FromMinutes(5));

    private static void ValidatePositiveTimeout(
        TimeSpan value,
        string parameterName,
        TimeSpan maximum)
    {
        if (value <= TimeSpan.Zero || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"The IPC timeout must be greater than zero and no longer than {maximum.TotalMinutes:g} minutes.");
        }
    }
}
