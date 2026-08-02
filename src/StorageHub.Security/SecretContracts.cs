namespace StorageHub.Security;

public interface ISecretProtector
{
    string Scheme { get; }

    byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy);
    byte[] Unprotect(ReadOnlySpan<byte> protectedData, ReadOnlySpan<byte> entropy);
}

public interface ISecretVault
{
    ValueTask<SecretVaultWriteResult> CreateAsync(
        ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken = default);

    ValueTask<SecretVaultWriteResult> RotateAsync(
        SecretReference reference,
        ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken = default);

    ValueTask<SecretLease> OpenAsync(
        SecretReference reference,
        CancellationToken cancellationToken = default);

    ValueTask<bool> ExistsAsync(
        SecretReference reference,
        CancellationToken cancellationToken = default);

    ValueTask<bool> DeleteAsync(
        SecretReference reference,
        CancellationToken cancellationToken = default);
}

public readonly record struct SecretVaultWriteResult(SecretReference Reference, int Version);
