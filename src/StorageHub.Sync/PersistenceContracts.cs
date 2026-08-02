using StorageHub.Domain.Identifiers;

namespace StorageHub.Sync.Persistence;

public static class SyncOutboxEventKinds
{
    public const string PreviewRequested = "sync.preview.requested";
    public const string ApplyRequested = "sync.apply.requested";
}

public sealed record ScheduledSyncPreviewOutboxPayload(
    string SyncScheduleId,
    string SyncProfileId,
    string LeaseId,
    long FencingToken,
    DateTimeOffset ScheduledForUtc);

public sealed record SyncApplyOutboxPayload(
    string SyncRunId,
    string SyncProfileId,
    string OperationPlanId,
    string PlanSha256,
    string ApprovalSha256,
    long ProfileRevision,
    string ProfilePolicySha256);

public enum SyncProfileWriteStatus
{
    Succeeded,
    AlreadyApplied,
    NotFound,
    RevisionConflict,
    ConstraintConflict,
}

public sealed record SyncProfileWriteResult(
    SyncProfileWriteStatus Status,
    SyncProfile? Profile = null,
    long? ActualRevision = null);

public interface ISyncProfileRepository
{
    ValueTask<SyncProfile?> GetAsync(
        SyncProfileId profileId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<SyncProfile>> ListAsync(
        bool includeDisabled = true,
        int maximumCount = 1_000,
        CancellationToken cancellationToken = default);

    ValueTask<SyncProfileWriteResult> CreateAsync(
        SyncProfile profile,
        CancellationToken cancellationToken = default);

    ValueTask<SyncProfileWriteResult> UpdateAsync(
        SyncProfile profile,
        long expectedRevision,
        CancellationToken cancellationToken = default);
}

public enum SyncPersistenceMutationStatus
{
    Applied,
    AlreadyApplied,
    NotFound,
    Conflict,
    StaleLease
}

public sealed record SyncPersistenceResult<T>(SyncPersistenceMutationStatus Status, T? Value)
    where T : class;

public sealed record SyncBaselineSnapshot(
    SyncProfileId ProfileId,
    long Generation,
    long Revision,
    IReadOnlyDictionary<string, SyncBaselineObservation> Items,
    string Sha256Digest,
    DateTimeOffset UpdatedAtUtc);

public sealed record SyncBaselineReplaceRequest(
    SyncProfileId ProfileId,
    long ExpectedRevision,
    long Generation,
    IReadOnlyDictionary<string, SyncBaselineObservation> Items,
    DateTimeOffset UpdatedAtUtc);

public interface ISyncBaselineStore
{
    ValueTask<SyncBaselineSnapshot?> GetAsync(
        SyncProfileId profileId,
        CancellationToken cancellationToken = default);

    ValueTask<SyncPersistenceResult<SyncBaselineSnapshot>> ReplaceAsync(
        SyncBaselineReplaceRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record PersistedSyncPlan(ImmutableSyncPlan Plan);

public interface ISyncPlanStore
{
    ValueTask<PersistedSyncPlan?> GetAsync(
        OperationPlanId planId,
        CancellationToken cancellationToken = default);

    ValueTask<SyncPersistenceResult<PersistedSyncPlan>> PutAsync(
        ImmutableSyncPlan plan,
        CancellationToken cancellationToken = default);
}

public enum SyncConflictState
{
    Unresolved,
    Resolved,
    Dismissed
}

public sealed record SyncConflictRecord(
    Guid ConflictId,
    SyncRunId SyncRunId,
    string RelativePath,
    string ConflictKind,
    SyncConflictState State,
    string SafeDetailsJson,
    DateTimeOffset DetectedAtUtc,
    DateTimeOffset? ResolvedAtUtc,
    string? SafeResolutionJson,
    long Revision);

public sealed record SyncConflictDraft(
    Guid ConflictId,
    SyncRunId SyncRunId,
    string RelativePath,
    string ConflictKind,
    string SafeDetailsJson,
    DateTimeOffset DetectedAtUtc);

public sealed record SyncConflictResolution(
    SyncConflictState State,
    string SafeResolutionJson,
    DateTimeOffset ResolvedAtUtc);

public interface ISyncConflictStore
{
    ValueTask<SyncConflictRecord?> GetAsync(
        Guid conflictId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<SyncConflictRecord>> ListForRunAsync(
        SyncRunId syncRunId,
        SyncConflictState? state = null,
        int maximumCount = 1_000,
        CancellationToken cancellationToken = default);

    ValueTask<SyncPersistenceResult<SyncConflictRecord>> AddAsync(
        SyncConflictDraft draft,
        CancellationToken cancellationToken = default);

    ValueTask<SyncPersistenceResult<SyncConflictRecord>> ResolveAsync(
        Guid conflictId,
        long expectedRevision,
        SyncConflictResolution resolution,
        CancellationToken cancellationToken = default);
}

public sealed record SyncPreviewDraft(
    SyncRunId SyncRunId,
    SyncProfileId ProfileId,
    long ExpectedProfileRevision,
    string ExpectedPolicySha256,
    OperationPlanId PlanId,
    SyncPlanDigest PlanDigest,
    SyncExecutionSnapshots Snapshots,
    string ApprovalChallengeSha256,
    SyncPreviewTrigger Trigger,
    string TriggerIdempotencyKey,
    IReadOnlyList<SyncPlanningConflict> Conflicts,
    DateTimeOffset CreatedAtUtc,
    bool DeletionGuardBlocked);

public sealed record SyncPreviewRecord(
    SyncRunId SyncRunId,
    SyncProfileId ProfileId,
    long Generation,
    SyncRunState State,
    long ProfileRevision,
    string ProfilePolicySha256,
    OperationPlanId PlanId,
    SyncPlanDigest PlanDigest,
    SyncExecutionSnapshots Snapshots,
    string ApprovalChallengeSha256,
    SyncPreviewTrigger Trigger,
    string TriggerIdempotencyKey,
    int ConflictCount,
    bool ApprovedForExecution,
    DateTimeOffset? ApprovedAtUtc,
    Guid? DispatchEventId,
    DateTimeOffset CreatedAtUtc);

public sealed record SyncApplyDispatchRequest(
    SyncRunId SyncRunId,
    long ExpectedRunRevision,
    long ExpectedProfileRevision,
    string ExpectedPolicySha256,
    string ApprovalSha256,
    Guid DispatchEventId,
    DateTimeOffset ApprovedAtUtc);

public interface ISyncRunStore
{
    ValueTask<SyncPreviewRecord?> GetAsync(
        SyncRunId syncRunId,
        CancellationToken cancellationToken = default);

    ValueTask<SyncPreviewRecord?> GetByTriggerAsync(
        SyncProfileId profileId,
        string triggerIdempotencyKey,
        CancellationToken cancellationToken = default);

    ValueTask<SyncPersistenceResult<SyncPreviewRecord>> CreatePreviewAsync(
        SyncPreviewDraft draft,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically verifies the still-enabled profile policy and run revision, transitions the run
    /// to Ready, and enqueues the apply request. It never calls a provider.
    /// </summary>
    ValueTask<SyncPersistenceResult<SyncPreviewRecord>> ApproveAndDispatchAsync(
        SyncApplyDispatchRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ScheduledSyncDispatchRequest(
    Guid JobId,
    SyncProfileId ProfileId,
    Guid LeaseId,
    long FencingToken,
    DateTimeOffset ScheduledForUtc,
    DateTimeOffset LeaseAcquiredAtUtc,
    DateTimeOffset LeaseExpiresAtUtc);

public interface IScheduledSyncDispatchStore
{
    /// <summary>
    /// Atomically checks the exact unexpired scheduler lease and fencing token before inserting a
    /// durable preview request. No provider operation is performed on this path.
    /// </summary>
    ValueTask<SyncPersistenceMutationStatus> TryDispatchAsync(
        ScheduledSyncDispatchRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record OutboxEventDraft(
    Guid EventId,
    string EventKind,
    string AggregateId,
    long SequenceNumber,
    string SafePayloadJson,
    DateTimeOffset CreatedAtUtc);

public sealed record OutboxEventRecord(
    Guid EventId,
    string EventKind,
    string AggregateId,
    long SequenceNumber,
    string SafePayloadJson,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DispatchedAtUtc,
    DateTimeOffset? DeadLetteredAtUtc,
    int AttemptCount,
    long DeliveryRevision,
    DateTimeOffset? NextAttemptAtUtc,
    string? LastErrorCode,
    string? LastErrorSummary);

public sealed record OutboxDeliveryLease(
    OutboxEventRecord Event,
    Guid ClaimId,
    string OwnerId,
    long FencingToken,
    DateTimeOffset AcquiredAtUtc,
    DateTimeOffset ExpiresAtUtc);

public interface IReliableOutboxStore
{
    ValueTask<OutboxEventRecord?> GetAsync(
        Guid eventId,
        CancellationToken cancellationToken = default);

    ValueTask<SyncPersistenceResult<OutboxEventRecord>> EnqueueAsync(
        OutboxEventDraft draft,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<OutboxDeliveryLease>> ClaimPendingAsync(
        string ownerId,
        int maximumCount,
        DateTimeOffset observedAtUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims only events whose kind is explicitly listed. A dedicated consumer must use this
    /// method so it cannot temporarily hide work owned by another outbox consumer.
    /// </summary>
    ValueTask<IReadOnlyList<OutboxDeliveryLease>> ClaimPendingByKindsAsync(
        string ownerId,
        IReadOnlyCollection<string> eventKinds,
        int maximumCount,
        DateTimeOffset observedAtUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>Renews the exact claim without changing its fencing token.</summary>
    ValueTask<SyncPersistenceResult<OutboxDeliveryLease>> RenewAsync(
        OutboxDeliveryLease lease,
        DateTimeOffset renewedAtUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    ValueTask<SyncPersistenceMutationStatus> CompleteAsync(
        OutboxDeliveryLease lease,
        DateTimeOffset dispatchedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<SyncPersistenceMutationStatus> FailAsync(
        OutboxDeliveryLease lease,
        DateTimeOffset failedAtUtc,
        DateTimeOffset nextAttemptAtUtc,
        string errorCode,
        string safeErrorSummary,
        bool deadLetter,
        CancellationToken cancellationToken = default);
}

public enum SyncExecutionBeginStatus
{
    Acquired,
    AlreadyCompleted,
    ReconciliationRequired,
    NotFound,
    Conflict,
    StaleLease,
}

public sealed record SyncExecutionContext(
    SyncPreviewRecord Preview,
    SyncBaselineSnapshot Baseline,
    bool ProviderMutationMayHaveStarted);

public sealed record SyncExecutionBeginResult(
    SyncExecutionBeginStatus Status,
    SyncExecutionContext? Context = null);

/// <summary>
/// Complete immutable apply binding copied from the claimed outbox event. Persistence verifies
/// every field against the run, profile, plan, approval, baseline, and live outbox claim in one
/// immediate transaction.
/// </summary>
public sealed record SyncExecutionBeginRequest(
    OutboxDeliveryLease Lease,
    SyncRunId SyncRunId,
    SyncProfileId ProfileId,
    OperationPlanId PlanId,
    SyncPlanDigest PlanDigest,
    long ExpectedRunRevision,
    long ExpectedProfileRevision,
    string ExpectedProfilePolicySha256,
    string ApprovalSha256,
    DateTimeOffset TransitionedAtUtc);

public sealed record SyncExecutionTransitionRequest(
    OutboxDeliveryLease Lease,
    SyncRunId SyncRunId,
    long ExpectedRunRevision,
    SyncRunPhase ExpectedPhase,
    SyncRunPhase NextPhase,
    DateTimeOffset TransitionedAtUtc,
    SyncStatusCode StatusCode = SyncStatusCode.None,
    string? SafeErrorSummary = null);

public sealed record SyncExecutionBaselineCommitRequest(
    OutboxDeliveryLease Lease,
    SyncRunId SyncRunId,
    long ExpectedRunRevision,
    SyncProfileId ProfileId,
    long ExpectedProfileRevision,
    string ExpectedProfilePolicySha256,
    long ExpectedBaselineGeneration,
    long ExpectedBaselineRevision,
    long NewBaselineGeneration,
    IReadOnlyDictionary<string, SyncBaselineObservation> Items,
    DateTimeOffset CommittedAtUtc);

/// <summary>
/// Durable apply state. All methods reject a superseded or expired outbox fence. The mutation
/// marker is armed immediately before provider I/O and is intentionally conservative.
/// </summary>
public interface ISyncExecutionStore
{
    ValueTask<SyncExecutionBeginResult> BeginAsync(
        SyncExecutionBeginRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<SyncPersistenceMutationStatus> ArmProviderMutationAsync(
        OutboxDeliveryLease lease,
        SyncRunId syncRunId,
        long expectedRunRevision,
        DateTimeOffset armedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<SyncPersistenceResult<SyncPreviewRecord>> TransitionAsync(
        SyncExecutionTransitionRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<SyncPersistenceResult<SyncPreviewRecord>> CommitBaselineAndCompleteAsync(
        SyncExecutionBaselineCommitRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record AuditEventDraft(
    Guid EventId,
    string EventKind,
    string? ActorId,
    string? SubjectType,
    string? SubjectId,
    string SafePayloadJson,
    DateTimeOffset OccurredAtUtc,
    string? CorrelationId,
    string IdempotencyKey);

public sealed record AuditEventRecord(
    Guid EventId,
    long SequenceNumber,
    string EventKind,
    string? ActorId,
    string? SubjectType,
    string? SubjectId,
    string SafePayloadJson,
    DateTimeOffset OccurredAtUtc,
    string? CorrelationId,
    string IdempotencyKey);

public sealed record AuditAppendRequest(AuditEventDraft AuditEvent, OutboxEventDraft? OutboxEvent);

public interface IAuditEventStore
{
    ValueTask<AuditEventRecord?> GetAsync(
        Guid eventId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AuditEventRecord>> ReadAfterAsync(
        long sequenceNumber,
        int maximumCount,
        CancellationToken cancellationToken = default);

    ValueTask<SyncPersistenceResult<AuditEventRecord>> AppendAsync(
        AuditAppendRequest request,
        CancellationToken cancellationToken = default);
}
