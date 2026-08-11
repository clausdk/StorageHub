using System.Globalization;
using System.Text.Json;
using StorageHub.Contracts.Ipc;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Capabilities;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Abstractions;
using StorageHub.Storage.Models;
using StorageHub.Sync;

namespace StorageHub.Agent.Windows.Tests;

public sealed class ObjectInspectorIpcCommandServiceTests
{
    [Fact]
    public async Task VersionListUsesExactSavedProfileRootAndPathAndDisposesConnection()
    {
        var profileId = ConnectionProfileId.New();
        var requested = new ObjectInspectorAddress(
            profileId.Value,
            "s3:archive/root",
            "photos/original.jpg",
            NativeItemId: "native-7",
            EntityTag: "etag-current");
        var versionAddress = StorageAddress.Create(
            profileId,
            requested.RootIdentity,
            requested.RelativePath,
            versionId: "version-2",
            entityTag: "etag-2").Value;
        var version = StorageObjectVersion.Create(
            versionAddress,
            4096,
            DateTimeOffset.Parse("2026-08-02T10:00:00Z", CultureInfo.InvariantCulture),
            isLatest: true,
            isDeleteMarker: false).Value;
        var page = StorageObjectVersionPage.Create([version], "next-token").Value;
        var session = new FakeAdvancedSession(profileId, requested.RootIdentity)
        {
            VersionResult = StorageResult<StorageObjectVersionPage>.Success(page)
        };
        var connection = new FakeConnection(session);
        var connector = new FakeConnector(StorageResult<ISyncEndpointConnection>.Success(connection));
        var service = new ObjectInspectorIpcCommandService(connector);
        var request = new ObjectVersionListRequest(
            ObjectInspectorIpcContract.CurrentVersion,
            requested,
            PageSize: 7,
            ContinuationToken: "start-token",
            IncludeDeleteMarkers: false);

        var response = await service.HandleAsync(IpcEnvelope.Create(
            ObjectInspectorIpcMessageTypes.VersionListRequest,
            Guid.NewGuid(),
            1,
            request));
        var payload = response.Payload.Deserialize<ObjectVersionListResponse>();

        Assert.Equal(ObjectInspectorIpcMessageTypes.VersionListResponse, response.MessageType);
        Assert.Null(payload?.Failure);
        Assert.Equal(requested, payload?.Address);
        Assert.Equal("next-token", payload?.ContinuationToken);
        var returned = Assert.Single(Assert.IsType<ObjectVersionListResponse>(payload).Versions);
        Assert.Equal("version-2", returned.VersionId);
        Assert.Equal(TimeSpan.Zero, returned.LastModifiedUtc?.Offset);
        Assert.Equal(profileId, connector.LastProfileId);
        Assert.Equal(requested.RelativePath, session.LastAddress?.CanonicalRelativePath);
        Assert.Equal(requested.NativeItemId, session.LastAddress?.NativeItemId);
        Assert.Equal(requested.EntityTag, session.LastAddress?.EntityTag);
        Assert.Equal(7, session.LastVersionRequest?.PageSize);
        Assert.Equal("start-token", session.LastVersionRequest?.ContinuationToken);
        Assert.True(connection.IsDisposed);
    }

    [Fact]
    public async Task RootMismatchIsRejectedBeforeCallingAdvancedProvider()
    {
        var profileId = ConnectionProfileId.New();
        var session = new FakeAdvancedSession(profileId, "opened-root");
        var connection = new FakeConnection(session);
        var service = new ObjectInspectorIpcCommandService(new FakeConnector(
            StorageResult<ISyncEndpointConnection>.Success(connection)));
        var request = new ObjectMetadataGetRequest(
            ObjectInspectorIpcContract.CurrentVersion,
            new ObjectInspectorAddress(profileId.Value, "stale-root", "item.bin"));

        var response = await service.HandleAsync(IpcEnvelope.Create(
            ObjectInspectorIpcMessageTypes.MetadataGetRequest,
            Guid.NewGuid(),
            1,
            request));
        var payload = response.Payload.Deserialize<ObjectMetadataGetResponse>();

        Assert.Equal(StorageIpcFailureCategory.Integrity, payload?.Failure?.Category);
        Assert.Equal("storage.inspector.session_identity_mismatch", payload?.Failure?.Code);
        Assert.Empty(Assert.IsType<ObjectMetadataGetResponse>(payload).Metadata);
        Assert.Equal(0, session.MetadataReadCount);
        Assert.True(connection.IsDisposed);
    }

    [Fact]
    public async Task MetadataAndTagsAreReadOnlyBoundedAndDeterministicallyOrdered()
    {
        var profileId = ConnectionProfileId.New();
        const string root = "local:root";
        var session = new FakeAdvancedSession(profileId, root)
        {
            MetadataResult = StorageResult<StorageMetadata>.Success(StorageMetadata.Create(
                new Dictionary<string, string>
                {
                    ["z-last"] = "two",
                    ["a-first"] = "one"
                }).Value),
            TagsResult = StorageResult<StorageTags>.Success(StorageTags.Create(
                new Dictionary<string, string>
                {
                    ["tier"] = "archive",
                    ["owner"] = "team"
                }).Value)
        };
        var connector = new FakeConnector(StorageResult<ISyncEndpointConnection>.Success(
            new FakeConnection(session)));
        var service = new ObjectInspectorIpcCommandService(connector);
        var address = new ObjectInspectorAddress(profileId.Value, root, "reports/q2.csv");

        var metadataResponse = await service.HandleAsync(IpcEnvelope.Create(
            ObjectInspectorIpcMessageTypes.MetadataGetRequest,
            Guid.NewGuid(),
            1,
            new ObjectMetadataGetRequest(ObjectInspectorIpcContract.CurrentVersion, address)));
        var tagsResponse = await service.HandleAsync(IpcEnvelope.Create(
            ObjectInspectorIpcMessageTypes.TagsGetRequest,
            Guid.NewGuid(),
            1,
            new ObjectTagsGetRequest(ObjectInspectorIpcContract.CurrentVersion, address)));
        var metadata = metadataResponse.Payload.Deserialize<ObjectMetadataGetResponse>();
        var tags = tagsResponse.Payload.Deserialize<ObjectTagsGetResponse>();

        Assert.Equal(["a-first", "z-last"], metadata?.Metadata.Select(static item => item.Name));
        Assert.Equal(["owner", "tier"], tags?.Tags.Select(static item => item.Name));
        Assert.True(metadata?.HasValidMetadataBounds);
        Assert.True(tags?.HasValidTagBounds);
        Assert.False(service.CanHandle("object-inspector.metadata.set.request"));
        Assert.False(service.CanHandle("object-inspector.tags.set.request"));
        Assert.False(service.CanHandle("object-inspector.signed-url.request"));
        Assert.False(service.CanHandle("object-inspector.version.delete.request"));
    }

    [Fact]
    public async Task ProviderFailureCodeMessageAndDiagnosticsAreNeverReturned()
    {
        const string secret = "access_key=hunter2";
        var profileId = ConnectionProfileId.New();
        var session = new FakeAdvancedSession(profileId, "root")
        {
            TagsResult = StorageResult<StorageTags>.Fail(new StorageFailure(
                secret,
                StorageFailureKind.Unauthorized,
                secret,
                providerCode: secret,
                diagnosticId: secret))
        };
        var service = new ObjectInspectorIpcCommandService(new FakeConnector(
            StorageResult<ISyncEndpointConnection>.Success(new FakeConnection(session))));
        var request = new ObjectTagsGetRequest(
            ObjectInspectorIpcContract.CurrentVersion,
            new ObjectInspectorAddress(profileId.Value, "root", "item"));

        var response = await service.HandleAsync(IpcEnvelope.Create(
            ObjectInspectorIpcMessageTypes.TagsGetRequest,
            Guid.NewGuid(),
            1,
            request));
        var payload = response.Payload.Deserialize<ObjectTagsGetResponse>();

        Assert.Equal("storage.inspector.unauthorized", payload?.Failure?.Code);
        Assert.Equal(StorageIpcFailureCategory.Unauthorized, payload?.Failure?.Category);
        Assert.DoesNotContain(secret, response.Payload.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteUsesCapturedRootPathAndConditionalIdentity()
    {
        var profileId = ConnectionProfileId.New();
        var address = new ObjectInspectorAddress(
            profileId.Value,
            "s3:root",
            "folder/item.bin",
            NativeItemId: "native-1",
            EntityTag: "etag-1");
        var session = new FakeAdvancedSession(profileId, address.RootIdentity)
        {
            EffectiveCapabilities = new EffectiveStorageCapabilities([
                new(StorageFeature.Delete, FeatureSupport.Native()),
                new(StorageFeature.ConditionalDelete, FeatureSupport.Native())]),
            DeleteResult = StorageResult.Success()
        };
        var service = new ObjectInspectorIpcCommandService(new FakeConnector(
            StorageResult<ISyncEndpointConnection>.Success(new FakeConnection(session))));

        var response = await service.HandleAsync(IpcEnvelope.Create(
            EditableFileIpcMessageTypes.DeleteRequest,
            Guid.NewGuid(),
            1,
            new StorageItemDeleteRequest(
                EditableFileIpcContract.CurrentVersion,
                address,
                Recursive: false)));
        var payload = response.Payload.Deserialize<StorageItemDeleteResponse>();

        Assert.Equal(EditableFileIpcMessageTypes.DeleteResponse, response.MessageType);
        Assert.True(payload?.Deleted);
        Assert.Null(payload?.Failure);
        Assert.Equal(address.RelativePath, session.LastDeleteRequest?.Address.CanonicalRelativePath);
        Assert.Equal(address.EntityTag, session.LastDeleteRequest?.ExpectedEntityTag);
    }

    [Fact]
    public async Task InFlightCancellationDisposesRootScopedConnection()
    {
        var profileId = ConnectionProfileId.New();
        var session = new FakeAdvancedSession(profileId, "root")
        {
            BlockVersionRead = true
        };
        var connection = new FakeConnection(session);
        var service = new ObjectInspectorIpcCommandService(new FakeConnector(
            StorageResult<ISyncEndpointConnection>.Success(connection)));
        var request = new ObjectVersionListRequest(
            ObjectInspectorIpcContract.CurrentVersion,
            new ObjectInspectorAddress(profileId.Value, "root", "item"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.HandleAsync(
            IpcEnvelope.Create(
                ObjectInspectorIpcMessageTypes.VersionListRequest,
                Guid.NewGuid(),
                1,
                request),
            cancellation.Token).AsTask());

        Assert.True(connection.IsDisposed);
    }

    private sealed class FakeConnector(StorageResult<ISyncEndpointConnection> result)
        : ISyncEndpointConnector
    {
        public ConnectionProfileId? LastProfileId { get; private set; }

        public ValueTask<StorageResult<ISyncEndpointConnection>> OpenAsync(
            ConnectionProfileId profileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastProfileId = profileId;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeConnection(IStorageEndpointSession session) : ISyncEndpointConnection
    {
        public IStorageEndpointSession Session { get; } = session;
        public bool IsDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeAdvancedSession(
        ConnectionProfileId profileId,
        string rootIdentity) : IStorageEndpointSession, IStorageAdvancedEndpointSession
    {
        public ConnectionProfileId ProfileId { get; } = profileId;
        public string RootIdentity { get; } = rootIdentity;
        public EffectiveStorageCapabilities EffectiveCapabilities { get; init; } = EffectiveStorageCapabilities.None;
        public EffectiveStorageCapabilities Capabilities => EffectiveCapabilities;
        public StorageAddress? LastAddress { get; private set; }
        public StorageVersionListRequest? LastVersionRequest { get; private set; }
        public int MetadataReadCount { get; private set; }
        public bool BlockVersionRead { get; init; }
        public StorageResult<StorageObjectVersionPage> VersionResult { get; init; } =
            StorageResult<StorageObjectVersionPage>.Success(StorageObjectVersionPage.Create([]).Value);
        public StorageResult<StorageMetadata> MetadataResult { get; init; } =
            StorageResult<StorageMetadata>.Success(StorageMetadata.Create(
                new Dictionary<string, string>()).Value);
        public StorageResult<StorageTags> TagsResult { get; init; } =
            StorageResult<StorageTags>.Success(StorageTags.Create(
                new Dictionary<string, string>()).Value);
        public StorageResult DeleteResult { get; init; } = StorageResult.Success();
        public StorageDeleteRequest? LastDeleteRequest { get; private set; }

        public async ValueTask<StorageResult<StorageObjectVersionPage>> ListObjectVersionsAsync(
            StorageAddress address,
            StorageVersionListRequest? request = null,
            CancellationToken cancellationToken = default)
        {
            LastAddress = address;
            LastVersionRequest = request;
            if (BlockVersionRead)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return VersionResult;
        }

        public ValueTask<StorageResult<StorageMetadata>> GetMetadataAsync(
            StorageAddress address,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastAddress = address;
            MetadataReadCount++;
            return ValueTask.FromResult(MetadataResult);
        }

        public ValueTask<StorageResult<StorageTags>> GetTagsAsync(
            StorageAddress address,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastAddress = address;
            return ValueTask.FromResult(TagsResult);
        }

        public ValueTask<StorageResult> DeleteObjectVersionAsync(
            StorageDeleteVersionRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<StorageResult<StorageEntry>> SetMetadataAsync(
            StorageSetMetadataRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<StorageResult<StorageEntry>> SetTagsAsync(
            StorageSetTagsRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<StorageResult<StorageSignedUrl>> CreateSignedUrlAsync(
            StorageSignedUrlRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<StorageResult> CheckHealthAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<StorageResult<StorageEntry>> GetEntryAsync(
            StorageAddress address,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<StorageResult<StoragePage>> ListAsync(
            StorageAddress address,
            StorageListRequest? request = null,
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
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastDeleteRequest = request;
            return ValueTask.FromResult(DeleteResult);
        }
        public ValueTask<StorageResult<StorageEntry>> CopyAsync(
            StorageCopyRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<StorageResult<StorageEntry>> MoveAsync(
            StorageMoveRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
