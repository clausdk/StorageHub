using StorageHub.Contracts.Ipc;
using StorageHub.Security;

namespace StorageHub.Agent.Windows.Tests;

public sealed class SecretVaultIpcCommandServiceTests
{
    [Fact]
    public async Task EnrollStoresMaterialReturnsOnlyReferenceAndZerosRequestBuffer()
    {
        var vault = new FakeSecretVault();
        var service = new SecretVaultIpcCommandService(() => vault);
        var material = new byte[] { 1, 2, 3, 4 };
        var request = new SecretIpcRequestEnvelope(
            SecretVaultIpcMessageTypes.EnrollRequest,
            Guid.NewGuid(),
            1,
            new SecretVaultRequest(
                SecretVaultIpcContract.CurrentVersion,
                SecretVaultOperation.Enroll,
                SecretMaterialPurpose.Password,
                Reference: null,
                material));

        var result = await service.HandleAsync(request);

        Assert.Equal(SecretVaultIpcMessageTypes.EnrollResponse, result.MessageType);
        Assert.True(result.Payload.Succeeded);
        Assert.True(SecretReference.TryParse(result.Payload.Reference, out _));
        Assert.Equal([1, 2, 3, 4], vault.LastStoredMaterial);
        Assert.All(material, value => Assert.Equal(0, value));
        Assert.DoesNotContain("AQIDBA", System.Text.Json.JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateAndDeleteRequireOpaqueReferenceAndNeverEchoVaultDiagnostics()
    {
        const string diagnosticSecret = "private-value-from-vault";
        var service = new SecretVaultIpcCommandService(() => new ThrowingSecretVault(diagnosticSecret));
        var reference = SecretReference.Create();
        var material = new byte[] { 9, 8, 7 };
        var update = new SecretIpcRequestEnvelope(
            SecretVaultIpcMessageTypes.UpdateRequest,
            Guid.NewGuid(),
            1,
            new SecretVaultRequest(
                SecretVaultIpcContract.CurrentVersion,
                SecretVaultOperation.Update,
                SecretMaterialPurpose.SshPrivateKey,
                reference.Value,
                material));

        var result = await service.HandleAsync(update);

        Assert.False(result.Payload.Succeeded);
        Assert.Equal(StorageIpcFailureCategory.Unavailable, result.Payload.Failure?.Category);
        Assert.DoesNotContain(diagnosticSecret, System.Text.Json.JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.All(material, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task NullTypedPayloadReturnsSanitizedValidationFailure()
    {
        var service = new SecretVaultIpcCommandService(() => new FakeSecretVault());
        var request = new SecretIpcRequestEnvelope(
            SecretVaultIpcMessageTypes.EnrollRequest,
            Guid.NewGuid(),
            1,
            Payload: null!);

        var result = await service.HandleAsync(request);

        Assert.False(result.Payload.Succeeded);
        Assert.Equal(StorageIpcFailureCategory.Validation, result.Payload.Failure?.Category);
    }

    private sealed class FakeSecretVault : ISecretVault
    {
        public byte[]? LastStoredMaterial { get; private set; }

        public ValueTask<SecretVaultWriteResult> CreateAsync(
            ReadOnlyMemory<byte> secret,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastStoredMaterial = secret.ToArray();
            return ValueTask.FromResult(new SecretVaultWriteResult(SecretReference.Create(), 1));
        }

        public ValueTask<SecretVaultWriteResult> RotateAsync(
            SecretReference reference,
            ReadOnlyMemory<byte> secret,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<SecretLease> OpenAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<bool> ExistsAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<bool> DeleteAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ThrowingSecretVault(string message) : ISecretVault
    {
        public ValueTask<SecretVaultWriteResult> CreateAsync(
            ReadOnlyMemory<byte> secret,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException(message);

        public ValueTask<SecretVaultWriteResult> RotateAsync(
            SecretReference reference,
            ReadOnlyMemory<byte> secret,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException(message);

        public ValueTask<SecretLease> OpenAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException(message);

        public ValueTask<bool> ExistsAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException(message);

        public ValueTask<bool> DeleteAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException(message);
    }
}
