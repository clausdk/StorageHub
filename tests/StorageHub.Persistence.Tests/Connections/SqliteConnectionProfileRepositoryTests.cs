using Microsoft.Data.Sqlite;
using StorageHub.Application.Connections;
using StorageHub.Domain.Identifiers;
using StorageHub.Persistence.Connections;
using StorageHub.Security;
using Xunit;

namespace StorageHub.Persistence.Tests.Connections;

public sealed class SqliteConnectionProfileRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"storagehub-profile-repository-{Guid.NewGuid():N}");

    [Fact]
    public async Task Crud_round_trip_preserves_provider_metadata_and_reference_authentication()
    {
        var repository = Repository();
        var profile = SftpProfile("Production SFTP", "Partners/Acme", ["partner", "nightly"]);

        var created = await repository.CreateAsync(profile);
        var loaded = await repository.GetAsync(profile.Id);

        Assert.Equal(ConnectionProfileWriteStatus.Succeeded, created.Status);
        Assert.NotNull(loaded);
        Assert.Equal(profile.Metadata.DisplayName, loaded.Metadata.DisplayName);
        Assert.Equal(profile.Metadata.FolderPath, loaded.Metadata.FolderPath);
        Assert.Equal(profile.Metadata.Tags.AsEnumerable(), loaded.Metadata.Tags.AsEnumerable());
        Assert.Equal(profile.Metadata.IsFavorite, loaded.Metadata.IsFavorite);
        Assert.Equal(profile.Metadata.IconKey, loaded.Metadata.IconKey);
        Assert.Equal(profile.Metadata.AccentColor, loaded.Metadata.AccentColor);
        Assert.Equal(profile.Endpoint, loaded.Endpoint);
        Assert.Equal(profile.Authentication, loaded.Authentication);
        Assert.Equal(1, loaded.Version);
    }

    [Fact]
    public async Task Every_supported_provider_endpoint_and_authentication_round_trips()
    {
        var repository = Repository();
        var now = DateTimeOffset.UtcNow;
        var password = SecretReference.Create();
        var profiles = new[]
        {
            ConnectionProfile.Create(ConnectionProfileId.New(), new ConnectionProfileMetadata("Local provider"),
                new LocalEndpoint("C:\\Storage"), new NoAuthentication(), OptionsForConnection(), now),
            ConnectionProfile.Create(ConnectionProfileId.New(), new ConnectionProfileMetadata("S3 provider"),
                new S3Endpoint("backups", "eu-west-1", new Uri("https://s3.example.test"), true,
                    TlsCertificatePolicy.Pinned),
                new S3AccessKeyAuthentication(
                    SecretReference.Create(),
                    SecretReference.Create(),
                    SecretReference.Create()), OptionsForConnection(), now),
            ConnectionProfile.Create(ConnectionProfileId.New(), new ConnectionProfileMetadata("S3 default chain"),
                new S3Endpoint("public-archive", "eu-west-1"),
                new S3DefaultCredentialChainAuthentication(), OptionsForConnection(), now),
            ConnectionProfile.Create(ConnectionProfileId.New(), new ConnectionProfileMetadata("FTP provider"),
                new FtpEndpoint("ftp.example.test", 21, true),
                new UsernamePasswordAuthentication("ftp-user", password), OptionsForConnection(), now),
            ConnectionProfile.Create(ConnectionProfileId.New(), new ConnectionProfileMetadata("FTPS provider"),
                new FtpsEndpoint("ftps.example.test", 990, FtpsTlsMode.Implicit,
                    TlsCertificatePolicy.TrustOnFirstUse, SecretReference.Create(), SecretReference.Create()),
                new UsernamePasswordAuthentication("ftps-user", password), OptionsForConnection(), now),
            ConnectionProfile.Create(ConnectionProfileId.New(), new ConnectionProfileMetadata("SFTP provider"),
                new SftpEndpoint("sftp.example.test", 22, SshHostKeyPolicy.TrustOnFirstUse),
                new SftpPrivateKeyAuthentication("sftp-user", SecretReference.Create(), SecretReference.Create(),
                    SftpPrivateKeyFormat.Pkcs8), OptionsForConnection(), now),
            ConnectionProfile.Create(ConnectionProfileId.New(), new ConnectionProfileMetadata("SSH client"),
                new SshClientEndpoint("ssh.example.test", 22, SshHostKeyPolicy.Pinned),
                new UsernamePasswordAuthentication("ssh-user", password), OptionsForConnection(), now)
        };

        foreach (var profile in profiles)
        {
            Assert.Equal(ConnectionProfileWriteStatus.Succeeded, (await repository.CreateAsync(profile)).Status);
            var loaded = await repository.GetAsync(profile.Id);
            Assert.NotNull(loaded);
            Assert.Equal(profile.Provider, loaded.Provider);
            Assert.Equal(profile.Type, loaded.Type);
            Assert.Equal(profile.Endpoint, loaded.Endpoint);
            Assert.Equal(profile.Authentication, loaded.Authentication);
            Assert.Equal(profile.OperationalOptions, loaded.OperationalOptions);
        }
    }

    [Fact]
    public async Task Search_filters_by_text_folder_tag_provider_and_favorite()
    {
        var repository = Repository();
        await repository.CreateAsync(SftpProfile("Production SFTP", "Partners/Acme", ["partner", "nightly"]));
        await repository.CreateAsync(LocalProfile("Downloads", "Local", ["personal"]));

        var matches = await repository.SearchAsync(new ConnectionProfileSearch(
            Text: "production",
            FolderPath: "partners/acme",
            Tag: "PARTNER",
            Provider: ConnectionProviderKind.Sftp,
            IsFavorite: true));

        var match = Assert.Single(matches);
        Assert.Equal("Production SFTP", match.Metadata.DisplayName);
    }

    [Fact]
    public async Task Update_uses_optimistic_concurrency_and_returns_the_new_version()
    {
        var repository = Repository();
        var profile = LocalProfile("Local", "Workstations", ["fast"]);
        var created = await repository.CreateAsync(profile);
        var updatedProfile = created.Profile! with
        {
            Metadata = new ConnectionProfileMetadata("Local SSD", "Workstations", ["fast"])
        };

        var updated = await repository.UpdateAsync(updatedProfile, expectedVersion: 1);
        var stale = await repository.UpdateAsync(updatedProfile, expectedVersion: 1);

        Assert.Equal(ConnectionProfileWriteStatus.Succeeded, updated.Status);
        Assert.Equal(2, updated.Profile!.Version);
        Assert.Equal(ConnectionProfileWriteStatus.VersionConflict, stale.Status);
        Assert.Equal(2, stale.ActualVersion);
    }

    [Fact]
    public async Task Disable_and_soft_delete_are_versioned_and_deleted_profiles_stay_hidden()
    {
        var repository = Repository();
        var profile = LocalProfile("Disposable", "Lab", []);
        await repository.CreateAsync(profile);

        var disabled = await repository.SetEnabledAsync(profile.Id, false, expectedVersion: 1);
        var deleted = await repository.SoftDeleteAsync(profile.Id, expectedVersion: 2);

        Assert.False(disabled.Profile!.IsEnabled);
        Assert.Equal(ConnectionProfileWriteStatus.Succeeded, deleted.Status);
        Assert.Null(await repository.GetAsync(profile.Id));
        Assert.NotNull(await repository.GetAsync(profile.Id, includeDeleted: true));
        Assert.Empty(await repository.SearchAsync(new ConnectionProfileSearch(IncludeDisabled: true)));
    }

    [Fact]
    public async Task Profile_schema_is_idempotent_and_can_coexist_with_the_foundation_schema()
    {
        var options = Options();
        Assert.True((await new StorageHubDatabaseInitializer(options).InitializeAsync()).IsReady);
        var initializer = new ConnectionProfileSchemaInitializer(options);

        await initializer.InitializeAsync();
        await initializer.InitializeAsync();

        var repository = new SqliteConnectionProfileRepository(options);
        var profile = LocalProfile("Foundation profile", "Local", []);
        Assert.Equal(ConnectionProfileWriteStatus.Succeeded, (await repository.CreateAsync(profile)).Status);
        Assert.NotNull(await repository.GetAsync(profile.Id));

        await using var connection = new SqliteConnection($"Data Source={options.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='connection_profiles';";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Repository_first_access_runs_authoritative_migrations_without_poisoning_the_database()
    {
        var options = Options();
        var repository = new SqliteConnectionProfileRepository(options);
        var profile = LocalProfile("First profile", "Local", []);

        Assert.Equal(ConnectionProfileWriteStatus.Succeeded, (await repository.CreateAsync(profile)).Status);

        var foundation = await new StorageHubDatabaseInitializer(options).InitializeAsync();
        Assert.True(foundation.IsReady);
        Assert.True(foundation.SchemaVersion >= 1);
        Assert.NotNull(await repository.GetAsync(profile.Id));
    }

    [Fact]
    public async Task Duplicate_active_names_return_a_conflict_without_overwriting()
    {
        var repository = Repository();
        await repository.CreateAsync(LocalProfile("Archive", "Local", []));

        var duplicate = await repository.CreateAsync(LocalProfile("archive", "Cloud", []));

        Assert.Equal(ConnectionProfileWriteStatus.NameConflict, duplicate.Status);
        Assert.Single(await repository.SearchAsync(new ConnectionProfileSearch(Text: "archive")));
    }

    private SqliteConnectionProfileRepository Repository() => new(Options());

    private SqliteDatabaseOptions Options() => new(
        Path.Combine(_directory, "storagehub.db"), pooling: false);

    private static ConnectionProfile LocalProfile(string name, string folder, IReadOnlyList<string> tags) =>
        ConnectionProfile.Create(
            ConnectionProfileId.New(),
            new ConnectionProfileMetadata(name, folder, tags),
            new LocalEndpoint("C:\\Data"),
            new NoAuthentication(),
            OptionsForConnection(),
            DateTimeOffset.UtcNow);

    private static ConnectionProfile SftpProfile(string name, string folder, IReadOnlyList<string> tags) =>
        ConnectionProfile.Create(
            ConnectionProfileId.New(),
            new ConnectionProfileMetadata(name, folder, tags, isFavorite: true, iconKey: "server-lock",
                accentColor: "#33AA77"),
            new SftpEndpoint("sftp.example.test", 22, SshHostKeyPolicy.Pinned),
            new SftpPrivateKeyAuthentication(
                "operator", SecretReference.Create(), SecretReference.Create(), SftpPrivateKeyFormat.OpenSsh),
            OptionsForConnection(),
            DateTimeOffset.UtcNow);

    private static ConnectionOperationalOptions OptionsForConnection() => new(
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMinutes(2),
        new ConnectionRetryPolicy(3, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5)),
        new ConnectionProxy(new Uri("socks5://proxy.example.test:1080"), CredentialReferenceId.New()),
        new ConnectionBandwidthLimits(10_000_000, 20_000_000),
        "utf-8");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
