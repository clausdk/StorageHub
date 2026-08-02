using CL.Storage;
using CL.Storage.Configuration;
using CodeLogic;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Models;

namespace StorageHub.Storage.CodeLogic.Tests;

[Collection(ProviderIntegrationFixtureGroup.Name)]
public sealed class FtpFtpsProviderIntegrationTests : IAsyncLifetime
{
    private FtpFixtureSettings? _settings;
    private StorageLibrary? _library;
    private string? _testRoot;
    private bool _codeLogicStarted;

    public async Task InitializeAsync()
    {
        _settings = FtpFixtureSettings.Load();
        if (_settings is null)
        {
            return;
        }

        _testRoot = Path.Combine(Path.GetTempPath(), $"storagehub-ftp-{Guid.NewGuid():N}");
        var initialization = await global::CodeLogic.CodeLogic.InitializeAsync(options =>
        {
            options.FrameworkRootPath = Path.Combine(_testRoot, "framework");
            options.ApplicationRootPath = Path.Combine(_testRoot, "application");
            options.AppVersion = "test";
            options.HandleShutdownSignals = false;
        });
        Assert.True(initialization.Success);

        await Libraries.LoadAsync<StorageLibrary>();
        Libraries.OverrideConfig<StorageConfig>(
            "CL.Storage",
            "storage",
            configuration => configuration.Enabled = false);
        await global::CodeLogic.CodeLogic.ConfigureAsync();
        await global::CodeLogic.CodeLogic.StartAsync();
        _codeLogicStarted = true;
        _library = Libraries.Get<StorageLibrary>() ??
            throw new InvalidOperationException("CL.Storage was not registered by CodeLogic.");
    }

    [Fact]
    [Trait("Category", "FtpProviderIntegration")]
    public async Task FtpAndFtpsEndpoints_ConformAndFailClosedAcrossTlsModes()
    {
        if (_settings is null)
        {
            return;
        }

        await AssertPlainFtpConformanceAsync(_settings);
        await AssertExplicitFtpsConformanceAsync(_settings);
        await AssertImplicitFtpsConformanceAsync(_settings);
        await AssertMutualTlsConformanceAsync(_settings);
        await AssertHostileAuthenticationAndDowngradeFailuresAsync(_settings);
    }

    public async Task DisposeAsync()
    {
        if (_codeLogicStarted)
        {
            await global::CodeLogic.CodeLogic.StopAsync();
        }

        if (_testRoot is not null && Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private async Task AssertPlainFtpConformanceAsync(FtpFixtureSettings settings)
    {
        var profileId = ConnectionProfileId.New();
        var rootIdentity = $"ftp-root-{Guid.NewGuid():N}";
        await using var connection = await RegisterRequiredAsync(
            profileId,
            rootIdentity,
            CreateConfiguration(settings, settings.PlainPort, StorageFtpEncryptionMode.None));

        await ProviderSessionConformance.AssertBoundedRoundTripAsync(
            connection.Session,
            profileId,
            rootIdentity,
            StorageWriteMode.Overwrite);
        await ProviderSessionConformance.AssertAbortDoesNotPublishAsync(
            connection.Session,
            profileId,
            rootIdentity,
            StorageWriteMode.Overwrite);
        await ProviderSessionConformance.AssertAddressSubstitutionFailsBeforeProviderIoAsync(
            connection.Session,
            profileId,
            rootIdentity);

        var createOnlyAddress = StorageAddress.Create(
            profileId,
            rootIdentity,
            "create-only-must-not-start.bin").Value;
        var createOnly = await connection.Session.OpenWriteAsync(new StorageWriteRequest(
            createOnlyAddress,
            StorageWriteMode.CreateNew));
        Assert.True(createOnly.IsFailure);
        Assert.Equal("storage.create_new.atomicity_unsupported", createOnly.Error.Code);
    }

    private async Task AssertExplicitFtpsConformanceAsync(FtpFixtureSettings settings)
    {
        var profileId = ConnectionProfileId.New();
        var rootIdentity = $"ftpes-root-{Guid.NewGuid():N}";
        await using var connection = await RegisterRequiredAsync(
            profileId,
            rootIdentity,
            CreateConfiguration(
                settings,
                settings.ExplicitPort,
                StorageFtpEncryptionMode.Explicit,
                [settings.ServerFingerprint]));
        await ProviderSessionConformance.AssertBoundedRoundTripAsync(
            connection.Session,
            profileId,
            rootIdentity,
            StorageWriteMode.Overwrite);

        var wrongPin = new string('A', 64);
        if (StringComparer.OrdinalIgnoreCase.Equals(wrongPin, settings.ServerFingerprint))
        {
            wrongPin = new string('B', 64);
        }
        var pinFailure = await RejectAsync(CreateConfiguration(
            settings,
            settings.ExplicitPort,
            StorageFtpEncryptionMode.Explicit,
            [wrongPin]));
        Assert.Equal(StorageFailureKind.Unauthorized, pinFailure.Kind);

        var systemTrustFailure = await RejectAsync(CreateConfiguration(
            settings,
            settings.ExplicitPort,
            StorageFtpEncryptionMode.Explicit));
        Assert.Equal(StorageFailureKind.Unauthorized, systemTrustFailure.Kind);
    }

    private async Task AssertImplicitFtpsConformanceAsync(FtpFixtureSettings settings)
    {
        var profileId = ConnectionProfileId.New();
        var rootIdentity = $"ftpis-root-{Guid.NewGuid():N}";
        await using var connection = await RegisterRequiredAsync(
            profileId,
            rootIdentity,
            CreateConfiguration(
                settings,
                settings.ImplicitPort,
                StorageFtpEncryptionMode.Implicit,
                [settings.ServerFingerprint]));
        await ProviderSessionConformance.AssertBoundedRoundTripAsync(
            connection.Session,
            profileId,
            rootIdentity,
            StorageWriteMode.Overwrite);
    }

    private async Task AssertMutualTlsConformanceAsync(FtpFixtureSettings settings)
    {
        var missingCertificate = await RejectAsync(CreateConfiguration(
            settings,
            settings.MutualTlsPort,
            StorageFtpEncryptionMode.Explicit,
            [settings.ServerFingerprint]));
        Assert.Equal(StorageFailureKind.Unauthorized, missingCertificate.Kind);

        var profileId = ConnectionProfileId.New();
        var rootIdentity = $"ftps-mtls-root-{Guid.NewGuid():N}";
        await using var connection = await RegisterRequiredAsync(
            profileId,
            rootIdentity,
            CreateConfiguration(
                settings,
                settings.MutualTlsPort,
                StorageFtpEncryptionMode.Explicit,
                [settings.ServerFingerprint],
                settings.ClientPfxPath,
                settings.ClientPfxPassword));
        var health = await connection.Session.CheckHealthAsync();
        Assert.True(health.IsSuccess, Failure(health.Error));
        await ProviderSessionConformance.AssertAddressSubstitutionFailsBeforeProviderIoAsync(
            connection.Session,
            profileId,
            rootIdentity);

        var invalidPfxPassword = "invalid-" + Guid.NewGuid().ToString("N");
        var invalidPfxFailure = await RejectAsync(CreateConfiguration(
            settings,
            settings.MutualTlsPort,
            StorageFtpEncryptionMode.Explicit,
            [settings.ServerFingerprint],
            settings.ClientPfxPath,
            invalidPfxPassword));
        Assert.Equal(StorageFailureKind.Provider, invalidPfxFailure.Kind);
        AssertDoesNotDisclose(invalidPfxFailure, settings, invalidPfxPassword);
    }

    private async Task AssertHostileAuthenticationAndDowngradeFailuresAsync(FtpFixtureSettings settings)
    {
        var wrongPassword = "invalid-" + Guid.NewGuid().ToString("N");
        var wrongPasswordFailure = await RejectAsync(CreateConfiguration(
            settings,
            settings.PlainPort,
            StorageFtpEncryptionMode.None,
            password: wrongPassword));
        AssertDoesNotDisclose(wrongPasswordFailure, settings, wrongPassword);

        var tlsAgainstPlain = await RejectAsync(CreateConfiguration(
            settings,
            settings.PlainPort,
            StorageFtpEncryptionMode.Explicit,
            [settings.ServerFingerprint]));
        AssertDoesNotDisclose(tlsAgainstPlain, settings);

        var plaintextAgainstTls = await RejectAsync(CreateConfiguration(
            settings,
            settings.ExplicitPort,
            StorageFtpEncryptionMode.None));
        AssertDoesNotDisclose(plaintextAgainstTls, settings);
    }

    private async Task<RuntimeStorageConnection> RegisterRequiredAsync(
        ConnectionProfileId profileId,
        string rootIdentity,
        FtpConnectionConfig configuration)
    {
        var factory = new CodeLogicStorageSessionFactory(Assert.IsType<StorageLibrary>(_library));
        var registration = await factory.RegisterAsync(profileId, rootIdentity, configuration);
        Assert.True(registration.IsSuccess, Failure(registration.Error));
        return registration.Value;
    }

    private async Task<StorageFailure> RejectAsync(FtpConnectionConfig configuration)
    {
        var factory = new CodeLogicStorageSessionFactory(Assert.IsType<StorageLibrary>(_library));
        var registration = await factory.RegisterAsync(
            ConnectionProfileId.New(),
            $"rejected-ftp-{Guid.NewGuid():N}",
            configuration);
        if (registration.IsFailure)
        {
            AssertDoesNotDisclose(registration.Error, Assert.IsType<FtpFixtureSettings>(_settings));
            return registration.Error;
        }

        await using var connection = registration.Value;
        var health = await connection.Session.CheckHealthAsync();
        Assert.True(health.IsFailure);
        AssertDoesNotDisclose(health.Error, Assert.IsType<FtpFixtureSettings>(_settings));
        return health.Error;
    }

    private static FtpConnectionConfig CreateConfiguration(
        FtpFixtureSettings settings,
        int port,
        StorageFtpEncryptionMode encryptionMode,
        List<string>? fingerprints = null,
        string? clientCertificatePath = null,
        string? clientCertificatePassword = null,
        string? password = null) => new()
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = port,
            Root = "mounted",
            EncryptionMode = encryptionMode,
            DataConnectionMode = StorageFtpDataConnectionMode.Pasv,
            Username = settings.Username,
            Password = password ?? settings.Password,
            TrustedCertificateSha256 = fingerprints ?? [],
            ClientCertificatePath = clientCertificatePath,
            ClientCertificatePassword = clientCertificatePassword,
            TimeoutSeconds = 10
        };

    private static void AssertDoesNotDisclose(
        StorageFailure failure,
        FtpFixtureSettings settings,
        params string[] additionalSecrets)
    {
        var disclosed = string.Join(
            "|",
            failure.Code,
            failure.Message,
            failure.ProviderCode,
            failure.DiagnosticId);
        foreach (var secret in new[]
                 {
                     settings.Username,
                     settings.Password,
                     settings.ClientPfxPassword
                 }.Concat(additionalSecrets))
        {
            Assert.False(disclosed.Contains(secret, StringComparison.Ordinal));
        }
    }

    private static string Failure(StorageFailure? failure) => failure is null
        ? "The operation failed without a structured failure."
        : $"{failure.Code}: {failure.Message}";

    private sealed record FtpFixtureSettings(
        int PlainPort,
        int ExplicitPort,
        int ImplicitPort,
        int MutualTlsPort,
        string Username,
        string Password,
        string ServerFingerprint,
        string ClientPfxPath,
        string ClientPfxPassword)
    {
        private static readonly string[] PortNames =
            ["plain port", "explicit port", "implicit port", "mutual TLS port"];

        public static FtpFixtureSettings? Load()
        {
            var required = string.Equals(
                Environment.GetEnvironmentVariable("STORAGEHUB_REQUIRE_FTP"),
                "1",
                StringComparison.Ordinal);
            var values = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["plain port"] = Environment.GetEnvironmentVariable("STORAGEHUB_FTP_PLAIN_PORT"),
                ["explicit port"] = Environment.GetEnvironmentVariable("STORAGEHUB_FTP_EXPLICIT_PORT"),
                ["implicit port"] = Environment.GetEnvironmentVariable("STORAGEHUB_FTP_IMPLICIT_PORT"),
                ["mutual TLS port"] = Environment.GetEnvironmentVariable("STORAGEHUB_FTP_MTLS_PORT"),
                ["username"] = Environment.GetEnvironmentVariable("STORAGEHUB_FTP_USERNAME"),
                ["password"] = Environment.GetEnvironmentVariable("STORAGEHUB_FTP_PASSWORD"),
                ["server fingerprint"] = Environment.GetEnvironmentVariable("STORAGEHUB_FTP_SERVER_SHA256"),
                ["client PFX path"] = Environment.GetEnvironmentVariable("STORAGEHUB_FTP_CLIENT_PFX_PATH"),
                ["client PFX password"] = Environment.GetEnvironmentVariable("STORAGEHUB_FTP_CLIENT_PFX_PASSWORD")
            };
            if (!required && values.Values.All(value => value is null))
            {
                return null;
            }

            var ports = PortNames
                .Select(name => int.TryParse(values[name], out var port) && port is >= 1 and <= 65535
                    ? port
                    : throw new InvalidOperationException($"The FTP fixture {name} is missing or invalid."))
                .ToArray();
            if (ports.Distinct().Count() != ports.Length)
            {
                throw new InvalidOperationException("FTP fixture control ports must be distinct.");
            }

            var username = RequiredBounded(values["username"], "username");
            var password = RequiredBounded(values["password"], "password");
            var pfxPassword = RequiredBounded(values["client PFX password"], "client PFX password");
            var fingerprint = values["server fingerprint"];
            if (fingerprint is null ||
                fingerprint.Length != 64 ||
                fingerprint.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new InvalidOperationException("The FTP fixture server fingerprint is missing or invalid.");
            }

            var pfxPath = values["client PFX path"];
            if (string.IsNullOrWhiteSpace(pfxPath) ||
                !Path.IsPathFullyQualified(pfxPath) ||
                !File.Exists(pfxPath) ||
                !Path.GetExtension(pfxPath).Equals(".pfx", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The FTP fixture client PFX path is missing or invalid.");
            }

            return new FtpFixtureSettings(
                ports[0],
                ports[1],
                ports[2],
                ports[3],
                username,
                password,
                fingerprint.ToUpperInvariant(),
                Path.GetFullPath(pfxPath),
                pfxPassword);
        }

        private static string RequiredBounded(string? value, string description)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
            {
                throw new InvalidOperationException($"The FTP fixture {description} is missing or invalid.");
            }
            return value;
        }
    }
}
