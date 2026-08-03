using StorageHub.Agent.Ipc;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

public sealed class SyncManagementAgentClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 13, 0, 0, TimeSpan.Zero);
    private static readonly string Digest = new('a', 64);

    [Fact]
    public async Task Client_rejects_an_uncorrelated_response_and_disconnects()
    {
        var transport = new FakeTransport(_ => IpcEnvelope.Create(
            SyncManagementIpcMessageTypes.ProfileListResponse,
            Guid.NewGuid(),
            1,
            new SyncProfileListResponse(SyncManagementIpcContract.CurrentVersion, [])));
        await using var client = CreateClient(transport);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.ListProfilesAsync(new SyncProfileListRequest()));

        Assert.Equal(1, transport.DisconnectCount);
    }

    [Fact]
    public async Task Client_surfaces_the_agents_safe_error_message()
    {
        var transport = new FakeTransport(request => IpcEnvelope.Create(
            IpcProtocol.ErrorResponseMessageType,
            request.RequestId,
            1,
            new IpcErrorResponse(
                "ipc.message.unsupported",
                "The requested operation is not supported by this agent version.")));
        await using var client = CreateClient(transport);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ListProfilesAsync(new SyncProfileListRequest()));

        Assert.Equal("The requested operation is not supported by this agent version.", error.Message);
        Assert.Equal(1, transport.DisconnectCount);
    }

    [Fact]
    public async Task Client_accepts_a_bounded_run_history_request()
    {
        var transport = new FakeTransport(request => IpcEnvelope.Create(
            SyncManagementIpcMessageTypes.RunListResponse,
            request.RequestId,
            1,
            new SyncRunListResponse(
                SyncManagementIpcContract.CurrentVersion,
                [],
                ContinuationToken: null)));
        await using var client = CreateClient(transport);

        var response = await client.ListRunsAsync(new SyncRunListRequest(PageSize: 50));

        Assert.Empty(response.Runs);
        Assert.Null(response.ContinuationToken);
    }

    [Fact]
    public async Task Client_rejects_a_mismatched_run_identity()
    {
        var runId = Guid.NewGuid();
        var transport = new FakeTransport(request => IpcEnvelope.Create(
            SyncManagementIpcMessageTypes.RunStatusResponse,
            request.RequestId,
            1,
            new SyncRunStatusResponse(
                SyncManagementIpcContract.CurrentVersion,
                Guid.NewGuid(),
                CreateRun(Guid.NewGuid()))));
        await using var client = CreateClient(transport);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetRunStatusAsync(
            new SyncRunStatusRequest(SyncManagementIpcContract.CurrentVersion, runId)));

        Assert.Equal(1, transport.DisconnectCount);
    }

    [Fact]
    public async Task Client_rejects_an_invalid_plan_digest_and_oversized_page()
    {
        var runId = Guid.NewGuid();
        var transport = new FakeTransport(request => IpcEnvelope.Create(
            SyncManagementIpcMessageTypes.PlanPageResponse,
            request.RequestId,
            1,
            new SyncPlanPageResponse(
                SyncManagementIpcContract.CurrentVersion,
                runId,
                Guid.NewGuid(),
                "not-a-digest",
                101,
                Enumerable.Range(0, 101).Select(index => CreateOperation(index)).ToArray(),
                null)));
        await using var client = CreateClient(transport);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetPlanPageAsync(
            new SyncPlanPageRequest(SyncManagementIpcContract.CurrentVersion, runId, PageSize: 100)));

        Assert.Equal(1, transport.DisconnectCount);
    }

    [Fact]
    public async Task Client_accepts_a_bounded_preview_plan_and_exact_durable_approval()
    {
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var run = CreateRun(runId, profileId);
        var call = 0;
        var transport = new FakeTransport(request =>
        {
            call++;
            return call switch
            {
                1 => IpcEnvelope.Create(
                    SyncManagementIpcMessageTypes.PreviewGenerateResponse,
                    request.RequestId,
                    call,
                    new SyncPreviewGenerateResponse(
                        SyncManagementIpcContract.CurrentVersion,
                        profileId,
                        run,
                        new SyncPlanOverview(
                            runId,
                            run.PlanId,
                            Digest,
                            0,
                            1,
                            1,
                            0,
                            0,
                            Now))),
                2 => IpcEnvelope.Create(
                    SyncManagementIpcMessageTypes.PlanPageResponse,
                    request.RequestId,
                    call,
                    new SyncPlanPageResponse(
                        SyncManagementIpcContract.CurrentVersion,
                        runId,
                        run.PlanId,
                        Digest,
                        1,
                        [CreateOperation(0)],
                        null)),
                _ => IpcEnvelope.Create(
                    SyncManagementIpcMessageTypes.ApproveDispatchResponse,
                    request.RequestId,
                    call,
                    new SyncApproveDispatchResponse(
                        SyncManagementIpcContract.CurrentVersion,
                        runId,
                        DurablyDispatched: true,
                        run with
                        {
                            Phase = SyncIpcRunPhase.Ready,
                            Revision = run.Revision + 1,
                            DispatchState = SyncIpcDispatchState.DurablyDispatched,
                            DispatchedUtc = Now
                        }))
            };
        });
        await using var client = CreateClient(transport);

        var preview = await client.GeneratePreviewAsync(new SyncPreviewGenerateRequest(
            SyncManagementIpcContract.CurrentVersion,
            profileId,
            Guid.NewGuid()));
        var page = await client.GetPlanPageAsync(new SyncPlanPageRequest(
            SyncManagementIpcContract.CurrentVersion,
            runId,
            PageSize: 10));
        var approved = await client.ApproveAndDispatchAsync(new SyncApproveDispatchRequest(
            SyncManagementIpcContract.CurrentVersion,
            runId,
            run.Revision,
            run.ApprovalSha256));

        Assert.Equal(runId, preview.Run?.SyncRunId);
        Assert.Single(page.Operations);
        Assert.True(approved.DurablyDispatched);
        Assert.Equal(SyncIpcDispatchState.DurablyDispatched, approved.Run?.DispatchState);
    }

    [Fact]
    public async Task Client_applies_a_bounded_deadline()
    {
        var transport = new FakeTransport(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        });
        await using var client = new NamedPipeSyncManagementAgentClient(
            transport,
            new SyncManagementAgentClientOptions { RequestTimeout = TimeSpan.FromMilliseconds(50) });

        await Assert.ThrowsAsync<TimeoutException>(() => client.ListProfilesAsync(new SyncProfileListRequest()));

        Assert.Equal(1, transport.DisconnectCount);
    }

    private static NamedPipeSyncManagementAgentClient CreateClient(IStorageIpcTransport transport) => new(
        transport,
        new SyncManagementAgentClientOptions { RequestTimeout = TimeSpan.FromSeconds(2) });

    private static SyncRunSummary CreateRun(Guid runId, Guid? profileId = null) => new(
        runId,
        profileId ?? Guid.NewGuid(),
        Generation: 1,
        SyncIpcRunPhase.AwaitingApproval,
        SyncIpcStatusCode.None,
        Revision: 3,
        Now,
        Guid.NewGuid(),
        Digest,
        Digest,
        ConflictCount: 0,
        SyncIpcDispatchState.NotDispatched,
        DispatchedUtc: null,
        Now,
        BaselineItemCount: 0,
        LeftItemCount: 1,
        RightItemCount: 0,
        LeftSnapshotComplete: true,
        RightSnapshotComplete: true);

    private static SyncPlanOperationSummary CreateOperation(int sequence) => new(
        sequence,
        SyncIpcPlanOperationKind.Copy,
        Guid.NewGuid(),
        $"source-{sequence}.bin",
        Guid.NewGuid(),
        $"destination-{sequence}.bin",
        ExpectedLength: 12,
        IsDestructive: false);

    private sealed class FakeTransport : IStorageIpcTransport
    {
        private readonly Func<IpcEnvelope, CancellationToken, Task<IpcEnvelope>> _responseFactory;
        private IpcEnvelope? _request;

        public FakeTransport(Func<IpcEnvelope, IpcEnvelope> responseFactory)
            : this((request, _) => Task.FromResult(responseFactory(request)))
        {
        }

        public FakeTransport(Func<IpcEnvelope, CancellationToken, Task<IpcEnvelope>> responseFactory) =>
            _responseFactory = responseFactory;

        public bool IsConnected { get; private set; }

        public int DisconnectCount { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
