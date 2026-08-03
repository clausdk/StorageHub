using System.Text.Json;
using System.Text.Json.Serialization;

namespace StorageHub.Contracts.Ipc;

/// <summary>Independently versioned normal-pipe contract for preview-first sync management.</summary>
public static class SyncManagementIpcContract
{
    public const int LegacyVersion = 1;
    public const int CurrentVersion = 2;

    public static bool IsSupported(int version) => version is LegacyVersion or CurrentVersion;
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
    public const int MaximumFilterCount = 128;
    public const int MaximumGlobLength = 512;
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
    public const string RunListRequest = "sync.run.list.request";
    public const string RunListResponse = "sync.run.list.response";
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
    Block,
    KeepBoth
}

[JsonConverter(typeof(JsonStringEnumConverter<SyncIpcBehavior>))]
public enum SyncIpcBehavior
{
    CopyNewFilesAToB,
    UpdateAToB,
    MirrorAToB,
    CopyNewFilesBToA,
    UpdateBToA,
    MirrorBToA,
    TwoWaySync,
    TwoWayWithDeletionPropagation,
    CompareOnly
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

[JsonConverter(typeof(SyncProfileDraftDocumentJsonConverter))]
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
    public bool Equals(SyncProfileDraftDocument? other) => other is not null &&
        string.Equals(DisplayName, other.DisplayName, StringComparison.Ordinal) &&
        LeftConnectionId == other.LeftConnectionId && string.Equals(LeftRoot, other.LeftRoot, StringComparison.Ordinal) &&
        RightConnectionId == other.RightConnectionId && string.Equals(RightRoot, other.RightRoot, StringComparison.Ordinal) &&
        Direction == other.Direction && DeletionMode == other.DeletionMode &&
        ConflictPolicy == other.ConflictPolicy && MaximumDeletionCount == other.MaximumDeletionCount &&
        MaximumDeletionPercentage == other.MaximumDeletionPercentage && Overwrite == other.Overwrite &&
        TransferBufferSize == other.TransferBufferSize && Enabled == other.Enabled && Behavior == other.Behavior &&
        IncludeHiddenFiles == other.IncludeHiddenFiles && IncludeGlobs.SequenceEqual(other.IncludeGlobs, StringComparer.Ordinal) &&
        ExcludeGlobs.SequenceEqual(other.ExcludeGlobs, StringComparer.Ordinal);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(DisplayName, StringComparer.Ordinal);
        hash.Add(LeftConnectionId);
        hash.Add(LeftRoot, StringComparer.Ordinal);
        hash.Add(RightConnectionId);
        hash.Add(RightRoot, StringComparer.Ordinal);
        hash.Add(Direction);
        hash.Add(DeletionMode);
        hash.Add(ConflictPolicy);
        hash.Add(MaximumDeletionCount);
        hash.Add(MaximumDeletionPercentage);
        hash.Add(Overwrite);
        hash.Add(TransferBufferSize);
        hash.Add(Enabled);
        hash.Add(Behavior);
        hash.Add(IncludeHiddenFiles);
        foreach (var glob in IncludeGlobs)
        {
            hash.Add(glob, StringComparer.Ordinal);
        }
        foreach (var glob in ExcludeGlobs)
        {
            hash.Add(glob, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }

    /// <summary>Neutral v2 name. The legacy JSON field remains accepted during migration.</summary>
    public Guid LocationAConnectionId => LeftConnectionId;
    public string LocationARoot => LeftRoot;
    public Guid LocationBConnectionId => RightConnectionId;
    public string LocationBRoot => RightRoot;

    public SyncIpcBehavior Behavior { get; init; } = ToBehavior(Direction, DeletionMode, Overwrite);
    public string[] IncludeGlobs { get; init; } = [];
    public string[] ExcludeGlobs { get; init; } = [".storagehub", ".storagehub/**", "**/.storagehub/**"];
    public bool IncludeHiddenFiles { get; init; } = true;

    public SyncProfileDraftDocument(
        string displayName,
        Guid locationAConnectionId,
        string locationARoot,
        Guid locationBConnectionId,
        string locationBRoot,
        SyncIpcBehavior behavior,
        SyncIpcConflictPolicy conflictPolicy,
        IEnumerable<string>? includeGlobs,
        IEnumerable<string>? excludeGlobs,
        bool includeHiddenFiles,
        int maximumDeletionCount,
        decimal maximumDeletionPercentage,
        int transferBufferSize,
        bool enabled)
        : this(
            displayName,
            locationAConnectionId,
            locationARoot,
            locationBConnectionId,
            locationBRoot,
            ToDirection(behavior),
            ToDeletionMode(behavior),
            conflictPolicy,
            maximumDeletionCount,
            maximumDeletionPercentage,
            IsUpdateBehavior(behavior),
            transferBufferSize,
            enabled)
    {
        Behavior = behavior;
        IncludeGlobs = includeGlobs?.ToArray() ?? [];
        ExcludeGlobs = excludeGlobs?.ToArray() ?? [];
        IncludeHiddenFiles = includeHiddenFiles;
    }

    public bool HasValidBounds =>
        IsSafeText(DisplayName, SyncManagementIpcLimits.MaximumDisplayNameLength, required: true) &&
        LeftConnectionId != Guid.Empty &&
        RightConnectionId != Guid.Empty &&
        LocationsAreDistinctAndNonOverlapping() &&
        IsSafeText(LeftRoot, SyncManagementIpcLimits.MaximumRelativeRootLength, allowEmpty: true) &&
        IsSafeText(RightRoot, SyncManagementIpcLimits.MaximumRelativeRootLength, allowEmpty: true) &&
        Enum.IsDefined(Direction) &&
        Enum.IsDefined(DeletionMode) &&
        Enum.IsDefined(ConflictPolicy) &&
        IsCompatible(Direction, DeletionMode) &&
        MaximumDeletionCount is >= 1 and <= SyncManagementIpcLimits.MaximumDeletionCount &&
        MaximumDeletionPercentage is > 0 and <= 100 &&
        TransferBufferSize is >= 1 and <= SyncManagementIpcLimits.MaximumTransferBufferSize;

    public bool HasValidV2Bounds => HasValidBounds &&
        Enum.IsDefined(Behavior) &&
        (Behavior == SyncIpcBehavior.CompareOnly || Behavior == ToBehavior(Direction, DeletionMode, Overwrite)) &&
        ValidateGlobs(IncludeGlobs) &&
        ValidateGlobs(ExcludeGlobs);

    private bool LocationsAreDistinctAndNonOverlapping()
    {
        if (LeftConnectionId != RightConnectionId)
        {
            return true;
        }

        var a = LeftRoot.Trim('/');
        var b = RightRoot.Trim('/');
        return a.Length > 0 && b.Length > 0 &&
               !string.Equals(a, b, StringComparison.OrdinalIgnoreCase) &&
               !a.StartsWith(b + "/", StringComparison.OrdinalIgnoreCase) &&
               !b.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ValidateGlobs(string[]? globs) =>
        globs is not null && globs.Length <= SyncManagementIpcLimits.MaximumFilterCount &&
        globs.All(static glob => IsSafeText(
            glob,
            SyncManagementIpcLimits.MaximumGlobLength,
            required: true));

    private static SyncIpcBehavior ToBehavior(
        SyncIpcDirection direction,
        SyncIpcDeletionMode deletionMode,
        bool overwrite) => (direction, deletionMode, overwrite) switch
        {
            (SyncIpcDirection.LeftToRight, SyncIpcDeletionMode.Mirror, _) => SyncIpcBehavior.MirrorAToB,
            (SyncIpcDirection.LeftToRight, _, true) => SyncIpcBehavior.UpdateAToB,
            (SyncIpcDirection.LeftToRight, _, false) => SyncIpcBehavior.CopyNewFilesAToB,
            (SyncIpcDirection.RightToLeft, SyncIpcDeletionMode.Mirror, _) => SyncIpcBehavior.MirrorBToA,
            (SyncIpcDirection.RightToLeft, _, true) => SyncIpcBehavior.UpdateBToA,
            (SyncIpcDirection.RightToLeft, _, false) => SyncIpcBehavior.CopyNewFilesBToA,
            (SyncIpcDirection.TwoWay, SyncIpcDeletionMode.Propagate, _) => SyncIpcBehavior.TwoWayWithDeletionPropagation,
            _ => SyncIpcBehavior.TwoWaySync,
        };

    private static SyncIpcDirection ToDirection(SyncIpcBehavior behavior) => behavior switch
    {
        SyncIpcBehavior.CopyNewFilesAToB or SyncIpcBehavior.UpdateAToB or SyncIpcBehavior.MirrorAToB or
            SyncIpcBehavior.CompareOnly => SyncIpcDirection.LeftToRight,
        SyncIpcBehavior.CopyNewFilesBToA or SyncIpcBehavior.UpdateBToA or SyncIpcBehavior.MirrorBToA =>
            SyncIpcDirection.RightToLeft,
        _ => SyncIpcDirection.TwoWay,
    };

    private static SyncIpcDeletionMode ToDeletionMode(SyncIpcBehavior behavior) => behavior switch
    {
        SyncIpcBehavior.MirrorAToB or SyncIpcBehavior.MirrorBToA => SyncIpcDeletionMode.Mirror,
        SyncIpcBehavior.TwoWayWithDeletionPropagation => SyncIpcDeletionMode.Propagate,
        _ => SyncIpcDeletionMode.Disabled,
    };

    private static bool IsUpdateBehavior(SyncIpcBehavior behavior) => behavior is
        SyncIpcBehavior.UpdateAToB or SyncIpcBehavior.MirrorAToB or
        SyncIpcBehavior.UpdateBToA or SyncIpcBehavior.MirrorBToA or
        SyncIpcBehavior.TwoWaySync or SyncIpcBehavior.TwoWayWithDeletionPropagation;

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
    DateTimeOffset UpdatedUtc)
{
    public Guid LocationAConnectionId => LeftConnectionId;
    public Guid LocationBConnectionId => RightConnectionId;
    public SyncIpcBehavior Behavior => new SyncProfileDraftDocument(
        DisplayName, LeftConnectionId, string.Empty, RightConnectionId, string.Empty,
        Direction, DeletionMode, SyncIpcConflictPolicy.Block, 1, 100, false, 1, Enabled).Behavior;
}

internal sealed class SyncProfileDraftDocumentJsonConverter : JsonConverter<SyncProfileDraftDocument>
{
    public override SyncProfileDraftDocument Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (TryGet(root, "LocationAConnectionId", out _))
        {
            return new SyncProfileDraftDocument(
                Text(root, "DisplayName"),
                GuidValue(root, "LocationAConnectionId"),
                Text(root, "LocationARoot"),
                GuidValue(root, "LocationBConnectionId"),
                Text(root, "LocationBRoot"),
                EnumValue<SyncIpcBehavior>(root, "Behavior"),
                EnumValue<SyncIpcConflictPolicy>(root, "ConflictPolicy"),
                Strings(root, "IncludeGlobs"),
                Strings(root, "ExcludeGlobs"),
                Bool(root, "IncludeHiddenFiles"),
                Int(root, "MaximumDeletionCount"),
                Decimal(root, "MaximumDeletionPercentage"),
                Int(root, "TransferBufferSize"),
                Bool(root, "Enabled"));
        }

        var legacy = new SyncProfileDraftDocument(
            Text(root, "DisplayName"),
            GuidValue(root, "LeftConnectionId"),
            Text(root, "LeftRoot"),
            GuidValue(root, "RightConnectionId"),
            Text(root, "RightRoot"),
            EnumValue<SyncIpcDirection>(root, "Direction"),
            EnumValue<SyncIpcDeletionMode>(root, "DeletionMode"),
            EnumValue<SyncIpcConflictPolicy>(root, "ConflictPolicy"),
            Int(root, "MaximumDeletionCount"),
            Decimal(root, "MaximumDeletionPercentage"),
            Bool(root, "Overwrite"),
            Int(root, "TransferBufferSize"),
            Bool(root, "Enabled"));
        return legacy with
        {
            Behavior = TryGet(root, "Behavior", out _) ? EnumValue<SyncIpcBehavior>(root, "Behavior") : legacy.Behavior,
            IncludeGlobs = TryGet(root, "IncludeGlobs", out _) ? Strings(root, "IncludeGlobs") : legacy.IncludeGlobs,
            ExcludeGlobs = TryGet(root, "ExcludeGlobs", out _) ? Strings(root, "ExcludeGlobs") : legacy.ExcludeGlobs,
            IncludeHiddenFiles = !TryGet(root, "IncludeHiddenFiles", out _) || Bool(root, "IncludeHiddenFiles"),
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        SyncProfileDraftDocument value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        Write(writer, options, "DisplayName", value.DisplayName);
        Write(writer, options, "LocationAConnectionId", value.LocationAConnectionId);
        Write(writer, options, "LocationARoot", value.LocationARoot);
        Write(writer, options, "LocationBConnectionId", value.LocationBConnectionId);
        Write(writer, options, "LocationBRoot", value.LocationBRoot);
        Write(writer, options, "Behavior", value.Behavior.ToString());
        Write(writer, options, "ConflictPolicy", value.ConflictPolicy.ToString());
        WriteArray(writer, options, "IncludeGlobs", value.IncludeGlobs);
        WriteArray(writer, options, "ExcludeGlobs", value.ExcludeGlobs);
        Write(writer, options, "IncludeHiddenFiles", value.IncludeHiddenFiles);
        Write(writer, options, "MaximumDeletionCount", value.MaximumDeletionCount);
        Write(writer, options, "MaximumDeletionPercentage", value.MaximumDeletionPercentage);
        Write(writer, options, "TransferBufferSize", value.TransferBufferSize);
        Write(writer, options, "Enabled", value.Enabled);
        writer.WriteEndObject();
    }

    private static bool TryGet(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static JsonElement Required(JsonElement root, string name) =>
        TryGet(root, name, out var value) ? value : throw new JsonException($"Required property '{name}' is missing.");
    private static string Text(JsonElement root, string name) => Required(root, name).GetString() ?? throw new JsonException();
    private static Guid GuidValue(JsonElement root, string name) => Required(root, name).GetGuid();
    private static int Int(JsonElement root, string name) => Required(root, name).GetInt32();
    private static decimal Decimal(JsonElement root, string name) => Required(root, name).GetDecimal();
    private static bool Bool(JsonElement root, string name) => Required(root, name).GetBoolean();
    private static string[] Strings(JsonElement root, string name) =>
        Required(root, name).EnumerateArray().Select(static item => item.GetString() ?? throw new JsonException()).ToArray();
    private static T EnumValue<T>(JsonElement root, string name) where T : struct, Enum =>
        Enum.TryParse<T>(Required(root, name).GetString(), ignoreCase: false, out var value)
            ? value
            : throw new JsonException($"Property '{name}' is not a valid {typeof(T).Name}.");

    private static string Name(JsonSerializerOptions options, string value) =>
        options.PropertyNamingPolicy?.ConvertName(value) ?? value;
    private static void Write(Utf8JsonWriter writer, JsonSerializerOptions options, string name, string value) =>
        writer.WriteString(Name(options, name), value);
    private static void Write(Utf8JsonWriter writer, JsonSerializerOptions options, string name, Guid value) =>
        writer.WriteString(Name(options, name), value);
    private static void Write(Utf8JsonWriter writer, JsonSerializerOptions options, string name, bool value) =>
        writer.WriteBoolean(Name(options, name), value);
    private static void Write(Utf8JsonWriter writer, JsonSerializerOptions options, string name, int value) =>
        writer.WriteNumber(Name(options, name), value);
    private static void Write(Utf8JsonWriter writer, JsonSerializerOptions options, string name, decimal value) =>
        writer.WriteNumber(Name(options, name), value);
    private static void WriteArray(Utf8JsonWriter writer, JsonSerializerOptions options, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(Name(options, name));
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }
        writer.WriteEndArray();
    }
}

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

public sealed record SyncRunListRequest(
    int ContractVersion = SyncManagementIpcContract.CurrentVersion,
    Guid? ProfileId = null,
    int PageSize = 50,
    string? ContinuationToken = null)
{
    public bool HasValidBounds => ContractVersion > 0 &&
        (ProfileId is null || ProfileId != Guid.Empty) &&
        PageSize is >= 1 and <= SyncManagementIpcLimits.MaximumPageSize &&
        (ContinuationToken is null ||
         ContinuationToken.Length is > 0 and <= SyncManagementIpcLimits.MaximumContinuationTokenLength &&
         ContinuationToken.All(char.IsAsciiDigit));
}

public sealed record SyncRunListResponse(
    int ContractVersion,
    SyncRunSummary[] Runs,
    string? ContinuationToken,
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
    bool RightSnapshotComplete)
{
    public long LocationAItemCount => LeftItemCount;
    public long LocationBItemCount => RightItemCount;
    public bool LocationASnapshotComplete => LeftSnapshotComplete;
    public bool LocationBSnapshotComplete => RightSnapshotComplete;
}

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
    bool IsDestructive)
{
    public Guid FromLocationConnectionId => SourceConnectionId;
    public string FromLocationPath => SourcePath;
    public Guid? ToLocationConnectionId => DestinationConnectionId;
    public string? ToLocationPath => DestinationPath;
}

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
