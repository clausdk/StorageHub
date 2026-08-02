using StorageHub.Agent.Scheduling;
using StorageHub.Domain.Identifiers;
using StorageHub.Sync.Persistence;

namespace StorageHub.Agent.IntegrationTests;

public sealed class DurableScheduledSyncJobRunnerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Runner_maps_the_exact_scheduler_lease_to_durable_dispatch_only()
    {
        var store = new RecordingDispatchStore(SyncPersistenceMutationStatus.Applied);
        var runner = new DurableScheduledSyncJobRunner(store);
        var execution = CreateExecution();

        var result = await runner.RunAsync(execution, CancellationToken.None);

        Assert.Equal(ScheduledSyncRunOutcome.Completed, result.Outcome);
        var request = Assert.Single(store.Requests);
        Assert.Equal(execution.Lease.JobId.Value, request.JobId);
        Assert.Equal(execution.Lease.ProfileId, request.ProfileId);
        Assert.Equal(execution.Lease.LeaseId, request.LeaseId);
        Assert.Equal(execution.Lease.FencingToken, request.FencingToken);
    }

    [Fact]
    public async Task Runner_reports_a_stale_fence_without_retrying_or_executing_provider_work()
    {
        var store = new RecordingDispatchStore(SyncPersistenceMutationStatus.StaleLease);
        var runner = new DurableScheduledSyncJobRunner(store);

        var result = await runner.RunAsync(CreateExecution(), CancellationToken.None);

        Assert.Equal(ScheduledSyncRunOutcome.Failed, result.Outcome);
        Assert.Equal("scheduler.dispatch.stale_lease", result.Code);
        Assert.False(result.IsTransient);
        Assert.Single(store.Requests);
    }

    private static ScheduledSyncJobExecution CreateExecution()
    {
        Assert.True(CronScheduleDefinition.TryCreate(
            "* * * * *",
            "UTC",
            out var schedule,
            out _));
        var jobId = ScheduledSyncJobId.New();
        var profileId = SyncProfileId.New();
        var job = new ScheduledSyncJobSnapshot(
            jobId,
            profileId,
            schedule!,
            enabled: true,
            queueOneWhileRunning: true,
            Now,
            queuedOccurrenceUtc: null,
            revision: 4);
        var lease = new ScheduledSyncJobLease(
            Guid.NewGuid(),
            jobId,
            profileId,
            Now,
            Now.AddMinutes(-1),
            Now.AddMinutes(10),
            fencingToken: 12);
        return new ScheduledSyncJobExecution(job, lease);
    }

    private sealed class RecordingDispatchStore(SyncPersistenceMutationStatus result)
        : IScheduledSyncDispatchStore
    {
        public List<ScheduledSyncDispatchRequest> Requests { get; } = [];

        public ValueTask<SyncPersistenceMutationStatus> TryDispatchAsync(
            ScheduledSyncDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return ValueTask.FromResult(result);
        }
    }

}
