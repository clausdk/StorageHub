using System.Globalization;
using System.Text.Json;
using StorageHub.Agent.Transfers;
using StorageHub.Contracts.Ipc;
using StorageHub.Domain.Identifiers;
using StorageHub.Persistence;
using StorageHub.Persistence.Transfers;
using StorageHub.Transfers;

namespace StorageHub.Agent.Windows.Tests;

public sealed class TransferQueueIpcCommandServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"storagehub-transfer-ipc-{Guid.NewGuid():N}");

    [Fact]
    public async Task Enqueue_persists_all_identity_evidence_and_is_idempotent()
    {
        var fixture = await CreateFixtureAsync();
        var request = await CreateRequestAsync(fixture);

        var first = await SendAsync<TransferEnqueueRequest, TransferEnqueueResponse>(
            fixture.Service,
            TransferQueueIpcMessageTypes.EnqueueRequest,
            request);
        var second = await SendAsync<TransferEnqueueRequest, TransferEnqueueResponse>(
            fixture.Service,
            TransferQueueIpcMessageTypes.EnqueueRequest,
            request);
        var stored = Assert.IsType<DurableTransferJob>(
            await fixture.Store.FindAsync(new TransferJobId(request.TransferId)));

        Assert.True(first.Accepted);
        Assert.False(first.AlreadyExisted);
        Assert.True(second.Accepted);
        Assert.True(second.AlreadyExisted);
        Assert.Equal("source-id", stored.Intent.Source.NativeItemId);
        Assert.Equal("source-version", stored.Intent.Source.VersionId);
        Assert.Equal("source-etag", stored.Intent.Source.EntityTag);
        Assert.Equal("destination-version", stored.Intent.ExpectedDestinationVersionId);
        Assert.Equal("destination-etag", stored.Intent.ExpectedDestinationEntityTag);
    }

    [Fact]
    public async Task Enqueue_rejects_same_id_with_a_different_intent()
    {
        var fixture = await CreateFixtureAsync();
        var request = await CreateRequestAsync(fixture);
        var changed = request with
        {
            Destination = request.Destination with { RelativePath = "archive/other.bin" }
        };

        var first = await SendAsync<TransferEnqueueRequest, TransferEnqueueResponse>(
            fixture.Service,
            TransferQueueIpcMessageTypes.EnqueueRequest,
            request);
        var second = await SendAsync<TransferEnqueueRequest, TransferEnqueueResponse>(
            fixture.Service,
            TransferQueueIpcMessageTypes.EnqueueRequest,
            changed);

        Assert.True(first.Accepted);
        Assert.False(second.Accepted);
        Assert.Equal(StorageIpcFailureCategory.Conflict, second.Failure?.Category);
    }

    [Fact]
    public async Task Enqueue_rejects_noncanonical_paths_and_unfenced_move()
    {
        var fixture = await CreateFixtureAsync();
        var request = await CreateRequestAsync(fixture);
        var noncanonical = request with
        {
            Source = request.Source with { RelativePath = "folder\\source.bin" }
        };
        var unfencedMove = request with
        {
            TransferId = Guid.NewGuid(),
            Operation = TransferQueueOperation.Move,
            Source = request.Source with { VersionId = null, EntityTag = null },
            Destination = request.Destination with { VersionId = null, EntityTag = null },
            ExpectedDestinationVersionId = null,
            ExpectedDestinationEntityTag = null
        };

        var pathResponse = await SendAsync<TransferEnqueueRequest, TransferEnqueueResponse>(
            fixture.Service,
            TransferQueueIpcMessageTypes.EnqueueRequest,
            noncanonical);
        var moveResponse = await SendAsync<TransferEnqueueRequest, TransferEnqueueResponse>(
            fixture.Service,
            TransferQueueIpcMessageTypes.EnqueueRequest,
            unfencedMove);

        Assert.False(pathResponse.Accepted);
        Assert.Equal(StorageIpcFailureCategory.Validation, pathResponse.Failure?.Category);
        Assert.False(moveResponse.Accepted);
        Assert.Equal(StorageIpcFailureCategory.Validation, moveResponse.Failure?.Category);
    }

    [Fact]
    public async Task List_is_bounded_paginated_and_omits_internal_identity_and_resume_fields()
    {
        var fixture = await CreateFixtureAsync();
        for (var index = 0; index < 3; index++)
        {
            var request = await CreateRequestAsync(fixture) with { Priority = index };
            var enqueued = await SendAsync<TransferEnqueueRequest, TransferEnqueueResponse>(
                fixture.Service,
                TransferQueueIpcMessageTypes.EnqueueRequest,
                request);
            Assert.True(enqueued.Accepted);
        }

        var envelope = CreateEnvelope(
            TransferQueueIpcMessageTypes.ListRequest,
            new TransferListRequest(
                TransferQueueIpcContract.CurrentVersion,
                [TransferQueueState.Pending],
                PageSize: 2));
        var command = await fixture.Service.HandleAsync(envelope);
        var first = command.Payload.Deserialize<TransferListResponse>()!;
        var second = await SendAsync<TransferListRequest, TransferListResponse>(
            fixture.Service,
            TransferQueueIpcMessageTypes.ListRequest,
            new TransferListRequest(
                TransferQueueIpcContract.CurrentVersion,
                [TransferQueueState.Pending],
                PageSize: 2,
                first.ContinuationToken));
        var payload = command.Payload.GetRawText();

        Assert.Equal(2, first.Transfers.Length);
        Assert.Equal(3, first.StateCounts![TransferQueueState.Pending]);
        Assert.Single(second.Transfers);
        Assert.NotNull(first.ContinuationToken);
        Assert.Null(second.ContinuationToken);
        Assert.DoesNotContain("source-root", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("source-version", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("source-etag", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Resume", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Lease", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancel_uses_revision_cas_and_returns_latest_summary_on_conflict()
    {
        var fixture = await CreateFixtureAsync();
        var request = await CreateRequestAsync(fixture);
        var enqueued = await SendAsync<TransferEnqueueRequest, TransferEnqueueResponse>(
            fixture.Service,
            TransferQueueIpcMessageTypes.EnqueueRequest,
            request);

        var applied = await SendAsync<TransferCancelRequest, TransferMutationResponse>(
            fixture.Service,
            TransferQueueIpcMessageTypes.CancelRequest,
            new TransferCancelRequest(
                TransferQueueIpcContract.CurrentVersion,
                request.TransferId,
                enqueued.Transfer!.Revision));
        var stale = await SendAsync<TransferCancelRequest, TransferMutationResponse>(
            fixture.Service,
            TransferQueueIpcMessageTypes.CancelRequest,
            new TransferCancelRequest(
                TransferQueueIpcContract.CurrentVersion,
                request.TransferId,
                enqueued.Transfer.Revision));

        Assert.Equal(TransferQueueMutationOutcome.Applied, applied.Outcome);
        Assert.Equal(TransferQueueState.Cancelled, applied.Transfer?.State);
        Assert.Equal(TransferQueueMutationOutcome.RevisionConflict, stale.Outcome);
        Assert.Equal(applied.Transfer?.Revision, stale.Transfer?.Revision);
    }

    [Fact]
    public async Task Clear_history_removes_terminal_jobs_but_not_pending_work()
    {
        var fixture = await CreateFixtureAsync();
        var cancelledRequest = await CreateRequestAsync(fixture);
        var pendingRequest = await CreateRequestAsync(fixture);
        var cancelled = await SendAsync<TransferEnqueueRequest, TransferEnqueueResponse>(
            fixture.Service, TransferQueueIpcMessageTypes.EnqueueRequest, cancelledRequest);
        _ = await SendAsync<TransferEnqueueRequest, TransferEnqueueResponse>(
            fixture.Service, TransferQueueIpcMessageTypes.EnqueueRequest, pendingRequest);
        _ = await SendAsync<TransferCancelRequest, TransferMutationResponse>(
            fixture.Service, TransferQueueIpcMessageTypes.CancelRequest,
            new TransferCancelRequest(TransferQueueIpcContract.CurrentVersion, cancelledRequest.TransferId, cancelled.Transfer!.Revision));

        var cleared = await SendAsync<TransferHistoryClearRequest, TransferHistoryClearResponse>(
            fixture.Service, TransferQueueIpcMessageTypes.ClearHistoryRequest,
            new TransferHistoryClearRequest(TransferQueueIpcContract.CurrentVersion, [], ClearAll: true));

        Assert.Equal(1, cleared.ClearedCount);
        Assert.Null(await fixture.Store.FindAsync(new TransferJobId(cancelledRequest.TransferId)));
        Assert.NotNull(await fixture.Store.FindAsync(new TransferJobId(pendingRequest.TransferId)));
    }

    [Fact]
    public async Task Active_cancel_is_requested_without_bypassing_the_worker_lease_fence()
    {
        var fixture = await CreateFixtureAsync();
        var request = await CreateRequestAsync(fixture);
        _ = await SendAsync<TransferEnqueueRequest, TransferEnqueueResponse>(
            fixture.Service,
            TransferQueueIpcMessageTypes.EnqueueRequest,
            request);
        var claim = Assert.IsType<TransferJobClaim>(await fixture.Store.TryClaimNextAsync(
            new TransferClaimRequest("worker", Now, TimeSpan.FromMinutes(1))));
        var cancellation = new RecordingCancellation(ActiveTransferCancellationResult.Accepted);
        var service = new TransferQueueIpcCommandService(
            fixture.Store,
            fixture.Store,
            cancellation,
            new FixedTimeProvider(Now));

        var response = await SendAsync<TransferCancelRequest, TransferMutationResponse>(
            service,
            TransferQueueIpcMessageTypes.CancelRequest,
            new TransferCancelRequest(
                TransferQueueIpcContract.CurrentVersion,
                request.TransferId,
                claim.Job.State.Revision));
        var stored = Assert.IsType<DurableTransferJob>(
            await fixture.Store.FindAsync(new TransferJobId(request.TransferId)));

        Assert.Equal(TransferQueueMutationOutcome.Accepted, response.Outcome);
        Assert.Equal(new TransferJobId(request.TransferId), cancellation.TransferId);
        Assert.Equal(claim.Job.State.Revision, cancellation.ExpectedRevision);
        Assert.Equal(TransferState.Preparing, stored.State.State);
        Assert.NotNull(stored.ActiveLease);
    }

    [Fact]
    public async Task Retry_moves_an_inactive_failed_job_to_pending_with_revision_cas()
    {
        var fixture = await CreateFixtureAsync();
        var request = await CreateRequestAsync(fixture);
        _ = await SendAsync<TransferEnqueueRequest, TransferEnqueueResponse>(
            fixture.Service,
            TransferQueueIpcMessageTypes.EnqueueRequest,
            request);
        var claim = Assert.IsType<TransferJobClaim>(await fixture.Store.TryClaimNextAsync(
            new TransferClaimRequest("worker", Now, TimeSpan.FromMinutes(1))));
        var failed = await fixture.Store.TryTransitionAsync(new TransferStateTransitionRequest(
            claim.Lease,
            claim.Job.State.Revision,
            TransferState.Failed,
            Now,
            TransferStatusCode.ProviderFailure,
            new TransferSafeError("transfer.test.failed", "A safe test failure.")));
        Assert.Equal(TransferStoreMutationStatus.Applied, failed.Status);

        var response = await SendAsync<TransferRetryRequest, TransferMutationResponse>(
            fixture.Service,
            TransferQueueIpcMessageTypes.RetryRequest,
            new TransferRetryRequest(
                TransferQueueIpcContract.CurrentVersion,
                request.TransferId,
                failed.Value!.State.Revision));

        Assert.Equal(TransferQueueMutationOutcome.Applied, response.Outcome);
        Assert.Equal(TransferQueueState.Pending, response.Transfer?.State);
        Assert.Equal(failed.Value.State.Revision + 1, response.Transfer?.Revision);
    }

    [Fact]
    public async Task Reconciliation_requires_explicit_review_then_applies_operator_decision()
    {
        var fixture = await CreateFixtureAsync();
        var request = await CreateRequestAsync(fixture);
        _ = await SendAsync<TransferEnqueueRequest, TransferEnqueueResponse>(
            fixture.Service,
            TransferQueueIpcMessageTypes.EnqueueRequest,
            request);
        var claim = Assert.IsType<TransferJobClaim>(await fixture.Store.TryClaimNextAsync(
            new TransferClaimRequest("worker", Now, TimeSpan.FromMinutes(1))));
        var interrupted = await fixture.Store.TryTransitionAsync(new TransferStateTransitionRequest(
            claim.Lease,
            claim.Job.State.Revision,
            TransferState.Interrupted,
            Now,
            TransferStatusCode.Interrupted,
            new TransferSafeError("transfer.interrupted", "The transfer was interrupted.")));
        Assert.Equal(TransferStoreMutationStatus.Applied, interrupted.Status);

        var reviewed = await SendAsync<TransferReconcileRequest, TransferMutationResponse>(
            fixture.Service,
            TransferQueueIpcMessageTypes.ReconcileRequest,
            new TransferReconcileRequest(
                TransferQueueIpcContract.CurrentVersion,
                request.TransferId,
                interrupted.Value!.State.Revision,
                TransferReconciliationAction.Review));
        var completed = await SendAsync<TransferReconcileRequest, TransferMutationResponse>(
            fixture.Service,
            TransferQueueIpcMessageTypes.ReconcileRequest,
            new TransferReconcileRequest(
                TransferQueueIpcContract.CurrentVersion,
                request.TransferId,
                reviewed.Transfer!.Revision,
                TransferReconciliationAction.MarkCompleted));

        Assert.Equal(TransferQueueState.NeedsReconciliation, reviewed.Transfer.State);
        Assert.Equal(TransferQueueMutationOutcome.Applied, completed.Outcome);
        Assert.Equal(TransferQueueState.Completed, completed.Transfer?.State);
    }

    [Fact]
    public async Task Provider_exception_text_is_not_returned_by_queue_failures()
    {
        var fixture = await CreateFixtureAsync();
        var service = new TransferQueueIpcCommandService(
            fixture.Store,
            new ThrowingQueueQueryStore(),
            timeProvider: new FixedTimeProvider(Now));
        var command = await service.HandleAsync(CreateEnvelope(
            TransferQueueIpcMessageTypes.ListRequest,
            new TransferListRequest(
                TransferQueueIpcContract.CurrentVersion,
                [TransferQueueState.Pending])));
        var response = command.Payload.Deserialize<TransferListResponse>()!;

        Assert.NotNull(response.Failure);
        Assert.Equal(StorageIpcFailureCategory.Unavailable, response.Failure.Category);
        Assert.DoesNotContain("super-secret", command.Payload.GetRawText(), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private async Task<Fixture> CreateFixtureAsync()
    {
        var options = new SqliteDatabaseOptions(
            Path.Combine(_directory, $"{Guid.NewGuid():N}.db"),
            pooling: false);
        var initialized = await new StorageHubDatabaseInitializer(options).InitializeAsync();
        Assert.True(initialized.IsReady, initialized.Message);
        var database = new SingleWriterSqliteDatabase(options);
        var store = new SqliteTransferJobStore(database);
        return new Fixture(
            database,
            store,
            new TransferQueueIpcCommandService(
                store,
                store,
                activeCancellation: null,
                new FixedTimeProvider(Now)));
    }

    private static async Task<TransferEnqueueRequest> CreateRequestAsync(Fixture fixture)
    {
        var source = ConnectionProfileId.New();
        var destination = ConnectionProfileId.New();
        await SeedProfileAsync(fixture.Database, source, $"source-{source}");
        await SeedProfileAsync(fixture.Database, destination, $"destination-{destination}");
        return new TransferEnqueueRequest(
            TransferQueueIpcContract.CurrentVersion,
            Guid.NewGuid(),
            TransferQueueOperation.Copy,
            new TransferQueueAddress(
                source.Value,
                "source-root",
                "folder/source.bin",
                "source-id",
                "source-version",
                "source-etag"),
            new TransferQueueAddress(
                destination.Value,
                "destination-root",
                "archive/source.bin",
                "destination-id",
                "destination-version",
                "destination-etag"),
            ExpectedLength: 128,
            TransferQueueVerification.StrongHashRequired,
            Priority: 5,
            ExpectedDestinationVersionId: "destination-version",
            ExpectedDestinationEntityTag: "destination-etag");
    }

    private static async Task SeedProfileAsync(
        SingleWriterSqliteDatabase database,
        ConnectionProfileId profileId,
        string name)
    {
        await using var writer = await database.AcquireWriterAsync();
        await using var command = writer.Connection.CreateCommand();
        command.CommandText = """
            INSERT INTO connection_profiles
            (
                profile_id, provider, display_name, tags_json, metadata_json, endpoint_json,
                authentication_json, operational_options_json, is_favorite, is_enabled,
                version, created_utc, updated_utc
            )
            VALUES ($id, 'local', $name, '[]', '{}', '{}', '{}', '{}', 0, 1, 1, $now, $now);
            """;
        command.Parameters.AddWithValue("$id", profileId.ToString());
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$now", Now.ToString("O", CultureInfo.InvariantCulture));
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<TResponse> SendAsync<TRequest, TResponse>(
        TransferQueueIpcCommandService service,
        string messageType,
        TRequest request)
    {
        var response = await service.HandleAsync(CreateEnvelope(messageType, request));
        return response.Payload.Deserialize<TResponse>()!;
    }

    private static IpcEnvelope CreateEnvelope<TRequest>(string messageType, TRequest request) =>
        IpcEnvelope.Create(messageType, Guid.NewGuid(), sequence: 1, request);

    private sealed record Fixture(
        SingleWriterSqliteDatabase Database,
        SqliteTransferJobStore Store,
        TransferQueueIpcCommandService Service);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingCancellation(ActiveTransferCancellationResult result)
        : IActiveTransferCancellation
    {
        public TransferJobId TransferId { get; private set; }
        public long ExpectedRevision { get; private set; } = -1;

        public ActiveTransferCancellationResult TryRequestActiveCancellation(
            TransferJobId transferJobId,
            long expectedRevision)
        {
            TransferId = transferJobId;
            ExpectedRevision = expectedRevision;
            return result;
        }
    }

    private sealed class ThrowingQueueQueryStore : ITransferQueueQueryStore
    {
        public ValueTask<TransferQueuePage> ListAsync(
            TransferQueueQuery query,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("provider-url?password=super-secret");
    }
}
