using System.Text.Json.Serialization;

namespace StorageHub.Contracts.Ipc;

/// <summary>Independently versioned normal-pipe contract for preview-first sync management.</summary>
public static class SyncManagementIpcContract
{
    public const int CurrentVersion = 1;

    public static bool IsSupported(int version) => version == CurrentVersion;
}

public static class SyncManagementIpcLimits
{
    public const int MaximumProfileResults = 100;
    public const int MaximumDisplayNameLength = 256;
    public const int MaximumRelativeRootLength = 4_096;
    public const int MaximumRelativePathLength = 32_768;
    public const int MaximumPageSize = 100;
    public const int MaximumPlanOperationCount = 1_000_000;
    public const int MaximumContinuationTokenLength = 128;
    public const int MaximumConflictReasonLength = 2_048;
    public const int MaximumConflictKindLength = 128;
    public const int MaximumDeletionCount = 1_000_000;
    public const int MaximumTransferBufferSize = 1_048_576;
}

public static class SyncManagementIpcMessageTypes
{
    public const string ProfileListRequest = "sync.profile.list.request";
    public const string ProfileListResponse = "sync.profile.list.response";
    public const string ProfileGetRequest = "sync.profile.get.request";
    public const string ProfileGetResponse = "sync.profile.get.response";
    public const string ProfileCreateRequest = "sync.profile.create.request";
    public const string ProfileCreateResponse = "sync.profile.create.response";
    public const string ProfileUpdateRequest = "sync.profile.update.request";
    public const string ProfileUpdateResponse = "sync.profile.update.response";
    public const string PreviewGenerateRequest = "sync.preview.generate.request";
    public const string PreviewGenerateResponse = "sync.preview.generate.response";
    public const string RunStatusRequest = "sync.run.status.request";
    public const string RunStatusResponse = "sync.run.status.response";
    public const string PlanPageRequest = "sync.plan.page.request";
    public const string PlanPageResponse = "sync.plan.page.response";
    public const string ConflictPageRequest = "sync.conflict.page.request";
    public const string ConflictPageResponse = "sync.conflict.page.response";
    public const string ApproveDispatchRequest = "sync.run.approve-dispatch.request";
    public const string ApproveDispatchResponse = "sync.run.approve-dispatch.response";
}

[JsonConverter(typeof(JsonStringEnumConverter<SyncIpcDirection>))]
public enum SyncIpcDirection
{
    LeftToRight,
    RightToLeft,
    TwoWay
}

[JsonConverter(typeof(JsonStringEnumConverter<SyncIpcDeletionMode>))]
public enum SyncIpcDeletionMode
{
    Disabled,
    Mirror,
    Propagate
}

[JsonConverter(typeof(JsonStringEnumConverter<SyncIpcConflictPolicy>))]
public enum SyncIpcConflictPolicy
{
    Block
}

[JsonConverter(typeof(JsonStringEnumConverter<SyncProfileMutationOutcome>))]
public enum SyncProfileMutationOutcome
{
    Succeeded,
    AlreadyApplied,
    NotFound,
    RevisionConflict,
    ConstraintConflict,
    Unavailable
}

[JsonConverter(typeof(JsonStringEnumConverter<SyncIpcRunPhase>))]
public enum SyncIpcRunPhase
{
    Pending,
    Scanning,
    Planning,
    AwaitingApproval,
    Ready,
    Executing,
    Verifying,
    CommittingBaseline,
    BlockedConflict,
    BlockedDeletionGuard,
    BlockedEndpoint,
    BlockedCredential,
    BlockedTrust,
    Interrupted,
    NeedsReconciliation,
    Completed,
    Failed,
    Cancelled
}

[JsonConverter(typeof(JsonStringEnumConverter<SyncIpcStatusCode>))]
public enum SyncIpcStatusCode
{
    None,
    ConflictRequiresDecision,
    DeletionGuardTriggered,
    EndpointUnavailable,
    CredentialUnavailable,
    TrustRequired,
    Interrupted,
    StateUncertain,
    VerificationFailed,
    ProviderFailure
}

[JsonConverter(typeof(JsonStringEnumConverter<SyncIpcDispatchState>))]
public enum SyncIpcDispatchState
{
    NotDispatched,
    DurablyDispatched
}

[JsonConverter(typeof(JsonStringEnumConverter<SyncIpcPlanOperationKind>))]
public enum SyncIpcPlanOperationKind
{
    Copy,
    Delete,
    CreateDirectory
}

[JsonConverter(typeof(JsonStringEnumConverter<SyncIpcConflictState>))]
public enum SyncIpcConflictState
{
    Unresolved,
    Resolved,
    Dismissed
}

public sealed record SyncProfileDraftDocument(
    string DisplayName,
    Guid LeftConnectionId,
    string LeftRoot,
    Guid RightConnectionId,
    string RightRoot,
    SyncIpcDirection Direction,
    SyncIpcDeletionMode DeletionMode,
    SyncIpcConflictPolicy ConflictPolicy,
    int MaximumDeletionCount,
    decimal MaximumDeletionPercentage,
    bool Overwrite,
    int TransferBufferSize,
    bool Enabled)
{
    public bool HasValidBounds =>
        IsSafeText(DisplayName, SyncManagementIpcLimits.MaximumDisplayNameLength, required: true) &&
        LeftConnectionId != Guid.Empty &&
        RightConnectionId != Guid.Empty &&
        LeftConnectionId != RightConnectionId &&
        IsSafeText(LeftRoot, SyncManagementIpcLimits.MaximumRelativeRootLength, allowEmpty: true) &&
        IsSafeText(RightRoot, SyncManagementIpcLimits.MaximumRelativeRootLength, allowEmpty: true) &&
        Enum.IsDefined(Direction) &&
        Enum.IsDefined(DeletionMode) &&
        Enum.IsDefined(ConflictPolicy) &&
        IsCompatible(Direction, DeletionMode) &&
        MaximumDeletionCount is >= 1 and <= SyncManagementIpcLimits.MaximumDeletionCount &&
        MaximumDeletionPercentage is > 0 and <= 100 &&
        TransferBufferSize is >= 1 and <= SyncManagementIpcLimits.MaximumTransferBufferSize;

    private static bool IsCompatible(SyncIpcDirection direction, SyncIpcDeletionMode deletionMode) =>
        direction == SyncIpcDirection.TwoWay
            ? deletionMode != SyncIpcDeletionMode.Mirror
            : deletionMode != SyncIpcDeletionMode.Propagate;

    private static bool IsSafeText(
        string? value,
        int maximumLength,
        bool required = false,
        bool allowEmpty = false)
    {
        if (value is null)
        {
            return false;
        }

        if (required && string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return (allowEmpty || value.Length > 0) &&
               value.Length <= maximumLength &&
               !value.Any(char.IsControl);
    }
}

public sealed record SyncProfileSummary(
    Guid ProfileId,
    string DisplayName,
    Guid LeftConnectionId,
    Guid RightConnectionId,
    SyncIpcDirection Direction,
    SyncIpcDeletionMode DeletionMode,
    bool Enabled,
    long Revision,
    DateTimeOffset UpdatedUtc);

public sealed record SyncProfileDocument(
    Guid ProfileId,
    SyncProfileDraftDocument Draft,
    long Revision,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public sealed record SyncProfileListRequest(
    int ContractVersion = SyncManagementIpcContract.CurrentVersion,
    bool IncludeDisabled = true,
    int MaximumCount = 100)
{
    public bool HasValidBounds =>
        ContractVersion > 0 &&
        MaximumCount is >= 1 and <= SyncManagementIpcLimits.MaximumProfileResults;
}

public sealed record SyncProfileListResponse(
    int ContractVersion,
    SyncProfileSummary[] Profiles,
    StorageIpcFailure? Failure = null);

public sealed record SyncProfileGetRequest(int ContractVersion, Guid ProfileId)
{
    public bool HasValidBounds => ContractVersion > 0 && ProfileId != Guid.Empty;
}

public sealed record SyncProfileGetResponse(
    int ContractVersion,
    Guid ProfileId,
    SyncProfileDocument? Profile,
    StorageIpcFailure? Failure = null);

public sealed record SyncProfileCreateRequest(
    int ContractVersion,
    Guid ProfileId,
    SyncProfileDraftDocument Draft)
{
    public bool HasValidBounds =>
        ContractVersion > 0 && ProfileId != Guid.Empty && Draft is not null && Draft.HasValidBounds;
}

public sealed record SyncProfileUpdateRequest(
    int ContractVersion,
    Guid ProfileId,
    long ExpectedRevision,
    SyncProfileDraftDocument Draft)
{
    public bool HasValidBounds =>
        ContractVersion > 0 &&
        ProfileId != Guid.Empty &&
        ExpectedRevision >= 1 &&
        Draft is not null && Draft.HasValidBounds;
}

public sealed record SyncProfileMutationResponse(
    int ContractVersion,
    Guid ProfileId,
    SyncProfileMutationOutcome Outcome,
    SyncProfileDocument? Profile = null,
    long? ActualRevision = null,
    StorageIpcFailure? Failure = null);

public sealed record SyncPreviewGenerateRequest(
    int ContractVersion,
    Guid ProfileId,
    Guid PreviewRequestId)
{
    public bool HasValidBounds =>
        ContractVersion > 0 && ProfileId != Guid.Empty && PreviewRequestId != Guid.Empty;
}

public sealed record SyncPreviewGenerateResponse(
    int ContractVersion,
    Guid ProfileId,
    SyncRunSummary? Run,
    SyncPlanOverview? Plan,
    StorageIpcFailure? Failure = null);

public sealed record SyncRunStatusRequest(int ContractVersion, Guid SyncRunId)
{
    public bool HasValidBounds => ContractVersion > 0 && SyncRunId != Guid.Empty;
}

public sealed record SyncRunStatusResponse(
    int ContractVersion,
    Guid SyncRunId,
    SyncRunSummary? Run,
    StorageIpcFailure? Failure = null);

public sealed record SyncRunSummary(
    Guid SyncRunId,
    Guid ProfileId,
    long Generation,
    SyncIpcRunPhase Phase,
    SyncIpcStatusCode StatusCode,
    long Revision,
    DateTimeOffset UpdatedUtc,
    Guid PlanId,
    string PlanSha256,
    string ApprovalSha256,
    int ConflictCount,
    SyncIpcDispatchState DispatchState,
    DateTimeOffset? DispatchedUtc,
    DateTimeOffset CreatedUtc,
    long BaselineItemCount,
    long LeftItemCount,
    long RightItemCount,
    bool LeftSnapshotComplete,
    bool RightSnapshotComplete);

public sealed record SyncPlanOverview(
    Guid SyncRunId,
    Guid PlanId,
    string PlanSha256,
    long BaselineGeneration,
    int OperationCount,
    int CopyCount,
    int DeleteCount,
    int CreateDirectoryCount,
    DateTimeOffset CreatedUtc);

public sealed record SyncPlanPageRequest(
    int ContractVersion,
    Guid SyncRunId,
    int PageSize = 50,
    string? ContinuationToken = null)
{
    public bool HasValidBounds =>
        ContractVersion > 0 &&
        SyncRunId != Guid.Empty &&
        PageSize is >= 1 and <= SyncManagementIpcLimits.MaximumPageSize &&
        IsValidContinuation(ContinuationToken);

    private static bool IsValidContinuation(string? value) => value is null ||
        value.Length is > 0 and <= SyncManagementIpcLimits.MaximumContinuationTokenLength &&
        !value.Any(char.IsControl);
}

public sealed record SyncPlanOperationSummary(
    int Sequence,
    SyncIpcPlanOperationKind Kind,
    Guid SourceConnectionId,
    string SourcePath,
    Guid? DestinationConnectionId,
    string? DestinationPath,
    long? ExpectedLength,
    bool IsDestructive);

public sealed record SyncPlanPageResponse(
    int ContractVersion,
    Guid SyncRunId,
    Guid PlanId,
    string PlanSha256,
    int TotalOperations,
    SyncPlanOperationSummary[] Operations,
    string? ContinuationToken,
    StorageIpcFailure? Failure = null);

public sealed record SyncConflictPageRequest(
    int ContractVersion,
    Guid SyncRunId,
    SyncIpcConflictState? State = null,
    int PageSize = 50,
    string? ContinuationToken = null)
{
    public bool HasValidBounds =>
        ContractVersion > 0 &&
        SyncRunId != Guid.Empty &&
        (State is null || Enum.IsDefined(State.Value)) &&
        PageSize is >= 1 and <= SyncManagementIpcLimits.MaximumPageSize &&
        IsValidContinuation(ContinuationToken);

    private static bool IsValidContinuation(string? value) => value is null ||
        value.Length is > 0 and <= SyncManagementIpcLimits.MaximumContinuationTokenLength &&
        !value.Any(char.IsControl);
}

public sealed record SyncConflictSummary(
    Guid ConflictId,
    string RelativePath,
    string ConflictKind,
    SyncIpcConflictState State,
    string SafeReason,
    DateTimeOffset DetectedUtc,
    DateTimeOffset? ResolvedUtc,
    long Revision);

public sealed record SyncConflictPageResponse(
    int ContractVersion,
    Guid SyncRunId,
    int ReportedConflictCount,
    SyncConflictSummary[] Conflicts,
    string? ContinuationToken,
    bool IsTruncatedAtSource,
    StorageIpcFailure? Failure = null);

public sealed record SyncApproveDispatchRequest(
    int ContractVersion,
    Guid SyncRunId,
    long ExpectedRevision,
    string ApprovalSha256)
{
    public bool HasValidBounds =>
        ContractVersion > 0 &&
        SyncRunId != Guid.Empty &&
        ExpectedRevision >= 0 &&
        IsSha256(ApprovalSha256);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

/// <summary>
/// A successful response means only that the immutable apply request is durable. It never means
/// provider execution has started or completed.
/// </summary>
public sealed record SyncApproveDispatchResponse(
    int ContractVersion,
    Guid SyncRunId,
    bool DurablyDispatched,
    SyncRunSummary? Run,
    StorageIpcFailure? Failure = null);
