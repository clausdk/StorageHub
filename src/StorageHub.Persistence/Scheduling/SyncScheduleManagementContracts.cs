using StorageHub.Agent.Scheduling;
using StorageHub.Domain.Identifiers;

namespace StorageHub.Persistence.Scheduling;

public sealed record SyncScheduleManagementDraft(
    SyncProfileId ProfileId,
    string CronExpression,
    string TimeZoneId,
    TimeSpan MisfireGrace,
    bool QueueOneWhileRunning,
    bool Enabled);

/// <summary>Management-safe schedule state. Ownership and fencing evidence never leaves the store.</summary>
public sealed record SyncScheduleManagementRecord(
    ScheduledSyncJobId ScheduleId,
    SyncProfileId ProfileId,
    string ProfileDisplayName,
    string CronExpression,
    string TimeZoneId,
    TimeSpan MisfireGrace,
    bool QueueOneWhileRunning,
    bool Enabled,
    DateTimeOffset? NextOccurrenceUtc,
    DateTimeOffset? QueuedOccurrenceUtc,
    bool IsBusy,
    string? LastRunOutcome,
    string? LastErrorCode,
    long Revision);

public enum SyncScheduleManagementMutationStatus
{
    Applied,
    AlreadyApplied,
    NotFound,
    RevisionConflict,
    ActiveRun,
    ConstraintConflict
}

public sealed record SyncScheduleManagementMutationResult(
    SyncScheduleManagementMutationStatus Status,
    SyncScheduleManagementRecord? Schedule = null,
    long? ActualRevision = null);

public interface ISyncScheduleManagementRepository
{
    ValueTask<IReadOnlyList<SyncScheduleManagementRecord>> ListAsync(
        bool includeDisabled,
        int maximumCount,
        CancellationToken cancellationToken = default);

    ValueTask<SyncScheduleManagementRecord?> GetAsync(
        ScheduledSyncJobId scheduleId,
        CancellationToken cancellationToken = default);

    ValueTask<SyncScheduleManagementMutationResult> CreateAsync(
        ScheduledSyncJobId scheduleId,
        SyncScheduleManagementDraft draft,
        CancellationToken cancellationToken = default);

    ValueTask<SyncScheduleManagementMutationResult> UpdateAsync(
        ScheduledSyncJobId scheduleId,
        long expectedRevision,
        SyncScheduleManagementDraft draft,
        CancellationToken cancellationToken = default);

    ValueTask<SyncScheduleManagementMutationResult> SetEnabledAsync(
        ScheduledSyncJobId scheduleId,
        long expectedRevision,
        bool enabled,
        CancellationToken cancellationToken = default);

    ValueTask<SyncScheduleManagementMutationResult> DeleteAsync(
        ScheduledSyncJobId scheduleId,
        long expectedRevision,
        CancellationToken cancellationToken = default);
}
