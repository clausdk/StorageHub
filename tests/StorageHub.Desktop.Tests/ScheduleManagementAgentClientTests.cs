using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

public sealed class ScheduleManagementAgentClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Client_rejects_an_uncorrelated_response_and_disconnects()
    {
        var transport = new FakeTransport(_ => IpcEnvelope.Create(
            ScheduleManagementIpcMessageTypes.ListResponse,
            Guid.NewGuid(),
            1,
            new ScheduleListResponse(ScheduleManagementIpcContract.CurrentVersion, [])));
        await using var client = CreateClient(transport);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.ListAsync(new ScheduleListRequest()));

        Assert.Equal(1, transport.DisconnectCount);
    }

    [Fact]
    public async Task Client_rejects_a_mismatched_schedule_identity()
    {
        var requestedId = Guid.NewGuid();
        var transport = new FakeTransport(request => IpcEnvelope.Create(
            ScheduleManagementIpcMessageTypes.GetResponse,
            request.RequestId,
            1,
            new ScheduleGetResponse(
                ScheduleManagementIpcContract.CurrentVersion,
                Guid.NewGuid(),
                CreateSchedule(Guid.NewGuid(), revision: 1))));
        await using var client = CreateClient(transport);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetAsync(new ScheduleGetRequest(
            ScheduleManagementIpcContract.CurrentVersion,
            requestedId)));

        Assert.Equal(1, transport.DisconnectCount);
    }

    [Fact]
    public async Task Client_accepts_exact_create_update_and_delete_revisions()
    {
        var scheduleId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var call = 0;
        var transport = new FakeTransport(request =>
        {
            call++;
            return call switch
            {
                1 => IpcEnvelope.Create(
                    ScheduleManagementIpcMessageTypes.CreateResponse,
                    request.RequestId,
                    call,
                    new ScheduleMutationResponse(
                        ScheduleManagementIpcContract.CurrentVersion,
                        scheduleId,
                        ScheduleMutationOutcome.Succeeded,
                        CreateSchedule(scheduleId, 1, profileId),
                        ActualRevision: 1)),
                2 => IpcEnvelope.Create(
                    ScheduleManagementIpcMessageTypes.UpdateResponse,
                    request.RequestId,
                    call,
                    new ScheduleMutationResponse(
                        ScheduleManagementIpcContract.CurrentVersion,
                        scheduleId,
                        ScheduleMutationOutcome.Succeeded,
                        CreateSchedule(scheduleId, 2, profileId),
                        ActualRevision: 2)),
                _ => IpcEnvelope.Create(
                    ScheduleManagementIpcMessageTypes.DeleteResponse,
                    request.RequestId,
                    call,
                    new ScheduleMutationResponse(
                        ScheduleManagementIpcContract.CurrentVersion,
                        scheduleId,
                        ScheduleMutationOutcome.Succeeded,
                        Schedule: null,
                        ActualRevision: 2))
            };
        });
        await using var client = CreateClient(transport);
        var draft = new ScheduleDraftDocument(
            profileId,
            "0 2 * * *",
            "UTC",
            3_600,
            QueueOneWhileRunning: true,
            Enabled: false);

        var created = await client.CreateAsync(new ScheduleCreateRequest(
            ScheduleManagementIpcContract.CurrentVersion,
            scheduleId,
            draft));
        var updated = await client.UpdateAsync(new ScheduleUpdateRequest(
            ScheduleManagementIpcContract.CurrentVersion,
            scheduleId,
            ExpectedRevision: 1,
            draft with { CronExpression = "0 3 * * *" }));
        var deleted = await client.DeleteAsync(new ScheduleDeleteRequest(
            ScheduleManagementIpcContract.CurrentVersion,
            scheduleId,
            ExpectedRevision: 2));

        Assert.Equal(1, created.Schedule?.Revision);
        Assert.Equal(2, updated.Schedule?.Revision);
        Assert.Null(deleted.Schedule);
    }

    [Fact]
    public async Task Client_applies_a_bounded_deadline()
    {
        var transport = new FakeTransport(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        });
        await using var client = new NamedPipeScheduleManagementAgentClient(
            transport,
            new ScheduleManagementAgentClientOptions { RequestTimeout = TimeSpan.FromMilliseconds(50) });

        await Assert.ThrowsAsync<TimeoutException>(() => client.ListAsync(new ScheduleListRequest()));

        Assert.Equal(1, transport.DisconnectCount);
    }

    private static ScheduleDocument CreateSchedule(Guid scheduleId, long revision, Guid? profileId = null) => new(
        scheduleId,
        profileId ?? Guid.NewGuid(),
        "Documents",
        "0 2 * * *",
        "UTC",
        MisfireGraceSeconds: 3_600,
        QueueOneWhileRunning: true,
        Enabled: false,
        NextOccurrenceUtc: null,
        QueuedOccurrenceUtc: null,
        IsBusy: false,
        LastRunOutcome: null,
        LastErrorCode: null,
        revision,
        ScheduleIpcExecutionMode.PreviewOnly);

    private static NamedPipeScheduleManagementAgentClient CreateClient(IStorageIpcTransport transport) => new(
        transport,
        new ScheduleManagementAgentClientOptions { RequestTimeout = TimeSpan.FromSeconds(2) });

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
