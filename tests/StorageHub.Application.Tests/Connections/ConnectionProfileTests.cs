using StorageHub.Application.Connections;
using StorageHub.Domain.Identifiers;
using StorageHub.Security;

namespace StorageHub.Application.Tests.Connections;

public sealed class ConnectionProfileTests
{
    private static readonly SecretReference Password = SecretReference.Create();

    [Fact]
    public void Metadata_normalizes_tags_and_keeps_an_immutable_snapshot()
    {
        var tags = new[] { " Production ", "backup", "PRODUCTION" };

        var metadata = new ConnectionProfileMetadata(
            "Archive",
            folderPath: "Cloud/Production",
            tags: tags,
            isFavorite: true,
            defaultPaths: new ConnectionDefaultPaths("/", "/incoming", "/downloads"),
            iconKey: "cloud-lock",
            accentColor: "#3366CC",
            notes: "Off-site archive");
        tags[0] = "mutated";

        Assert.Collection(
            metadata.Tags,
            tag => Assert.Equal("backup", tag),
            tag => Assert.Equal("Production", tag));
        Assert.Equal("Cloud/Production", metadata.FolderPath);
        Assert.Equal("#3366CC", metadata.AccentColor);
    }

    [Theory]
    [MemberData(nameof(ValidProfiles))]
    public void Supported_provider_profiles_are_valid(ConnectionProfile profile)
    {
        Assert.False(profile.Id.IsEmpty);
        Assert.Equal(profile.Provider, profile.Endpoint.Provider);
        Assert.Equal(1, profile.Version);
    }

    [Fact]
    public void Plain_ftp_requires_an_explicit_insecure_transport_acknowledgement()
    {
        var error = Assert.Throws<ArgumentException>(() => new FtpEndpoint(
            "ftp.example.test", 21, allowInsecurePlainText: false));

        Assert.Contains("insecure", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Endpoints_reject_embedded_credentials_and_insecure_s3_without_acknowledgement()
    {
        Assert.Throws<ArgumentException>(() => new ConnectionProxy(
            new Uri("https://user:password@proxy.example.test:8443")));
        Assert.Throws<ArgumentException>(() => new S3Endpoint(
            "archive",
            "local",
            new Uri("https://access:secret@s3.example.test")));
        Assert.Throws<ArgumentException>(() => new S3Endpoint(
            "archive",
            "local",
            new Uri("http://minio.example.test:9000")));

        var acknowledged = new S3Endpoint(
            "archive",
            "local",
            new Uri("http://minio.example.test:9000"),
            forcePathStyle: true,
            allowInsecureHttp: true);

        Assert.True(acknowledged.AllowInsecureHttp);
    }

    [Fact]
    public void Ftps_requires_a_tls_policy_and_accepts_only_opaque_pfx_references()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FtpsEndpoint(
            "ftps.example.test",
            21,
            FtpsTlsMode.Explicit,
            TlsCertificatePolicy.Unspecified));

        var pfx = SecretReference.Create();
        var endpoint = new FtpsEndpoint(
            "ftps.example.test",
            21,
            FtpsTlsMode.Explicit,
            TlsCertificatePolicy.SystemTrust,
            pfx,
            SecretReference.Create());

        Assert.Equal(pfx, endpoint.ClientCertificatePfxReference);
        Assert.Throws<ArgumentException>(() => new FtpsEndpoint(
            "ftps.example.test",
            21,
            FtpsTlsMode.Explicit,
            TlsCertificatePolicy.SystemTrust,
            SecretReference.Create()));
    }

    [Fact]
    public void Sftp_requires_a_host_key_policy_and_rejects_unknown_private_key_formats()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SftpEndpoint(
            "sftp.example.test", 22, SshHostKeyPolicy.Unspecified));

        Assert.Throws<ArgumentOutOfRangeException>(() => new SftpPrivateKeyAuthentication(
            "operator",
            SecretReference.Create(),
            passphraseReference: null,
            (SftpPrivateKeyFormat)999));
        Assert.Throws<ArgumentException>(() => new SftpPrivateKeyAuthentication(
            "operator",
            SecretReference.Create(),
            passphraseReference: null,
            SftpPrivateKeyFormat.OpenSsh));
    }

    [Fact]
    public void Authentication_contracts_expose_references_not_secret_strings()
    {
        var passwordProperty = typeof(UsernamePasswordAuthentication)
            .GetProperty(nameof(UsernamePasswordAuthentication.PasswordReference));
        var keyProperty = typeof(SftpPrivateKeyAuthentication)
            .GetProperty(nameof(SftpPrivateKeyAuthentication.PrivateKeyReference));
        var s3SecretProperty = typeof(S3AccessKeyAuthentication)
            .GetProperty(nameof(S3AccessKeyAuthentication.SecretKeyReference));

        Assert.Equal(typeof(SecretReference), passwordProperty!.PropertyType);
        Assert.Equal(typeof(SecretReference), keyProperty!.PropertyType);
        Assert.Equal(typeof(SecretReference), s3SecretProperty!.PropertyType);
        Assert.DoesNotContain(
            typeof(UsernamePasswordAuthentication).GetProperties(),
            property => property.PropertyType == typeof(string) &&
                property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void S3_default_credential_chain_requires_an_explicit_authentication_choice()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentException>(() => ConnectionProfile.Create(
            ConnectionProfileId.New(),
            Metadata("S3"),
            new S3Endpoint("archive", "eu-north-1"),
            new NoAuthentication(),
            OperationalOptions(),
            now));

        var profile = ConnectionProfile.Create(
            ConnectionProfileId.New(),
            Metadata("S3"),
            new S3Endpoint("archive", "eu-north-1"),
            new S3DefaultCredentialChainAuthentication(),
            OperationalOptions(),
            now);
        Assert.IsType<S3DefaultCredentialChainAuthentication>(profile.Authentication);
    }

    [Fact]
    public void Provider_and_endpoint_must_match()
    {
        Assert.Throws<ArgumentException>(() => ConnectionProfile.Create(
            ConnectionProfileId.New(),
            Metadata("Mismatch"),
            new LocalEndpoint("C:\\Data"),
            new NoAuthentication(),
            OperationalOptions(),
            DateTimeOffset.UtcNow,
            provider: ConnectionProviderKind.S3));
    }

    public static TheoryData<ConnectionProfile> ValidProfiles()
    {
        var now = DateTimeOffset.UtcNow;
        var data = new TheoryData<ConnectionProfile>
        {
            ConnectionProfile.Create(ConnectionProfileId.New(), Metadata("Local"),
                new LocalEndpoint("C:\\Data"), new NoAuthentication(), OperationalOptions(), now),
            ConnectionProfile.Create(ConnectionProfileId.New(), Metadata("S3"),
                new S3Endpoint("archive", "eu-north-1"),
                new S3AccessKeyAuthentication(
                    SecretReference.Create(),
                    SecretReference.Create(),
                    SecretReference.Create()), OperationalOptions(), now),
            ConnectionProfile.Create(ConnectionProfileId.New(), Metadata("FTP"),
                new FtpEndpoint("ftp.example.test", 21, allowInsecurePlainText: true),
                new UsernamePasswordAuthentication("operator", Password), OperationalOptions(), now),
            ConnectionProfile.Create(ConnectionProfileId.New(), Metadata("FTPS"),
                new FtpsEndpoint("ftps.example.test", 21, FtpsTlsMode.Explicit, TlsCertificatePolicy.SystemTrust),
                new UsernamePasswordAuthentication("operator", Password), OperationalOptions(), now),
            ConnectionProfile.Create(ConnectionProfileId.New(), Metadata("SFTP"),
                new SftpEndpoint("sftp.example.test", 22, SshHostKeyPolicy.Pinned),
                new SftpPrivateKeyAuthentication("operator", SecretReference.Create(), SecretReference.Create(),
                    SftpPrivateKeyFormat.OpenSsh), OperationalOptions(), now)
        };
        return data;
    }

    private static ConnectionProfileMetadata Metadata(string name) => new(name);

    private static ConnectionOperationalOptions OperationalOptions() => new(
        TimeSpan.FromSeconds(15),
        TimeSpan.FromMinutes(5),
        new ConnectionRetryPolicy(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10)),
        proxy: null,
        new ConnectionBandwidthLimits(null, null),
        "utf-8");
}
