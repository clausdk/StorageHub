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

    [Fact]
    public async Task ChangedVerifiedFingerprintUsesExplicitRolloverAndRejectUsesExactRecordVersion()
    {
        var profiles = new FakeProfileClient();
        var controller = new ConnectionManagerController(profiles, new FakeSecretClient());
        var current = PinnedSftpDocument();
        profiles.TrustSnapshot = new ConnectionTrustSnapshot(
            current.ConnectionId,
            current.Version,
            new ConnectionTrustTargetDocument(
                ConnectionTrustArtifactKind.SshHostKey,
                "sftp.example.test",
                22),
            [
                new ConnectionTrustRecordDocument(
                    "record-1",
                    new string('A', 64),
                    ConnectionTrustDecision.Trusted,
                    current.CreatedUtc,
                    current.UpdatedUtc,
                    ExpiresUtc: null,
                    PreviousFingerprint: null,
                    Version: 3)
            ]);

        await controller.TrustOrRolloverAsync(current, new string('B', 64));
        await controller.RejectAsync(current, new string('A', 64));

        Assert.Equal("record-1", profiles.RolledOver?.PreviousTrustId);
        Assert.Equal(3, profiles.RolledOver?.ExpectedPreviousTrustVersion);
        Assert.Equal(ConnectionTrustDecision.Rejected, profiles.Decided?.Decision);
        Assert.Equal("record-1", profiles.Decided?.ExistingTrustId);
        Assert.Equal(3, profiles.Decided?.ExpectedTrustVersion);
    }

    private static ConnectionProfileDraft LocalDraft() => new(
        new ConnectionProfileMetadataDocument("Local", Tags: []),
        new ConnectionEndpointDocument(StorageConnectionProvider.Local, RootPath: "C:\\Data"),
        new ConnectionAuthenticationDocument(ConnectionAuthenticationKind.None),
        new ConnectionOperationalOptionsDocument());

    private static ConnectionProfileDocument PinnedSftpDocument()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        return new ConnectionProfileDocument(
            Guid.NewGuid(),
            Version: 4,
            new ConnectionProfileDraft(
                new ConnectionProfileMetadataDocument("SFTP", Tags: []),
                new ConnectionEndpointDocument(
                    StorageConnectionProvider.Sftp,
                    Host: "sftp.example.test",
                    Port: 22,
                    SshHostKeyPolicy: ConnectionSshHostKeyPolicy.Pinned),
                new ConnectionAuthenticationDocument(
                    ConnectionAuthenticationKind.UsernamePassword,
                    Username: "operator",
                    PasswordReference: "shs_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"),
                new ConnectionOperationalOptionsDocument()),
            now,
            now);
    }

    private sealed class FakeProfileClient : IRemoteConnectionProfileClient
    {
        public ConnectionProfileCreateRequest? Created { get; private set; }
        public ConnectionProfileUpdateRequest? Updated { get; private set; }
        public ConnectionProfileDeleteRequest? Deleted { get; private set; }
        public ConnectionTrustSnapshot? TrustSnapshot { get; set; }
        public ConnectionTrustDecisionRequest? Decided { get; private set; }
        public ConnectionTrustRolloverRequest? RolledOver { get; private set; }

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

        public Task<ConnectionTrustGetResponse> GetTrustAsync(
            ConnectionTrustGetRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(new ConnectionTrustGetResponse(
                ConnectionTrustIpcContract.CurrentVersion,
                TrustSnapshot ?? throw new InvalidOperationException("No trust snapshot configured.")));

        public Task<ConnectionTrustMutationResponse> DecideTrustAsync(
            ConnectionTrustDecisionRequest request,
            CancellationToken cancellationToken = default)
        {
            Decided = request;
            return Task.FromResult(TrustSuccess());
        }

        public Task<ConnectionTrustMutationResponse> RolloverTrustAsync(
            ConnectionTrustRolloverRequest request,
            CancellationToken cancellationToken = default)
        {
            RolledOver = request;
            return Task.FromResult(TrustSuccess());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static ConnectionProfileWriteResponse Success() => new(
            ConnectionProfileIpcContract.CurrentVersion,
            ConnectionProfileWriteStatus.Succeeded,
            ActualVersion: 1);

        private ConnectionTrustMutationResponse TrustSuccess() => new(
            ConnectionTrustIpcContract.CurrentVersion,
            ConnectionTrustMutationStatus.Succeeded,
            TrustSnapshot ?? throw new InvalidOperationException("No trust snapshot configured."));
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
