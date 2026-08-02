using System.Text.Json.Serialization;

namespace StorageHub.Contracts.Ipc;

public static class TransferQueueIpcContract
{
    public const int CurrentVersion = 1;

    public static bool IsSupported(int version) => version == CurrentVersion;
}

public static class TransferQueueIpcLimits
{
    public const int MaximumPageSize = 50;
    public const int MaximumStateFilters = 17;
    public const int MaximumRelativePathLength = 4_096;
    public const int MaximumOpaqueIdentityLength = 8_192;
    public const int MaximumContinuationTokenLength = 256;
    public const int MinimumPriority = -10_000;
    public const int MaximumPriority = 10_000;
}

public static class TransferQueueIpcMessageTypes
{
    public const string EnqueueRequest = "transfer.enqueue.request";
    public const string EnqueueResponse = "transfer.enqueue.response";
    public const string ListRequest = "transfer.list.request";
    public const string ListResponse = "transfer.list.response";
    public const string StatusRequest = "transfer.status.request";
    public const string StatusResponse = "transfer.status.response";
    public const string CancelRequest = "transfer.cancel.request";
    public const string CancelResponse = "transfer.cancel.response";
    public const string RetryRequest = "transfer.retry.request";
    public const string RetryResponse = "transfer.retry.response";
    public const string ReconcileRequest = "transfer.reconcile.request";
    public const string ReconcileResponse = "transfer.reconcile.response";
}

[JsonConverter(typeof(JsonStringEnumConverter<TransferQueueOperation>))]
public enum TransferQueueOperation
{
    Copy,
    Move
}

[JsonConverter(typeof(JsonStringEnumConverter<TransferQueueVerification>))]
public enum TransferQueueVerification
{
    Size,
    StrongHashWhenAvailable,
    StrongHashRequired
}

[JsonConverter(typeof(JsonStringEnumConverter<TransferQueueState>))]
public enum TransferQueueState
{
    Pending,
    Preparing,
    Connecting,
    Transferring,
    Verifying,
    Finalizing,
    Paused,
    Retrying,
    BlockedCredential,
    BlockedTrust,
    Interrupted,
    NeedsReconciliation,
    RestartRequired,
    CleanupPending,
    Completed,
    Failed,
    Cancelled
}

[JsonConverter(typeof(JsonStringEnumConverter<TransferQueueMutationOutcome>))]
public enum TransferQueueMutationOutcome
{
    Applied,
    Accepted,
    NotFound,
    RevisionConflict,
    InvalidState
}

[JsonConverter(typeof(JsonStringEnumConverter<TransferReconciliationAction>))]
public enum TransferReconciliationAction
{
    Review,
    Restart,
    MarkCompleted,
    MarkFailed,
    Cancel
}

public sealed record TransferQueueAddress(
    Guid ConnectionId,
    string RootIdentity,
    string RelativePath,
    string? NativeItemId = null,
    string? VersionId = null,
    string? EntityTag = null)
{
    public bool HasValidBounds =>
        ConnectionId != Guid.Empty &&
        IsBoundedOpaque(RootIdentity, required: true) &&
        RelativePath is not null &&
        RelativePath.Length <= TransferQueueIpcLimits.MaximumRelativePathLength &&
        !RelativePath.Any(char.IsControl) &&
        IsBoundedOpaque(NativeItemId, required: false) &&
        IsBoundedOpaque(VersionId, required: false) &&
        IsBoundedOpaque(EntityTag, required: false);

    private static bool IsBoundedOpaque(string? value, bool required) =>
        value is null
            ? !required
            : !string.IsNullOrWhiteSpace(value) &&
              value.Length <= TransferQueueIpcLimits.MaximumOpaqueIdentityLength &&
              !value.Any(char.IsControl);
}

public sealed record TransferEnqueueRequest(
    int ContractVersion,
    Guid TransferId,
    TransferQueueOperation Operation,
    TransferQueueAddress Source,
    TransferQueueAddress Destination,
    long? ExpectedLength,
    TransferQueueVerification Verification,
    int Priority = 0,
    string? ExpectedDestinationVersionId = null,
    string? ExpectedDestinationEntityTag = null)
{
    public bool HasValidBounds =>
        ContractVersion > 0 &&
        TransferId != Guid.Empty &&
        Enum.IsDefined(Operation) &&
        Source is not null && Source.HasValidBounds &&
        Destination is not null && Destination.HasValidBounds &&
        ExpectedLength is null or >= 0 &&
        Enum.IsDefined(Verification) &&
        Priority is >= TransferQueueIpcLimits.MinimumPriority and <= TransferQueueIpcLimits.MaximumPriority &&
        (ExpectedDestinationVersionId is null ||
            !string.IsNullOrWhiteSpace(ExpectedDestinationVersionId) &&
            ExpectedDestinationVersionId.Length <= TransferQueueIpcLimits.MaximumOpaqueIdentityLength &&
            !ExpectedDestinationVersionId.Any(char.IsControl)) &&
        (ExpectedDestinationEntityTag is null ||
            !string.IsNullOrWhiteSpace(ExpectedDestinationEntityTag) &&
            ExpectedDestinationEntityTag.Length <= TransferQueueIpcLimits.MaximumOpaqueIdentityLength &&
            !ExpectedDestinationEntityTag.Any(char.IsControl));
}

public sealed record TransferEnqueueResponse(
    int ContractVersion,
    Guid TransferId,
    bool Accepted,
    bool AlreadyExisted,
    TransferQueueSummary? Transfer = null,
    StorageIpcFailure? Failure = null);

public sealed record TransferListRequest(
    int ContractVersion,
    TransferQueueState[] States,
    int PageSize = 25,
    string? ContinuationToken = null)
{
    public bool HasValidBounds =>
        ContractVersion > 0 &&
        States is not null &&
        States.Length is >= 1 and <= TransferQueueIpcLimits.MaximumStateFilters &&
        States.All(Enum.IsDefined) &&
        States.Distinct().Count() == States.Length &&
        PageSize is >= 1 and <= TransferQueueIpcLimits.MaximumPageSize &&
        (ContinuationToken is null ||
            ContinuationToken.Length is > 0 and <= TransferQueueIpcLimits.MaximumContinuationTokenLength &&
            !ContinuationToken.Any(char.IsControl));
}

public sealed record TransferListResponse(
    int ContractVersion,
    TransferQueueSummary[] Transfers,
    string? ContinuationToken,
    StorageIpcFailure? Failure = null);

public sealed record TransferStatusRequest(int ContractVersion, Guid TransferId)
{
    public bool HasValidBounds => ContractVersion > 0 && TransferId != Guid.Empty;
}

public sealed record TransferStatusResponse(
    int ContractVersion,
    Guid TransferId,
    TransferQueueSummary? Transfer,
    StorageIpcFailure? Failure = null);

public sealed record TransferCancelRequest(int ContractVersion, Guid TransferId, long ExpectedRevision)
{
    public bool HasValidBounds =>
        ContractVersion > 0 && TransferId != Guid.Empty && ExpectedRevision >= 0;
}

public sealed record TransferRetryRequest(int ContractVersion, Guid TransferId, long ExpectedRevision)
{
    public bool HasValidBounds =>
        ContractVersion > 0 && TransferId != Guid.Empty && ExpectedRevision >= 0;
}

public sealed record TransferReconcileRequest(
    int ContractVersion,
    Guid TransferId,
    long ExpectedRevision,
    TransferReconciliationAction Action)
{
    public bool HasValidBounds =>
        ContractVersion > 0 &&
        TransferId != Guid.Empty &&
        ExpectedRevision >= 0 &&
        Enum.IsDefined(Action);
}

public sealed record TransferMutationResponse(
    int ContractVersion,
    Guid TransferId,
    TransferQueueMutationOutcome Outcome,
    TransferQueueSummary? Transfer = null,
    StorageIpcFailure? Failure = null);

public sealed record TransferQueueSummary(
    Guid TransferId,
    TransferQueueOperation Operation,
    Guid SourceConnectionId,
    string SourcePath,
    Guid DestinationConnectionId,
    string DestinationPath,
    TransferQueueState State,
    long Revision,
    int Attempt,
    int Priority,
    long? ExpectedBytes,
    long ProgressBytes,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? RetryAvailableUtc,
    string? ErrorCode,
    string? ErrorSummary,
    bool CanCancel,
    bool CanRetry,
    bool NeedsReconciliation);
