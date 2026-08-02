using StorageHub.Sync.Persistence;

namespace StorageHub.Agent.Scheduling;

/// <summary>
/// The scheduled runner only records a fenced durable preview request. Provider mutation belongs
/// to a separately composed outbox consumer and can never run under an unchecked scheduler lease.
/// </summary>
public sealed class DurableScheduledSyncJobRunner(
    IScheduledSyncDispatchStore dispatchStore) : IScheduledSyncJobRunner
{
    private readonly IScheduledSyncDispatchStore _dispatchStore =
        dispatchStore ?? throw new ArgumentNullException(nameof(dispatchStore));

    public async ValueTask<ScheduledSyncJobRunResult> RunAsync(
        ScheduledSyncJobExecution execution,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (execution.Job.JobId != execution.Lease.JobId ||
            execution.Job.ProfileId != execution.Lease.ProfileId)
        {
            throw new InvalidOperationException("The scheduler job and lease identities do not match.");
        }

        try
        {
            var status = await _dispatchStore.TryDispatchAsync(
                new ScheduledSyncDispatchRequest(
                    execution.Lease.JobId.Value,
                    execution.Lease.ProfileId,
                    execution.Lease.LeaseId,
                    execution.Lease.FencingToken,
                    execution.Lease.ScheduledForUtc,
                    execution.Lease.AcquiredAtUtc,
                    execution.Lease.ExpiresAtUtc),
                cancellationToken).ConfigureAwait(false);
            return status switch
            {
                SyncPersistenceMutationStatus.Applied or
                SyncPersistenceMutationStatus.AlreadyApplied => ScheduledSyncJobRunResult.Completed(),
                SyncPersistenceMutationStatus.StaleLease => ScheduledSyncJobRunResult.Failed(
                    "scheduler.dispatch.stale_lease",
                    "The scheduler lease expired or was superseded before durable dispatch."),
                SyncPersistenceMutationStatus.NotFound => ScheduledSyncJobRunResult.Failed(
                    "scheduler.dispatch.not_found",
                    "The scheduled sync job no longer exists."),
                _ => ScheduledSyncJobRunResult.Failed(
                    "scheduler.dispatch.conflict",
                    "The scheduled sync request could not be durably recorded."),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ScheduledSyncJobRunResult.Cancelled();
        }
    }
}
