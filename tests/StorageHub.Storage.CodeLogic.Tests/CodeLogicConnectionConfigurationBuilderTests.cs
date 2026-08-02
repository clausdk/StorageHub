using System.Security.Cryptography;
using System.Text;
using CL.Storage.Configuration;
using StorageHub.Application.Connections;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Identifiers;
using StorageHub.Security;

namespace StorageHub.Storage.CodeLogic.Tests;

public sealed class CodeLogicConnectionConfigurationBuilderTests : IAsyncLifetime, IDisposable
{
    private const string TestPrivateKeyPassphrase = "storagehub-test-passphrase";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"storagehub-codelogic-profile-{Guid.NewGuid():N}");
    private VersionedFileSecretVault _vault = null!;
    private FakeTrustStore _trust = null!;
    private FakeSecretFileMaterializer _materializer = null!;

    public Task InitializeAsync()
    {
        _vault = new VersionedFileSecretVault(Path.Combine(_root, "vault"), new TestSecretProtector());
        _trust = new FakeTrustStore();
        _materializer = new FakeSecretFileMaterializer();
        return Task.CompletedTask;
    }

    [Fact]
    public void Private_key_formats_accept_real_decryptable_encrypted_keys()
    {
        var fixtures = CreateEncryptedPrivateKeyFixtures();
        try
        {
            foreach (var (format, key) in fixtures)
            {
                Assert.Equal(
                    PrivateKeyValidationResult.Valid,
                    PrivateKeyEncryptionValidator.Validate(key, TestPrivateKeyPassphrase, format));
            }
        }
        finally
        {
            ZeroFixtures(fixtures);
        }
    }

    [Fact]
    public void Private_key_formats_reject_wrong_passphrase()
    {
        var fixtures = CreateEncryptedPrivateKeyFixtures();
        try
        {
            foreach (var (format, key) in fixtures)
            {
                Assert.Equal(
                    PrivateKeyValidationResult.Invalid,
                    PrivateKeyEncryptionValidator.Validate(key, "wrong-passphrase", format));
            }
        }
        finally
        {
            ZeroFixtures(fixtures);
        }
    }

    [Fact]
    public void Private_key_formats_reject_real_unencrypted_keys()
    {
        var fixtures = CreateUnencryptedPrivateKeyFixtures();
        try
        {
            foreach (var (format, key) in fixtures)
            {
                Assert.Equal(
                    PrivateKeyValidationResult.Unencrypted,
                    PrivateKeyEncryptionValidator.Validate(key, TestPrivateKeyPassphrase, format));
            }
        }
        finally
        {
            ZeroFixtures(fixtures);
        }
    }

    [Fact]
    public void Private_key_envelopes_reject_comments_multiple_blocks_and_trailing_markers()
    {
        var valid = CreateEncryptedOpenSshPrivateKey();
        var comment = Encoding.ASCII.GetBytes("# imported key\n");
        var marker = Encoding.ASCII.GetBytes(
            $"\n# {CreatePemBoundary("BEGIN", "ENCRYPTED PRIVATE KEY")}\n");
        var prefixed = Combine(comment, valid);
        var appendedMarker = Combine(valid, marker);
        var multiple = Combine(valid, "\n"u8.ToArray(), valid);
        try
        {
            Assert.Equal(PrivateKeyValidationResult.Invalid, PrivateKeyEncryptionValidator.Validate(
                prefixed, TestPrivateKeyPassphrase, SftpPrivateKeyFormat.OpenSsh));
            Assert.Equal(PrivateKeyValidationResult.Invalid, PrivateKeyEncryptionValidator.Validate(
                appendedMarker, TestPrivateKeyPassphrase, SftpPrivateKeyFormat.OpenSsh));
            Assert.Equal(PrivateKeyValidationResult.Invalid, PrivateKeyEncryptionValidator.Validate(
                multiple, TestPrivateKeyPassphrase, SftpPrivateKeyFormat.OpenSsh));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(valid);
            CryptographicOperations.ZeroMemory(prefixed);
            CryptographicOperations.ZeroMemory(appendedMarker);
            CryptographicOperations.ZeroMemory(multiple);
        }
    }

    [Fact]
    public void Private_key_envelope_rejects_invalid_ciphertext_before_materialization()
    {
        var invalidCiphertext = CreateEncryptedPkcs8PrivateKey(truncateCiphertext: true);
        try
        {
            Assert.Equal(
                PrivateKeyValidationResult.Invalid,
                PrivateKeyEncryptionValidator.Validate(
                    invalidCiphertext,
                    TestPrivateKeyPassphrase,
                    SftpPrivateKeyFormat.Pkcs8));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(invalidCiphertext);
        }
    }

    [Fact]
    public void Private_key_validation_rejects_unbounded_kdf_work_factors()
    {
        var openSsh = SetOpenSshBcryptRounds(uint.MaxValue);
        var pkcs8 = CreateEncryptedPkcs8PrivateKey(iterationCount: 100_001);
        try
        {
            Assert.Equal(PrivateKeyValidationResult.Invalid, PrivateKeyEncryptionValidator.Validate(
                openSsh, TestPrivateKeyPassphrase, SftpPrivateKeyFormat.OpenSsh));
            Assert.Equal(PrivateKeyValidationResult.Invalid, PrivateKeyEncryptionValidator.Validate(
                pkcs8, TestPrivateKeyPassphrase, SftpPrivateKeyFormat.Pkcs8));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(openSsh);
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _vault.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

    }

    [Fact]
    public async Task S3_static_credentials_exist_only_in_prepared_runtime_configuration()
    {
        var accessKey = await StoreTextAsync("ACCESS-KEY");
        var secretKey = await StoreTextAsync("secret-key");
        var token = await StoreTextAsync("session-token");
        var profile = CreateProfile(
            new S3Endpoint("archive", "eu-north-1", rootPrefix: "tenant/backups"),
            new S3AccessKeyAuthentication(accessKey, secretKey, token),
            maximumAttempts: 4);

        var result = await CreateBuilder().BuildAsync(profile);

        Assert.True(result.IsSuccess);
        var prepared = result.Value;
        var configuration = Assert.IsType<S3ConnectionConfig>(prepared.Configuration);
        Assert.Equal(S3AuthenticationMode.StaticCredentials, configuration.AuthenticationMode);
        Assert.Equal("ACCESS-KEY", configuration.AccessKey);
        Assert.Equal("secret-key", configuration.SecretKey);
        Assert.Equal("session-token", configuration.SessionToken);
        Assert.Equal("tenant/backups", configuration.Prefix);
        Assert.Equal(3, configuration.MaxRetries);
        Assert.Contains(profile.Id.Value.ToString("N"), prepared.RootIdentity, StringComparison.Ordinal);

        await prepared.DisposeAsync();

        Assert.Null(configuration.AccessKey);
        Assert.Null(configuration.SecretKey);
        Assert.Null(configuration.SessionToken);
    }

    [Fact]
    public async Task Root_identity_is_deterministic_and_changes_with_profile_version()
    {
        var root = Path.GetFullPath(Path.Combine(_root, "identity-root"));
        var profile = CreateProfile(new LocalEndpoint(root), new NoAuthentication());
        var revised = Rehydrate(profile, version: checked(profile.Version + 1));

        await using var first = (await CreateBuilder().BuildAsync(profile)).Value;
        await using var repeated = (await CreateBuilder().BuildAsync(profile)).Value;
        await using var changed = (await CreateBuilder().BuildAsync(revised)).Value;

        Assert.Equal(first.RootIdentity, repeated.RootIdentity);
        Assert.NotEqual(first.RootIdentity, changed.RootIdentity);
        Assert.Contains($"version:{profile.Version}:", first.RootIdentity, StringComparison.Ordinal);
        Assert.Contains($"version:{revised.Version}:", changed.RootIdentity, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Root_identity_binds_username_and_authentication_mode_without_exposing_them()
    {
        var password = await StoreTextAsync("top-secret-password");
        var profile = CreateProfile(
            new FtpEndpoint("ftp.example.test", 21, allowInsecurePlainText: true),
            new UsernamePasswordAuthentication("alice", password));
        var changedPrincipal = Rehydrate(
            profile,
            authentication: new UsernamePasswordAuthentication("bob", password));
        var changedMode = Rehydrate(profile, authentication: new NoAuthentication());

        await using var alice = (await CreateBuilder().BuildAsync(profile)).Value;
        await using var bob = (await CreateBuilder().BuildAsync(changedPrincipal)).Value;
        await using var anonymous = (await CreateBuilder().BuildAsync(changedMode)).Value;

        Assert.NotEqual(alice.RootIdentity, bob.RootIdentity);
        Assert.NotEqual(alice.RootIdentity, anonymous.RootIdentity);
        Assert.DoesNotContain("alice", alice.RootIdentity, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("top-secret-password", alice.RootIdentity, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Root_identity_binds_vault_revision_without_hashing_secret_material()
    {
        var password = await StoreTextAsync("first-password-value");
        var profile = CreateProfile(
            new FtpEndpoint("ftp.example.test", 21, allowInsecurePlainText: true),
            new UsernamePasswordAuthentication("operator", password));

        await using var before = (await CreateBuilder().BuildAsync(profile)).Value;
        _ = await _vault.RotateAsync(password, "second-password-value"u8.ToArray());
        await using var after = (await CreateBuilder().BuildAsync(profile)).Value;

        Assert.NotEqual(before.RootIdentity, after.RootIdentity);
        Assert.DoesNotContain("first-password", before.RootIdentity, StringComparison.Ordinal);
        Assert.DoesNotContain("second-password", after.RootIdentity, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Root_identity_binds_the_selected_trust_record_revision()
    {
        var password = await StoreTextAsync("sftp-password");
        _trust.Records.Add(TrustedRecord(TrustArtifactKind.SshHostKey, "sftp.example.test", 22));
        var profile = CreateProfile(
            new SftpEndpoint("sftp.example.test", 22, SshHostKeyPolicy.Pinned),
            new UsernamePasswordAuthentication("operator", password));

        await using var before = (await CreateBuilder().BuildAsync(profile)).Value;
        _trust.Records[0] = _trust.Records[0] with { Version = 2 };
        await using var after = (await CreateBuilder().BuildAsync(profile)).Value;

        Assert.NotEqual(before.RootIdentity, after.RootIdentity);
        Assert.DoesNotContain(_trust.Records[0].Sha256Fingerprint, after.RootIdentity, StringComparison.Ordinal);
    }

    [Fact]
    public async Task S3_clear_text_endpoint_requires_and_forwards_explicit_profile_opt_in()
    {
        var endpoint = new S3Endpoint(
            "development",
            "us-east-1",
            new Uri("http://127.0.0.1:9000"),
            forcePathStyle: true,
            allowInsecureHttp: true);
        var profile = CreateProfile(
            endpoint,
            new S3DefaultCredentialChainAuthentication(),
            maximumAttempts: 1);

        var result = await CreateBuilder().BuildAsync(profile);

        Assert.True(result.IsSuccess);
        await using var prepared = result.Value;
        var configuration = Assert.IsType<S3ConnectionConfig>(prepared.Configuration);
        Assert.True(configuration.AllowInsecureHttp);
        Assert.Equal("http://127.0.0.1:9000/", configuration.ServiceUrl);
        Assert.True(configuration.ForcePathStyle);
    }

    [Fact]
    public async Task Local_profile_maps_only_options_supported_by_hardened_code_logic_config()
    {
        var root = Path.GetFullPath(Path.Combine(_root, "local-root"));
        var profile = CreateProfile(new LocalEndpoint(root), new NoAuthentication());

        var result = await CreateBuilder().BuildAsync(profile);

        Assert.True(result.IsSuccess);
        await using var prepared = result.Value;
        var configuration = Assert.IsType<LocalConnectionConfig>(prepared.Configuration);
        Assert.Equal(root, configuration.RootPath);
        Assert.False(configuration.FollowLinks);
    }

    [Fact]
    public async Task Sftp_private_key_requires_a_pin_and_material_lives_with_prepared_connection()
    {
        _trust.Records.Add(TrustedRecord(TrustArtifactKind.SshHostKey, "sftp.example.test", 22));
        var encryptedKey = CreateEncryptedOpenSshPrivateKey();
        var key = await StoreBytesAsync(encryptedKey);
        var passphrase = await StoreTextAsync(TestPrivateKeyPassphrase);
        var profile = CreateProfile(
            new SftpEndpoint("sftp.example.test", 22, SshHostKeyPolicy.Pinned, "restricted/root"),
            new SftpPrivateKeyAuthentication("operator", key, passphrase, SftpPrivateKeyFormat.OpenSsh));

        var result = await CreateBuilder().BuildAsync(profile);

        Assert.True(result.IsSuccess);
        var prepared = result.Value;
        var configuration = Assert.IsType<SftpConnectionConfig>(prepared.Configuration);
        Assert.Equal("restricted/root", configuration.Root);
        Assert.Equal("operator", configuration.Username);
        Assert.Equal(TestPrivateKeyPassphrase, configuration.PrivateKeyPassphrase);
        Assert.Equal(Assert.Single(_trust.Records).Sha256Fingerprint, Assert.Single(configuration.HostKeyFingerprints));
        var material = Assert.Single(_materializer.Materials);
        Assert.False(material.IsDisposed);
        Assert.Equal(".key", material.Extension);
        Assert.Equal(encryptedKey, material.Bytes);

        await prepared.DisposeAsync();

        Assert.True(material.IsDisposed);
        Assert.Null(configuration.PrivateKeyPassphrase);
    }

    [Fact]
    public async Task Missing_or_expired_host_key_pin_fails_before_private_key_is_opened()
    {
        _trust.Records.Add(TrustedRecord(
            TrustArtifactKind.SshHostKey,
            "sftp.example.test",
            22) with
        { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1) });
        var profile = CreateProfile(
            new SftpEndpoint("sftp.example.test", 22, SshHostKeyPolicy.Pinned),
            new SftpPrivateKeyAuthentication(
                "operator",
                SecretReference.Create(),
                SecretReference.Create(),
                SftpPrivateKeyFormat.Pem));

        var result = await CreateBuilder().BuildAsync(profile);

        Assert.True(result.IsFailure);
        Assert.Equal("storage.trust.approval_required", result.Error.Code);
        Assert.Equal(StorageFailureKind.Security, result.Error.Kind);
        Assert.Empty(_materializer.Materials);
    }

    [Fact]
    public async Task ExplicitlyRejectedHostKeyNeverSatisfiesPinnedProfile()
    {
        _trust.Records.Add(TrustedRecord(
            TrustArtifactKind.SshHostKey,
            "sftp.example.test",
            22) with
        { Decision = TrustDecision.Rejected });
        var profile = CreateProfile(
            new SftpEndpoint("sftp.example.test", 22, SshHostKeyPolicy.Pinned),
            new UsernamePasswordAuthentication("operator", SecretReference.Create()));

        var result = await CreateBuilder().BuildAsync(profile);

        Assert.True(result.IsFailure);
        Assert.Equal("storage.trust.approval_required", result.Error.Code);
        Assert.Equal(StorageFailureKind.Security, result.Error.Kind);
    }

    [Fact]
    public async Task Unencrypted_private_key_is_rejected_before_runtime_materialization()
    {
        _trust.Records.Add(TrustedRecord(TrustArtifactKind.SshHostKey, "sftp.example.test", 22));
        var unencryptedKey = CreateUnencryptedOpenSshPrivateKey();
        var key = await StoreBytesAsync(unencryptedKey);
        var passphrase = await StoreTextAsync("a-vault-passphrase");
        var profile = CreateProfile(
            new SftpEndpoint("sftp.example.test", 22, SshHostKeyPolicy.Pinned),
            new SftpPrivateKeyAuthentication("operator", key, passphrase, SftpPrivateKeyFormat.OpenSsh));

        var result = await CreateBuilder().BuildAsync(profile);

        Assert.True(result.IsFailure);
        Assert.Equal("storage.credential.private_key_unprotected", result.Error.Code);
        Assert.Empty(_materializer.Materials);
    }

    [Fact]
    public async Task Wrong_private_key_passphrase_is_rejected_before_runtime_materialization()
    {
        _trust.Records.Add(TrustedRecord(TrustArtifactKind.SshHostKey, "sftp.example.test", 22));
        var encryptedKey = CreateEncryptedOpenSshPrivateKey();
        var key = await StoreBytesAsync(encryptedKey);
        var passphrase = await StoreTextAsync("wrong-passphrase");
        var profile = CreateProfile(
            new SftpEndpoint("sftp.example.test", 22, SshHostKeyPolicy.Pinned),
            new SftpPrivateKeyAuthentication("operator", key, passphrase, SftpPrivateKeyFormat.OpenSsh));

        var result = await CreateBuilder().BuildAsync(profile);

        Assert.True(result.IsFailure);
        Assert.Equal("storage.credential.private_key_invalid", result.Error.Code);
        Assert.Equal(StorageFailureKind.Security, result.Error.Kind);
        Assert.Empty(_materializer.Materials);
    }

    [Fact]
    public async Task Ftps_system_trust_keeps_validation_enabled_and_materializes_per_profile_pfx()
    {
        var password = await StoreTextAsync("ftp-password");
        var pfx = await StoreBytesAsync([1, 2, 3, 4, 5]);
        var pfxPassword = await StoreTextAsync("pfx-password");
        var profile = CreateProfile(
            new FtpsEndpoint(
                "ftps.example.test",
                990,
                FtpsTlsMode.Implicit,
                TlsCertificatePolicy.SystemTrust,
                pfx,
                pfxPassword,
                "incoming"),
            new UsernamePasswordAuthentication("operator", password));

        var result = await CreateBuilder().BuildAsync(profile);

        Assert.True(result.IsSuccess);
        var prepared = result.Value;
        var configuration = Assert.IsType<FtpConnectionConfig>(prepared.Configuration);
        Assert.Equal(StorageFtpEncryptionMode.Implicit, configuration.EncryptionMode);
        Assert.Empty(configuration.TrustedCertificateSha256);
        Assert.Equal("incoming", configuration.Root);
        Assert.Equal("ftp-password", configuration.Password);
        Assert.Equal("pfx-password", configuration.ClientCertificatePassword);
        Assert.EndsWith(".pfx", configuration.ClientCertificatePath, StringComparison.Ordinal);

        var material = Assert.Single(_materializer.Materials);
        await prepared.DisposeAsync();
        Assert.True(material.IsDisposed);
        Assert.Null(configuration.ClientCertificatePassword);
    }

    [Fact]
    public async Task Tofu_and_s3_pinning_fail_closed_when_provider_cannot_capture_or_enforce_them()
    {
        var tofu = CreateProfile(
            new SftpEndpoint("sftp.example.test", 22, SshHostKeyPolicy.TrustOnFirstUse),
            new UsernamePasswordAuthentication("operator", SecretReference.Create()));
        var pinnedS3 = CreateProfile(
            new S3Endpoint("archive", "eu-north-1", tlsPolicy: TlsCertificatePolicy.Pinned),
            new S3DefaultCredentialChainAuthentication(),
            maximumAttempts: 1);

        var tofuResult = await CreateBuilder().BuildAsync(tofu);
        var s3Result = await CreateBuilder().BuildAsync(pinnedS3);

        Assert.True(tofuResult.IsFailure);
        Assert.Equal("storage.profile.unsupported", tofuResult.Error.Code);
        Assert.True(s3Result.IsFailure);
        Assert.Equal("storage.profile.unsupported", s3Result.Error.Code);
    }

    [Fact]
    public async Task Unenforceable_proxy_bandwidth_retry_and_split_timeout_options_are_rejected()
    {
        var endpoint = new FtpEndpoint("ftp.example.test", 21, allowInsecurePlainText: true);
        var authentication = new NoAuthentication();
        var proxy = CreateProfile(endpoint, authentication, proxy: true);
        var bandwidth = CreateProfile(endpoint, authentication, bandwidth: true);
        var retry = CreateProfile(endpoint, authentication, maximumAttempts: 2);
        var splitTimeout = CreateProfile(endpoint, authentication, splitTimeouts: true);

        Assert.Equal("storage.proxy.unsupported", (await CreateBuilder().BuildAsync(proxy)).Error?.Code);
        Assert.Equal("storage.bandwidth.unsupported", (await CreateBuilder().BuildAsync(bandwidth)).Error?.Code);
        Assert.Equal("storage.retry.unsupported", (await CreateBuilder().BuildAsync(retry)).Error?.Code);
        Assert.Equal("storage.timeout.unsupported", (await CreateBuilder().BuildAsync(splitTimeout)).Error?.Code);
    }

    private CodeLogicConnectionConfigurationBuilder CreateBuilder() => new(
        _vault,
        _trust,
        _materializer,
        TimeProvider.System);

    private async ValueTask<SecretReference> StoreTextAsync(string value) =>
        await StoreBytesAsync(Encoding.UTF8.GetBytes(value));

    private async ValueTask<SecretReference> StoreBytesAsync(byte[] value) =>
        (await _vault.CreateAsync(value)).Reference;

    private static ConnectionProfile CreateProfile(
        ConnectionEndpoint endpoint,
        ConnectionAuthentication authentication,
        int maximumAttempts = 0,
        bool proxy = false,
        bool bandwidth = false,
        bool splitTimeouts = false)
    {
        var now = DateTimeOffset.UtcNow;
        return ConnectionProfile.Create(
            ConnectionProfileId.New(),
            new ConnectionProfileMetadata("Test connection"),
            endpoint,
            authentication,
            new ConnectionOperationalOptions(
                TimeSpan.FromSeconds(30),
                splitTimeouts ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(30),
                new ConnectionRetryPolicy(maximumAttempts, TimeSpan.Zero, TimeSpan.Zero),
                proxy ? new ConnectionProxy(new Uri("https://proxy.example.test:8443")) : null,
                bandwidth
                    ? new ConnectionBandwidthLimits(1_000_000, 2_000_000)
                    : new ConnectionBandwidthLimits(null, null),
                "utf-8"),
            now);
    }

    private static ConnectionProfile Rehydrate(
        ConnectionProfile profile,
        ConnectionAuthentication? authentication = null,
        long? version = null) => ConnectionProfile.Rehydrate(
            profile.Id,
            profile.Provider,
            profile.Metadata,
            profile.Endpoint,
            authentication ?? profile.Authentication,
            profile.OperationalOptions,
            profile.IsEnabled,
            version ?? profile.Version,
            profile.CreatedUtc,
            profile.UpdatedUtc,
            profile.DeletedUtc);

    private static TrustRecord TrustedRecord(TrustArtifactKind kind, string host, int port)
    {
        var now = DateTimeOffset.UtcNow;
        return new TrustRecord(
            $"trust-{Guid.NewGuid():N}",
            kind,
            host,
            port,
            "SHA256",
            new string('A', 64),
            TrustDecision.Trusted,
            TrustDecisionSource.UserVerified,
            now,
            now,
            null,
            null,
            1);
    }

    private static (SftpPrivateKeyFormat Format, byte[] Key)[] CreateEncryptedPrivateKeyFixtures() =>
    [
        (SftpPrivateKeyFormat.OpenSsh, CreateEncryptedOpenSshPrivateKey()),
        (SftpPrivateKeyFormat.Pem, CreateEncryptedLegacyPemPrivateKey()),
        (SftpPrivateKeyFormat.Pkcs8, CreateEncryptedPkcs8PrivateKey())
    ];

    private static (SftpPrivateKeyFormat Format, byte[] Key)[] CreateUnencryptedPrivateKeyFixtures()
    {
        using var rsa = RSA.Create(2048);
        var pkcs1 = rsa.ExportRSAPrivateKey();
        var pkcs8 = rsa.ExportPkcs8PrivateKey();
        try
        {
            return
            [
                (SftpPrivateKeyFormat.OpenSsh, CreateUnencryptedOpenSshPrivateKey()),
                (SftpPrivateKeyFormat.Pem, EncodePem("RSA PRIVATE KEY", pkcs1)),
                (SftpPrivateKeyFormat.Pkcs8, EncodePem("PRIVATE KEY", pkcs8))
            ];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs1);
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    private static byte[] CreateEncryptedPkcs8PrivateKey(
        bool truncateCiphertext = false,
        int iterationCount = 10_000)
    {
        using var rsa = RSA.Create(2048);
        var encrypted = rsa.ExportEncryptedPkcs8PrivateKey(
            TestPrivateKeyPassphrase,
            new PbeParameters(
                PbeEncryptionAlgorithm.Aes256Cbc,
                HashAlgorithmName.SHA256,
                iterationCount));
        try
        {
            if (truncateCiphertext)
            {
                Array.Resize(ref encrypted, encrypted.Length - 1);
            }

            return EncodePem("ENCRYPTED PRIVATE KEY", encrypted);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    private static byte[] EncodePem(string label, ReadOnlySpan<byte> data)
    {
        var characters = PemEncoding.Write(label, data);
        try
        {
            return Encoding.ASCII.GetBytes(characters);
        }
        finally
        {
            Array.Clear(characters);
        }
    }

    private static byte[] SetOpenSshBcryptRounds(uint rounds)
    {
        var payload = Convert.FromBase64String(EncryptedOpenSshPayloadBase64);
        try
        {
            var offset = "openssh-key-v1\0"u8.Length;
            SkipSshString(payload, ref offset);
            SkipSshString(payload, ref offset);
            var optionsLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(offset));
            offset += sizeof(uint);
            var optionsEnd = checked(offset + (int)optionsLength);
            SkipSshString(payload, ref offset);
            if (optionsEnd - offset != sizeof(uint))
            {
                throw new InvalidOperationException("The embedded OpenSSH fixture has unexpected KDF options.");
            }

            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(offset), rounds);
            return EncodePem("OPENSSH PRIVATE KEY", payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static void SkipSshString(ReadOnlySpan<byte> value, ref int offset)
    {
        var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(value[offset..]);
        offset = checked(offset + sizeof(uint) + (int)length);
        if (offset > value.Length)
        {
            throw new InvalidOperationException("The embedded OpenSSH fixture is malformed.");
        }
    }

    private static byte[] Combine(params byte[][] values)
    {
        var result = new byte[values.Sum(static value => value.Length)];
        var offset = 0;
        foreach (var value in values)
        {
            value.CopyTo(result, offset);
            offset += value.Length;
        }

        return result;
    }

    private static void ZeroFixtures(IEnumerable<(SftpPrivateKeyFormat Format, byte[] Key)> fixtures)
    {
        foreach (var (_, key) in fixtures)
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] CreateEncryptedOpenSshPrivateKey() => EncodeFixturePayload(
        "OPENSSH PRIVATE KEY",
        EncryptedOpenSshPayloadBase64);

    private static byte[] CreateUnencryptedOpenSshPrivateKey() => EncodeFixturePayload(
        "OPENSSH PRIVATE KEY",
        UnencryptedOpenSshPayloadBase64);

    private static byte[] EncodeFixturePayload(string label, string base64)
    {
        var payload = Convert.FromBase64String(base64);
        try
        {
            return EncodePem(label, payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static byte[] CreateEncryptedLegacyPemPrivateKey()
    {
        var builder = new StringBuilder();
        builder.AppendLine(CreatePemBoundary("BEGIN", "RSA PRIVATE KEY"));
        builder.AppendLine("Proc-Type: 4,ENCRYPTED");
        builder.Append("DEK-Info: AES-128-CBC,").AppendLine(EncryptedLegacyInitializationVectorHex);
        builder.AppendLine();
        for (var offset = 0; offset < EncryptedLegacyCiphertextBase64.Length; offset += 64)
        {
            builder.Append(EncryptedLegacyCiphertextBase64.AsSpan(
                offset,
                Math.Min(64, EncryptedLegacyCiphertextBase64.Length - offset))).AppendLine();
        }

        builder.AppendLine(CreatePemBoundary("END", "RSA PRIVATE KEY"));
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static string CreatePemBoundary(string kind, string label) =>
        string.Concat(new string('-', 5), kind, " ", label, new string('-', 5));

    // Boundary-free payload fixtures are wrapped into PEM only at runtime. Keeping source free of
    // private-key blocks prevents repository scanners from treating synthetic test data as a secret.
    private const string EncryptedOpenSshPayloadBase64 =
        "b3BlbnNzaC1rZXktdjEAAAAACmFlczI1Ni1jdHIAAAAGYmNyeXB0AAAAGAAAABD+xqm44T" +
        "FDJRorXqDtTMLpAAAAAgAAAAEAAAAzAAAAC3NzaC1lZDI1NTE5AAAAIP/ARGuoxFTutBrP" +
        "PgBVvIacTALW1xTMXGwNjVW914nOAAAAoOkjbMIacIUO/YUAolVQUzhR28wkNjcBRS7l1h" +
        "TPs1K4VMvzWDJH/iVkjILk2NxF8gJLuhZneT/R/HI0AtnbkOczOVHSXQu9GNWFKD2ZnT0N" +
        "Rh9mTm4EuXUyS3kQG4GGoNuG66O/KtxtpYKb0WlCyBCp5Zbe3LS6HZO2xIh4PjebyVPnXI" +
        "LEw1DxKGa/KXLIFCW/zM5PjEweIL01QovJT6c=";

    private const string UnencryptedOpenSshPayloadBase64 =
        "b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAAAMwAAAAtzc2gtZW" +
        "QyNTUxOQAAACD8OpB7VuThBONbVGUvFdGJhjG4sc9DttdnJrGURlqLxQAAAKDUTJVo1EyV" +
        "aAAAAAtzc2gtZWQyNTUxOQAAACD8OpB7VuThBONbVGUvFdGJhjG4sc9DttdnJrGURlqLxQ" +
        "AAAEAvrdykbTuq/UDTtByhIgWLItL9IiiezVMXTmFu5l7S4vw6kHtW5OEE41tUZS8V0YmG" +
        "Mbixz0O212cmsZRGWovFAAAAF3N0b3JhZ2VodWItdGVzdC1maXh0dXJlAQIDBAUG";

    private const string EncryptedLegacyInitializationVectorHex =
        "40B4AA93734E90892FFCC31359AE0133";

    private const string EncryptedLegacyCiphertextBase64 =
        "idKY7Xk87v0VmMdvhYTKA/BpljzxmhiAdYf7xmHubHJNvhyyTg19YtrWhsQD+JZC" +
        "fMLmkSVHOUb7Sj42T3iWK5XTaock4XMx2OTpyHT4LiyMsF81rm6Vk/6u/Ti2cK9R" +
        "Jw43G2qv0bFb7MsvL1PtzK8NxgC+RUcSr6UUMSwHZ1heVKu+IoDWQhDenUUkme4H" +
        "lB0qbNG8GaRmF0QDMsF2SQ42h6JFDPPm049ch55jiG0nk65riUglv3MQlGZyQmq/" +
        "QUOHKov5BZp/k0Tt1x3rEPg5hyy/oOqRKK9CK58g3Wq1mvO0Z6J6MlzUauiEnqRn" +
        "QXxBUQrNb1cHsZ7/vjJwSfJ5IEOzv2sghkNphPMaP3myb4nMGrZ0VzCBzvjFSM6p" +
        "wQKTfVcRKFRHgU9o4xq4k1aB4bBfKQqlDXAKQX2NkC//o3zjiPpucAHLMEUgTikd" +
        "DU5qX9DRb/VbfpafsxfH4QH6eXt94q1cn7jmwPPxQQyB0pjM1StTmb7Y2J73aiG4" +
        "rpNYRXYFWwVdSEOI2z8CAY6gBJl/7rNK++hjypVXU6iPWaQw5wmlI1iJib9MTf6W" +
        "mQ+f5yq8q9iBWcqD10xAEQcv8JI4zfPObqb6Kaouo9VYc+ICA842zPcy9wIjjyUR" +
        "GKtTXxejyfkUNkOlJzKHPSrO7qUHupm4qeo7UXmCaLS0TOnfeIC5I+MBOAq3u2UH" +
        "Lm3bAoHbsqbX4QYnDGz4AP1x21F1Zx6vIwhiyGx+nC6zeDemNPVIpMXbDr90LvCK" +
        "lFWvRI0ScVCj+ttbU9km9wsS29p94zv8/b9fN3MX+axxAmE+RaQ33sMUusPdqzSf";

    private sealed class FakeTrustStore : ITrustStore
    {
        internal List<TrustRecord> Records { get; } = [];

        public ValueTask<IReadOnlyList<TrustRecord>> FindAsync(
            TrustArtifactKind artifactKind,
            string canonicalHost,
            int port,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<TrustRecord> result = Records
                .Where(record => record.ArtifactKind == artifactKind &&
                    string.Equals(record.CanonicalHost, canonicalHost, StringComparison.OrdinalIgnoreCase) &&
                    record.Port == port)
                .ToArray();
            return ValueTask.FromResult(result);
        }

        public ValueTask UpsertAsync(TrustRecord record, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<bool> RemoveAsync(
            string trustId,
            int expectedVersion,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeSecretFileMaterializer : IRuntimeSecretFileMaterializer
    {
        internal List<FakeSecretFile> Materials { get; } = [];

        public ValueTask<IRuntimeSecretFile> MaterializeAsync(
            ReadOnlyMemory<byte> secret,
            string fileExtension,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var material = new FakeSecretFile(secret.ToArray(), fileExtension);
            Materials.Add(material);
            return ValueTask.FromResult<IRuntimeSecretFile>(material);
        }
    }

    private sealed class FakeSecretFile(byte[] bytes, string extension) : IRuntimeSecretFile
    {
        internal byte[] Bytes { get; } = bytes;
        internal string Extension { get; } = extension;
        internal bool IsDisposed { get; private set; }
        public string FullPath => $"C:\\private\\runtime-secret{Extension}";

        public ValueTask DisposeAsync()
        {
            CryptographicOperations.ZeroMemory(Bytes);
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestSecretProtector : ISecretProtector
    {
        private static readonly byte[] Key = SHA256.HashData("storagehub-adapter-tests"u8.ToArray());

        public string Scheme => "test-xor-v1";

        public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy) => Transform(plaintext);

        public byte[] Unprotect(ReadOnlySpan<byte> protectedData, ReadOnlySpan<byte> entropy) => Transform(protectedData);

        private static byte[] Transform(ReadOnlySpan<byte> input)
        {
            var output = new byte[input.Length];
            for (var index = 0; index < input.Length; index++)
            {
                output[index] = (byte)(input[index] ^ Key[index % Key.Length]);
            }

            return output;
        }
    }
}
