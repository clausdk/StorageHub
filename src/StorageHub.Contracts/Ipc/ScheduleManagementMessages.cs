using System.Text.Json.Serialization;

namespace StorageHub.Contracts.Ipc;

/// <summary>Independently versioned normal-pipe contract for preview-only schedule management.</summary>
public static class ScheduleManagementIpcContract
{
    public const int LegacyVersion = 1;
    public const int CurrentVersion = 2;

    public static bool IsSupported(int version) => version is LegacyVersion or CurrentVersion;
}

public static class ScheduleManagementIpcLimits
{
    public const int MaximumScheduleResults = 100;
    public const int MaximumCronExpressionLength = 128;
    public const int MaximumTimeZoneIdLength = 256;
    public const int MaximumProfileDisplayNameLength = 256;
    public const int MaximumOutcomeLength = 64;
    public const int MaximumErrorCodeLength = 256;
    public const int MinimumMisfireGraceSeconds = 1;
    public const int MaximumMisfireGraceSeconds = 30 * 24 * 60 * 60;
}

public static class ScheduleManagementIpcMessageTypes
{
    public const string ListRequest = "schedule.list.request";
    public const string ListResponse = "schedule.list.response";
    public const string GetRequest = "schedule.get.request";
    public const string GetResponse = "schedule.get.response";
    public const string CreateRequest = "schedule.create.request";
    public const string CreateResponse = "schedule.create.response";
    public const string UpdateRequest = "schedule.update.request";
    public const string UpdateResponse = "schedule.update.response";
    public const string SetEnabledRequest = "schedule.set-enabled.request";
    public const string SetEnabledResponse = "schedule.set-enabled.response";
    public const string DeleteRequest = "schedule.delete.request";
    public const string DeleteResponse = "schedule.delete.response";
}

[JsonConverter(typeof(JsonStringEnumConverter<ScheduleIpcExecutionMode>))]
public enum ScheduleIpcExecutionMode
{
    PreviewOnly,
    SafeAutomatic
}

[JsonConverter(typeof(JsonStringEnumConverter<ScheduleMutationOutcome>))]
public enum ScheduleMutationOutcome
{
    Succeeded,
    AlreadyApplied,
    NotFound,
    RevisionConflict,
    ActiveRun,
    ConstraintConflict,
    Unavailable
}

public sealed record ScheduleDraftDocument(
    Guid ProfileId,
    string CronExpression,
    string TimeZoneId,
    int MisfireGraceSeconds,
    bool QueueOneWhileRunning,
    bool Enabled,
    ScheduleIpcExecutionMode ExecutionMode = ScheduleIpcExecutionMode.SafeAutomatic)
{
    public bool HasValidBounds =>
        ProfileId != Guid.Empty &&
        IsSafeText(CronExpression, ScheduleManagementIpcLimits.MaximumCronExpressionLength) &&
        IsSafeText(TimeZoneId, ScheduleManagementIpcLimits.MaximumTimeZoneIdLength) &&
        MisfireGraceSeconds is >= ScheduleManagementIpcLimits.MinimumMisfireGraceSeconds and
            <= ScheduleManagementIpcLimits.MaximumMisfireGraceSeconds &&
        Enum.IsDefined(ExecutionMode);

    private static bool IsSafeText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        !value.Any(char.IsControl);
}

/// <summary>
/// Safe management projection. It deliberately omits ownership IDs, fencing tokens, and provider
/// details. <see cref="IsBusy"/> only tells the UI to avoid conflicting mutations.
/// </summary>
public sealed record ScheduleDocument(
    Guid ScheduleId,
    Guid ProfileId,
    string ProfileDisplayName,
    string CronExpression,
    string TimeZoneId,
    int MisfireGraceSeconds,
    bool QueueOneWhileRunning,
    bool Enabled,
    DateTimeOffset? NextOccurrenceUtc,
    DateTimeOffset? QueuedOccurrenceUtc,
    bool IsBusy,
    string? LastRunOutcome,
    string? LastErrorCode,
    long Revision,
    ScheduleIpcExecutionMode ExecutionMode = ScheduleIpcExecutionMode.PreviewOnly);

public sealed record ScheduleListRequest(
    int ContractVersion = ScheduleManagementIpcContract.CurrentVersion,
    bool IncludeDisabled = true,
    int MaximumCount = ScheduleManagementIpcLimits.MaximumScheduleResults)
{
    public bool HasValidBounds =>
        ContractVersion > 0 &&
        MaximumCount is >= 1 and <= ScheduleManagementIpcLimits.MaximumScheduleResults;
}

public sealed record ScheduleListResponse(
    int ContractVersion,
    ScheduleDocument[] Schedules,
    StorageIpcFailure? Failure = null);

public sealed record ScheduleGetRequest(int ContractVersion, Guid ScheduleId)
{
    public bool HasValidBounds => ContractVersion > 0 && ScheduleId != Guid.Empty;
}

public sealed record ScheduleGetResponse(
    int ContractVersion,
    Guid ScheduleId,
    ScheduleDocument? Schedule,
    StorageIpcFailure? Failure = null);

public sealed record ScheduleCreateRequest(
    int ContractVersion,
    Guid ScheduleId,
    ScheduleDraftDocument Draft)
{
    public bool HasValidBounds =>
        ContractVersion > 0 &&
        ScheduleId != Guid.Empty &&
        Draft is not null &&
        Draft.HasValidBounds;
}

public sealed record ScheduleUpdateRequest(
    int ContractVersion,
    Guid ScheduleId,
    long ExpectedRevision,
    ScheduleDraftDocument Draft)
{
    public bool HasValidBounds =>
        ContractVersion > 0 &&
        ScheduleId != Guid.Empty &&
        ExpectedRevision >= 0 &&
        Draft is not null &&
        Draft.HasValidBounds;
}

public sealed record ScheduleSetEnabledRequest(
    int ContractVersion,
    Guid ScheduleId,
    long ExpectedRevision,
    bool Enabled)
{
    public bool HasValidBounds =>
        ContractVersion > 0 && ScheduleId != Guid.Empty && ExpectedRevision >= 0;
}

public sealed record ScheduleDeleteRequest(
    int ContractVersion,
    Guid ScheduleId,
    long ExpectedRevision)
{
    public bool HasValidBounds =>
        ContractVersion > 0 && ScheduleId != Guid.Empty && ExpectedRevision >= 0;
}

public sealed record ScheduleMutationResponse(
    int ContractVersion,
    Guid ScheduleId,
    ScheduleMutationOutcome Outcome,
    ScheduleDocument? Schedule = null,
    long? ActualRevision = null,
    StorageIpcFailure? Failure = null);
