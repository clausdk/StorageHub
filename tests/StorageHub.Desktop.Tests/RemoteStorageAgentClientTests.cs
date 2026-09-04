using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

public sealed class RemoteStorageAgentClientTests
{
    [Fact]
    public async Task ClientRejectsResponseForAnotherRequestAndDisconnectsTransport()
    {
        var transport = new FakeTransport(request => IpcEnvelope.Create(
            StorageIpcMessageTypes.ConnectionListResponse,
            Guid.NewGuid(),
            1,
            new ConnectionListResponse(StorageIpcContract.CurrentVersion, [])));
        await using var client = CreateClient(transport);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.ListConnectionsAsync(
            new ConnectionListRequest(StorageIpcContract.CurrentVersion)));

        Assert.Equal(1, transport.DisconnectCount);
    }

    [Fact]
    public async Task ClientRejectsAResourceIdentityMismatch()
    {
        var connectionId = Guid.NewGuid();
        var transport = new FakeTransport(request => IpcEnvelope.Create(
            StorageIpcMessageTypes.StorageListResponse,
            request.RequestId,
            1,
            new StorageListPageResponse(
                StorageIpcContract.CurrentVersion,
                connectionId,
                "different-folder",
                [],
                ContinuationToken: null,
                RootIdentity: "root-1")));
        await using var client = CreateClient(transport);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.ListStorageAsync(
            new StorageListPageRequest(
                StorageIpcContract.CurrentVersion,
                connectionId,
                "folder")));
    }

    [Fact]
    public async Task ClientAppliesBoundedRequestDeadline()
    {
        var transport = new FakeTransport(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        });
        await using var client = new NamedPipeRemoteStorageAgentClient(
            transport,
            new RemoteStorageAgentClientOptions
            {
                RequestTimeout = TimeSpan.FromMilliseconds(50)
            });

        await Assert.ThrowsAsync<TimeoutException>(() => client.ListConnectionsAsync(
            new ConnectionListRequest(StorageIpcContract.CurrentVersion)));
        Assert.Equal(1, transport.DisconnectCount);
    }

    [Fact]
    public async Task CallerCancellationInFlightRetiresSessionAndNextRequestReconnects()
    {
        var receiveCount = 0;
        var transport = new FakeTransport(async (request, cancellationToken) =>
        {
            if (Interlocked.Increment(ref receiveCount) == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return IpcEnvelope.Create(
                StorageIpcMessageTypes.ConnectionListResponse,
                request.RequestId,
                1,
                new ConnectionListResponse(StorageIpcContract.CurrentVersion, []));
        });
        await using var client = CreateClient(transport);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ListConnectionsAsync(
            new ConnectionListRequest(StorageIpcContract.CurrentVersion),
            cancellation.Token));
        var recovered = await client.ListConnectionsAsync(
            new ConnectionListRequest(StorageIpcContract.CurrentVersion));

        Assert.Empty(recovered.Connections);
        Assert.Equal(1, transport.DisconnectCount);
        Assert.Equal(2, transport.ConnectCount);
    }

    [Fact]
    public async Task CallerCancellationWhileWaitingForGateDoesNotRetireActiveSession()
    {
        var receiveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport(async (request, cancellationToken) =>
        {
            receiveStarted.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return IpcEnvelope.Create(
                StorageIpcMessageTypes.ConnectionListResponse,
                request.RequestId,
                1,
                new ConnectionListResponse(StorageIpcContract.CurrentVersion, []));
        });
        await using var client = CreateClient(transport);
        var active = client.ListConnectionsAsync(
            new ConnectionListRequest(StorageIpcContract.CurrentVersion));
        await receiveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ListConnectionsAsync(
            new ConnectionListRequest(StorageIpcContract.CurrentVersion),
            cancellation.Token));

        Assert.Equal(0, transport.DisconnectCount);
        release.TrySetResult();
        _ = await active;
        Assert.Equal(0, transport.DisconnectCount);
    }

    [Fact]
    public async Task UnauthorizedTransportDetailsAreSanitizedAndSessionIsRetired()
    {
        var transport = new FakeTransport((_, _) =>
            throw new UnauthorizedAccessException("pipe ACL denied token=super-secret"));
        await using var client = CreateClient(transport);

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            client.ListConnectionsAsync(new ConnectionListRequest(StorageIpcContract.CurrentVersion)));

        Assert.DoesNotContain("super-secret", error.Message, StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        Assert.Equal(1, transport.DisconnectCount);
    }

    [Fact]
    public async Task ClientAcceptsOnlyBoundedMatchingPage()
    {
        var connectionId = Guid.NewGuid();
        var transport = new FakeTransport(request => IpcEnvelope.Create(
            StorageIpcMessageTypes.StorageListResponse,
            request.RequestId,
            1,
            new StorageListPageResponse(
                StorageIpcContract.CurrentVersion,
                connectionId,
                "folder",
                [new StorageListItem(
                    "file.txt",
                    "folder/file.txt",
                    StorageItemKind.File,
                    12,
                    LastModifiedUtc: null,
                    "text/plain",
                    IsContainer: false,
                    NativeItemId: "native-1",
                    VersionId: "version-1",
                    EntityTag: "etag-1")],
                ContinuationToken: null,
                RootIdentity: "root-1")));
        await using var client = CreateClient(transport);

        var response = await client.ListStorageAsync(new StorageListPageRequest(
            StorageIpcContract.CurrentVersion,
            connectionId,
            "folder",
            PageSize: 25));

        Assert.Single(response.Entries);
        Assert.Equal("root-1", response.RootIdentity);
        Assert.Equal("folder/file.txt", response.Entries[0].RelativePath);
        Assert.Equal("version-1", response.Entries[0].VersionId);
        Assert.Equal("etag-1", response.Entries[0].EntityTag);
    }

    [Fact]
    public async Task ClientRejectsV2SuccessWithoutRootIdentity()
    {
        var connectionId = Guid.NewGuid();
        var transport = new FakeTransport(request => IpcEnvelope.Create(
            StorageIpcMessageTypes.StorageListResponse,
            request.RequestId,
            1,
            new StorageListPageResponse(
                StorageIpcContract.CurrentVersion,
                connectionId,
                "folder",
                [],
                ContinuationToken: null)));
        await using var client = CreateClient(transport);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.ListStorageAsync(
            new StorageListPageRequest(StorageIpcContract.CurrentVersion, connectionId, "folder")));
    }

    [Fact]
    public async Task ClientRejectsHealthSnapshotsFromPreHealthContractVersions()
    {
        var connectionId = Guid.NewGuid();
        var transport = new FakeTransport(request => IpcEnvelope.Create(
            StorageIpcMessageTypes.ConnectionListResponse,
            request.RequestId,
            1,
            new ConnectionListResponse(
                StorageIpcContract.StableIdentityVersion,
                [new ConnectionSummary(
                    connectionId,
                    "Archive",
                    StorageConnectionProvider.S3,
                    FolderPath: null,
                    Tags: [],
                    IsFavorite: false,
                    IsEnabled: true,
                    IconKey: null,
                    AccentColor: null,
                    Version: 1,
                    Health: new ConnectionHealthSnapshot(
                        ConnectionHealthState.Healthy,
                        DateTimeOffset.UtcNow,
                        1,
                        "Connection healthy"))])));
        await using var client = CreateClient(transport);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.ListConnectionsAsync(
            new ConnectionListRequest(StorageIpcContract.StableIdentityVersion)));
    }

    [Fact]
    public async Task ClientKeepsV1BrowsingCompatibilityWithoutStableIdentities()
    {
        var connectionId = Guid.NewGuid();
        var transport = new FakeTransport(request => IpcEnvelope.Create(
            StorageIpcMessageTypes.StorageListResponse,
            request.RequestId,
            1,
            new StorageListPageResponse(
                StorageIpcContract.LegacyVersion,
                connectionId,
                "folder",
                [],
                ContinuationToken: null)));
        await using var client = CreateClient(transport);

        var response = await client.ListStorageAsync(new StorageListPageRequest(
            StorageIpcContract.LegacyVersion,
            connectionId,
            "folder"));

        Assert.Equal(StorageIpcContract.LegacyVersion, response.ContractVersion);
        Assert.Null(response.RootIdentity);
    }

    private static NamedPipeRemoteStorageAgentClient CreateClient(IStorageIpcTransport transport) => new(
        transport,
        new RemoteStorageAgentClientOptions
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

        public ValueTask SendAsync(IpcEnvelope envelope, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _request = envelope;
            return ValueTask.CompletedTask;
        }

        public async ValueTask<IpcEnvelope> ReceiveAsync(CancellationToken cancellationToken = default) =>
            await _responseFactory(
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
