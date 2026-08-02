using System.Globalization;
using StorageHub.Domain.Identifiers;

namespace StorageHub.Agent.Scheduling;

public readonly record struct ScheduledSyncJobId
{
    public ScheduledSyncJobId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A scheduled job ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public bool IsEmpty => Value == Guid.Empty;

    public static ScheduledSyncJobId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

/// <summary>An immutable durable scheduler row read at one store revision.</summary>
public sealed record ScheduledSyncJobSnapshot
{
    public ScheduledSyncJobSnapshot(
        ScheduledSyncJobId jobId,
        SyncProfileId profileId,
        CronScheduleDefinition schedule,
        bool enabled,
        bool queueOneWhileRunning,
        DateTimeOffset? nextOccurrenceUtc,
        DateTimeOffset? queuedOccurrenceUtc,
        long revision)
    {
        if (jobId.IsEmpty)
        {
            throw new ArgumentException("A scheduled job ID is required.", nameof(jobId));
        }

        if (profileId.IsEmpty)
        {
            throw new ArgumentException("A sync profile ID is required.", nameof(profileId));
        }

        ArgumentNullException.ThrowIfNull(schedule);
        ValidateUtc(nextOccurrenceUtc, nameof(nextOccurrenceUtc));
        ValidateUtc(queuedOccurrenceUtc, nameof(queuedOccurrenceUtc));
        ArgumentOutOfRangeException.ThrowIfNegative(revision);

        JobId = jobId;
        ProfileId = profileId;
        Schedule = schedule;
        Enabled = enabled;
        QueueOneWhileRunning = queueOneWhileRunning;
        NextOccurrenceUtc = nextOccurrenceUtc;
        QueuedOccurrenceUtc = queuedOccurrenceUtc;
        Revision = revision;
    }

    public ScheduledSyncJobId JobId { get; }

    public SyncProfileId ProfileId { get; }

    public CronScheduleDefinition Schedule { get; }

    public bool Enabled { get; }

    public bool QueueOneWhileRunning { get; }

    public DateTimeOffset? NextOccurrenceUtc { get; }

    /// <summary>
    /// The single durable coalesced occurrence retained while the profile was already running.
    /// It takes priority over the normal next occurrence and is not subject to misfire expiry.
    /// </summary>
    public DateTimeOffset? QueuedOccurrenceUtc { get; }

    public long Revision { get; }

    public DateTimeOffset? DueOccurrenceUtc => QueuedOccurrenceUtc ?? NextOccurrenceUtc;

    private static void ValidateUtc(DateTimeOffset? value, string parameterName)
    {
        if (value is { } occurrence && occurrence.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Scheduled occurrence times must be UTC.", parameterName);
        }
    }
}

public sealed record ScheduledSyncLeaseRequest
{
    public ScheduledSyncLeaseRequest(
        ScheduledSyncJobId jobId,
        SyncProfileId profileId,
        long expectedRevision,
        DateTimeOffset scheduledForUtc,
        bool isQueuedOccurrence,
        DateTimeOffset observedAtUtc,
        DateTimeOffset? nextOccurrenceUtc,
        TimeSpan leaseDuration)
    {
        if (jobId.IsEmpty)
        {
            throw new ArgumentException("A scheduled job ID is required.", nameof(jobId));
        }

        if (profileId.IsEmpty)
        {
            throw new ArgumentException("A sync profile ID is required.", nameof(profileId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        RequireUtc(scheduledForUtc, nameof(scheduledForUtc));
        RequireUtc(observedAtUtc, nameof(observedAtUtc));
        if (nextOccurrenceUtc is { } next)
        {
            RequireUtc(next, nameof(nextOccurrenceUtc));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);

        JobId = jobId;
        ProfileId = profileId;
        ExpectedRevision = expectedRevision;
        ScheduledForUtc = scheduledForUtc;
        IsQueuedOccurrence = isQueuedOccurrence;
        ObservedAtUtc = observedAtUtc;
        NextOccurrenceUtc = nextOccurrenceUtc;
        LeaseDuration = leaseDuration;
    }

    public ScheduledSyncJobId JobId { get; }

    public SyncProfileId ProfileId { get; }

    public long ExpectedRevision { get; }

    public DateTimeOffset ScheduledForUtc { get; }

    public bool IsQueuedOccurrence { get; }

    public DateTimeOffset ObservedAtUtc { get; }

    public DateTimeOffset? NextOccurrenceUtc { get; }

    public TimeSpan LeaseDuration { get; }

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Lease times must be UTC.", parameterName);
        }
    }
}

public sealed record ScheduledSyncJobLease
{
    public ScheduledSyncJobLease(
        Guid leaseId,
        ScheduledSyncJobId jobId,
        SyncProfileId profileId,
        DateTimeOffset scheduledForUtc,
        DateTimeOffset acquiredAtUtc,
        DateTimeOffset expiresAtUtc,
        long fencingToken)
    {
        if (leaseId == Guid.Empty)
        {
            throw new ArgumentException("A lease ID is required.", nameof(leaseId));
        }

        if (jobId.IsEmpty)
        {
            throw new ArgumentException("A scheduled job ID is required.", nameof(jobId));
        }

        if (profileId.IsEmpty)
        {
            throw new ArgumentException("A sync profile ID is required.", nameof(profileId));
        }

        RequireUtc(scheduledForUtc, nameof(scheduledForUtc));
        RequireUtc(acquiredAtUtc, nameof(acquiredAtUtc));
        RequireUtc(expiresAtUtc, nameof(expiresAtUtc));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiresAtUtc, acquiredAtUtc);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fencingToken);
        LeaseId = leaseId;
        JobId = jobId;
        ProfileId = profileId;
        ScheduledForUtc = scheduledForUtc;
        AcquiredAtUtc = acquiredAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        FencingToken = fencingToken;
    }

    public Guid LeaseId { get; }

    public ScheduledSyncJobId JobId { get; }

    public SyncProfileId ProfileId { get; }

    public DateTimeOffset ScheduledForUtc { get; }

    public DateTimeOffset AcquiredAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    /// <summary>A monotonically increasing token used by the store to reject stale completion writes.</summary>
    public long FencingToken { get; }

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Lease times must be UTC.", parameterName);
        }
    }
}

public sealed record ScheduledSyncLeaseRenewal
{
    public ScheduledSyncLeaseRenewal(
        ScheduledSyncJobLease lease,
        DateTimeOffset renewedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        Lease = lease ?? throw new ArgumentNullException(nameof(lease));
        if (renewedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Lease renewal times must be UTC.", nameof(renewedAtUtc));
        }

        if (expiresAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Lease renewal times must be UTC.", nameof(expiresAtUtc));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiresAtUtc, renewedAtUtc);
        RenewedAtUtc = renewedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public ScheduledSyncJobLease Lease { get; }

    public DateTimeOffset RenewedAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }
}

public enum ScheduledSyncLeaseAcquisitionStatus
{
    None = 0,
    Acquired = 1,
    ProfileBusy = 2,
    StaleSnapshot = 3,
    Disabled = 4,
}

public sealed record ScheduledSyncLeaseAcquisition
{
    private ScheduledSyncLeaseAcquisition(
        ScheduledSyncLeaseAcquisitionStatus status,
        ScheduledSyncJobLease? lease)
    {
        Status = status;
        Lease = lease;
    }

    public ScheduledSyncLeaseAcquisitionStatus Status { get; }

    public ScheduledSyncJobLease? Lease { get; }

    public static ScheduledSyncLeaseAcquisition Acquired(ScheduledSyncJobLease lease) =>
        new(ScheduledSyncLeaseAcquisitionStatus.Acquired, lease ?? throw new ArgumentNullException(nameof(lease)));

    public static ScheduledSyncLeaseAcquisition ProfileBusy() =>
        new(ScheduledSyncLeaseAcquisitionStatus.ProfileBusy, null);

    public static ScheduledSyncLeaseAcquisition StaleSnapshot() =>
        new(ScheduledSyncLeaseAcquisitionStatus.StaleSnapshot, null);

    public static ScheduledSyncLeaseAcquisition Disabled() =>
        new(ScheduledSyncLeaseAcquisitionStatus.Disabled, null);
}

public enum ScheduledOccurrenceDispositionKind
{
    None = 0,
    ExpiredMisfireSkipped = 1,
    OverlapSkipped = 2,
    OverlapQueued = 3,
}

public sealed record ScheduledOccurrenceDisposition
{
    public ScheduledOccurrenceDisposition(
        ScheduledSyncJobId jobId,
        SyncProfileId profileId,
        long expectedRevision,
        DateTimeOffset scheduledForUtc,
        DateTimeOffset observedAtUtc,
        DateTimeOffset? nextOccurrenceUtc,
        ScheduledOccurrenceDispositionKind disposition)
    {
        if (jobId.IsEmpty)
        {
            throw new ArgumentException("A scheduled job ID is required.", nameof(jobId));
        }

        if (profileId.IsEmpty)
        {
            throw new ArgumentException("A sync profile ID is required.", nameof(profileId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        RequireUtc(scheduledForUtc, nameof(scheduledForUtc));
        RequireUtc(observedAtUtc, nameof(observedAtUtc));
        if (nextOccurrenceUtc is { } next)
        {
            RequireUtc(next, nameof(nextOccurrenceUtc));
        }

        if (disposition == ScheduledOccurrenceDispositionKind.None || !Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        JobId = jobId;
        ProfileId = profileId;
        ExpectedRevision = expectedRevision;
        ScheduledForUtc = scheduledForUtc;
        ObservedAtUtc = observedAtUtc;
        NextOccurrenceUtc = nextOccurrenceUtc;
        Disposition = disposition;
    }

    public ScheduledSyncJobId JobId { get; }

    public SyncProfileId ProfileId { get; }

    public long ExpectedRevision { get; }

    public DateTimeOffset ScheduledForUtc { get; }

    public DateTimeOffset ObservedAtUtc { get; }

    public DateTimeOffset? NextOccurrenceUtc { get; }

    public ScheduledOccurrenceDispositionKind Disposition { get; }

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Occurrence times must be UTC.", parameterName);
        }
    }
}

public enum ScheduledSyncRunOutcome
{
    None = 0,
    Completed = 1,
    Failed = 2,
    Cancelled = 3,
}

public sealed record ScheduledSyncJobRunResult
{
    private ScheduledSyncJobRunResult(
        ScheduledSyncRunOutcome outcome,
        string? code,
        string? message,
        bool isTransient)
    {
        Outcome = outcome;
        Code = code;
        Message = message;
        IsTransient = isTransient;
    }

    public ScheduledSyncRunOutcome Outcome { get; }

    public string? Code { get; }

    public string? Message { get; }

    public bool IsTransient { get; }

    public static ScheduledSyncJobRunResult Completed() =>
        new(ScheduledSyncRunOutcome.Completed, null, null, false);

    public static ScheduledSyncJobRunResult Failed(
        string code,
        string message,
        bool isTransient = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new ScheduledSyncJobRunResult(
            ScheduledSyncRunOutcome.Failed,
            code,
            message,
            isTransient);
    }

    public static ScheduledSyncJobRunResult Cancelled() => new(
        ScheduledSyncRunOutcome.Cancelled,
        "scheduler.run.cancelled",
        "The scheduled sync run was cancelled.",
        false);
}

public sealed record ScheduledSyncJobExecution(
    ScheduledSyncJobSnapshot Job,
    ScheduledSyncJobLease Lease);

public sealed record ScheduledSyncJobCompletion
{
    public ScheduledSyncJobCompletion(
        ScheduledSyncJobLease lease,
        ScheduledSyncJobRunResult result,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc)
    {
        Lease = lease ?? throw new ArgumentNullException(nameof(lease));
        Result = result ?? throw new ArgumentNullException(nameof(result));
        if (startedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Completion times must be UTC.", nameof(startedAtUtc));
        }

        if (completedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Completion times must be UTC.", nameof(completedAtUtc));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(completedAtUtc, startedAtUtc);

        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
    }

    public ScheduledSyncJobLease Lease { get; }

    public ScheduledSyncJobRunResult Result { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset CompletedAtUtc { get; }
}

/// <summary>
/// Persistence seam for the scheduler. Lease acquisition must atomically compare the snapshot
/// revision and occurrence, verify the job is enabled, reject any unexpired lease for the same
/// sync profile, issue a monotonically fenced lease, and advance/clear the claimed occurrence.
/// Completion writes must be idempotent and reject stale fencing tokens.
/// </summary>
public interface IScheduledSyncJobStore
{
    ValueTask<IReadOnlyList<ScheduledSyncJobSnapshot>> GetJobsAsync(
        CancellationToken cancellationToken = default);

    ValueTask<ScheduledSyncLeaseAcquisition> TryAcquireLeaseAsync(
        ScheduledSyncLeaseRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically renews the still-current lease identified by its lease ID and fencing token.
    /// Returning false means ownership was lost and the runner must stop cooperatively.
    /// </summary>
    ValueTask<bool> TryRenewLeaseAsync(
        ScheduledSyncLeaseRenewal renewal,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically compares the job revision/current occurrence and advances it. For
    /// <see cref="ScheduledOccurrenceDispositionKind.OverlapQueued"/>, the store also retains at
    /// most one durable queued occurrence.
    /// </summary>
    ValueTask<bool> TryRecordOccurrenceDispositionAsync(
        ScheduledOccurrenceDisposition disposition,
        CancellationToken cancellationToken = default);

    ValueTask RecordCompletionAsync(
        ScheduledSyncJobCompletion completion,
        CancellationToken cancellationToken = default);
}

public interface IScheduledSyncJobRunner
{
    ValueTask<ScheduledSyncJobRunResult> RunAsync(
        ScheduledSyncJobExecution execution,
        CancellationToken cancellationToken);
}
