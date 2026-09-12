using StorageHub.Application.Connections;
using StorageHub.Application.Credentials;
using StorageHub.Domain.Identifiers;
using StorageHub.Persistence.Connections;
using StorageHub.Persistence.Credentials;
using StorageHub.Security;
using Xunit;

namespace StorageHub.Persistence.Tests.Credentials;

public sealed class SqliteKeyStoreRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"storagehub-key-store-{Guid.NewGuid():N}");

    [Fact]
    public async Task Certificate_entries_round_trip_with_their_derived_summary()
    {
        var repository = Repository();
        var entry = CertificateEntry("Partner FTPS certificate");

        var created = await repository.CreateAsync(entry);
        var loaded = await repository.GetAsync(entry.Id);

        Assert.Equal(KeyStoreWriteStatus.Succeeded, created.Status);
        Assert.NotNull(loaded);
        Assert.Equal(KeyMaterialKind.Pkcs12Certificate, loaded.Kind);
        Assert.Equal(entry.DisplayName, loaded.DisplayName);
        Assert.Equal(entry.MaterialReference, loaded.MaterialReference);
        Assert.Equal(entry.PassphraseReference, loaded.PassphraseReference);
        Assert.Equal(entry.Tags.AsEnumerable(), loaded.Tags.AsEnumerable());
        var summary = Assert.IsType<Pkcs12CertificateSummary>(loaded.Summary);
        Assert.Equal("CN=partner.example.test", summary.Subject);
        Assert.Equal(2048, summary.KeySizeBits);
        Assert.True(summary.HasPrivateKey);
    }

    [Fact]
    public async Task Ssh_key_entries_round_trip_with_their_derived_summary()
    {
        var repository = Repository();
        var entry = SshKeyEntry("Operator key");

        await repository.CreateAsync(entry);
        var loaded = await repository.GetAsync(entry.Id);

        var summary = Assert.IsType<SshPrivateKeySummary>(loaded!.Summary);
        Assert.Equal(SftpPrivateKeyFormat.OpenSsh, summary.Format);
        Assert.Equal("ssh-ed25519", summary.PublicKeyAlgorithm);
        Assert.StartsWith("SHA256:", summary.Sha256Fingerprint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_certificate_round_trips_without_a_passphrase()
    {
        var repository = Repository();
        var entry = KeyStoreEntry.Create(
            KeyStoreEntryId.New(),
            KeyMaterialKind.Pkcs12Certificate,
            "Password-less certificate",
            SecretReference.Create(),
            passphraseReference: null,
            Summary("CN=none.example.test"),
            DateTimeOffset.UtcNow);

        var created = await repository.CreateAsync(entry);
        var loaded = await repository.GetAsync(entry.Id);

        Assert.Equal(KeyStoreWriteStatus.Succeeded, created.Status);
        Assert.Null(loaded!.PassphraseReference);
        Assert.Equal(entry.MaterialReference, loaded.MaterialReference);
    }

    [Fact]
    public void An_ssh_key_still_requires_a_passphrase()
    {
        Assert.Throws<ArgumentException>(() => KeyStoreEntry.Create(
            KeyStoreEntryId.New(),
            KeyMaterialKind.SshPrivateKey,
            "Unprotected key",
            SecretReference.Create(),
            passphraseReference: null,
            new SshPrivateKeySummary(
                SftpPrivateKeyFormat.OpenSsh,
                "ssh-ed25519",
                "SHA256:3q2+7wAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                comment: null),
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Display_names_are_unique_regardless_of_casing()
    {
        var repository = Repository();
        await repository.CreateAsync(CertificateEntry("Partner certificate"));

        var duplicate = await repository.CreateAsync(CertificateEntry("PARTNER CERTIFICATE"));

        Assert.Equal(KeyStoreWriteStatus.NameConflict, duplicate.Status);
    }

    [Fact]
    public async Task Updates_require_the_expected_version()
    {
        var repository = Repository();
        var entry = CertificateEntry("Partner certificate");
        await repository.CreateAsync(entry);
        var renamed = entry.WithDetails("Renamed", "now described", ["ftps"], DateTimeOffset.UtcNow);

        var stale = await repository.UpdateAsync(renamed, expectedVersion: 7);
        var applied = await repository.UpdateAsync(renamed, expectedVersion: 1);
        var loaded = await repository.GetAsync(entry.Id);

        Assert.Equal(KeyStoreWriteStatus.VersionConflict, stale.Status);
        Assert.Equal(KeyStoreWriteStatus.Succeeded, applied.Status);
        Assert.Equal("Renamed", loaded!.DisplayName);
        Assert.Equal(2, loaded.Version);
    }

    [Fact]
    public async Task A_referenced_entry_cannot_be_deleted_and_names_its_consumers()
    {
        var repository = Repository();
        var entry = CertificateEntry("Shared certificate");
        await repository.CreateAsync(entry);
        var profile = await SeedProfileAsync("Nightly FTPS");
        await repository.BindAsync(profile, "ftps.client-certificate", entry.Id);

        var refused = await repository.DeleteAsync(entry.Id, expectedVersion: 1);

        Assert.Equal(KeyStoreWriteStatus.StillReferenced, refused.Status);
        Assert.Equal(["Nightly FTPS"], refused.ReferencedBy!);
        Assert.NotNull(await repository.GetAsync(entry.Id));
    }

    [Fact]
    public async Task An_unbound_entry_can_be_deleted()
    {
        var repository = Repository();
        var entry = CertificateEntry("Retired certificate");
        await repository.CreateAsync(entry);
        var profile = await SeedProfileAsync("Nightly FTPS");
        await repository.BindAsync(profile, "ftps.client-certificate", entry.Id);
        await repository.UnbindAsync(profile, "ftps.client-certificate");

        var deleted = await repository.DeleteAsync(entry.Id, expectedVersion: 1);

        Assert.Equal(KeyStoreWriteStatus.Succeeded, deleted.Status);
        Assert.Null(await repository.GetAsync(entry.Id));
    }

    [Fact]
    public async Task Rotating_material_keeps_the_identity_and_every_binding()
    {
        // This is the point of sharing: consumers are never rewritten, so a rotation cannot miss
        // one of them.
        var repository = Repository();
        var entry = CertificateEntry("Rotating certificate");
        await repository.CreateAsync(entry);
        var profile = await SeedProfileAsync("Nightly FTPS");
        await repository.BindAsync(profile, "ftps.client-certificate", entry.Id);

        var rotated = entry.WithRotatedMaterial(
            Summary("CN=rotated.example.test"),
            DateTimeOffset.UtcNow);
        var applied = await repository.UpdateAsync(rotated, expectedVersion: 1);
        var loaded = await repository.GetAsync(entry.Id);
        var refused = await repository.DeleteAsync(entry.Id, expectedVersion: 2);

        Assert.Equal(KeyStoreWriteStatus.Succeeded, applied.Status);
        Assert.Equal(entry.MaterialReference, loaded!.MaterialReference);
        Assert.Equal("CN=rotated.example.test", ((Pkcs12CertificateSummary)loaded.Summary).Subject);
        Assert.Equal(KeyStoreWriteStatus.StillReferenced, refused.Status);
    }

    [Fact]
    public async Task Search_filters_by_kind_text_and_tag_and_reports_usage()
    {
        var repository = Repository();
        var certificate = CertificateEntry("Partner certificate", ["ftps", "partner"]);
        await repository.CreateAsync(certificate);
        await repository.CreateAsync(SshKeyEntry("Operator key", ["sftp"]));
        var profile = await SeedProfileAsync("Nightly FTPS");
        await repository.BindAsync(profile, "ftps.client-certificate", certificate.Id);

        var all = await repository.SearchAsync(new KeyStoreSearch());
        var certificates = await repository.SearchAsync(
            new KeyStoreSearch(Kind: KeyMaterialKind.Pkcs12Certificate));
        var byTag = await repository.SearchAsync(new KeyStoreSearch(Tag: "SFTP"));
        var byText = await repository.SearchAsync(new KeyStoreSearch(Text: "operator"));

        Assert.Equal(2, all.Count);
        Assert.Equal(["Nightly FTPS"], all.Single(u => u.Entry.Id == certificate.Id).ReferencedByProfileNames);
        Assert.Empty(all.Single(u => u.Entry.Kind == KeyMaterialKind.SshPrivateKey).ReferencedByProfileNames);
        Assert.Single(certificates);
        Assert.Single(byTag);
        Assert.Equal(KeyMaterialKind.SshPrivateKey, byTag[0].Entry.Kind);
        Assert.Single(byText);
    }

    [Fact]
    public async Task A_soft_deleted_profile_still_pins_its_entry_and_is_labelled()
    {
        // Soft delete keeps the binding row, so the foreign key would refuse the delete. The report
        // has to agree with the constraint, or a clear refusal becomes an opaque failure.
        var repository = Repository();
        var entry = CertificateEntry("Shared certificate");
        await repository.CreateAsync(entry);
        var profile = await SeedProfileAsync("Nightly FTPS");
        await repository.BindAsync(profile, "ftps.client-certificate", entry.Id);
        await new SqliteConnectionProfileRepository(Options()).SoftDeleteAsync(profile, expectedVersion: 1);

        var refused = await repository.DeleteAsync(entry.Id, expectedVersion: 1);

        Assert.Equal(KeyStoreWriteStatus.StillReferenced, refused.Status);
        Assert.Equal(["Nightly FTPS (deleted)"], refused.ReferencedBy!);
        Assert.NotNull(await repository.GetAsync(entry.Id));
    }

    [Fact]
    public async Task An_entry_can_be_found_by_the_vault_reference_it_owns()
    {
        // This is what lets a saved profile's bindings be derived from what it actually references,
        // instead of trusting the caller to declare them.
        var repository = Repository();
        var entry = CertificateEntry("Partner certificate");
        await repository.CreateAsync(entry);

        var found = await repository.FindByMaterialReferenceAsync(entry.MaterialReference.Value);
        var missing = await repository.FindByMaterialReferenceAsync(SecretReference.Create().Value);
        var blank = await repository.FindByMaterialReferenceAsync("   ");

        Assert.NotNull(found);
        Assert.Equal(entry.Id, found.Id);
        Assert.Null(missing);
        Assert.Null(blank);
    }

    [Fact]
    public async Task A_passphrase_reference_does_not_resolve_as_material()
    {
        // The two references are distinct slots; only the material identifies the entry.
        var repository = Repository();
        var entry = CertificateEntry("Partner certificate");
        await repository.CreateAsync(entry);

        Assert.Null(await repository.FindByMaterialReferenceAsync(entry.PassphraseReference!.Value.Value));
    }

    [Fact]
    public async Task Rebinding_a_slot_replaces_the_previous_entry()
    {
        var repository = Repository();
        var first = CertificateEntry("First certificate");
        var second = CertificateEntry("Second certificate");
        await repository.CreateAsync(first);
        await repository.CreateAsync(second);
        var profile = await SeedProfileAsync("Nightly FTPS");

        await repository.BindAsync(profile, "ftps.client-certificate", first.Id);
        await repository.BindAsync(profile, "ftps.client-certificate", second.Id);

        // The first entry is released by the rebind, so it can be deleted again.
        Assert.Equal(
            KeyStoreWriteStatus.Succeeded,
            (await repository.DeleteAsync(first.Id, expectedVersion: 1)).Status);
        Assert.Equal(
            KeyStoreWriteStatus.StillReferenced,
            (await repository.DeleteAsync(second.Id, expectedVersion: 1)).Status);
    }

    private async Task<ConnectionProfileId> SeedProfileAsync(string name)
    {
        var profile = ConnectionProfile.Create(
            ConnectionProfileId.New(),
            new ConnectionProfileMetadata(name),
            new LocalEndpoint("C:\\Data"),
            new NoAuthentication(),
            new ConnectionOperationalOptions(
                TimeSpan.FromSeconds(10),
                TimeSpan.FromMinutes(2),
                new ConnectionRetryPolicy(3, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5)),
                null,
                new ConnectionBandwidthLimits(null, null),
                "utf-8"),
            DateTimeOffset.UtcNow);
        await new SqliteConnectionProfileRepository(Options()).CreateAsync(profile);
        return profile.Id;
    }

    private SqliteKeyStoreRepository Repository() => new(Options());

    private SqliteDatabaseOptions Options() => new(
        Path.Combine(_directory, "storagehub.db"), pooling: false);

    private static KeyStoreEntry CertificateEntry(string name, IEnumerable<string>? tags = null) =>
        KeyStoreEntry.Create(
            KeyStoreEntryId.New(),
            KeyMaterialKind.Pkcs12Certificate,
            name,
            SecretReference.Create(),
            SecretReference.Create(),
            Summary("CN=partner.example.test"),
            DateTimeOffset.UtcNow,
            tags: tags);

    private static KeyStoreEntry SshKeyEntry(string name, IEnumerable<string>? tags = null) =>
        KeyStoreEntry.Create(
            KeyStoreEntryId.New(),
            KeyMaterialKind.SshPrivateKey,
            name,
            SecretReference.Create(),
            SecretReference.Create(),
            new SshPrivateKeySummary(
                SftpPrivateKeyFormat.OpenSsh,
                "ssh-ed25519",
                "SHA256:3q2+7wAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                comment: "operator@example"),
            DateTimeOffset.UtcNow,
            tags: tags);

    private static Pkcs12CertificateSummary Summary(string subject) => new(
        subject,
        "CN=StorageHub Test CA",
        DateTimeOffset.UtcNow.AddDays(-1),
        DateTimeOffset.UtcNow.AddDays(30),
        new string('A', 64),
        "RSA",
        2048,
        hasPrivateKey: true,
        chainLength: 2);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
