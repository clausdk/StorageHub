using System.Text.Json;
using StorageHub.Agent.Ipc;
using StorageHub.Application.Connections;
using StorageHub.Application.Credentials;
using StorageHub.Contracts.Ipc;
using StorageHub.Domain.Identifiers;
using StorageHub.Persistence;
using StorageHub.Persistence.Credentials;
using StorageHub.Security;
using ContractWriteStatus = StorageHub.Contracts.Ipc.ConnectionProfileWriteStatus;

namespace StorageHub.Agent.Windows.Tests;

/// <summary>
/// Bindings are derived from what a saved profile actually references, so these run against the
/// real repositories rather than fakes: the point is that the two stores agree.
/// </summary>
public sealed class ConnectionProfileKeyStoreBindingTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"storagehub-binding-{Guid.NewGuid():N}");

    [Fact]
    public async Task Saving_a_profile_binds_the_key_store_entry_it_references()
    {
        var fixture = await CreateFixtureAsync();
        var certificate = await ImportAsync(fixture, KeyMaterialKind.Pkcs12Certificate, "Partner certificate");

        var response = await CreateAsync(fixture, FtpsDraft(certificate));

        Assert.Equal(ContractWriteStatus.Succeeded, response.Status);
        var usage = Assert.Single(await fixture.KeyStore.SearchAsync(new KeyStoreSearch()));
        Assert.Equal(["Nightly FTPS"], usage.ReferencedByProfileNames);
    }

    [Fact]
    public async Task A_bound_entry_cannot_be_deleted_while_the_profile_uses_it()
    {
        var fixture = await CreateFixtureAsync();
        var certificate = await ImportAsync(fixture, KeyMaterialKind.Pkcs12Certificate, "Partner certificate");
        await CreateAsync(fixture, FtpsDraft(certificate));

        var refused = await fixture.KeyStore.DeleteAsync(certificate.Id, certificate.Version);

        Assert.Equal(KeyStoreWriteStatus.StillReferenced, refused.Status);
        Assert.Equal(["Nightly FTPS"], refused.ReferencedBy!);
    }

    [Fact]
    public async Task A_profile_that_enrolled_its_own_material_binds_nothing()
    {
        // Material enrolled straight against a profile was never imported into the store, so there
        // is nothing to bind and nothing to protect.
        var fixture = await CreateFixtureAsync();
        await ImportAsync(fixture, KeyMaterialKind.Pkcs12Certificate, "Unrelated certificate");

        var response = await CreateAsync(fixture, FtpsDraft(
            SecretReference.Create().Value,
            SecretReference.Create().Value,
            "Standalone FTPS"));

        Assert.Equal(ContractWriteStatus.Succeeded, response.Status);
        var usage = Assert.Single(await fixture.KeyStore.SearchAsync(new KeyStoreSearch()));
        Assert.Empty(usage.ReferencedByProfileNames);
    }

    [Fact]
    public async Task A_certificate_is_refused_where_an_ssh_key_is_required()
    {
        // The kind check is enforced server-side, before anything is written.
        var fixture = await CreateFixtureAsync();
        var certificate = await ImportAsync(fixture, KeyMaterialKind.Pkcs12Certificate, "Partner certificate");

        var response = await CreateAsync(fixture, SftpDraft(certificate));

        Assert.Equal(ContractWriteStatus.ValidationFailed, response.Status);
        Assert.Contains("cannot be used as an SSH private key", response.Failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ssh_key_is_refused_where_a_certificate_is_required()
    {
        var fixture = await CreateFixtureAsync();
        var key = await ImportAsync(fixture, KeyMaterialKind.SshPrivateKey, "Operator key");

        var response = await CreateAsync(fixture, FtpsDraft(key));

        Assert.Equal(ContractWriteStatus.ValidationFailed, response.Status);
        Assert.Contains("cannot be used as a client certificate", response.Failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ssh_key_binds_to_an_sftp_profile()
    {
        var fixture = await CreateFixtureAsync();
        var key = await ImportAsync(fixture, KeyMaterialKind.SshPrivateKey, "Operator key");

        var response = await CreateAsync(fixture, SftpDraft(key));

        Assert.Equal(ContractWriteStatus.Succeeded, response.Status);
        var usage = Assert.Single(await fixture.KeyStore.SearchAsync(new KeyStoreSearch()));
        Assert.Equal(["Nightly SFTP"], usage.ReferencedByProfileNames);
    }

    [Fact]
    public async Task Pointing_a_profile_at_different_material_releases_the_old_entry()
    {
        var fixture = await CreateFixtureAsync();
        var certificate = await ImportAsync(fixture, KeyMaterialKind.Pkcs12Certificate, "Partner certificate");
        var created = await CreateAsync(fixture, FtpsDraft(certificate));

        // Replace the bound entry with material the store does not own.
        var updated = await SendAsync<ConnectionProfileUpdateRequest, ConnectionProfileWriteResponse>(
            fixture.Service,
            ConnectionProfileIpcMessageTypes.UpdateRequest,
            new ConnectionProfileUpdateRequest(
                ConnectionProfileIpcContract.CurrentVersion,
                created.Profile!.ConnectionId,
                created.Profile.Version,
                FtpsDraft(SecretReference.Create().Value, SecretReference.Create().Value, "Nightly FTPS")));

        Assert.Equal(ContractWriteStatus.Succeeded, updated.Status);
        var usage = Assert.Single(await fixture.KeyStore.SearchAsync(new KeyStoreSearch()));
        Assert.Empty(usage.ReferencedByProfileNames);
        Assert.Equal(
            KeyStoreWriteStatus.Succeeded,
            (await fixture.KeyStore.DeleteAsync(certificate.Id, certificate.Version)).Status);
    }

    private static Task<ConnectionProfileWriteResponse> CreateAsync(Fixture fixture, ConnectionProfileDraft draft) =>
        SendAsync<ConnectionProfileCreateRequest, ConnectionProfileWriteResponse>(
            fixture.Service,
            ConnectionProfileIpcMessageTypes.CreateRequest,
            new ConnectionProfileCreateRequest(ConnectionProfileIpcContract.CurrentVersion, draft));

    private static ConnectionProfileDraft FtpsDraft(KeyStoreEntry entry) =>
        FtpsDraft(entry.MaterialReference.Value, entry.PassphraseReference!.Value.Value, "Nightly FTPS");

    private static ConnectionProfileDraft FtpsDraft(string material, string passphrase, string name) => new(
        new ConnectionProfileMetadataDocument(name, Tags: []),
        new ConnectionEndpointDocument(
            StorageConnectionProvider.Ftps,
            Host: "ftps.example.test",
            Port: 990,
            ClientCertificatePfxReference: material,
            ClientCertificatePasswordReference: passphrase),
        new ConnectionAuthenticationDocument(
            ConnectionAuthenticationKind.UsernamePassword,
            Username: "operator",
            PasswordReference: SecretReference.Create().Value),
        new ConnectionOperationalOptionsDocument());

    private static ConnectionProfileDraft SftpDraft(KeyStoreEntry entry) => new(
        new ConnectionProfileMetadataDocument("Nightly SFTP", Tags: []),
        new ConnectionEndpointDocument(
            StorageConnectionProvider.Sftp,
            Host: "sftp.example.test",
            Port: 22),
        new ConnectionAuthenticationDocument(
            ConnectionAuthenticationKind.SftpPrivateKey,
            Username: "operator",
            PrivateKeyReference: entry.MaterialReference.Value,
            PrivateKeyPassphraseReference: entry.PassphraseReference!.Value.Value,
            PrivateKeyFormat: ConnectionSftpPrivateKeyFormat.OpenSsh),
        new ConnectionOperationalOptionsDocument());

    private static async Task<KeyStoreEntry> ImportAsync(Fixture fixture, KeyMaterialKind kind, string name)
    {
        var entry = KeyStoreEntry.Create(
            KeyStoreEntryId.New(),
            kind,
            name,
            SecretReference.Create(),
            SecretReference.Create(),
            kind is KeyMaterialKind.Pkcs12Certificate
                ? new Pkcs12CertificateSummary(
                    "CN=partner.example.test",
                    "CN=Test CA",
                    DateTimeOffset.UtcNow.AddDays(-1),
                    DateTimeOffset.UtcNow.AddDays(30),
                    new string('A', 64),
                    "RSA",
                    2048,
                    hasPrivateKey: true,
                    chainLength: 1)
                : new SshPrivateKeySummary(
                    SftpPrivateKeyFormat.OpenSsh,
                    "ssh-ed25519",
                    "SHA256:3q2+7wAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                    comment: null),
            DateTimeOffset.UtcNow);
        Assert.Equal(KeyStoreWriteStatus.Succeeded, (await fixture.KeyStore.CreateAsync(entry)).Status);
        return entry;
    }

    private async Task<Fixture> CreateFixtureAsync()
    {
        var options = new SqliteDatabaseOptions(
            Path.Combine(_directory, $"{Guid.NewGuid():N}.db"), pooling: false);
        Assert.True((await new StorageHubDatabaseInitializer(options).InitializeAsync()).IsReady);
        return new Fixture(
            new SqliteKeyStoreRepository(options),
            new ConnectionProfileIpcCommandService(options));
    }

    private static async Task<TResponse> SendAsync<TRequest, TResponse>(
        ConnectionProfileIpcCommandService service,
        string messageType,
        TRequest request)
    {
        var response = await service.HandleAsync(
            IpcEnvelope.Create(messageType, Guid.NewGuid(), sequence: 1, request));
        return response.Payload.Deserialize<TResponse>()!;
    }

    private sealed record Fixture(SqliteKeyStoreRepository KeyStore, ConnectionProfileIpcCommandService Service);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
