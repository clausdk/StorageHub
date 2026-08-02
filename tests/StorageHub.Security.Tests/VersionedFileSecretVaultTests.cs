using System.Security.Cryptography;
using Xunit;

namespace StorageHub.Security.Tests;

public sealed class VersionedFileSecretVaultTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"storagehub-vault-{Guid.NewGuid():N}");

    [Fact]
    public async Task Create_writes_only_protected_bytes_and_returns_opaque_reference()
    {
        var protector = new DeterministicTestProtector();
        var vault = new VersionedFileSecretVault(_directory, protector);
        var plaintext = "canary-password-934"u8.ToArray();

        var created = await vault.CreateAsync(plaintext);

        Assert.True(SecretReference.TryParse(created.Reference.Value, out _));
        Assert.Equal(1, created.Version);
        var file = Assert.Single(Directory.GetFiles(_directory, "*.shv"));
        Assert.DoesNotContain(Convert.ToHexString(plaintext), Convert.ToHexString(await File.ReadAllBytesAsync(file)));

        await using var lease = await vault.OpenAsync(created.Reference);
        Assert.Equal(plaintext, lease.Memory.ToArray());
        Assert.Equal(1, lease.Version);
    }

    [Fact]
    public async Task Rotate_atomically_replaces_payload_and_increments_version()
    {
        var vault = new VersionedFileSecretVault(_directory, new DeterministicTestProtector());
        var created = await vault.CreateAsync("first"u8.ToArray());

        var rotated = await vault.RotateAsync(created.Reference, "second"u8.ToArray());

        Assert.Equal(created.Reference, rotated.Reference);
        Assert.Equal(2, rotated.Version);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp-*"));
        await using var lease = await vault.OpenAsync(created.Reference);
        Assert.Equal("second"u8.ToArray(), lease.Memory.ToArray());
        Assert.Equal(2, lease.Version);
    }

    [Fact]
    public async Task Tampered_vault_file_fails_closed_without_returning_plaintext()
    {
        var vault = new VersionedFileSecretVault(_directory, new DeterministicTestProtector());
        var created = await vault.CreateAsync("sensitive"u8.ToArray());
        var file = Assert.Single(Directory.GetFiles(_directory, "*.shv"));
        var bytes = await File.ReadAllBytesAsync(file);
        bytes[^1] ^= 0x5A;
        await File.WriteAllBytesAsync(file, bytes);

        var error = await Assert.ThrowsAsync<SecretVaultCorruptedException>(
            () => vault.OpenAsync(created.Reference).AsTask());

        Assert.DoesNotContain("sensitive", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tampered_envelope_metadata_is_authenticated_and_fails_closed()
    {
        var vault = new VersionedFileSecretVault(_directory, new DeterministicTestProtector());
        var created = await vault.CreateAsync("metadata-bound"u8.ToArray());
        var file = Assert.Single(Directory.GetFiles(_directory, "*.shv"));
        var bytes = await File.ReadAllBytesAsync(file);
        bytes[12] = 2; // Secret revision follows the 8-byte magic and 4-byte envelope version.
        await File.WriteAllBytesAsync(file, bytes);

        await Assert.ThrowsAsync<SecretVaultCorruptedException>(
            () => vault.OpenAsync(created.Reference).AsTask());
        await Assert.ThrowsAsync<SecretVaultCorruptedException>(
            () => vault.RotateAsync(created.Reference, "replacement"u8.ToArray()).AsTask());
    }

    [Fact]
    public async Task Oversized_vault_file_is_rejected_before_allocation_or_unprotect()
    {
        var protector = new DeterministicTestProtector();
        var vault = new VersionedFileSecretVault(_directory, protector);
        var created = await vault.CreateAsync("sensitive"u8.ToArray());
        var file = Assert.Single(Directory.GetFiles(_directory, "*.shv"));
        await using (var stream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(34L * 1024 * 1024);
        }

        await Assert.ThrowsAsync<SecretVaultCorruptedException>(
            () => vault.OpenAsync(created.Reference).AsTask());

        Assert.Equal(0, protector.UnprotectCallCount);
    }

    [Fact]
    public async Task Disposing_lease_zeroes_the_owned_plaintext_buffer()
    {
        var protector = new DeterministicTestProtector();
        var vault = new VersionedFileSecretVault(_directory, protector);
        var created = await vault.CreateAsync("wipe-me"u8.ToArray());
        var lease = await vault.OpenAsync(created.Reference);
        var ownedBuffer = Assert.IsType<byte[]>(protector.LastUnprotectedBuffer);

        await lease.DisposeAsync();

        Assert.All(ownedBuffer, value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() => _ = lease.Memory);
    }

    [Fact]
    public async Task Delete_removes_only_the_exact_opaque_reference()
    {
        var vault = new VersionedFileSecretVault(_directory, new DeterministicTestProtector());
        var first = await vault.CreateAsync("one"u8.ToArray());
        var second = await vault.CreateAsync("two"u8.ToArray());

        Assert.True(await vault.DeleteAsync(first.Reference));
        Assert.False(await vault.ExistsAsync(first.Reference));
        Assert.True(await vault.ExistsAsync(second.Reference));
        await Assert.ThrowsAsync<SecretNotFoundException>(() => vault.OpenAsync(first.Reference).AsTask());
    }

    [Theory]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("shs_not-base64")]
    [InlineData("SHS_abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNO")]
    public void Secret_reference_rejects_nonopaque_or_path_like_values(string value)
    {
        Assert.False(SecretReference.TryParse(value, out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class DeterministicTestProtector : ISecretProtector
    {
        private static readonly byte[] Key = SHA256.HashData("storagehub-test-protector"u8.ToArray());

        public string Scheme => "test-hmac-xor-v1";
        public byte[]? LastUnprotectedBuffer { get; private set; }
        public int UnprotectCallCount { get; private set; }

        public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy)
        {
            var ciphertext = new byte[plaintext.Length];
            for (var index = 0; index < plaintext.Length; index++)
            {
                ciphertext[index] = (byte)(plaintext[index] ^ Key[index % Key.Length]);
            }

            using var hmac = new HMACSHA256(Key);
            var authenticated = new byte[entropy.Length + ciphertext.Length];
            entropy.CopyTo(authenticated);
            ciphertext.CopyTo(authenticated.AsSpan(entropy.Length));
            var tag = hmac.ComputeHash(authenticated);
            var result = new byte[tag.Length + ciphertext.Length];
            tag.CopyTo(result, 0);
            ciphertext.CopyTo(result, tag.Length);
            CryptographicOperations.ZeroMemory(authenticated);
            return result;
        }

        public byte[] Unprotect(ReadOnlySpan<byte> protectedData, ReadOnlySpan<byte> entropy)
        {
            UnprotectCallCount++;
            if (protectedData.Length < 32)
            {
                throw new CryptographicException("Invalid protected payload.");
            }

            var ciphertext = protectedData[32..];
            using var hmac = new HMACSHA256(Key);
            var authenticated = new byte[entropy.Length + ciphertext.Length];
            entropy.CopyTo(authenticated);
            ciphertext.CopyTo(authenticated.AsSpan(entropy.Length));
            var expected = hmac.ComputeHash(authenticated);
            CryptographicOperations.ZeroMemory(authenticated);
            if (!CryptographicOperations.FixedTimeEquals(expected, protectedData[..32]))
            {
                throw new CryptographicException("Authentication failed.");
            }

            var plaintext = new byte[ciphertext.Length];
            for (var index = 0; index < ciphertext.Length; index++)
            {
                plaintext[index] = (byte)(ciphertext[index] ^ Key[index % Key.Length]);
            }

            LastUnprotectedBuffer = plaintext;
            return plaintext;
        }
    }
}
