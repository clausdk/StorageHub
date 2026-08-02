using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

public sealed class ConnectionManagerControllerTests
{
    [Fact]
    public async Task SaveSelectsCreateOrOptimisticUpdateAndDeleteUsesCurrentVersion()
    {
        var profiles = new FakeProfileClient();
        var secrets = new FakeSecretClient();
        var controller = new ConnectionManagerController(profiles, secrets);
        var draft = LocalDraft();
        var now = DateTimeOffset.UtcNow;
        var current = new ConnectionProfileDocument(Guid.NewGuid(), 7, draft, now, now);

        await controller.SaveAsync(draft, current: null);
        await controller.SaveAsync(draft, current);
        await controller.DeleteAsync(current);

        Assert.NotNull(profiles.Created);
        Assert.Equal(current.ConnectionId, profiles.Updated?.ConnectionId);
        Assert.Equal(7, profiles.Updated?.ExpectedVersion);
        Assert.Equal(7, profiles.Deleted?.ExpectedVersion);
    }

    [Fact]
    public async Task ExistingOpaqueReferenceSelectsSecretUpdateAndDeleteIsExplicit()
    {
        var profiles = new FakeProfileClient();
        var secrets = new FakeSecretClient();
        var controller = new ConnectionManagerController(profiles, secrets);
        const string reference = "shs_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

        await controller.EnrollOrUpdateSecretAsync(
            SecretMaterialPurpose.Password,
            reference,
            new byte[] { 1, 2 });
        await controller.DeleteSecretAsync(reference, SecretMaterialPurpose.Password);

        Assert.Equal(reference, secrets.UpdatedReference);
        Assert.Equal(reference, secrets.DeletedReference);
        Assert.Equal([1, 2], secrets.UpdatedMaterial);
    }

    private static ConnectionProfileDraft LocalDraft() => new(
        new ConnectionProfileMetadataDocument("Local", Tags: []),
        new ConnectionEndpointDocument(StorageConnectionProvider.Local, RootPath: "C:\\Data"),
        new ConnectionAuthenticationDocument(ConnectionAuthenticationKind.None),
        new ConnectionOperationalOptionsDocument());

    private sealed class FakeProfileClient : IRemoteConnectionProfileClient
    {
        public ConnectionProfileCreateRequest? Created { get; private set; }
        public ConnectionProfileUpdateRequest? Updated { get; private set; }
        public ConnectionProfileDeleteRequest? Deleted { get; private set; }

        public Task<ConnectionProfileGetResponse> GetAsync(
            ConnectionProfileGetRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ConnectionProfileWriteResponse> CreateAsync(
            ConnectionProfileCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            Created = request;
            return Task.FromResult(Success());
        }

        public Task<ConnectionProfileWriteResponse> UpdateAsync(
            ConnectionProfileUpdateRequest request,
            CancellationToken cancellationToken = default)
        {
            Updated = request;
            return Task.FromResult(Success());
        }

        public Task<ConnectionProfileWriteResponse> DeleteAsync(
            ConnectionProfileDeleteRequest request,
            CancellationToken cancellationToken = default)
        {
            Deleted = request;
            return Task.FromResult(Success());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static ConnectionProfileWriteResponse Success() => new(
            ConnectionProfileIpcContract.CurrentVersion,
            ConnectionProfileWriteStatus.Succeeded,
            ActualVersion: 1);
    }

    private sealed class FakeSecretClient : IRemoteSecretVaultClient
    {
        public string? UpdatedReference { get; private set; }
        public byte[]? UpdatedMaterial { get; private set; }
        public string? DeletedReference { get; private set; }

        public Task<SecretVaultResponse> EnrollAsync(
            SecretMaterialPurpose purpose,
            ReadOnlyMemory<byte> secret,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SecretVaultResponse> UpdateAsync(
            string reference,
            SecretMaterialPurpose purpose,
            ReadOnlyMemory<byte> secret,
            CancellationToken cancellationToken = default)
        {
            UpdatedReference = reference;
            UpdatedMaterial = secret.ToArray();
            return Task.FromResult(Success(SecretVaultOperation.Update, reference));
        }

        public Task<SecretVaultResponse> DeleteAsync(
            string reference,
            SecretMaterialPurpose purpose,
            CancellationToken cancellationToken = default)
        {
            DeletedReference = reference;
            return Task.FromResult(Success(SecretVaultOperation.Delete, reference: null));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static SecretVaultResponse Success(SecretVaultOperation operation, string? reference) => new(
            SecretVaultIpcContract.CurrentVersion,
            operation,
            Succeeded: true,
            reference,
            Version: operation == SecretVaultOperation.Delete ? null : 1);
    }
}
