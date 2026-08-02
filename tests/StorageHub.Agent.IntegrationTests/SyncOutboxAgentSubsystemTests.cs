using StorageHub.Agent.Sync;
using StorageHub.Persistence;
using StorageHub.Persistence.Sync;
using StorageHub.Sync;
using StorageHub.Sync.Persistence;

namespace StorageHub.Agent.IntegrationTests;

public sealed class SyncOutboxAgentSubsystemTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"storagehub-sync-outbox-agent-{Guid.NewGuid():N}");

    [Fact]
    public async Task Worker_claims_only_owned_sync_kinds()
    {
        var options = new SqliteDatabaseOptions(Path.Combine(_directory, "storagehub.db"), pooling: false);
        Assert.True((await new StorageHubDatabaseInitializer(options).InitializeAsync()).IsReady);
        var outbox = new SqliteReliableOutboxStore(new SingleWriterSqliteDatabase(options));
        var now = DateTimeOffset.UtcNow;
        var unrelatedId = Guid.NewGuid();
        var ownedId = Guid.NewGuid();
        _ = await outbox.EnqueueAsync(new OutboxEventDraft(
            unrelatedId,
            "notifications.send",
            "notification:1",
            1,
            "{}",
            now));
        _ = await outbox.EnqueueAsync(new OutboxEventDraft(
            ownedId,
            SyncOutboxEventKinds.PreviewRequested,
            "sync-schedule:1",
            1,
            "{}",
            now));
        var processor = new ImmediateProcessor(SyncOutboxProcessingResult.Complete());
        await using var worker = new SyncOutboxAgentSubsystem(
            outbox,
            processor,
            new SyncOutboxWorkerOptions { LeaseDuration = TimeSpan.FromMinutes(1) },
            ownerId: "test-sync-worker");
        _ = await worker.InitializeAsync(CancellationToken.None);

        Assert.True(await worker.RunClaimOnceAsync());

        Assert.Equal(ownedId, Assert.Single(processor.Events).EventId);
        Assert.NotNull((await outbox.GetAsync(ownedId))!.DispatchedAtUtc);
        Assert.Null((await outbox.GetAsync(unrelatedId))!.DispatchedAtUtc);
    }

    [Fact]
    public async Task Renewal_loss_cancels_processor_and_never_completes_or_fails_stale_claim()
    {
        var now = DateTimeOffset.UtcNow;
        var eventRecord = new OutboxEventRecord(
            Guid.NewGuid(),
            SyncOutboxEventKinds.PreviewRequested,
            "sync-schedule:1",
            1,
            "{}",
            now,
            null,
            null,
            1,
            1,
            null,
            null,
            null);
        var store = new LeaseLosingOutboxStore(new OutboxDeliveryLease(
            eventRecord,
            Guid.NewGuid(),
            "test-sync-worker",
            1,
            now,
            now.AddSeconds(1)));
        var processor = new BlockingProcessor();
        await using var worker = new SyncOutboxAgentSubsystem(
            store,
            processor,
            new SyncOutboxWorkerOptions
            {
                LeaseDuration = TimeSpan.FromSeconds(1),
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(20),
            },
            ownerId: "test-sync-worker");
        _ = await worker.InitializeAsync(CancellationToken.None);

        Assert.True(await worker.RunClaimOnceAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));

        Assert.True(await processor.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, store.RenewCalls);
        Assert.Equal(0, store.CompleteCalls);
        Assert.Equal(0, store.FailCalls);
        Assert.Equal(1, worker.LeaseLossCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class ImmediateProcessor(SyncOutboxProcessingResult result) : ISyncOutboxEventProcessor
    {
        public List<OutboxEventRecord> Events { get; } = [];

        public ValueTask<SyncOutboxProcessingResult> ProcessAsync(
            OutboxDeliveryLease lease,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(lease.Event);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class BlockingProcessor : ISyncOutboxEventProcessor
    {
        public TaskCompletionSource<bool> Cancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<SyncOutboxProcessingResult> ProcessAsync(
            OutboxDeliveryLease lease,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return SyncOutboxProcessingResult.Complete();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _ = Cancelled.TrySetResult(true);
                throw;
            }
        }
    }

    private sealed class LeaseLosingOutboxStore(OutboxDeliveryLease lease) : IReliableOutboxStore
    {
        private int _claimed;

        public int RenewCalls { get; private set; }
        public int CompleteCalls { get; private set; }
        public int FailCalls { get; private set; }

        public ValueTask<OutboxEventRecord?> GetAsync(
            Guid eventId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<OutboxEventRecord?>(eventId == lease.Event.EventId ? lease.Event : null);

        public ValueTask<SyncPersistenceResult<OutboxEventRecord>> EnqueueAsync(
            OutboxEventDraft draft,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<OutboxDeliveryLease>> ClaimPendingAsync(
            string ownerId,
            int maximumCount,
            DateTimeOffset observedAtUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<OutboxDeliveryLease>> ClaimPendingByKindsAsync(
            string ownerId,
            IReadOnlyCollection<string> eventKinds,
            int maximumCount,
            DateTimeOffset observedAtUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<OutboxDeliveryLease>>(
                Interlocked.Exchange(ref _claimed, 1) == 0 ? [lease] : []);

        public ValueTask<SyncPersistenceResult<OutboxDeliveryLease>> RenewAsync(
            OutboxDeliveryLease current,
            DateTimeOffset renewedAtUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            RenewCalls++;
            return ValueTask.FromResult(new SyncPersistenceResult<OutboxDeliveryLease>(
                SyncPersistenceMutationStatus.StaleLease,
                null));
        }

        public ValueTask<SyncPersistenceMutationStatus> CompleteAsync(
            OutboxDeliveryLease current,
            DateTimeOffset dispatchedAtUtc,
            CancellationToken cancellationToken = default)
        {
            CompleteCalls++;
            return ValueTask.FromResult(SyncPersistenceMutationStatus.Applied);
        }

        public ValueTask<SyncPersistenceMutationStatus> FailAsync(
            OutboxDeliveryLease current,
            DateTimeOffset failedAtUtc,
            DateTimeOffset nextAttemptAtUtc,
            string errorCode,
            string safeErrorSummary,
            bool deadLetter,
            CancellationToken cancellationToken = default)
        {
            FailCalls++;
            return ValueTask.FromResult(SyncPersistenceMutationStatus.Applied);
        }
    }
}
