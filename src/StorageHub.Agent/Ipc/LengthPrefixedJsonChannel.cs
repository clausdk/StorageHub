using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace StorageHub.Agent.Ipc;

public static class LengthPrefixedJsonChannel
{
    public const int NormalFrameLimit = 8 * 1024 * 1024;
    public const int SecretEnrollmentFrameLimit = 32 * 1024 * 1024;

    public static async ValueTask WriteAsync<T>(
        Stream stream,
        T message,
        int maximumPayloadBytes = NormalFrameLimit,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateLimit(maximumPayloadBytes);

        var payload = JsonSerializer.SerializeToUtf8Bytes(message, options);
        try
        {
            if (payload.Length == 0 || payload.Length > maximumPayloadBytes)
            {
                throw new InvalidDataException($"IPC payload length {payload.Length} is outside the allowed range.");
            }

            var header = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public static async ValueTask<T> ReadAsync<T>(
        Stream stream,
        int maximumPayloadBytes = NormalFrameLimit,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateLimit(maximumPayloadBytes);

        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (payloadLength <= 0 || payloadLength > maximumPayloadBytes)
        {
            throw new InvalidDataException($"IPC payload length {payloadLength} is outside the allowed range.");
        }

        var payload = GC.AllocateUninitializedArray<byte>(payloadLength);
        try
        {
            await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(payload, options)
                ?? throw new InvalidDataException("The IPC payload contained JSON null or could not be deserialized.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static void ValidateLimit(int maximumPayloadBytes)
    {
        if (maximumPayloadBytes <= 0 || maximumPayloadBytes > SecretEnrollmentFrameLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        }
    }
}
