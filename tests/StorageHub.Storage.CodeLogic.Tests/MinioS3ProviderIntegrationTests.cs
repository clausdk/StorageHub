using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using CL.Storage;
using CL.Storage.Configuration;
using CodeLogic;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Models;

namespace StorageHub.Storage.CodeLogic.Tests;

[Collection(ProviderIntegrationFixtureGroup.Name)]
public sealed class MinioS3ProviderIntegrationTests : IAsyncLifetime
{
    private MinioFixtureSettings? _settings;
    private AmazonS3Client? _administrativeClient;
    private RuntimeStorageConnection? _connection;
    private ConnectionProfileId _profileId;
    private string _rootIdentity = string.Empty;
    private string _prefix = string.Empty;
    private string? _testRoot;
    private bool _codeLogicStarted;

    public async Task InitializeAsync()
    {
        _settings = MinioFixtureSettings.Load();
        if (_settings is null)
        {
            return;
        }

        _testRoot = Path.Combine(
            Path.GetTempPath(),
            $"storagehub-minio-{Guid.NewGuid():N}");
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

        _administrativeClient = CreateClient(
            _settings.Endpoint,
            _settings.AccessKey,
            _settings.SecretKey);
        await _administrativeClient.PutBucketAsync(new PutBucketRequest
        {
            BucketName = _settings.Bucket
        });
        await _administrativeClient.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _settings.Bucket,
            Key = "outside/sentinel.txt",
            ContentBody = "must-not-be-mounted"
        });

        _profileId = ConnectionProfileId.New();
        _rootIdentity = $"minio-root-{Guid.NewGuid():N}";
        _prefix = $"mounted/{Guid.NewGuid():N}";
        var library = Libraries.Get<StorageLibrary>() ??
            throw new InvalidOperationException("CL.Storage was not registered by CodeLogic.");
        var factory = new CodeLogicStorageSessionFactory(library);
        var registration = await factory.RegisterAsync(
            _profileId,
            _rootIdentity,
            CreateConfiguration(_settings, _prefix));
        Assert.True(registration.IsSuccess, Failure(registration.Error));
        _connection = registration.Value;
    }

    [Fact]
    [Trait("Category", "ProviderIntegration")]
    public async Task S3CompatibleEndpoint_ConformsAndFailsClosedAgainstHostileInputs()
    {
        if (_settings is null)
        {
            return;
        }

        var connection = Assert.IsType<RuntimeStorageConnection>(_connection);
        var health = await connection.Session.CheckHealthAsync();
        Assert.True(health.IsSuccess, Failure(health.Error));

        await ProviderSessionConformance.AssertBoundedRoundTripAsync(
            connection.Session,
            _profileId,
            _rootIdentity);
        await ProviderSessionConformance.AssertCreateNewCollisionPreservesOriginalAsync(
            connection.Session,
            _profileId,
            _rootIdentity);
        await ProviderSessionConformance.AssertAbortDoesNotPublishAsync(
            connection.Session,
            _profileId,
            _rootIdentity);
        await ProviderSessionConformance.AssertAddressSubstitutionFailsBeforeProviderIoAsync(
            connection.Session,
            _profileId,
            _rootIdentity);

        var root = StorageAddress.Create(_profileId, _rootIdentity, string.Empty).Value;
        var rootListing = await connection.Session.ListAsync(
            root,
            new StorageListRequest(Recursive: true));
        Assert.True(rootListing.IsSuccess, Failure(rootListing.Error));
        Assert.DoesNotContain(rootListing.Value.Entries, entry =>
            entry.Address.CanonicalRelativePath.Contains("sentinel", StringComparison.Ordinal));

        var outside = StorageAddress.Create(
            _profileId,
            _rootIdentity,
            "outside/sentinel.txt").Value;
        var outsideLookup = await connection.Session.GetEntryAsync(outside);
        Assert.True(outsideLookup.IsFailure);
        Assert.Equal(StorageFailureKind.NotFound, outsideLookup.Error.Kind);

        var invalidToken = await connection.Session.ListAsync(
            root,
            new StorageListRequest(ContinuationToken: "hostile\0token"));
        Assert.True(invalidToken.IsFailure);
        Assert.Equal("storage.list.invalid_token", invalidToken.Error.Code);

        await AssertWrongCredentialsFailWithoutSecretDisclosureAsync();
    }

    [Theory]
    [InlineData("https://127.0.0.1:9000/")]
    [InlineData("http://192.0.2.10:9000/")]
    [InlineData("http://user@127.0.0.1:9000/")]
    [InlineData("http://127.0.0.1:9000/untrusted")]
    [InlineData("http://127.0.0.1:9000/?credential=value")]
    public void FixtureSettings_RejectNonLoopbackOrDecoratedEndpoints(string endpoint)
    {
        Assert.Throws<InvalidOperationException>(() => MinioFixtureSettings.Parse(
            endpoint,
            "fixture-access-key",
            "fixture-secret-key",
            "fixture-bucket",
            required: true));
    }

    public async Task DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _administrativeClient?.Dispose();
        if (_codeLogicStarted)
        {
            await global::CodeLogic.CodeLogic.StopAsync();
        }

        if (_testRoot is not null && Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private async Task AssertWrongCredentialsFailWithoutSecretDisclosureAsync()
    {
        var settings = Assert.IsType<MinioFixtureSettings>(_settings);
        var badAccessKey = $"invalid-{Guid.NewGuid():N}";
        var badSecretKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var library = Libraries.Get<StorageLibrary>() ??
            throw new InvalidOperationException("CL.Storage was not registered by CodeLogic.");
        var factory = new CodeLogicStorageSessionFactory(library);
        var registration = await factory.RegisterAsync(
            ConnectionProfileId.New(),
            $"wrong-credentials-{Guid.NewGuid():N}",
            CreateConfiguration(settings, _prefix, badAccessKey, badSecretKey));
        StorageFailure failure;
        if (registration.IsFailure)
        {
            failure = registration.Error;
        }
        else
        {
            await using var wrongConnection = registration.Value;
            var health = await wrongConnection.Session.CheckHealthAsync();
            Assert.True(health.IsFailure);
            failure = health.Error;
        }

        Assert.Equal(StorageFailureKind.Unauthorized, failure.Kind);

        var disclosed = string.Join(
            "|",
            failure.Code,
            failure.Message,
            failure.ProviderCode,
            failure.DiagnosticId);
        Assert.False(disclosed.Contains(settings.AccessKey, StringComparison.Ordinal));
        Assert.False(disclosed.Contains(settings.SecretKey, StringComparison.Ordinal));
        Assert.False(disclosed.Contains(badAccessKey, StringComparison.Ordinal));
        Assert.False(disclosed.Contains(badSecretKey, StringComparison.Ordinal));
    }

    private static S3ConnectionConfig CreateConfiguration(
        MinioFixtureSettings settings,
        string prefix,
        string? accessKey = null,
        string? secretKey = null) => new()
        {
            Bucket = settings.Bucket,
            Prefix = prefix,
            ServiceUrl = settings.Endpoint.AbsoluteUri,
            Region = "us-east-1",
            AuthenticationMode = S3AuthenticationMode.StaticCredentials,
            AccessKey = accessKey ?? settings.AccessKey,
            SecretKey = secretKey ?? settings.SecretKey,
            ForcePathStyle = true,
            AllowInsecureHttp = true,
            DisablePayloadSigning = false,
            DisableDefaultChecksumValidation = false,
            TimeoutSeconds = 10,
            MaxRetries = 0,
            Enabled = true
        };

    private static AmazonS3Client CreateClient(Uri endpoint, string accessKey, string secretKey) =>
        new(
            new BasicAWSCredentials(accessKey, secretKey),
            new AmazonS3Config
            {
                ServiceURL = endpoint.AbsoluteUri,
                AuthenticationRegion = "us-east-1",
                ForcePathStyle = true,
                MaxErrorRetry = 0,
                Timeout = TimeSpan.FromSeconds(10)
            });

    private static string Failure(StorageFailure? failure) => failure is null
        ? "The operation failed without a structured failure."
        : $"{failure.Code}: {failure.Message}";

    private sealed record MinioFixtureSettings(
        Uri Endpoint,
        string AccessKey,
        string SecretKey,
        string Bucket)
    {
        private const string EndpointVariable = "STORAGEHUB_MINIO_ENDPOINT";
        private const string AccessKeyVariable = "STORAGEHUB_MINIO_ACCESS_KEY";
        private const string SecretKeyVariable = "STORAGEHUB_MINIO_SECRET_KEY";
        private const string BucketVariable = "STORAGEHUB_MINIO_BUCKET";
        private const string RequiredVariable = "STORAGEHUB_REQUIRE_MINIO";

        public static MinioFixtureSettings? Load()
        {
            var endpointValue = Environment.GetEnvironmentVariable(EndpointVariable);
            var accessKey = Environment.GetEnvironmentVariable(AccessKeyVariable);
            var secretKey = Environment.GetEnvironmentVariable(SecretKeyVariable);
            var bucket = Environment.GetEnvironmentVariable(BucketVariable);
            var required = string.Equals(
                Environment.GetEnvironmentVariable(RequiredVariable),
                "1",
                StringComparison.Ordinal);

            return Parse(endpointValue, accessKey, secretKey, bucket, required);
        }

        internal static MinioFixtureSettings? Parse(
            string? endpointValue,
            string? accessKey,
            string? secretKey,
            string? bucket,
            bool required)
        {

            if (!required && endpointValue is null && accessKey is null && secretKey is null && bucket is null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(endpointValue) ||
                !Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint) ||
                endpoint.Scheme != Uri.UriSchemeHttp ||
                !endpoint.IsLoopback ||
                !string.IsNullOrEmpty(endpoint.UserInfo) ||
                !string.IsNullOrEmpty(endpoint.Query) ||
                !string.IsNullOrEmpty(endpoint.Fragment) ||
                endpoint.AbsolutePath != "/")
            {
                throw new InvalidOperationException(
                    $"{EndpointVariable} must be an HTTP loopback origin without credentials, path, query, or fragment.");
            }

            if (string.IsNullOrWhiteSpace(accessKey) || accessKey.Length > 128 || accessKey.Any(char.IsControl))
            {
                throw new InvalidOperationException($"{AccessKeyVariable} is missing or invalid.");
            }

            if (string.IsNullOrWhiteSpace(secretKey) || secretKey.Length > 256 || secretKey.Any(char.IsControl))
            {
                throw new InvalidOperationException($"{SecretKeyVariable} is missing or invalid.");
            }

            if (string.IsNullOrWhiteSpace(bucket) ||
                bucket.Length is < 3 or > 63 ||
                !char.IsAsciiLetterOrDigit(bucket[0]) ||
                !char.IsAsciiLetterOrDigit(bucket[^1]) ||
                !bucket.All(character =>
                    character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
            {
                throw new InvalidOperationException($"{BucketVariable} is missing or invalid.");
            }

            return new MinioFixtureSettings(endpoint, accessKey, secretKey, bucket);
        }
    }
}
