using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

public sealed class KeyStoreFormTests
{
    [Fact]
    public void KeyStoreWindowConstructsAndDisposesOnStaWithoutBeingShown()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var form = new KeyStoreForm(new FakeKeyStoreClient(), new FakeSecretVaultClient());
        });
    }

    [Fact]
    public void APasswordLessCertificateIsAccepted()
    {
        // PKCS#12 allows no password. Nothing is enrolled for it, and the import proceeds.
        Assert.Null(KeyStoreForm.DescribeMissingPassphrase(KeyStoreMaterialKind.Pkcs12Certificate, ""));
        Assert.Null(KeyStoreForm.DescribeMissingPassphrase(KeyStoreMaterialKind.Pkcs12Certificate, null));
    }

    [Fact]
    public void AnUnprotectedPrivateKeyIsRefusedWithAnActionableMessage()
    {
        var message = KeyStoreForm.DescribeMissingPassphrase(KeyStoreMaterialKind.SshPrivateKey, null);

        Assert.NotNull(message);
        Assert.Contains("passphrase", message, StringComparison.OrdinalIgnoreCase);
        // Never the raw vault range error the user used to see.
        Assert.DoesNotContain("Parameter", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AProvidedSecretIsAccepted()
    {
        Assert.Null(KeyStoreForm.DescribeMissingPassphrase(
            KeyStoreMaterialKind.Pkcs12Certificate, "correct horse"));
        Assert.Null(KeyStoreForm.DescribeMissingPassphrase(
            KeyStoreMaterialKind.SshPrivateKey, " "));
    }

    [Fact]
    public void StillReferencedFailuresNameTheConsumingProfiles()
    {
        // The refusal has to say what is holding the entry, because the user's next step is to go
        // and unbind those profiles.
        var message = KeyStoreForm.DescribeFailure(new KeyStoreWriteResponse(
            KeyStoreIpcContract.CurrentVersion,
            KeyStoreWriteOutcome.StillReferenced,
            ReferencedByProfiles: ["Nightly FTPS", "Partner archive"]));

        Assert.Contains("Nightly FTPS", message, StringComparison.Ordinal);
        Assert.Contains("Partner archive", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(KeyStoreWriteOutcome.NameConflict)]
    [InlineData(KeyStoreWriteOutcome.VersionConflict)]
    [InlineData(KeyStoreWriteOutcome.NotFound)]
    [InlineData(KeyStoreWriteOutcome.StillReferenced)]
    [InlineData(KeyStoreWriteOutcome.Rejected)]
    public void EveryFailureOutcomeHasAnActionableMessage(KeyStoreWriteOutcome outcome)
    {
        var message = KeyStoreForm.DescribeFailure(
            new KeyStoreWriteResponse(KeyStoreIpcContract.CurrentVersion, outcome));

        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    private sealed class FakeKeyStoreClient : IKeyStoreAgentClient
    {
        public Task<KeyStoreListResponse> ListAsync(
            KeyStoreListRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new KeyStoreListResponse(KeyStoreIpcContract.CurrentVersion, []));

        public Task<KeyStoreWriteResponse> CreateAsync(
            KeyStoreCreateRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new KeyStoreWriteResponse(
                KeyStoreIpcContract.CurrentVersion, KeyStoreWriteOutcome.Applied));

        public Task<KeyStoreWriteResponse> UpdateAsync(
            KeyStoreUpdateRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new KeyStoreWriteResponse(
                KeyStoreIpcContract.CurrentVersion, KeyStoreWriteOutcome.Applied));

        public Task<KeyStoreWriteResponse> DeleteAsync(
            KeyStoreDeleteRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new KeyStoreWriteResponse(
                KeyStoreIpcContract.CurrentVersion, KeyStoreWriteOutcome.Applied));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSecretVaultClient : IRemoteSecretVaultClient
    {
        public Task<SecretVaultResponse> EnrollAsync(
            SecretMaterialPurpose purpose,
            ReadOnlyMemory<byte> secret,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SecretVaultResponse(
                SecretVaultIpcContract.CurrentVersion, SecretVaultOperation.Enroll, true));

        public Task<SecretVaultResponse> UpdateAsync(
            string reference,
            SecretMaterialPurpose purpose,
            ReadOnlyMemory<byte> secret,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SecretVaultResponse(
                SecretVaultIpcContract.CurrentVersion, SecretVaultOperation.Update, true));

        public Task<SecretVaultResponse> DeleteAsync(
            string reference,
            SecretMaterialPurpose purpose,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SecretVaultResponse(
                SecretVaultIpcContract.CurrentVersion, SecretVaultOperation.Delete, true));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
