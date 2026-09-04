using System.Globalization;
using System.Text.Json;
using StorageHub.Application.Connections;
using StorageHub.Contracts.Ipc;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Capabilities;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Security;
using StorageHub.Storage.Abstractions;
using StorageHub.Storage.Models;

namespace StorageHub.Agent.Windows.Tests;

public sealed class StorageIpcCommandServiceTests
{
    [Fact]
    public async Task ConnectionListReturnsMetadataWithoutSecretReferences()
    {
        var accessKey = SecretReference.Create();
        var secretKey = SecretReference.Create();
        var profile = CreateS3Profile(accessKey, secretKey);
        var repository = new FakeProfileRepository(profile);
        var service = new StorageIpcCommandService(repository, new FakeSessionOpener());
        var request = IpcEnvelope.Create(
            StorageIpcMessageTypes.ConnectionListRequest,
            Guid.NewGuid(),
            1,
            new ConnectionListRequest(
                StorageIpcContract.CurrentVersion,
                SearchText: "Archive",
                Provider: StorageConnectionProvider.S3,
                IncludeDisabled: false,
                Limit: 10));

        var response = await service.HandleAsync(request);
        var payload = response.Payload.Deserialize<ConnectionListResponse>();
        var rawJson = response.Payload.GetRawText();

        Assert.Equal(StorageIpcMessageTypes.ConnectionListResponse, response.MessageType);
        var summary = Assert.Single(Assert.IsType<ConnectionListResponse>(payload).Connections);
        Assert.Equal(profile.Id.Value, summary.ConnectionId);
        Assert.Equal("Archive", summary.DisplayName);
        Assert.Equal(StorageConnectionProvider.S3, summary.Provider);
        Assert.DoesNotContain(accessKey.Value, rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain(secretKey.Value, rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("authentication", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("endpoint", rawJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectionTestSanitizesProviderFailureAndDoesNotOpenAWriteSurface()
    {
        var profile = CreateLocalProfile();
        var rawSecret = "password=hunter2";
        var opener = new FakeSessionOpener(StorageResult<IStorageIpcSessionLease>.Fail(
            new StorageFailure(
                "storage.provider.denied",
                StorageFailureKind.Unauthorized,
                rawSecret,
                isTransient: false,
                providerCode: rawSecret,
                diagnosticId: rawSecret)));
        var service = new StorageIpcCommandService(new FakeProfileRepository(profile), opener);
        var request = IpcEnvelope.Create(
            StorageIpcMessageTypes.ConnectionTestRequest,
            Guid.NewGuid(),
            1,
            new ConnectionTestRequest(StorageIpcContract.CurrentVersion, profile.Id.Value));

        var response = await service.HandleAsync(request);
        var payload = Assert.IsType<ConnectionTestResponse>(
            response.Payload.Deserialize<ConnectionTestResponse>());

        Assert.False(payload.Succeeded);
        Assert.Equal(StorageIpcFailureCategory.Unauthorized, payload.Failure?.Category);
        Assert.Equal("storage.provider.denied", payload.Failure?.Code);
        Assert.DoesNotContain(rawSecret, response.Payload.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(1, opener.OpenCount);
        Assert.False(service.CanHandle("storage.delete.request"));
        Assert.False(service.CanHandle("storage.write.request"));

        var listed = await service.HandleAsync(IpcEnvelope.Create(
            StorageIpcMessageTypes.ConnectionListRequest,
            Guid.NewGuid(),
            1,
            new ConnectionListRequest(StorageIpcContract.CurrentVersion, IncludeDisabled: true)));
        var health = Assert.Single(Assert.IsType<ConnectionListResponse>(
            listed.Payload.Deserialize<ConnectionListResponse>()).Connections).Health;
        Assert.NotNull(health);
        Assert.Equal(ConnectionHealthState.NeedsAttention, health.State);
        Assert.True(health.RequiresCredentialAction);
        Assert.False(health.RequiresTrustAction);
        Assert.Equal("The provider rejected the saved credentials.", health.Status);
        Assert.DoesNotContain(rawSecret, listed.Payload.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulConnectionTestIsReturnedAsARevisionBoundHealthSnapshot()
    {
        var profile = CreateLocalProfile();
        var session = new FakeSession(
            profile.Id,
            "root-test",
            StorageResult<StoragePage>.Success(new StoragePage([])));
        var repository = new FakeProfileRepository(profile);
        var service = new StorageIpcCommandService(
            repository,
            new FakeSessionOpener(StorageResult<IStorageIpcSessionLease>.Success(
                new FakeSessionLease(session))));

        var tested = await service.HandleAsync(IpcEnvelope.Create(
            StorageIpcMessageTypes.ConnectionTestRequest,
            Guid.NewGuid(),
            1,
            new ConnectionTestRequest(StorageIpcContract.CurrentVersion, profile.Id.Value)));
        Assert.True(Assert.IsType<ConnectionTestResponse>(
            tested.Payload.Deserialize<ConnectionTestResponse>()).Succeeded);

        var listed = await service.HandleAsync(IpcEnvelope.Create(
            StorageIpcMessageTypes.ConnectionListRequest,
            Guid.NewGuid(),
            1,
            new ConnectionListRequest(StorageIpcContract.CurrentVersion, IncludeDisabled: true)));
        var health = Assert.Single(Assert.IsType<ConnectionListResponse>(
            listed.Payload.Deserialize<ConnectionListResponse>()).Connections).Health;

        Assert.NotNull(health);
        Assert.True(health.HasValidBounds);
        Assert.Equal(ConnectionHealthState.Healthy, health.State);
        Assert.Equal("Connection healthy", health.Status);

        repository.Replace(profile with
        {
            Version = profile.Version + 1,
            UpdatedUtc = profile.UpdatedUtc.AddMinutes(1)
        });
        var revisedList = await service.HandleAsync(IpcEnvelope.Create(
            StorageIpcMessageTypes.ConnectionListRequest,
            Guid.NewGuid(),
            1,
            new ConnectionListRequest(StorageIpcContract.CurrentVersion, IncludeDisabled: true)));
        Assert.Null(Assert.Single(Assert.IsType<ConnectionListResponse>(
            revisedList.Payload.Deserialize<ConnectionListResponse>()).Connections).Health);
    }

    [Fact]
    public async Task StorageListV2ReturnsBoundedTransferIdentitiesAndDropsProviderMetadata()
    {
        var profile = CreateLocalProfile();
        const string rootIdentity = "root-test";
        var address = StorageAddress.Create(
            profile.Id,
            rootIdentity,
            "folder/file.txt",
            nativeItemId: "native-secret",
            versionId: "version-secret").Value;
        var entry = StorageEntry.Create(
            address,
            StorageEntryKind.File,
            size: 42,
            lastModifiedUtc: DateTimeOffset.Parse(
                "2026-08-02T10:00:00Z",
                CultureInfo.InvariantCulture),
            contentType: "text/plain",
            eTag: "etag-secret",
            checksum: "checksum-secret",
            metadata: new Dictionary<string, string> { ["token"] = "metadata-secret" }).Value;
        var session = new FakeSession(
            profile.Id,
            rootIdentity,
            StorageResult<StoragePage>.Success(new StoragePage([entry], "next-page")));
        var lease = new FakeSessionLease(session);
        var opener = new FakeSessionOpener(StorageResult<IStorageIpcSessionLease>.Success(lease));
        var service = new StorageIpcCommandService(new FakeProfileRepository(profile), opener);
        var request = IpcEnvelope.Create(
            StorageIpcMessageTypes.StorageListRequest,
            Guid.NewGuid(),
            1,
            new StorageListPageRequest(
                StorageIpcContract.CurrentVersion,
                profile.Id.Value,
                "folder",
                PageSize: 25));

        var response = await service.HandleAsync(request);
        var payload = Assert.IsType<StorageListPageResponse>(
            response.Payload.Deserialize<StorageListPageResponse>());
        var item = Assert.Single(payload.Entries);
        var rawJson = response.Payload.GetRawText();

        Assert.Null(payload.Failure);
        Assert.Equal(StorageIpcContract.CurrentVersion, payload.ContractVersion);
        Assert.Equal(rootIdentity, payload.RootIdentity);
        Assert.Equal("folder/file.txt", item.RelativePath);
        Assert.Equal(StorageItemKind.File, item.Kind);
        Assert.Equal("native-secret", item.NativeItemId);
        Assert.Equal("version-secret", item.VersionId);
        Assert.Equal("etag-secret", item.EntityTag);
        Assert.Equal("next-page", payload.ContinuationToken);
        Assert.DoesNotContain("metadata-secret", rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("checksum-secret", rawJson, StringComparison.Ordinal);
        Assert.True(lease.IsDisposed);
        Assert.Equal(1, session.ListCount);
    }

    [Fact]
    public async Task StorageListV1RemainsBrowsableWithoutAddingIdentityFields()
    {
        var profile = CreateLocalProfile();
        var address = StorageAddress.Create(
            profile.Id,
            "root-v1",
            "folder/file.txt",
            nativeItemId: "native-v1",
            versionId: "version-v1",
            entityTag: "etag-v1").Value;
        var entry = StorageEntry.Create(address, StorageEntryKind.File, size: 1, eTag: "etag-v1").Value;
        var session = new FakeSession(
            profile.Id,
            "root-v1",
            StorageResult<StoragePage>.Success(new StoragePage([entry])));
        var service = new StorageIpcCommandService(
            new FakeProfileRepository(profile),
            new FakeSessionOpener(StorageResult<IStorageIpcSessionLease>.Success(
                new FakeSessionLease(session))));
        var request = IpcEnvelope.Create(
            StorageIpcMessageTypes.StorageListRequest,
            Guid.NewGuid(),
            1,
            new StorageListPageRequest(
                StorageIpcContract.LegacyVersion,
                profile.Id.Value,
                "folder",
                PageSize: 25));

        var response = await service.HandleAsync(request);
        var payload = Assert.IsType<StorageListPageResponse>(
            response.Payload.Deserialize<StorageListPageResponse>());
        var item = Assert.Single(payload.Entries);

        Assert.Equal(StorageIpcContract.LegacyVersion, payload.ContractVersion);
        Assert.Null(payload.RootIdentity);
        Assert.Null(item.NativeItemId);
        Assert.Null(item.VersionId);
        Assert.Null(item.EntityTag);
    }

    [Fact]
    public async Task StorageListRejectsTraversalBeforeCallingProvider()
    {
        var profile = CreateLocalProfile();
        var session = new FakeSession(
            profile.Id,
            "root-test",
            StorageResult<StoragePage>.Success(new StoragePage([])));
        var lease = new FakeSessionLease(session);
        var service = new StorageIpcCommandService(
            new FakeProfileRepository(profile),
            new FakeSessionOpener(StorageResult<IStorageIpcSessionLease>.Success(lease)));
        var request = IpcEnvelope.Create(
            StorageIpcMessageTypes.StorageListRequest,
            Guid.NewGuid(),
            1,
            new StorageListPageRequest(
                StorageIpcContract.CurrentVersion,
                profile.Id.Value,
                "../private",
                PageSize: 25));

        var response = await service.HandleAsync(request);
        var payload = Assert.IsType<StorageListPageResponse>(
            response.Payload.Deserialize<StorageListPageResponse>());

        Assert.Empty(payload.Entries);
        Assert.Equal(StorageIpcFailureCategory.Validation, payload.Failure?.Category);
        Assert.Equal(0, session.ListCount);
        Assert.True(lease.IsDisposed);
    }

    [Fact]
    public async Task StorageListRejectsAnEntryOutsideTheOpenedRoot()
    {
        var profile = CreateLocalProfile();
        var foreignAddress = StorageAddress.Create(
            profile.Id,
            "different-root",
            "folder/leak.txt").Value;
        var foreignEntry = StorageEntry.Create(
            foreignAddress,
            StorageEntryKind.File,
            size: 1).Value;
        var session = new FakeSession(
            profile.Id,
            "root-test",
            StorageResult<StoragePage>.Success(new StoragePage([foreignEntry])));
        var service = new StorageIpcCommandService(
            new FakeProfileRepository(profile),
            new FakeSessionOpener(StorageResult<IStorageIpcSessionLease>.Success(
                new FakeSessionLease(session))));
        var request = IpcEnvelope.Create(
            StorageIpcMessageTypes.StorageListRequest,
            Guid.NewGuid(),
            1,
            new StorageListPageRequest(
                StorageIpcContract.CurrentVersion,
                profile.Id.Value,
                "folder",
                PageSize: 25));

        var response = await service.HandleAsync(request);
        var payload = Assert.IsType<StorageListPageResponse>(
            response.Payload.Deserialize<StorageListPageResponse>());

        Assert.Empty(payload.Entries);
        Assert.Equal(StorageIpcFailureCategory.Integrity, payload.Failure?.Category);
        Assert.DoesNotContain("leak.txt", response.Payload.GetRawText(), StringComparison.Ordinal);
    }

    private static ConnectionProfile CreateLocalProfile() => ConnectionProfile.Create(
        ConnectionProfileId.New(),
        new ConnectionProfileMetadata("Local files", iconKey: "drive", accentColor: "#3366CC"),
        new LocalEndpoint("C:\\Data"),
        new NoAuthentication(),
        CreateOperationalOptions(),
        DateTimeOffset.Parse("2026-08-02T09:00:00Z", CultureInfo.InvariantCulture));

    private static ConnectionProfile CreateS3Profile(
        SecretReference accessKey,
        SecretReference secretKey) => ConnectionProfile.Create(
        ConnectionProfileId.New(),
        new ConnectionProfileMetadata(
            "Archive",
            folderPath: "Cloud/Production",
            tags: ["backup", "production"],
            isFavorite: true,
            iconKey: "cloud-lock",
            accentColor: "#8844CC"),
        new S3Endpoint("archive", "eu-north-1"),
        new S3AccessKeyAuthentication(accessKey, secretKey),
        CreateOperationalOptions(),
        DateTimeOffset.Parse("2026-08-02T09:00:00Z", CultureInfo.InvariantCulture));

    private static ConnectionOperationalOptions CreateOperationalOptions() => new(
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(15),
        new ConnectionRetryPolicy(0, TimeSpan.Zero, TimeSpan.Zero),
        proxy: null,
        new ConnectionBandwidthLimits(null, null),
        "utf-8");

    private sealed class FakeProfileRepository(params ConnectionProfile[] profiles) : IConnectionProfileRepository
    {
        private ConnectionProfile[] _profiles = profiles;

        internal void Replace(params ConnectionProfile[] profiles) => _profiles = profiles;

        public ValueTask<ConnectionProfile?> GetAsync(
            ConnectionProfileId id,
            bool includeDeleted = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_profiles.SingleOrDefault(profile => profile.Id == id));
        }

        public ValueTask<IReadOnlyList<ConnectionProfile>> SearchAsync(
            ConnectionProfileSearch search,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = _profiles
                .Where(profile => search.IncludeDisabled || profile.IsEnabled)
                .Where(profile => search.Provider is null || profile.Provider == search.Provider)
                .Where(profile => string.IsNullOrWhiteSpace(search.Text) ||
                    profile.Metadata.DisplayName.Contains(search.Text, StringComparison.OrdinalIgnoreCase))
                .Take(search.ValidatedLimit)
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<ConnectionProfile>>(matches);
        }

        public ValueTask<ConnectionProfileWriteResult> CreateAsync(
            ConnectionProfile profile,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<ConnectionProfileWriteResult> UpdateAsync(
            ConnectionProfile profile,
            long expectedVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<ConnectionProfileWriteResult> SetEnabledAsync(
            ConnectionProfileId id,
            bool enabled,
            long expectedVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<ConnectionProfileWriteResult> SoftDeleteAsync(
            ConnectionProfileId id,
            long expectedVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeSessionOpener : IStorageIpcSessionOpener
    {
        private readonly StorageResult<IStorageIpcSessionLease> _result;

        public FakeSessionOpener(StorageResult<IStorageIpcSessionLease>? result = null)
        {
            _result = result ?? StorageResult<IStorageIpcSessionLease>.Fail(new StorageFailure(
                "storage.test.not_configured",
                StorageFailureKind.Unexpected,
                "The fake opener was not configured."));
        }

        public int OpenCount { get; private set; }

        public ValueTask<StorageResult<IStorageIpcSessionLease>> OpenAsync(
            ConnectionProfile profile,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCount++;
            return ValueTask.FromResult(_result);
        }
    }

    private sealed class FakeSessionLease(IStorageEndpointSession session) : IStorageIpcSessionLease
    {
        public IStorageEndpointSession Session { get; } = session;
        public bool IsDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSession(
        ConnectionProfileId profileId,
        string rootIdentity,
        StorageResult<StoragePage> listResult) : IStorageEndpointSession
    {
        public ConnectionProfileId ProfileId { get; } = profileId;
        public string RootIdentity { get; } = rootIdentity;
        public EffectiveStorageCapabilities Capabilities => EffectiveStorageCapabilities.None;
        public int ListCount { get; private set; }

        public ValueTask<StorageResult> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageResult.Success());

        public ValueTask<StorageResult<StoragePage>> ListAsync(
            StorageAddress address,
            StorageListRequest? request = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListCount++;
            return ValueTask.FromResult(listResult);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<StorageResult<StorageEntry>> GetEntryAsync(
            StorageAddress address,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<StorageResult<Stream>> OpenReadAsync(
            StorageReadRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<StorageResult<IStorageWriteHandle>> OpenWriteAsync(
            StorageWriteRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<StorageResult<StorageEntry>> CreateDirectoryAsync(
            StorageAddress address,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<StorageResult> DeleteAsync(
            StorageDeleteRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<StorageResult<StorageEntry>> CopyAsync(
            StorageCopyRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<StorageResult<StorageEntry>> MoveAsync(
            StorageMoveRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
