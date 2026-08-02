using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace StorageHub.Security;

public sealed class VersionedFileSecretVault : ISecretVault, IDisposable
{
    private static readonly byte[] Magic = "SHVLT001"u8.ToArray();
    private const int EnvelopeVersion = 1;
    private const int FixedHeaderLength = 8 + sizeof(int) + sizeof(int) + sizeof(long) + sizeof(long) + sizeof(int) + sizeof(int);
    private const int MaximumSecretLength = 16 * 1024 * 1024;
    private const int MaximumProtectedLength = 32 * 1024 * 1024;
    private const int MaximumEnvelopeLength = FixedHeaderLength + 1_024 + MaximumProtectedLength;

    private readonly string _rootDirectory;
    private readonly ISecretProtector _protector;
    private readonly string _protectionScheme;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public VersionedFileSecretVault(
        string rootDirectory,
        ISecretProtector protector,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        if (!Path.IsPathFullyQualified(rootDirectory))
        {
            throw new ArgumentException("The vault directory must be absolute.", nameof(rootDirectory));
        }

        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _protectionScheme = _protector.Scheme;
        if (string.IsNullOrWhiteSpace(_protectionScheme))
        {
            throw new ArgumentException("The protection scheme cannot be blank.", nameof(protector));
        }

        _rootDirectory = Path.GetFullPath(rootDirectory);
        _timeProvider = timeProvider ?? TimeProvider.System;
        Directory.CreateDirectory(_rootDirectory);
    }

    public async ValueTask<SecretVaultWriteResult> CreateAsync(
        ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken = default)
    {
        ValidateSecret(secret);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SecretReference reference;
            string path;
            do
            {
                reference = SecretReference.Create();
                path = GetPath(reference);
            }
            while (File.Exists(path));

            var now = _timeProvider.GetUtcNow();
            var envelope = Protect(reference, secret.Span, version: 1, now, now);
            try
            {
                await WriteAtomicallyAsync(path, envelope, replace: false, cancellationToken).ConfigureAwait(false);
                return new SecretVaultWriteResult(reference, 1);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(envelope);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<SecretVaultWriteResult> RotateAsync(
        SecretReference reference,
        ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(reference);
        ValidateSecret(secret);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = GetPath(reference);
            var current = await ReadEnvelopeAsync(reference, path, cancellationToken).ConfigureAwait(false);
            try
            {
                AuthenticateEnvelope(reference, current);
                if (current.Version == int.MaxValue)
                {
                    throw new SecretVaultException("The secret revision cannot be incremented further.");
                }

                var nextVersion = current.Version + 1;
                var updatedUtc = _timeProvider.GetUtcNow();
                if (updatedUtc < current.CreatedUtc)
                {
                    updatedUtc = current.CreatedUtc;
                }

                var envelope = Protect(
                    reference,
                    secret.Span,
                    nextVersion,
                    current.CreatedUtc,
                    updatedUtc);
                try
                {
                    await WriteAtomicallyAsync(path, envelope, replace: true, cancellationToken).ConfigureAwait(false);
                    return new SecretVaultWriteResult(reference, nextVersion);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(envelope);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(current.ProtectedData);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<SecretLease> OpenAsync(
        SecretReference reference,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(reference);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var envelope = await ReadEnvelopeAsync(reference, GetPath(reference), cancellationToken).ConfigureAwait(false);
            var entropy = CreateProtectionEntropy(
                reference,
                envelope.Version,
                envelope.CreatedUtc,
                envelope.UpdatedUtc);
            byte[]? plaintext = null;
            try
            {
                plaintext = _protector.Unprotect(envelope.ProtectedData, entropy);
                if (plaintext.Length is <= 0 or > MaximumSecretLength)
                {
                    throw new CryptographicException("Invalid plaintext length.");
                }

                var lease = new SecretLease(plaintext, envelope.Version);
                plaintext = null;
                return lease;
            }
            catch (CryptographicException)
            {
                throw new SecretVaultCorruptedException(reference);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(entropy);
                CryptographicOperations.ZeroMemory(envelope.ProtectedData);
                if (plaintext is not null)
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<bool> ExistsAsync(
        SecretReference reference,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(reference);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return File.Exists(GetPath(reference));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<bool> DeleteAsync(
        SecretReference reference,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(reference);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = GetPath(reference);
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private byte[] Protect(
        SecretReference reference,
        ReadOnlySpan<byte> secret,
        int version,
        DateTimeOffset createdUtc,
        DateTimeOffset updatedUtc)
    {
        var entropy = CreateProtectionEntropy(reference, version, createdUtc, updatedUtc);
        try
        {
            var protectedData = _protector.Protect(secret, entropy);
            try
            {
                if (protectedData.Length is <= 0 or > MaximumProtectedLength)
                {
                    throw new SecretVaultException("The protector returned an invalid payload length.");
                }

                return BuildEnvelope(version, createdUtc, updatedUtc, protectedData);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedData);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    private byte[] BuildEnvelope(
        int version,
        DateTimeOffset createdUtc,
        DateTimeOffset updatedUtc,
        ReadOnlySpan<byte> protectedData)
    {
        var scheme = Encoding.UTF8.GetBytes(_protectionScheme);
        try
        {
            if (scheme.Length is <= 0 or > 1_024)
            {
                throw new SecretVaultException("The protection scheme identifier is too long.");
            }

            var envelope = new byte[checked(FixedHeaderLength + scheme.Length + protectedData.Length)];
            Magic.CopyTo(envelope, 0);
            var offset = Magic.Length;
            BinaryPrimitives.WriteInt32LittleEndian(envelope.AsSpan(offset), EnvelopeVersion);
            offset += sizeof(int);
            BinaryPrimitives.WriteInt32LittleEndian(envelope.AsSpan(offset), version);
            offset += sizeof(int);
            BinaryPrimitives.WriteInt64LittleEndian(envelope.AsSpan(offset), createdUtc.ToUnixTimeMilliseconds());
            offset += sizeof(long);
            BinaryPrimitives.WriteInt64LittleEndian(envelope.AsSpan(offset), updatedUtc.ToUnixTimeMilliseconds());
            offset += sizeof(long);
            BinaryPrimitives.WriteInt32LittleEndian(envelope.AsSpan(offset), scheme.Length);
            offset += sizeof(int);
            BinaryPrimitives.WriteInt32LittleEndian(envelope.AsSpan(offset), protectedData.Length);
            offset += sizeof(int);
            scheme.CopyTo(envelope.AsSpan(offset));
            offset += scheme.Length;
            protectedData.CopyTo(envelope.AsSpan(offset));
            return envelope;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scheme);
        }
    }

    private async Task<VaultEnvelope> ReadEnvelopeAsync(
        SecretReference reference,
        string path,
        CancellationToken cancellationToken)
    {
        byte[]? bytes = null;
        try
        {
            await using var stream = new FileStream(path, new FileStreamOptions
            {
                Access = FileAccess.Read,
                Mode = FileMode.Open,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
            var fileLength = stream.Length;
            if (fileLength is < FixedHeaderLength or > MaximumEnvelopeLength)
            {
                throw new SecretVaultCorruptedException(reference);
            }

            bytes = GC.AllocateUninitializedArray<byte>(checked((int)fileLength));
            try
            {
                await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                throw new SecretVaultCorruptedException(reference);
            }
        }
        catch (FileNotFoundException)
        {
            throw new SecretNotFoundException(reference);
        }
        catch (DirectoryNotFoundException)
        {
            throw new SecretNotFoundException(reference);
        }

        try
        {
            if (!bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            {
                throw new SecretVaultCorruptedException(reference);
            }

            var offset = Magic.Length;
            var envelopeVersion = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
            offset += sizeof(int);
            var version = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
            offset += sizeof(int);
            var createdMilliseconds = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
            offset += sizeof(long);
            var updatedMilliseconds = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
            offset += sizeof(long);
            var schemeLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
            offset += sizeof(int);
            var protectedLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
            offset += sizeof(int);

            if (envelopeVersion != EnvelopeVersion || version <= 0 ||
                schemeLength is <= 0 or > 1_024 || protectedLength is <= 0 or > MaximumProtectedLength ||
                bytes.Length != checked(FixedHeaderLength + schemeLength + protectedLength))
            {
                throw new SecretVaultCorruptedException(reference);
            }

            var scheme = Encoding.UTF8.GetString(bytes, offset, schemeLength);
            offset += schemeLength;
            if (!string.Equals(scheme, _protectionScheme, StringComparison.Ordinal))
            {
                throw new SecretVaultCorruptedException(reference);
            }

            DateTimeOffset createdUtc;
            DateTimeOffset updatedUtc;
            try
            {
                createdUtc = DateTimeOffset.FromUnixTimeMilliseconds(createdMilliseconds);
                updatedUtc = DateTimeOffset.FromUnixTimeMilliseconds(updatedMilliseconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new SecretVaultCorruptedException(reference);
            }

            if (updatedUtc < createdUtc)
            {
                throw new SecretVaultCorruptedException(reference);
            }

            return new VaultEnvelope(version, createdUtc, updatedUtc, bytes.AsSpan(offset, protectedLength).ToArray());
        }
        catch (OverflowException)
        {
            throw new SecretVaultCorruptedException(reference);
        }
        finally
        {
            if (bytes is not null)
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    private static async Task WriteAtomicallyAsync(
        string destinationPath,
        ReadOnlyMemory<byte> envelope,
        bool replace,
        CancellationToken cancellationToken)
    {
        var temporaryPath = destinationPath + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var stream = new FileStream(temporaryPath, new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.CreateNew,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            }))
            {
                await stream.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            if (replace)
            {
                File.Move(temporaryPath, destinationPath, overwrite: true);
            }
            else
            {
                File.Move(temporaryPath, destinationPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string GetPath(SecretReference reference) =>
        Path.Combine(_rootDirectory, reference.Value + ".shv");

    private byte[] CreateProtectionEntropy(
        SecretReference reference,
        int version,
        DateTimeOffset createdUtc,
        DateTimeOffset updatedUtc)
    {
        var referenceBytes = Encoding.UTF8.GetBytes(reference.Value);
        var schemeBytes = Encoding.UTF8.GetBytes(_protectionScheme);
        try
        {
            var entropy = new byte[
                Magic.Length +
                sizeof(int) +
                sizeof(int) +
                sizeof(long) +
                sizeof(long) +
                sizeof(int) + referenceBytes.Length +
                sizeof(int) + schemeBytes.Length];
            var offset = 0;
            Magic.CopyTo(entropy, offset);
            offset += Magic.Length;
            BinaryPrimitives.WriteInt32LittleEndian(entropy.AsSpan(offset), EnvelopeVersion);
            offset += sizeof(int);
            BinaryPrimitives.WriteInt32LittleEndian(entropy.AsSpan(offset), version);
            offset += sizeof(int);
            BinaryPrimitives.WriteInt64LittleEndian(entropy.AsSpan(offset), createdUtc.ToUnixTimeMilliseconds());
            offset += sizeof(long);
            BinaryPrimitives.WriteInt64LittleEndian(entropy.AsSpan(offset), updatedUtc.ToUnixTimeMilliseconds());
            offset += sizeof(long);
            BinaryPrimitives.WriteInt32LittleEndian(entropy.AsSpan(offset), referenceBytes.Length);
            offset += sizeof(int);
            referenceBytes.CopyTo(entropy.AsSpan(offset));
            offset += referenceBytes.Length;
            BinaryPrimitives.WriteInt32LittleEndian(entropy.AsSpan(offset), schemeBytes.Length);
            offset += sizeof(int);
            schemeBytes.CopyTo(entropy.AsSpan(offset));
            return entropy;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(referenceBytes);
            CryptographicOperations.ZeroMemory(schemeBytes);
        }
    }

    private void AuthenticateEnvelope(SecretReference reference, VaultEnvelope envelope)
    {
        var entropy = CreateProtectionEntropy(
            reference,
            envelope.Version,
            envelope.CreatedUtc,
            envelope.UpdatedUtc);
        byte[]? plaintext = null;
        try
        {
            plaintext = _protector.Unprotect(envelope.ProtectedData, entropy);
            if (plaintext.Length is <= 0 or > MaximumSecretLength)
            {
                throw new CryptographicException("Invalid plaintext length.");
            }
        }
        catch (CryptographicException)
        {
            throw new SecretVaultCorruptedException(reference);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private static void ValidateReference(SecretReference reference)
    {
        if (!SecretReference.TryParse(reference.Value, out _))
        {
            throw new ArgumentException("A valid opaque secret reference is required.", nameof(reference));
        }
    }

    private static void ValidateSecret(ReadOnlyMemory<byte> secret)
    {
        if (secret.Length is <= 0 or > MaximumSecretLength)
        {
            throw new ArgumentOutOfRangeException(nameof(secret), "Secrets must be between 1 byte and 16 MiB.");
        }
    }

    private sealed record VaultEnvelope(
        int Version,
        DateTimeOffset CreatedUtc,
        DateTimeOffset UpdatedUtc,
        byte[] ProtectedData);
}
