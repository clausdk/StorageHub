using StorageHub.Domain.Identifiers;

namespace StorageHub.Transfers;

/// <summary>
/// Storage-neutral contract for the durable transfer queue. Implementations must make claims,
/// state transitions, and checkpoint compare-and-swap operations atomic.
/// </summary>
public interface ITransferJobStore
{
    ValueTask<bool> TryEnqueueAsync(
        TransferIntent intent,
        int priority = 0,
        CancellationToken cancellationToken = default);

    ValueTask<DurableTransferJob?> FindAsync(
        TransferJobId transferJobId,
        CancellationToken cancellationToken = default);

    ValueTask<TransferJobClaim?> TryClaimNextAsync(
        TransferClaimRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<TransferStoreResult<TransferJobLease>> TryRenewLeaseAsync(
        TransferLeaseRenewal renewal,
        CancellationToken cancellationToken = default);

    ValueTask<TransferStoreResult<DurableTransferJob>> TryTransitionAsync(
        TransferStateTransitionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies an optimistic transition to a job that has no execution lease. This is used for
    /// explicit queue controls and recovery decisions; it can never enter an execution-owned state.
    /// </summary>
    ValueTask<TransferStoreResult<DurableTransferJob>> TryTransitionControlStateAsync(
        TransferControlStateTransitionRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<PersistedTransferCheckpoint?> FindCheckpointAsync(
        TransferJobId transferJobId,
        CancellationToken cancellationToken = default);

    ValueTask<TransferStoreResult<PersistedTransferCheckpoint>> TrySaveCheckpointAsync(
        TransferCheckpointWriteRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<TransferStoreMutationStatus> TryClearCheckpointAsync(
        TransferCheckpointClearRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Converts in-flight jobs whose owner lease is absent or expired to Interrupted. This is
    /// deliberately not an automatic retry: a recovery coordinator must validate the checkpoint
    /// and remote side effects before deciding whether to resume or restart.
    /// </summary>
    ValueTask<int> RecoverInterruptedAsync(
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default);
}

public sealed record DurableTransferJob
{
    public DurableTransferJob(
        TransferIntent intent,
        TransferStateSnapshot state,
        int priority,
        DateTimeOffset? retryAvailableAtUtc,
        TransferJobLease? activeLease,
        TransferSafeError? lastError)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(state);
        if (intent.TransferJobId != state.TransferJobId)
        {
            throw new ArgumentException("The intent and state must identify the same transfer.", nameof(state));
        }

        ValidateUtc(retryAvailableAtUtc, nameof(retryAvailableAtUtc));
        if ((state.State == TransferState.Retrying) != retryAvailableAtUtc.HasValue)
        {
            throw new ArgumentException(
                "Retry availability must be present exactly while a transfer is retrying.",
                nameof(retryAvailableAtUtc));
        }

        if (activeLease is not null && activeLease.TransferJobId != intent.TransferJobId)
        {
            throw new ArgumentException("The active lease belongs to another transfer.", nameof(activeLease));
        }

        Intent = intent;
        State = state;
        Priority = priority;
        RetryAvailableAtUtc = retryAvailableAtUtc;
        ActiveLease = activeLease;
        LastError = lastError;
    }

    public TransferIntent Intent { get; }

    public TransferStateSnapshot State { get; }

    public int Priority { get; }

    public DateTimeOffset? RetryAvailableAtUtc { get; }

    public TransferJobLease? ActiveLease { get; }

    public TransferSafeError? LastError { get; }

    private static void ValidateUtc(DateTimeOffset? value, string parameterName)
    {
        if (value.HasValue && value.Value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The timestamp must be UTC.", parameterName);
        }
    }
}

public sealed record TransferJobLease
{
    public TransferJobLease(
        TransferJobId transferJobId,
        string ownerId,
        long fencingToken,
        int attempt,
        DateTimeOffset acquiredAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        if (transferJobId.IsEmpty)
        {
            throw new ArgumentException("A transfer job ID is required.", nameof(transferJobId));
        }

        ValidateOwner(ownerId, nameof(ownerId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fencingToken);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);
        ValidateUtc(acquiredAtUtc, nameof(acquiredAtUtc));
        ValidateUtc(expiresAtUtc, nameof(expiresAtUtc));
        if (expiresAtUtc <= acquiredAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAtUtc), "A lease must expire after acquisition.");
        }

        TransferJobId = transferJobId;
        OwnerId = ownerId;
        FencingToken = fencingToken;
        Attempt = attempt;
        AcquiredAtUtc = acquiredAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public TransferJobId TransferJobId { get; }

    /// <summary>A random, process-instance owner identifier; never a user credential.</summary>
    public string OwnerId { get; }

    /// <summary>Monotonically increasing token that fences writes from an earlier owner.</summary>
    public long FencingToken { get; }

    public int Attempt { get; }

    public DateTimeOffset AcquiredAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    internal static void ValidateOwner(string ownerId, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId, parameterName);
        if (ownerId.Length > 256 || ownerId.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The owner ID must be at most 256 characters and cannot contain control characters.",
                parameterName);
        }
    }

    internal static void ValidateUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The timestamp must be UTC.", parameterName);
        }
    }
}

public sealed record TransferJobClaim
{
    public TransferJobClaim(DurableTransferJob job, TransferJobLease lease)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(lease);
        if (job.Intent.TransferJobId != lease.TransferJobId || job.ActiveLease != lease)
        {
            throw new ArgumentException("The claimed job and lease must describe the same ownership epoch.");
        }

        Job = job;
        Lease = lease;
    }

    public DurableTransferJob Job { get; }
    public TransferJobLease Lease { get; }
}

public sealed record TransferClaimRequest
{
    public TransferClaimRequest(string ownerId, DateTimeOffset observedAtUtc, TimeSpan leaseDuration)
    {
        TransferJobLease.ValidateOwner(ownerId, nameof(ownerId));
        TransferJobLease.ValidateUtc(observedAtUtc, nameof(observedAtUtc));
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseDuration),
                "A transfer lease must be between zero and 24 hours.");
        }

        OwnerId = ownerId;
        ObservedAtUtc = observedAtUtc;
        LeaseDuration = leaseDuration;
    }

    public string OwnerId { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public TimeSpan LeaseDuration { get; }
}

public sealed record TransferLeaseRenewal
{
    public TransferLeaseRenewal(
        TransferJobLease lease,
        DateTimeOffset renewedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(lease);
        TransferJobLease.ValidateUtc(renewedAtUtc, nameof(renewedAtUtc));
        TransferJobLease.ValidateUtc(expiresAtUtc, nameof(expiresAtUtc));
        if (expiresAtUtc <= renewedAtUtc || expiresAtUtc <= lease.ExpiresAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAtUtc),
                "A renewed expiry must be later than both the renewal time and prior expiry.");
        }

        if (renewedAtUtc < lease.AcquiredAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(renewedAtUtc),
                "A lease cannot be renewed before it was acquired.");
        }

        if (expiresAtUtc - renewedAtUtc > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAtUtc), "A lease cannot exceed 24 hours.");
        }

        Lease = lease;
        RenewedAtUtc = renewedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public TransferJobLease Lease { get; }
    public DateTimeOffset RenewedAtUtc { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
}

public sealed record TransferStateTransitionRequest
{
    public TransferStateTransitionRequest(
        TransferJobLease lease,
        long expectedRevision,
        TransferState nextState,
        DateTimeOffset transitionedAtUtc,
        TransferStatusCode statusCode = TransferStatusCode.None,
        TransferSafeError? error = null,
        DateTimeOffset? retryAvailableAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        TransferJobLease.ValidateUtc(transitionedAtUtc, nameof(transitionedAtUtc));
        if (!Enum.IsDefined(nextState))
        {
            throw new ArgumentOutOfRangeException(nameof(nextState));
        }

        if (!Enum.IsDefined(statusCode))
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        }

        if (retryAvailableAtUtc is { } retry)
        {
            TransferJobLease.ValidateUtc(retry, nameof(retryAvailableAtUtc));
            if (retry < transitionedAtUtc)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(retryAvailableAtUtc),
                    "Retry availability cannot precede the transition.");
            }
        }

        if ((nextState == TransferState.Retrying) != retryAvailableAtUtc.HasValue)
        {
            throw new ArgumentException(
                "Retry availability must be provided exactly when entering Retrying.",
                nameof(retryAvailableAtUtc));
        }

        if (error is not null &&
            nextState != TransferState.Retrying &&
            !TransferStateMachine.RequiresStatus(nextState))
        {
            throw new ArgumentException(
                "A durable error can only accompany retry or recovery/failure states.",
                nameof(error));
        }

        Lease = lease;
        ExpectedRevision = expectedRevision;
        NextState = nextState;
        TransitionedAtUtc = transitionedAtUtc;
        StatusCode = statusCode;
        Error = error;
        RetryAvailableAtUtc = retryAvailableAtUtc;
    }

    public TransferJobLease Lease { get; }
    public long ExpectedRevision { get; }
    public TransferState NextState { get; }
    public DateTimeOffset TransitionedAtUtc { get; }
    public TransferStatusCode StatusCode { get; }
    public TransferSafeError? Error { get; }
    public DateTimeOffset? RetryAvailableAtUtc { get; }
}

public sealed record TransferControlStateTransitionRequest
{
    public TransferControlStateTransitionRequest(
        TransferJobId transferJobId,
        long expectedRevision,
        TransferState nextState,
        DateTimeOffset transitionedAtUtc,
        TransferStatusCode statusCode = TransferStatusCode.None,
        TransferSafeError? error = null)
    {
        if (transferJobId.IsEmpty)
        {
            throw new ArgumentException("A transfer job ID is required.", nameof(transferJobId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        TransferJobLease.ValidateUtc(transitionedAtUtc, nameof(transitionedAtUtc));
        if (!Enum.IsDefined(nextState))
        {
            throw new ArgumentOutOfRangeException(nameof(nextState));
        }

        if (!Enum.IsDefined(statusCode))
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        }

        if (IsExecutionOwnedState(nextState))
        {
            throw new ArgumentException(
                "A control transition cannot enter an execution-owned state.",
                nameof(nextState));
        }

        if (error is not null && !TransferStateMachine.RequiresStatus(nextState))
        {
            throw new ArgumentException(
                "A durable error can only accompany a recovery or failure state.",
                nameof(error));
        }

        TransferJobId = transferJobId;
        ExpectedRevision = expectedRevision;
        NextState = nextState;
        TransitionedAtUtc = transitionedAtUtc;
        StatusCode = statusCode;
        Error = error;
    }

    public TransferJobId TransferJobId { get; }
    public long ExpectedRevision { get; }
    public TransferState NextState { get; }
    public DateTimeOffset TransitionedAtUtc { get; }
    public TransferStatusCode StatusCode { get; }
    public TransferSafeError? Error { get; }

    private static bool IsExecutionOwnedState(TransferState state) => state is
        TransferState.Preparing or
        TransferState.Connecting or
        TransferState.Transferring or
        TransferState.Verifying or
        TransferState.Finalizing or
        TransferState.CleanupPending;
}

public sealed record TransferSafeError
{
    public TransferSafeError(string code, string summary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        if (code.Length > 128 || code.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
        {
            throw new ArgumentException("The error code contains unsupported characters.", nameof(code));
        }

        if (summary.Length > 1_024 || summary.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The safe error summary must be at most 1,024 characters and contain no control characters.",
                nameof(summary));
        }

        Code = code;
        Summary = summary;
    }

    public string Code { get; }
    public string Summary { get; }
}

public sealed record PersistedTransferCheckpoint
{
    public PersistedTransferCheckpoint(long version, TransferCheckpoint checkpoint)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        ArgumentNullException.ThrowIfNull(checkpoint);
        Version = version;
        Checkpoint = checkpoint;
    }

    public long Version { get; }
    public TransferCheckpoint Checkpoint { get; }
}

public sealed record TransferCheckpointWriteRequest
{
    public TransferCheckpointWriteRequest(
        TransferJobLease lease,
        TransferCheckpoint checkpoint,
        long? expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.TransferJobId != lease.TransferJobId || checkpoint.Attempt != lease.Attempt)
        {
            throw new ArgumentException(
                "A checkpoint must belong to the currently leased transfer attempt.",
                nameof(checkpoint));
        }

        if (expectedVersion is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        }

        Lease = lease;
        Checkpoint = checkpoint;
        ExpectedVersion = expectedVersion;
    }

    public TransferJobLease Lease { get; }
    public TransferCheckpoint Checkpoint { get; }

    /// <summary>Null means create-only; a value means compare-and-swap update.</summary>
    public long? ExpectedVersion { get; }
}

public sealed record TransferCheckpointClearRequest
{
    public TransferCheckpointClearRequest(TransferJobLease lease, long expectedVersion, DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedVersion);
        TransferJobLease.ValidateUtc(observedAtUtc, nameof(observedAtUtc));
        Lease = lease;
        ExpectedVersion = expectedVersion;
        ObservedAtUtc = observedAtUtc;
    }

    public TransferJobLease Lease { get; }
    public long ExpectedVersion { get; }
    public DateTimeOffset ObservedAtUtc { get; }
}

public enum TransferStoreMutationStatus
{
    Applied = 0,
    NotFound = 1,
    Conflict = 2,
    LeaseLost = 3,
}

public sealed record TransferStoreResult<T> where T : class
{
    public TransferStoreResult(TransferStoreMutationStatus status, T? value)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if ((status == TransferStoreMutationStatus.Applied) != (value is not null))
        {
            throw new ArgumentException("An applied mutation must contain its resulting value.", nameof(value));
        }

        Status = status;
        Value = value;
    }

    public TransferStoreMutationStatus Status { get; }
    public T? Value { get; }
}

/// <summary>Bounded, storage-neutral read model for queue/status IPC and administration.</summary>
public interface ITransferQueueQueryStore
{
    ValueTask<TransferQueuePage> ListAsync(
        TransferQueueQuery query,
        CancellationToken cancellationToken = default);
}

public sealed record TransferQueueQuery
{
    public TransferQueueQuery(
        IEnumerable<TransferState> states,
        int pageSize,
        TransferQueueCursor? cursor = null)
    {
        ArgumentNullException.ThrowIfNull(states);
        var stateArray = states.ToArray();
        if (stateArray.Length is < 1 or > 100 ||
            stateArray.Any(state => !Enum.IsDefined(state)) ||
            stateArray.Distinct().Count() != stateArray.Length)
        {
            throw new ArgumentException(
                "A queue query requires between 1 and 100 distinct valid states.",
                nameof(states));
        }

        if (pageSize is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Queue page size must be between 1 and 100.");
        }

        States = stateArray;
        PageSize = pageSize;
        Cursor = cursor;
    }

    public IReadOnlyList<TransferState> States { get; }
    public int PageSize { get; }
    public TransferQueueCursor? Cursor { get; }
}

public sealed record TransferQueueCursor
{
    public TransferQueueCursor(DateTimeOffset transitionedAtUtc, TransferJobId transferJobId)
    {
        TransferJobLease.ValidateUtc(transitionedAtUtc, nameof(transitionedAtUtc));
        if (transferJobId.IsEmpty)
        {
            throw new ArgumentException("A transfer job ID is required.", nameof(transferJobId));
        }

        TransitionedAtUtc = transitionedAtUtc;
        TransferJobId = transferJobId;
    }

    public DateTimeOffset TransitionedAtUtc { get; }
    public TransferJobId TransferJobId { get; }
}

public sealed record TransferQueuePage
{
    public TransferQueuePage(IReadOnlyList<DurableTransferJob> jobs, TransferQueueCursor? continuation)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        if (jobs.Any(job => job is null))
        {
            throw new ArgumentException("Queue pages cannot contain null jobs.", nameof(jobs));
        }

        Jobs = jobs;
        Continuation = continuation;
    }

    public IReadOnlyList<DurableTransferJob> Jobs { get; }
    public TransferQueueCursor? Continuation { get; }
}
