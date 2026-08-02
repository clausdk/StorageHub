using System.Security.Cryptography;

namespace StorageHub.Security;

public sealed class SecretLease : IDisposable, IAsyncDisposable
{
    private byte[]? _plaintext;

    internal SecretLease(byte[] plaintext, int version)
    {
        _plaintext = plaintext ?? throw new ArgumentNullException(nameof(plaintext));
        Version = version;
    }

    public int Version { get; }

    public ReadOnlyMemory<byte> Memory => _plaintext is { } plaintext
        ? plaintext
        : throw new ObjectDisposedException(nameof(SecretLease));

    public void Dispose()
    {
        var plaintext = Interlocked.Exchange(ref _plaintext, null);
        if (plaintext is not null)
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
