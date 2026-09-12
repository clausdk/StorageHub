using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using StorageHub.Agent.Ipc;
using StorageHub.Agent.Windows;
using StorageHub.Contracts.Ipc;
using StorageHub.Persistence;
using StorageHub.Persistence.Credentials;
using StorageHub.Security;

namespace StorageHub.Agent.Windows.Tests;

public sealed class KeyStoreIpcCommandServiceTests : IDisposable
{
    private const string Secret = "correct horse battery staple";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"storagehub-key-store-ipc-{Guid.NewGuid():N}");

    [Fact]
    public void Construction_never_resolves_the_vault()
    {
        // The vault subsystem only creates the vault during initialization, and never at all in
        // recovery-only mode. Resolving it at composition time crashed the whole agent on startup.
        var resolved = 0;
        var options = new SqliteDatabaseOptions(
            Path.Combine(_directory, $"{Guid.NewGuid():N}.db"), pooling: false);

        var service = new KeyStoreIpcCommandService(
            new SqliteKeyStoreRepository(options),
            () =>
            {
                resolved++;
                throw new InvalidOperationException("The credential vault is not initialized.");
            });

        Assert.NotNull(service);
        Assert.Equal(0, resolved);
    }

    [Fact]
    public async Task Listing_still_works_when_the_vault_is_unavailable()
    {
        // Recovery-only startup has no vault. Browsing stored metadata does not need one, so it
        // must keep working rather than faulting.
        var options = new SqliteDatabaseOptions(
            Path.Combine(_directory, $"{Guid.NewGuid():N}.db"), pooling: false);
        Assert.True((await new StorageHubDatabaseInitializer(options).InitializeAsync()).IsReady);
        var service = new KeyStoreIpcCommandService(
            new SqliteKeyStoreRepository(options),
            () => throw new InvalidOperationException("The credential vault is not initialized."));

        var listed = await SendAsync<KeyStoreListRequest, KeyStoreListResponse>(
            service,
            KeyStoreIpcMessageTypes.ListRequest,
            new KeyStoreListRequest(KeyStoreIpcContract.CurrentVersion));

        Assert.Null(listed.Failure);
        Assert.Empty(listed.Entries);
    }

    [Fact]
    public async Task Importing_is_refused_rather_than_faulting_when_the_vault_is_unavailable()
    {
        var options = new SqliteDatabaseOptions(
            Path.Combine(_directory, $"{Guid.NewGuid():N}.db"), pooling: false);
        Assert.True((await new StorageHubDatabaseInitializer(options).InitializeAsync()).IsReady);
        var service = new KeyStoreIpcCommandService(
            new SqliteKeyStoreRepository(options),
            () => throw new InvalidOperationException("The credential vault is not initialized."));

        var response = await SendAsync<KeyStoreCreateRequest, KeyStoreWriteResponse>(
            service,
            KeyStoreIpcMessageTypes.CreateRequest,
            new KeyStoreCreateRequest(
                KeyStoreIpcContract.CurrentVersion,
                KeyStoreMaterialKind.Pkcs12Certificate,
                "Partner certificate",
                null,
                [],
                SecretReference.Create().Value,
                SecretReference.Create().Value));

        Assert.Equal(KeyStoreWriteOutcome.Rejected, response.Outcome);
        Assert.NotNull(response.Failure);
    }

    [Fact]
    public async Task Create_derives_the_certificate_summary_from_the_enrolled_material()
    {
        // The caller supplies references only. Everything descriptive is computed by the agent, so
        // a caller cannot label material as something it is not.
        var fixture = await CreateFixtureAsync();
        var references = await EnrollAsync(fixture, CreateCertificate(), Secret);

        var response = await SendAsync<KeyStoreCreateRequest, KeyStoreWriteResponse>(
            fixture.Service,
            KeyStoreIpcMessageTypes.CreateRequest,
            new KeyStoreCreateRequest(
                KeyStoreIpcContract.CurrentVersion,
                KeyStoreMaterialKind.Pkcs12Certificate,
                "Partner certificate",
                "Used by the nightly FTPS job",
                ["ftps"],
                references.Material,
                references.Passphrase));

        Assert.Equal(KeyStoreWriteOutcome.Applied, response.Outcome);
        Assert.NotNull(response.Entry);
        Assert.True(response.Entry!.HasValidBounds);
        Assert.Equal("CN=partner.example.test", response.Entry.Summary.Subject);
        Assert.True(response.Entry.Summary.HasPrivateKey);
        Assert.Equal(2048, response.Entry.Summary.KeySizeBits);
    }

    [Fact]
    public async Task A_password_less_certificate_is_stored_without_any_passphrase()
    {
        var fixture = await CreateFixtureAsync();
        var stored = await fixture.Vault.CreateAsync(CreateCertificate(password: string.Empty));

        var response = await SendAsync<KeyStoreCreateRequest, KeyStoreWriteResponse>(
            fixture.Service,
            KeyStoreIpcMessageTypes.CreateRequest,
            new KeyStoreCreateRequest(
                KeyStoreIpcContract.CurrentVersion,
                KeyStoreMaterialKind.Pkcs12Certificate,
                "Password-less certificate",
                null,
                [],
                stored.Reference.Value,
                PassphraseReference: null));

        Assert.Equal(KeyStoreWriteOutcome.Applied, response.Outcome);
        Assert.Null(response.Entry!.PassphraseReference);
        Assert.Equal("CN=partner.example.test", response.Entry.Summary.Subject);
    }

    [Fact]
    public async Task An_ssh_key_without_a_passphrase_is_still_refused()
    {
        // The SFTP connector rejects an unprotected key, so storing one would be storing
        // something StorageHub could never use.
        var fixture = await CreateFixtureAsync();
        var stored = await fixture.Vault.CreateAsync(CreateCertificate());

        var response = await SendAsync<KeyStoreCreateRequest, KeyStoreWriteResponse>(
            fixture.Service,
            KeyStoreIpcMessageTypes.CreateRequest,
            new KeyStoreCreateRequest(
                KeyStoreIpcContract.CurrentVersion,
                KeyStoreMaterialKind.SshPrivateKey,
                "Unprotected key",
                null,
                [],
                stored.Reference.Value,
                PassphraseReference: null,
                KeyFormat: KeyStorePrivateKeyFormat.OpenSsh));

        Assert.Equal(KeyStoreWriteOutcome.Rejected, response.Outcome);
    }

    [Fact]
    public async Task No_response_ever_carries_key_material()
    {
        var fixture = await CreateFixtureAsync();
        var references = await EnrollAsync(fixture, CreateCertificate(), Secret);
        await CreateEntryAsync(fixture, references, "Partner certificate");

        var listed = await SendAsync<KeyStoreListRequest, KeyStoreListResponse>(
            fixture.Service,
            KeyStoreIpcMessageTypes.ListRequest,
            new KeyStoreListRequest(KeyStoreIpcContract.CurrentVersion));

        var json = JsonSerializer.Serialize(listed);
        Assert.Single(listed.Entries);
        Assert.DoesNotContain(Secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN", json, StringComparison.Ordinal);
        Assert.DoesNotContain("MII", json, StringComparison.Ordinal);
        // Only opaque references survive the boundary.
        Assert.StartsWith("shs_", listed.Entries[0].MaterialReference, StringComparison.Ordinal);
        Assert.StartsWith("shs_", listed.Entries[0].PassphraseReference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_is_refused_when_the_material_cannot_be_read_with_its_passphrase()
    {
        var fixture = await CreateFixtureAsync();
        var references = await EnrollAsync(fixture, CreateCertificate(), "not the password");

        var response = await SendAsync<KeyStoreCreateRequest, KeyStoreWriteResponse>(
            fixture.Service,
            KeyStoreIpcMessageTypes.CreateRequest,
            new KeyStoreCreateRequest(
                KeyStoreIpcContract.CurrentVersion,
                KeyStoreMaterialKind.Pkcs12Certificate,
                "Partner certificate",
                null,
                [],
                references.Material,
                references.Passphrase));

        Assert.Equal(KeyStoreWriteOutcome.Rejected, response.Outcome);
        Assert.Null(response.Entry);
    }

    [Fact]
    public async Task Create_is_refused_when_a_reference_does_not_resolve()
    {
        var fixture = await CreateFixtureAsync();

        var response = await SendAsync<KeyStoreCreateRequest, KeyStoreWriteResponse>(
            fixture.Service,
            KeyStoreIpcMessageTypes.CreateRequest,
            new KeyStoreCreateRequest(
                KeyStoreIpcContract.CurrentVersion,
                KeyStoreMaterialKind.Pkcs12Certificate,
                "Invented certificate",
                null,
                [],
                SecretReference.Create().Value,
                SecretReference.Create().Value));

        Assert.Equal(KeyStoreWriteOutcome.Rejected, response.Outcome);
    }

    [Fact]
    public async Task An_ssh_entry_must_declare_its_envelope_and_a_certificate_must_not()
    {
        var fixture = await CreateFixtureAsync();
        var references = await EnrollAsync(fixture, CreateCertificate(), Secret);

        var certificateWithFormat = await SendAsync<KeyStoreCreateRequest, KeyStoreWriteResponse>(
            fixture.Service,
            KeyStoreIpcMessageTypes.CreateRequest,
            new KeyStoreCreateRequest(
                KeyStoreIpcContract.CurrentVersion,
                KeyStoreMaterialKind.Pkcs12Certificate,
                "Mislabelled",
                null,
                [],
                references.Material,
                references.Passphrase,
                KeyStorePrivateKeyFormat.OpenSsh));
        var keyWithoutFormat = await SendAsync<KeyStoreCreateRequest, KeyStoreWriteResponse>(
            fixture.Service,
            KeyStoreIpcMessageTypes.CreateRequest,
            new KeyStoreCreateRequest(
                KeyStoreIpcContract.CurrentVersion,
                KeyStoreMaterialKind.SshPrivateKey,
                "Mislabelled key",
                null,
                [],
                references.Material,
                references.Passphrase));

        Assert.Equal(KeyStoreWriteOutcome.Rejected, certificateWithFormat.Outcome);
        Assert.Equal(KeyStoreWriteOutcome.Rejected, keyWithoutFormat.Outcome);
    }

    [Fact]
    public async Task Deleting_an_entry_removes_its_vault_envelopes()
    {
        var fixture = await CreateFixtureAsync();
        var references = await EnrollAsync(fixture, CreateCertificate(), Secret);
        var created = await CreateEntryAsync(fixture, references, "Retired certificate");

        var response = await SendAsync<KeyStoreDeleteRequest, KeyStoreWriteResponse>(
            fixture.Service,
            KeyStoreIpcMessageTypes.DeleteRequest,
            new KeyStoreDeleteRequest(
                KeyStoreIpcContract.CurrentVersion,
                created.Entry!.EntryId,
                created.Entry.Version));

        Assert.Equal(KeyStoreWriteOutcome.Applied, response.Outcome);
        Assert.False(await fixture.Vault.ExistsAsync(SecretReference.Parse(references.Material)));
        Assert.False(await fixture.Vault.ExistsAsync(SecretReference.Parse(references.Passphrase)));
    }

    [Fact]
    public async Task Renaming_requires_the_expected_version_and_keeps_the_summary()
    {
        var fixture = await CreateFixtureAsync();
        var references = await EnrollAsync(fixture, CreateCertificate(), Secret);
        var created = await CreateEntryAsync(fixture, references, "Partner certificate");

        var stale = await SendAsync<KeyStoreUpdateRequest, KeyStoreWriteResponse>(
            fixture.Service,
            KeyStoreIpcMessageTypes.UpdateRequest,
            new KeyStoreUpdateRequest(
                KeyStoreIpcContract.CurrentVersion, created.Entry!.EntryId, "Renamed", null, [], 9));
        var applied = await SendAsync<KeyStoreUpdateRequest, KeyStoreWriteResponse>(
            fixture.Service,
            KeyStoreIpcMessageTypes.UpdateRequest,
            new KeyStoreUpdateRequest(
                KeyStoreIpcContract.CurrentVersion, created.Entry.EntryId, "Renamed", "now described", ["ftps"], 1));

        Assert.Equal(KeyStoreWriteOutcome.VersionConflict, stale.Outcome);
        Assert.Equal(KeyStoreWriteOutcome.Applied, applied.Outcome);
        Assert.Equal("Renamed", applied.Entry!.DisplayName);
        Assert.Equal("CN=partner.example.test", applied.Entry.Summary.Subject);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public async Task Unsupported_contract_versions_are_refused(int version)
    {
        var fixture = await CreateFixtureAsync();

        var response = await SendAsync<KeyStoreListRequest, KeyStoreListResponse>(
            fixture.Service,
            KeyStoreIpcMessageTypes.ListRequest,
            new KeyStoreListRequest(version));

        Assert.NotNull(response.Failure);
        Assert.Empty(response.Entries);
    }

    private static async Task<KeyStoreWriteResponse> CreateEntryAsync(
        Fixture fixture,
        (string Material, string Passphrase) references,
        string name) =>
        await SendAsync<KeyStoreCreateRequest, KeyStoreWriteResponse>(
            fixture.Service,
            KeyStoreIpcMessageTypes.CreateRequest,
            new KeyStoreCreateRequest(
                KeyStoreIpcContract.CurrentVersion,
                KeyStoreMaterialKind.Pkcs12Certificate,
                name,
                null,
                [],
                references.Material,
                references.Passphrase));

    private static async Task<(string Material, string Passphrase)> EnrollAsync(
        Fixture fixture,
        byte[] material,
        string passphrase)
    {
        var storedMaterial = await fixture.Vault.CreateAsync(material);
        var storedPassphrase = await fixture.Vault.CreateAsync(Encoding.UTF8.GetBytes(passphrase));
        return (storedMaterial.Reference.Value, storedPassphrase.Reference.Value);
    }

    private static byte[] CreateCertificate(string? password = null)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=partner.example.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(2));
        return certificate.Export(X509ContentType.Pkcs12, password ?? Secret);
    }

    private async Task<Fixture> CreateFixtureAsync()
    {
        var options = new SqliteDatabaseOptions(
            Path.Combine(_directory, $"{Guid.NewGuid():N}.db"), pooling: false);
        var initialized = await new StorageHubDatabaseInitializer(options).InitializeAsync();
        Assert.True(initialized.IsReady, initialized.Message);
        var vault = new VersionedFileSecretVault(
            Path.Combine(_directory, $"vault-{Guid.NewGuid():N}"),
            new PassthroughProtector());
        return new Fixture(
            vault,
            new KeyStoreIpcCommandService(new SqliteKeyStoreRepository(options), () => vault));
    }

    private static async Task<TResponse> SendAsync<TRequest, TResponse>(
        KeyStoreIpcCommandService service,
        string messageType,
        TRequest request)
    {
        var response = await service.HandleAsync(
            IpcEnvelope.Create(messageType, Guid.NewGuid(), sequence: 1, request));
        return response.Payload.Deserialize<TResponse>()!;
    }

    private sealed record Fixture(ISecretVault Vault, KeyStoreIpcCommandService Service);

    /// <summary>Keeps the test off DPAPI; the envelope format is exercised by the vault's own suite.</summary>
    private sealed class PassthroughProtector : ISecretProtector
    {
        public string Scheme => "test-passthrough-v1";

        public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy) => plaintext.ToArray();

        public byte[] Unprotect(ReadOnlySpan<byte> protectedData, ReadOnlySpan<byte> entropy) =>
            protectedData.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
