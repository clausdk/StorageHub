using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

public sealed class ObjectInspectorAgentClientTests
{
    [Fact]
    public async Task ClientAcceptsOnlyMatchingBoundedUtcVersionPage()
    {
        var address = CreateAddress();
        var transport = new FakeTransport(request => IpcEnvelope.Create(
            ObjectInspectorIpcMessageTypes.VersionListResponse,
            request.RequestId,
            1,
            new ObjectVersionListResponse(
                ObjectInspectorIpcContract.CurrentVersion,
                address,
                [new ObjectVersionSummary(
                    "version-1",
                    "etag-1",
                    42,
                    new DateTimeOffset(2026, 8, 2, 10, 0, 0, TimeSpan.Zero),
                    IsLatest: true,
                    IsDeleteMarker: false)],
                "next")));
        await using var client = CreateClient(transport);

        var response = await client.ListVersionsAsync(new ObjectVersionListRequest(
            ObjectInspectorIpcContract.CurrentVersion,
            address,
            PageSize: 5));

        Assert.Single(response.Versions);
        Assert.Equal("next", response.ContinuationToken);
        Assert.Equal(TimeSpan.Zero, response.Versions[0].LastModifiedUtc?.Offset);
        Assert.Equal(0, transport.DisconnectCount);
    }

    [Fact]
    public async Task ClientRejectsAnyEchoedAddressMismatchAndDisconnects()
    {
        var address = CreateAddress();
        var mismatched = address with { EntityTag = "different-etag" };
        var transport = new FakeTransport(request => IpcEnvelope.Create(
            ObjectInspectorIpcMessageTypes.MetadataGetResponse,
            request.RequestId,
            1,
            new ObjectMetadataGetResponse(
                ObjectInspectorIpcContract.CurrentVersion,
                mismatched,
                [])));
        await using var client = CreateClient(transport);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetMetadataAsync(
            new ObjectMetadataGetRequest(ObjectInspectorIpcContract.CurrentVersion, address)));

        Assert.Equal(1, transport.DisconnectCount);
    }

    [Fact]
    public async Task ClientRejectsNonUtcVersionAndFailureMixedWithData()
    {
        var address = CreateAddress();
        var responseNumber = 0;
        var transport = new FakeTransport(request =>
        {
            if (Interlocked.Increment(ref responseNumber) == 1)
            {
                return IpcEnvelope.Create(
                    ObjectInspectorIpcMessageTypes.VersionListResponse,
                    request.RequestId,
                    1,
                    new ObjectVersionListResponse(
                        ObjectInspectorIpcContract.CurrentVersion,
                        address,
                        [new ObjectVersionSummary(
                            "version-1",
                            null,
                            1,
                            new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(2)),
                            IsLatest: true,
                            IsDeleteMarker: false)],
                        null));
            }

            return IpcEnvelope.Create(
                ObjectInspectorIpcMessageTypes.MetadataGetResponse,
                request.RequestId,
                1,
                new ObjectMetadataGetResponse(
                    ObjectInspectorIpcContract.CurrentVersion,
                    address,
                    [new ObjectMetadataEntry("owner", "team")],
                    new StorageIpcFailure(
                        "storage.inspector.unavailable",
                        StorageIpcFailureCategory.Unavailable,
                        "The storage provider is temporarily unavailable.",
                        IsTransient: true)));
        });
        await using var client = CreateClient(transport);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.ListVersionsAsync(
            new ObjectVersionListRequest(ObjectInspectorIpcContract.CurrentVersion, address)));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetMetadataAsync(
            new ObjectMetadataGetRequest(ObjectInspectorIpcContract.CurrentVersion, address)));

        Assert.Equal(2, transport.DisconnectCount);
        Assert.Equal(2, transport.ConnectCount);
    }

    [Fact]
    public async Task InFlightCancellationRetiresPipeAndNextRequestReconnects()
    {
        var address = CreateAddress();
        var receiveCount = 0;
        var transport = new FakeTransport(async (request, cancellationToken) =>
        {
            if (Interlocked.Increment(ref receiveCount) == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return IpcEnvelope.Create(
                ObjectInspectorIpcMessageTypes.TagsGetResponse,
                request.RequestId,
                1,
                new ObjectTagsGetResponse(
                    ObjectInspectorIpcContract.CurrentVersion,
                    address,
                    []));
        });
        await using var client = CreateClient(transport);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetTagsAsync(
            new ObjectTagsGetRequest(ObjectInspectorIpcContract.CurrentVersion, address),
            cancellation.Token));
        var recovered = await client.GetTagsAsync(new ObjectTagsGetRequest(
            ObjectInspectorIpcContract.CurrentVersion,
            address));

        Assert.Empty(recovered.Tags);
        Assert.Equal(1, transport.DisconnectCount);
        Assert.Equal(2, transport.ConnectCount);
    }

    [Fact]
    public async Task UnauthorizedTransportDetailsAreSanitizedAndPipeIsRetired()
    {
        var transport = new FakeTransport((_, _) =>
            throw new UnauthorizedAccessException("pipe token=super-secret"));
        await using var client = CreateClient(transport);

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            client.GetTagsAsync(new ObjectTagsGetRequest(
                ObjectInspectorIpcContract.CurrentVersion,
                CreateAddress())));

        Assert.DoesNotContain("super-secret", error.Message, StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        Assert.Equal(1, transport.DisconnectCount);
    }

    private static ObjectInspectorAddress CreateAddress() => new(
        Guid.NewGuid(),
        "s3:root",
        "folder/item.bin",
        NativeItemId: "native-1",
        EntityTag: "etag-1");

    private static NamedPipeObjectInspectorAgentClient CreateClient(IStorageIpcTransport transport) =>
        new(transport, new ObjectInspectorAgentClientOptions
        {
            RequestTimeout = TimeSpan.FromSeconds(2)
        });

    private sealed class FakeTransport : IStorageIpcTransport
    {
        private readonly Func<IpcEnvelope, CancellationToken, Task<IpcEnvelope>> _responseFactory;
        private IpcEnvelope? _request;

        public FakeTransport(Func<IpcEnvelope, IpcEnvelope> responseFactory)
            : this((request, _) => Task.FromResult(responseFactory(request)))
        {
        }

        public FakeTransport(Func<IpcEnvelope, CancellationToken, Task<IpcEnvelope>> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public bool IsConnected { get; private set; }
        public int DisconnectCount { get; private set; }
        public int ConnectCount { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectCount++;
            IsConnected = true;
            return Task.CompletedTask;
        }

        public ValueTask SendAsync(
            IpcEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _request = envelope;
            return ValueTask.CompletedTask;
        }

        public async ValueTask<IpcEnvelope> ReceiveAsync(
            CancellationToken cancellationToken = default) => await _responseFactory(
            _request ?? throw new InvalidOperationException("No request was sent."),
            cancellationToken);

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            DisconnectCount++;
            IsConnected = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }
}
