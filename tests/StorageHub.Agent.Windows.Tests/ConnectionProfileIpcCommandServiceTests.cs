using System.Text.Json;
using Microsoft.Data.Sqlite;
using StorageHub.Application.Connections;
using StorageHub.Contracts.Ipc;
using StorageHub.Domain.Identifiers;
using StorageHub.Persistence;
using ContractWriteStatus = StorageHub.Contracts.Ipc.ConnectionProfileWriteStatus;

namespace StorageHub.Agent.Windows.Tests;

public sealed class ConnectionProfileIpcCommandServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateMapsValidatedReferenceOnlyDraftIntoRepository()
    {
        var repository = new RecordingProfileRepository();
        var service = new ConnectionProfileIpcCommandService(
            repository,
            new FixedTimeProvider(Now));
        var accessKey = "shs_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var secretKey = "shs_BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        var draft = S3Draft(accessKey, secretKey);
        var request = IpcEnvelope.Create(
            ConnectionProfileIpcMessageTypes.CreateRequest,
            Guid.NewGuid(),
            1,
            new ConnectionProfileCreateRequest(ConnectionProfileIpcContract.CurrentVersion, draft));

        var result = await service.HandleAsync(request);
        var response = result.Payload.Deserialize<ConnectionProfileWriteResponse>();

        Assert.Equal(ConnectionProfileIpcMessageTypes.CreateResponse, result.MessageType);
        Assert.Equal(ContractWriteStatus.Succeeded, response?.Status);
        var stored = Assert.IsType<ConnectionProfile>(repository.Created);
        var authentication = Assert.IsType<S3AccessKeyAuthentication>(stored.Authentication);
        Assert.Equal(accessKey, authentication.AccessKeyReference.Value);
        Assert.Equal(secretKey, authentication.SecretKeyReference.Value);
        Assert.Equal(Now, stored.CreatedUtc);
        Assert.DoesNotContain("SecretMaterial", result.Payload.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateRejectsProviderIrrelevantFieldsBeforeRepositoryCall()
    {
        var repository = new RecordingProfileRepository();
        var service = new ConnectionProfileIpcCommandService(repository, new FixedTimeProvider(Now));
        var unsafeDraft = S3Draft(
            "shs_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            "shs_BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB") with
        {
            Endpoint = new ConnectionEndpointDocument(
                StorageConnectionProvider.S3,
                Host: "password-value-hidden-in-unused-field",
                Bucket: "archive",
                Region: "eu-north-1")
        };
        var request = IpcEnvelope.Create(
            ConnectionProfileIpcMessageTypes.CreateRequest,
            Guid.NewGuid(),
            1,
            new ConnectionProfileCreateRequest(ConnectionProfileIpcContract.CurrentVersion, unsafeDraft));

        var result = await service.HandleAsync(request);
        var response = result.Payload.Deserialize<ConnectionProfileWriteResponse>();

        Assert.Equal(ContractWriteStatus.ValidationFailed, response?.Status);
        Assert.Null(repository.Created);
        Assert.DoesNotContain("password-value", result.Payload.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateAndDeletePreserveOptimisticConcurrencyVersion()
    {
        var existing = ConnectionProfile.Create(
            ConnectionProfileId.New(),
            new ConnectionProfileMetadata("Local files", notes: "server-only operational note"),
            new LocalEndpoint("C:\\Data"),
            new NoAuthentication(),
            Options(),
            Now);
        var repository = new RecordingProfileRepository(existing);
        var service = new ConnectionProfileIpcCommandService(repository, new FixedTimeProvider(Now));
        var updateRequest = IpcEnvelope.Create(
            ConnectionProfileIpcMessageTypes.UpdateRequest,
            Guid.NewGuid(),
            1,
            new ConnectionProfileUpdateRequest(
                ConnectionProfileIpcContract.CurrentVersion,
                existing.Id.Value,
                ExpectedVersion: 7,
                LocalDraft("Renamed", "C:\\Data")));

        var updated = await service.HandleAsync(updateRequest);
        var updateResponse = updated.Payload.Deserialize<ConnectionProfileWriteResponse>();
        var deleteRequest = IpcEnvelope.Create(
            ConnectionProfileIpcMessageTypes.DeleteRequest,
            Guid.NewGuid(),
            2,
            new ConnectionProfileDeleteRequest(
                ConnectionProfileIpcContract.CurrentVersion,
                existing.Id.Value,
                ExpectedVersion: 8));
        var deleted = await service.HandleAsync(deleteRequest);
        var deleteResponse = deleted.Payload.Deserialize<ConnectionProfileWriteResponse>();

        Assert.Equal(7, repository.UpdateExpectedVersion);
        Assert.Equal("Renamed", repository.Updated?.Metadata.DisplayName);
        Assert.Equal("server-only operational note", repository.Updated?.Metadata.Notes);
        Assert.Equal(8, repository.DeleteExpectedVersion);
        Assert.Equal(ContractWriteStatus.Succeeded, updateResponse?.Status);
        Assert.Equal(ContractWriteStatus.Succeeded, deleteResponse?.Status);
    }

    [Fact]
    public async Task CrudRoundTripsThroughAuthoritativeSqliteRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "storagehub-profile-ipc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new ConnectionProfileIpcCommandService(
                new SqliteDatabaseOptions(Path.Combine(root, "storagehub.db")),
                new FixedTimeProvider(Now));
            var created = await service.HandleAsync(IpcEnvelope.Create(
                ConnectionProfileIpcMessageTypes.CreateRequest,
                Guid.NewGuid(),
                1,
                new ConnectionProfileCreateRequest(
                    ConnectionProfileIpcContract.CurrentVersion,
                    LocalDraft("Local", "C:\\Data"))));
            var createdPayload = created.Payload.Deserialize<ConnectionProfileWriteResponse>();
            var createdProfile = Assert.IsType<ConnectionProfileDocument>(createdPayload?.Profile);

            var read = await service.HandleAsync(IpcEnvelope.Create(
                ConnectionProfileIpcMessageTypes.GetRequest,
                Guid.NewGuid(),
                2,
                new ConnectionProfileGetRequest(
                    ConnectionProfileIpcContract.CurrentVersion,
                    createdProfile.ConnectionId)));
            var readPayload = read.Payload.Deserialize<ConnectionProfileGetResponse>();

            var updated = await service.HandleAsync(IpcEnvelope.Create(
                ConnectionProfileIpcMessageTypes.UpdateRequest,
                Guid.NewGuid(),
                3,
                new ConnectionProfileUpdateRequest(
                    ConnectionProfileIpcContract.CurrentVersion,
                    createdProfile.ConnectionId,
                    createdProfile.Version,
                    LocalDraft("Renamed", "C:\\Data"))));
            var updatedProfile = Assert.IsType<ConnectionProfileDocument>(
                updated.Payload.Deserialize<ConnectionProfileWriteResponse>()?.Profile);

            var deleted = await service.HandleAsync(IpcEnvelope.Create(
                ConnectionProfileIpcMessageTypes.DeleteRequest,
                Guid.NewGuid(),
                4,
                new ConnectionProfileDeleteRequest(
                    ConnectionProfileIpcContract.CurrentVersion,
                    updatedProfile.ConnectionId,
                    updatedProfile.Version)));

            Assert.Equal("Local", readPayload?.Profile?.Draft.Metadata.DisplayName);
            Assert.Equal(2, updatedProfile.Version);
            Assert.Equal(ContractWriteStatus.Succeeded,
                deleted.Payload.Deserialize<ConnectionProfileWriteResponse>()?.Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateReportsDatabaseRecoveryWithoutLeakingDatabaseDetails()
    {
        var root = Path.Combine(Path.GetTempPath(), "storagehub-profile-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "sensitive-database-name.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE unexpected(value TEXT); PRAGMA user_version = 1;";
                await command.ExecuteNonQueryAsync();
            }

            var service = new ConnectionProfileIpcCommandService(
                new SqliteDatabaseOptions(databasePath, pooling: false),
                new FixedTimeProvider(Now));
            var result = await service.HandleAsync(IpcEnvelope.Create(
                ConnectionProfileIpcMessageTypes.CreateRequest,
                Guid.NewGuid(),
                1,
                new ConnectionProfileCreateRequest(
                    ConnectionProfileIpcContract.CurrentVersion,
                    LocalDraft("Local", "C:\\Data"))));
            var response = result.Payload.Deserialize<ConnectionProfileWriteResponse>();

            Assert.Equal(ContractWriteStatus.Unavailable, response?.Status);
            Assert.Equal("connection.profile.database_recovery_required", response?.Failure?.Code);
            Assert.Contains("requires recovery", response?.Failure?.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(root, result.Payload.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sensitive-database-name", result.Payload.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static ConnectionProfileDraft S3Draft(string accessKey, string secretKey) => new(
        new ConnectionProfileMetadataDocument("Archive", Tags: ["backup"]),
        new ConnectionEndpointDocument(
            StorageConnectionProvider.S3,
            Bucket: "archive",
            Region: "eu-north-1",
            ServiceEndpoint: "https://s3.example.com"),
        new ConnectionAuthenticationDocument(
            ConnectionAuthenticationKind.S3AccessKey,
            AccessKeyReference: accessKey,
            SecretKeyReference: secretKey),
        new ConnectionOperationalOptionsDocument());

    private static ConnectionProfileDraft LocalDraft(string name, string root) => new(
        new ConnectionProfileMetadataDocument(name, Tags: []),
        new ConnectionEndpointDocument(StorageConnectionProvider.Local, RootPath: root),
        new ConnectionAuthenticationDocument(ConnectionAuthenticationKind.None),
        new ConnectionOperationalOptionsDocument());

    private static ConnectionOperationalOptions Options() => new(
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
        new ConnectionRetryPolicy(3, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5)),
        proxy: null,
        new ConnectionBandwidthLimits(null, null),
        "utf-8");

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class RecordingProfileRepository : IConnectionProfileRepository
    {
        private readonly ConnectionProfile? _existing;

        public RecordingProfileRepository(ConnectionProfile? existing = null) => _existing = existing;

        public ConnectionProfile? Created { get; private set; }
        public ConnectionProfile? Updated { get; private set; }
        public long? UpdateExpectedVersion { get; private set; }
        public long? DeleteExpectedVersion { get; private set; }

        public ValueTask<ConnectionProfileWriteResult> CreateAsync(
            ConnectionProfile profile,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Created = profile;
            return ValueTask.FromResult(new ConnectionProfileWriteResult(
                StorageHub.Application.Connections.ConnectionProfileWriteStatus.Succeeded,
                profile,
                profile.Version));
        }

        public ValueTask<ConnectionProfile?> GetAsync(
            ConnectionProfileId id,
            bool includeDeleted = false,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(_existing);

        public ValueTask<IReadOnlyList<ConnectionProfile>> SearchAsync(
            ConnectionProfileSearch search,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<ConnectionProfile>>(_existing is null ? [] : [_existing]);

        public ValueTask<ConnectionProfileWriteResult> UpdateAsync(
            ConnectionProfile profile,
            long expectedVersion,
            CancellationToken cancellationToken = default)
        {
            Updated = profile;
            UpdateExpectedVersion = expectedVersion;
            var result = profile with { Version = expectedVersion + 1, UpdatedUtc = Now };
            return ValueTask.FromResult(new ConnectionProfileWriteResult(
                StorageHub.Application.Connections.ConnectionProfileWriteStatus.Succeeded,
                result,
                result.Version));
        }

        public ValueTask<ConnectionProfileWriteResult> SetEnabledAsync(
            ConnectionProfileId id,
            bool enabled,
            long expectedVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<ConnectionProfileWriteResult> SoftDeleteAsync(
            ConnectionProfileId id,
            long expectedVersion,
            CancellationToken cancellationToken = default)
        {
            DeleteExpectedVersion = expectedVersion;
            return ValueTask.FromResult(new ConnectionProfileWriteResult(
                StorageHub.Application.Connections.ConnectionProfileWriteStatus.Succeeded,
                ActualVersion: expectedVersion + 1));
        }
    }
}
