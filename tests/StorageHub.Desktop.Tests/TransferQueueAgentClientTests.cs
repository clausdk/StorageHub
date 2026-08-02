using System.Globalization;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

public sealed class TransferQueueAgentClientTests
{
    [Fact]
    public async Task Client_rejects_a_response_for_another_request_and_disconnects()
    {
        var transport = new FakeTransport(_ => IpcEnvelope.Create(
            TransferQueueIpcMessageTypes.ListResponse,
            Guid.NewGuid(),
            sequence: 1,
            new TransferListResponse(TransferQueueIpcContract.CurrentVersion, [], null)));
        await using var client = CreateClient(transport);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.ListAsync(ListRequest()));

        Assert.Equal(1, transport.DisconnectCount);
    }

    [Fact]
    public async Task Client_rejects_an_oversized_or_wrong_state_page()
    {
        var transport = new FakeTransport(request => IpcEnvelope.Create(
            TransferQueueIpcMessageTypes.ListResponse,
            request.RequestId,
            sequence: 1,
            new TransferListResponse(
                TransferQueueIpcContract.CurrentVersion,
                [Summary(TransferQueueState.Completed)],
                null)));
        await using var client = CreateClient(transport);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.ListAsync(ListRequest()));
    }

    [Fact]
    public async Task Client_rejects_a_mutation_for_another_transfer()
    {
        var requestedId = Guid.NewGuid();
        var transport = new FakeTransport(request => IpcEnvelope.Create(
            TransferQueueIpcMessageTypes.CancelResponse,
            request.RequestId,
            sequence: 1,
            new TransferMutationResponse(
                TransferQueueIpcContract.CurrentVersion,
                Guid.NewGuid(),
                TransferQueueMutationOutcome.Applied,
                Summary(TransferQueueState.Cancelled))));
        await using var client = CreateClient(transport);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.CancelAsync(
            new TransferCancelRequest(
                TransferQueueIpcContract.CurrentVersion,
                requestedId,
                ExpectedRevision: 0)));
    }

    [Fact]
    public async Task Client_accepts_a_bounded_correlated_queue_page()
    {
        var expected = Summary(TransferQueueState.Pending);
        var transport = new FakeTransport(request => IpcEnvelope.Create(
            TransferQueueIpcMessageTypes.ListResponse,
            request.RequestId,
            sequence: 1,
            new TransferListResponse(
                TransferQueueIpcContract.CurrentVersion,
                [expected],
                ContinuationToken: "bmV4dA==")));
        await using var client = CreateClient(transport);

        var response = await client.ListAsync(ListRequest());

        Assert.Equal(expected, Assert.Single(response.Transfers));
        Assert.Equal("bmV4dA==", response.ContinuationToken);
        Assert.Equal(TransferQueueIpcMessageTypes.ListRequest, transport.LastRequest?.MessageType);
    }

    [Fact]
    public async Task Client_applies_a_bounded_deadline()
    {
        var transport = new FakeTransport(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        });
        await using var client = new NamedPipeTransferQueueAgentClient(
            transport,
            new RemoteStorageAgentClientOptions { RequestTimeout = TimeSpan.FromMilliseconds(50) });

        await Assert.ThrowsAsync<TimeoutException>(() => client.ListAsync(ListRequest()));
        Assert.Equal(1, transport.DisconnectCount);
    }

    [Fact]
    public async Task Client_disconnects_in_flight_cancellation_and_reconnects_for_next_request()
    {
        var receiveCount = 0;
        var transport = new FakeTransport(async (request, cancellationToken) =>
        {
            if (Interlocked.Increment(ref receiveCount) == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return IpcEnvelope.Create(
                TransferQueueIpcMessageTypes.ListResponse,
                request.RequestId,
                sequence: 1,
                new TransferListResponse(TransferQueueIpcContract.CurrentVersion, [], null));
        });
        await using var client = CreateClient(transport);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ListAsync(ListRequest(), cancellation.Token));

        Assert.Equal(1, transport.DisconnectCount);
        Assert.False(transport.IsConnected);

        var recovered = await client.ListAsync(ListRequest());

        Assert.Empty(recovered.Transfers);
        Assert.Equal(2, transport.ConnectCount);
    }

    [Fact]
    public async Task Client_does_not_disconnect_when_caller_cancels_only_while_waiting_for_gate()
    {
        var receiveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport(async (request, cancellationToken) =>
        {
            receiveStarted.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return IpcEnvelope.Create(
                TransferQueueIpcMessageTypes.ListResponse,
                request.RequestId,
                sequence: 1,
                new TransferListResponse(TransferQueueIpcContract.CurrentVersion, [], null));
        });
        await using var client = CreateClient(transport);
        var active = client.ListAsync(ListRequest());
        await receiveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ListAsync(ListRequest(), cancellation.Token));

        Assert.Equal(0, transport.DisconnectCount);
        release.TrySetResult();
        _ = await active;
        Assert.Equal(0, transport.DisconnectCount);
    }

    [Fact]
    public async Task Client_sanitizes_unauthorized_transport_details_and_disconnects()
    {
        var transport = new FakeTransport((_, _) =>
            throw new UnauthorizedAccessException("pipe ACL denied credential=super-secret"));
        await using var client = CreateClient(transport);

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            client.ListAsync(ListRequest()));

        Assert.DoesNotContain("super-secret", error.Message, StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        Assert.Equal(1, transport.DisconnectCount);
    }

    private static TransferListRequest ListRequest() => new(
        TransferQueueIpcContract.CurrentVersion,
        [TransferQueueState.Pending],
        PageSize: 10);

    private static TransferQueueSummary Summary(TransferQueueState state) => new(
        Guid.NewGuid(),
        TransferQueueOperation.Copy,
        Guid.NewGuid(),
        "source.bin",
        Guid.NewGuid(),
        "destination.bin",
        state,
        Revision: 2,
        Attempt: 1,
        Priority: 0,
        ExpectedBytes: 100,
        ProgressBytes: state == TransferQueueState.Completed ? 100 : 25,
        UpdatedUtc: DateTimeOffset.Parse("2026-08-02T12:00:00Z", CultureInfo.InvariantCulture),
        RetryAvailableUtc: null,
        ErrorCode: null,
        ErrorSummary: null,
        CanCancel: state == TransferQueueState.Pending,
        CanRetry: false,
        NeedsReconciliation: false);

    private static NamedPipeTransferQueueAgentClient CreateClient(IStorageIpcTransport transport) => new(
        transport,
        new RemoteStorageAgentClientOptions { RequestTimeout = TimeSpan.FromSeconds(2) });

    private sealed class FakeTransport : IStorageIpcTransport
    {
        private readonly Func<IpcEnvelope, CancellationToken, Task<IpcEnvelope>> _responseFactory;

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
        public IpcEnvelope? LastRequest { get; private set; }

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
            LastRequest = envelope;
            return ValueTask.CompletedTask;
        }

        public async ValueTask<IpcEnvelope> ReceiveAsync(CancellationToken cancellationToken = default) =>
            await _responseFactory(
                LastRequest ?? throw new InvalidOperationException("No request was sent."),
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
