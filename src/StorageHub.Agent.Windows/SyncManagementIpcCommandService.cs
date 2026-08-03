using System.Globalization;
using System.Text;
using System.Text.Json;
using StorageHub.Agent.Ipc;
using StorageHub.Contracts.Ipc;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Identifiers;
using StorageHub.Sync;
using StorageHub.Sync.Persistence;
using StorageHub.Transfers;

namespace StorageHub.Agent.Windows;

/// <summary>
/// Bounded normal-pipe management for preview-first sync orchestration. Approval responses report
/// durable dispatch only; provider execution remains a separate background concern.
/// </summary>
public sealed class SyncManagementIpcCommandService : IAgentIpcCommandHandler
{
    private const int MaximumConcurrentManualPreviews = 2;
    private const int MaximumConflictReadCount = 1_000;
    private const int MaximumPlanOperationCount = SyncManagementIpcLimits.MaximumPlanOperationCount;
    private readonly ISyncProfileRepository _profiles;
    private readonly ISyncOrchestrationService _orchestration;
    private readonly ISyncRunStore _runs;
    private readonly ISyncPlanStore _plans;
    private readonly ISyncConflictStore _conflicts;
    private readonly TimeProvider _timeProvider;
    private int _activeManualPreviews;

    public SyncManagementIpcCommandService(
        ISyncProfileRepository profiles,
        ISyncOrchestrationService orchestration,
        ISyncRunStore runs,
        ISyncPlanStore plans,
        ISyncConflictStore conflicts,
        TimeProvider? timeProvider = null)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _orchestration = orchestration ?? throw new ArgumentNullException(nameof(orchestration));
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _plans = plans ?? throw new ArgumentNullException(nameof(plans));
        _conflicts = conflicts ?? throw new ArgumentNullException(nameof(conflicts));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool CanHandle(string messageType) => messageType is
        SyncManagementIpcMessageTypes.ProfileListRequest or
        SyncManagementIpcMessageTypes.ProfileGetRequest or
        SyncManagementIpcMessageTypes.ProfileCreateRequest or
        SyncManagementIpcMessageTypes.ProfileUpdateRequest or
        SyncManagementIpcMessageTypes.PreviewGenerateRequest or
        SyncManagementIpcMessageTypes.RunStatusRequest or
        SyncManagementIpcMessageTypes.RunListRequest or
        SyncManagementIpcMessageTypes.PlanPageRequest or
        SyncManagementIpcMessageTypes.ConflictPageRequest or
        SyncManagementIpcMessageTypes.ApproveDispatchRequest;

    public ValueTask<AgentIpcCommandResponse> HandleAsync(
        IpcEnvelope request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.MessageType switch
        {
            SyncManagementIpcMessageTypes.ProfileListRequest => ListProfilesAsync(request, cancellationToken),
            SyncManagementIpcMessageTypes.ProfileGetRequest => GetProfileAsync(request, cancellationToken),
            SyncManagementIpcMessageTypes.ProfileCreateRequest => CreateProfileAsync(request, cancellationToken),
            SyncManagementIpcMessageTypes.ProfileUpdateRequest => UpdateProfileAsync(request, cancellationToken),
            SyncManagementIpcMessageTypes.PreviewGenerateRequest => GeneratePreviewAsync(request, cancellationToken),
            SyncManagementIpcMessageTypes.RunStatusRequest => GetRunStatusAsync(request, cancellationToken),
            SyncManagementIpcMessageTypes.RunListRequest => ListRunsAsync(request, cancellationToken),
            SyncManagementIpcMessageTypes.PlanPageRequest => GetPlanPageAsync(request, cancellationToken),
            SyncManagementIpcMessageTypes.ConflictPageRequest => GetConflictPageAsync(request, cancellationToken),
            SyncManagementIpcMessageTypes.ApproveDispatchRequest => ApproveAndDispatchAsync(request, cancellationToken),
            _ => ValueTask.FromResult(AgentIpcCommandResponse.Error(
                "ipc.message.unsupported",
                "The requested IPC operation is not supported by this agent version."))
        };
    }

    private async ValueTask<AgentIpcCommandResponse> ListProfilesAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<SyncProfileListRequest>();
        var invalid = ValidateRequest(request.ContractVersion, request.HasValidBounds);
        if (invalid is not null)
        {
            return invalid;
        }

        try
        {
            var profiles = await _profiles.ListAsync(
                request.IncludeDisabled,
                request.MaximumCount,
                cancellationToken).ConfigureAwait(false);
            if (profiles.Count > request.MaximumCount ||
                profiles.Count > SyncManagementIpcLimits.MaximumProfileResults)
            {
                return ProfileListFailure(IntegrityFailure());
            }

            return AgentIpcCommandResponse.Create(
                SyncManagementIpcMessageTypes.ProfileListResponse,
                new SyncProfileListResponse(
                    SyncManagementIpcContract.CurrentVersion,
                    profiles.Select(MapProfileSummary).ToArray()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ProfileListFailure(UnavailableFailure());
        }
    }

    private async ValueTask<AgentIpcCommandResponse> GetProfileAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<SyncProfileGetRequest>();
        var invalid = ValidateRequest(request.ContractVersion, request.HasValidBounds);
        if (invalid is not null)
        {
            return invalid;
        }

        try
        {
            var profile = await _profiles.GetAsync(new SyncProfileId(request.ProfileId), cancellationToken)
                .ConfigureAwait(false);
            return profile is null
                ? ProfileGetFailure(request.ProfileId, NotFoundFailure("sync.profile.not_found"))
                : AgentIpcCommandResponse.Create(
                    SyncManagementIpcMessageTypes.ProfileGetResponse,
                    new SyncProfileGetResponse(
                        SyncManagementIpcContract.CurrentVersion,
                        request.ProfileId,
                        MapProfile(profile)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ProfileGetFailure(request.ProfileId, UnavailableFailure());
        }
    }

    private async ValueTask<AgentIpcCommandResponse> CreateProfileAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<SyncProfileCreateRequest>();
        var invalid = ValidateRequest(
            request.ContractVersion,
            request.HasValidBounds &&
            (request.ContractVersion == SyncManagementIpcContract.LegacyVersion || request.Draft.HasValidV2Bounds));
        if (invalid is not null)
        {
            return invalid;
        }

        SyncProfile profile;
        try
        {
            var now = _timeProvider.GetUtcNow();
            profile = CreateProfile(
                new SyncProfileId(request.ProfileId),
                request.Draft,
                revision: 1,
                now,
                now);
        }
        catch (ArgumentException)
        {
            return ProfileMutationFailure(
                SyncManagementIpcMessageTypes.ProfileCreateResponse,
                request.ProfileId,
                SyncProfileMutationOutcome.ConstraintConflict,
                ValidationFailure());
        }

        try
        {
            return MapProfileWrite(
                SyncManagementIpcMessageTypes.ProfileCreateResponse,
                request.ProfileId,
                await _profiles.CreateAsync(profile, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ProfileMutationFailure(
                SyncManagementIpcMessageTypes.ProfileCreateResponse,
                request.ProfileId,
                SyncProfileMutationOutcome.Unavailable,
                UnavailableFailure());
        }
    }

    private async ValueTask<AgentIpcCommandResponse> UpdateProfileAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<SyncProfileUpdateRequest>();
        var invalid = ValidateRequest(
            request.ContractVersion,
            request.HasValidBounds &&
            (request.ContractVersion == SyncManagementIpcContract.LegacyVersion || request.Draft.HasValidV2Bounds));
        if (invalid is not null)
        {
            return invalid;
        }

        SyncProfile profile;
        try
        {
            var now = _timeProvider.GetUtcNow();
            profile = CreateProfile(
                new SyncProfileId(request.ProfileId),
                request.Draft,
                request.ExpectedRevision,
                now,
                now);
        }
        catch (ArgumentException)
        {
            return ProfileMutationFailure(
                SyncManagementIpcMessageTypes.ProfileUpdateResponse,
                request.ProfileId,
                SyncProfileMutationOutcome.ConstraintConflict,
                ValidationFailure());
        }

        try
        {
            return MapProfileWrite(
                SyncManagementIpcMessageTypes.ProfileUpdateResponse,
                request.ProfileId,
                await _profiles.UpdateAsync(profile, request.ExpectedRevision, cancellationToken)
                    .ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ProfileMutationFailure(
                SyncManagementIpcMessageTypes.ProfileUpdateResponse,
                request.ProfileId,
                SyncProfileMutationOutcome.Unavailable,
                UnavailableFailure());
        }
    }

    private async ValueTask<AgentIpcCommandResponse> GeneratePreviewAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<SyncPreviewGenerateRequest>();
        var invalid = ValidateRequest(request.ContractVersion, request.HasValidBounds);
        if (invalid is not null)
        {
            return invalid;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!TryEnterManualPreview())
        {
            return PreviewFailure(request.ProfileId, PreviewBusyFailure());
        }

        try
        {
            var generated = await _orchestration.GeneratePreviewAsync(
                new SyncProfileId(request.ProfileId),
                SyncPreviewTrigger.Manual,
                $"ipc-manual:{request.PreviewRequestId:D}",
                cancellationToken).ConfigureAwait(false);
            if (generated.IsFailure)
            {
                return PreviewFailure(request.ProfileId, SanitizeFailure(generated.Error));
            }

            var result = generated.Value;
            if (result.Preview.ProfileId.Value != request.ProfileId ||
                result.Plan.ProfileId != result.Preview.ProfileId ||
                result.Plan.PlanId != result.Preview.PlanId ||
                result.Plan.Digest != result.Preview.PlanDigest ||
                !result.Plan.HasValidDigest ||
                result.Conflicts.Count > MaximumConflictReadCount)
            {
                return PreviewFailure(request.ProfileId, IntegrityFailure());
            }

            return AgentIpcCommandResponse.Create(
                SyncManagementIpcMessageTypes.PreviewGenerateResponse,
                new SyncPreviewGenerateResponse(
                    SyncManagementIpcContract.CurrentVersion,
                    request.ProfileId,
                    MapRun(result.Preview),
                    MapPlanOverview(result.Preview.SyncRunId, result.Plan)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return PreviewFailure(request.ProfileId, UnavailableFailure());
        }
        finally
        {
            _ = Interlocked.Decrement(ref _activeManualPreviews);
        }
    }

    private async ValueTask<AgentIpcCommandResponse> GetRunStatusAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<SyncRunStatusRequest>();
        var invalid = ValidateRequest(request.ContractVersion, request.HasValidBounds);
        if (invalid is not null)
        {
            return invalid;
        }

        try
        {
            var run = await _runs.GetAsync(new SyncRunId(request.SyncRunId), cancellationToken)
                .ConfigureAwait(false);
            return run is null
                ? RunStatusFailure(request.SyncRunId, NotFoundFailure("sync.run.not_found"))
                : AgentIpcCommandResponse.Create(
                    SyncManagementIpcMessageTypes.RunStatusResponse,
                    new SyncRunStatusResponse(
                        SyncManagementIpcContract.CurrentVersion,
                        request.SyncRunId,
                        MapRun(run)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return RunStatusFailure(request.SyncRunId, UnavailableFailure());
        }
    }

    private async ValueTask<AgentIpcCommandResponse> ListRunsAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<SyncRunListRequest>();
        var invalid = ValidateRequest(request.ContractVersion, request.HasValidBounds);
        if (invalid is not null)
        {
            return invalid;
        }

        var offset = request.ContinuationToken is null
            ? 0
            : int.Parse(request.ContinuationToken, CultureInfo.InvariantCulture);
        try
        {
            var profileId = request.ProfileId is { } value ? new SyncProfileId(value) : (SyncProfileId?)null;
            var records = await _runs.ListAsync(
                profileId,
                offset,
                request.PageSize + 1,
                cancellationToken).ConfigureAwait(false);
            var hasMore = records.Count > request.PageSize;
            return AgentIpcCommandResponse.Create(
                SyncManagementIpcMessageTypes.RunListResponse,
                new SyncRunListResponse(
                    SyncManagementIpcContract.CurrentVersion,
                    records.Take(request.PageSize).Select(MapRun).ToArray(),
                    hasMore ? checked(offset + request.PageSize).ToString(CultureInfo.InvariantCulture) : null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return AgentIpcCommandResponse.Create(
                SyncManagementIpcMessageTypes.RunListResponse,
                new SyncRunListResponse(
                    SyncManagementIpcContract.CurrentVersion,
                    [],
                    null,
                    UnavailableFailure()));
        }
    }

    private async ValueTask<AgentIpcCommandResponse> GetPlanPageAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<SyncPlanPageRequest>();
        var invalid = ValidateRequest(request.ContractVersion, request.HasValidBounds);
        if (invalid is not null)
        {
            return invalid;
        }

        if (!TryDecodeOffset(request.ContinuationToken, MaximumPlanOperationCount, out var offset))
        {
            return PlanPageFailure(request.SyncRunId, ValidationFailure());
        }

        try
        {
            var run = await _runs.GetAsync(new SyncRunId(request.SyncRunId), cancellationToken)
                .ConfigureAwait(false);
            if (run is null)
            {
                return PlanPageFailure(request.SyncRunId, NotFoundFailure("sync.run.not_found"));
            }

            var persisted = await _plans.GetAsync(run.PlanId, cancellationToken).ConfigureAwait(false);
            if (persisted is null ||
                persisted.Plan.ProfileId != run.ProfileId ||
                persisted.Plan.Digest != run.PlanDigest ||
                !persisted.Plan.HasValidDigest ||
                persisted.Plan.Operations.Length > MaximumPlanOperationCount)
            {
                return PlanPageFailure(request.SyncRunId, IntegrityFailure());
            }

            var plan = persisted.Plan;
            if (offset > plan.Operations.Length)
            {
                return PlanPageFailure(request.SyncRunId, ValidationFailure());
            }

            var operations = plan.Operations
                .Skip(offset)
                .Take(request.PageSize)
                .Select(MapPlanOperation)
                .ToArray();
            var nextOffset = checked(offset + operations.Length);
            return AgentIpcCommandResponse.Create(
                SyncManagementIpcMessageTypes.PlanPageResponse,
                new SyncPlanPageResponse(
                    SyncManagementIpcContract.CurrentVersion,
                    request.SyncRunId,
                    plan.PlanId.Value,
                    plan.Digest.Sha256Hex,
                    plan.Operations.Length,
                    operations,
                    nextOffset < plan.Operations.Length ? EncodeOffset(nextOffset) : null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return PlanPageFailure(request.SyncRunId, UnavailableFailure());
        }
    }

    private async ValueTask<AgentIpcCommandResponse> GetConflictPageAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<SyncConflictPageRequest>();
        var invalid = ValidateRequest(request.ContractVersion, request.HasValidBounds);
        if (invalid is not null)
        {
            return invalid;
        }

        if (!TryDecodeOffset(request.ContinuationToken, MaximumConflictReadCount, out var offset))
        {
            return ConflictPageFailure(request.SyncRunId, ValidationFailure());
        }

        try
        {
            var runId = new SyncRunId(request.SyncRunId);
            var run = await _runs.GetAsync(runId, cancellationToken).ConfigureAwait(false);
            if (run is null)
            {
                return ConflictPageFailure(request.SyncRunId, NotFoundFailure("sync.run.not_found"));
            }

            var records = await _conflicts.ListForRunAsync(
                runId,
                request.State is { } state ? Map(state) : null,
                MaximumConflictReadCount,
                cancellationToken).ConfigureAwait(false);
            if (records.Count > MaximumConflictReadCount || offset > records.Count)
            {
                return ConflictPageFailure(request.SyncRunId, IntegrityFailure());
            }

            var conflicts = records
                .Skip(offset)
                .Take(request.PageSize)
                .Select(MapConflict)
                .ToArray();
            var nextOffset = checked(offset + conflicts.Length);
            var truncated = request.State is null
                ? run.ConflictCount > records.Count
                : records.Count == MaximumConflictReadCount;
            return AgentIpcCommandResponse.Create(
                SyncManagementIpcMessageTypes.ConflictPageResponse,
                new SyncConflictPageResponse(
                    SyncManagementIpcContract.CurrentVersion,
                    request.SyncRunId,
                    run.ConflictCount,
                    conflicts,
                    nextOffset < records.Count ? EncodeOffset(nextOffset) : null,
                    truncated));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ConflictPageFailure(request.SyncRunId, UnavailableFailure());
        }
    }

    private async ValueTask<AgentIpcCommandResponse> ApproveAndDispatchAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<SyncApproveDispatchRequest>();
        var invalid = ValidateRequest(request.ContractVersion, request.HasValidBounds);
        if (invalid is not null)
        {
            return invalid;
        }

        try
        {
            var approved = await _orchestration.ApproveAndDispatchAsync(
                new SyncRunId(request.SyncRunId),
                request.ExpectedRevision,
                request.ApprovalSha256,
                cancellationToken).ConfigureAwait(false);
            if (approved.IsFailure)
            {
                return ApproveFailure(request.SyncRunId, SanitizeFailure(approved.Error));
            }

            var run = approved.Value;
            var durablyDispatched = run.SyncRunId.Value == request.SyncRunId &&
                run.ApprovedForExecution &&
                run.DispatchEventId == request.SyncRunId &&
                string.Equals(
                    run.ApprovalChallengeSha256,
                    request.ApprovalSha256,
                    StringComparison.OrdinalIgnoreCase);
            return durablyDispatched
                ? AgentIpcCommandResponse.Create(
                    SyncManagementIpcMessageTypes.ApproveDispatchResponse,
                    new SyncApproveDispatchResponse(
                        SyncManagementIpcContract.CurrentVersion,
                        request.SyncRunId,
                        DurablyDispatched: true,
                        MapRun(run)))
                : ApproveFailure(request.SyncRunId, IntegrityFailure());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ApproveFailure(request.SyncRunId, UnavailableFailure());
        }
    }

    private static SyncProfile CreateProfile(
        SyncProfileId profileId,
        SyncProfileDraftDocument draft,
        long revision,
        DateTimeOffset createdUtc,
        DateTimeOffset updatedUtc) => new(
            profileId,
            draft.DisplayName,
            new ConnectionProfileId(draft.LeftConnectionId),
            draft.LeftRoot,
            new ConnectionProfileId(draft.RightConnectionId),
            draft.RightRoot,
            Map(draft.Direction),
            Map(draft.DeletionMode),
            Map(draft.ConflictPolicy),
            new DeletionSafetyPolicy(
                draft.MaximumDeletionCount,
                draft.MaximumDeletionPercentage),
            new TransferExecutionOptions(draft.Overwrite, draft.TransferBufferSize),
            draft.Enabled,
            revision,
            createdUtc,
            updatedUtc,
            new SyncPathFilterPolicy(
                draft.IncludeGlobs,
                draft.ExcludeGlobs,
                draft.IncludeHiddenFiles),
            Map(draft.Behavior));

    private static SyncProfileSummary MapProfileSummary(SyncProfile profile) => new(
        profile.ProfileId.Value,
        profile.DisplayName,
        profile.LeftConnectionProfileId.Value,
        profile.RightConnectionProfileId.Value,
        Map(profile.Direction),
        Map(profile.DeletionMode),
        profile.Enabled,
        profile.Revision,
        profile.UpdatedAtUtc);

    private static SyncProfileDocument MapProfile(SyncProfile profile) => new(
        profile.ProfileId.Value,
        new SyncProfileDraftDocument(
            profile.DisplayName,
            profile.LeftConnectionProfileId.Value,
            profile.LeftRoot,
            profile.RightConnectionProfileId.Value,
            profile.RightRoot,
            Map(profile.Behavior),
            Map(profile.ConflictPolicy),
            profile.FilterPolicy.IncludeGlobs,
            profile.FilterPolicy.ExcludeGlobs,
            profile.FilterPolicy.IncludeHiddenFiles,
            profile.DeletionSafetyPolicy.MaximumDeletionCount,
            profile.DeletionSafetyPolicy.MaximumDeletionPercentage,
            profile.TransferOptions.BufferSize,
            profile.Enabled),
        profile.Revision,
        profile.CreatedAtUtc,
        profile.UpdatedAtUtc);

    private static SyncRunSummary MapRun(SyncPreviewRecord run) => new(
        run.SyncRunId.Value,
        run.ProfileId.Value,
        run.Generation,
        Map(run.State.Phase),
        Map(run.State.StatusCode),
        run.State.Revision,
        run.State.TransitionedAtUtc,
        run.PlanId.Value,
        run.PlanDigest.Sha256Hex,
        run.ApprovalChallengeSha256,
        run.ConflictCount,
        run.ApprovedForExecution && run.DispatchEventId == run.SyncRunId.Value
            ? SyncIpcDispatchState.DurablyDispatched
            : SyncIpcDispatchState.NotDispatched,
        run.ApprovedForExecution && run.DispatchEventId == run.SyncRunId.Value
            ? run.ApprovedAtUtc
            : null,
        run.CreatedAtUtc,
        run.Snapshots.BaselineItemCount,
        run.Snapshots.Left.TotalItemCount,
        run.Snapshots.Right.TotalItemCount,
        run.Snapshots.Left.IsComplete,
        run.Snapshots.Right.IsComplete);

    private static SyncPlanOverview MapPlanOverview(SyncRunId runId, ImmutableSyncPlan plan) => new(
        runId.Value,
        plan.PlanId.Value,
        plan.Digest.Sha256Hex,
        plan.BaselineGeneration,
        plan.Operations.Length,
        plan.Operations.Count(static operation => operation.Kind == SyncPlanOperationKind.Copy),
        plan.Operations.Count(static operation => operation.Kind == SyncPlanOperationKind.Delete),
        plan.Operations.Count(static operation => operation.Kind == SyncPlanOperationKind.CreateDirectory),
        plan.CreatedAtUtc);

    private static SyncPlanOperationSummary MapPlanOperation(SyncPlanOperation operation) => new(
        operation.Sequence,
        Map(operation.Kind),
        operation.SourceOrTarget.ProfileId.Value,
        operation.SourceOrTarget.CanonicalRelativePath,
        operation.Destination?.ProfileId.Value,
        operation.Destination?.CanonicalRelativePath,
        operation.ExpectedLength,
        operation.IsDestructive);

    private static SyncConflictSummary MapConflict(SyncConflictRecord conflict) => new(
        conflict.ConflictId,
        conflict.RelativePath,
        SafeText(conflict.ConflictKind, SyncManagementIpcLimits.MaximumConflictKindLength, "Unknown"),
        Map(conflict.State),
        ExtractSafeReason(conflict.SafeDetailsJson),
        conflict.DetectedAtUtc,
        conflict.ResolvedAtUtc,
        conflict.Revision);

    private static string ExtractSafeReason(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("reason", out var reason) &&
                   reason.ValueKind == JsonValueKind.String
                ? SafeText(
                    reason.GetString(),
                    SyncManagementIpcLimits.MaximumConflictReasonLength,
                    "Conflict details are unavailable.")
                : "Conflict details are unavailable.";
        }
        catch (JsonException)
        {
            return "Conflict details are unavailable.";
        }
    }

    private static string SafeText(string? value, int maximumLength, string fallback) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl)
            ? fallback
            : value;

    private static AgentIpcCommandResponse MapProfileWrite(
        string responseType,
        Guid profileId,
        SyncProfileWriteResult result)
    {
        var outcome = result.Status switch
        {
            SyncProfileWriteStatus.Succeeded => SyncProfileMutationOutcome.Succeeded,
            SyncProfileWriteStatus.AlreadyApplied => SyncProfileMutationOutcome.AlreadyApplied,
            SyncProfileWriteStatus.NotFound => SyncProfileMutationOutcome.NotFound,
            SyncProfileWriteStatus.RevisionConflict => SyncProfileMutationOutcome.RevisionConflict,
            SyncProfileWriteStatus.ConstraintConflict => SyncProfileMutationOutcome.ConstraintConflict,
            _ => throw new ArgumentOutOfRangeException(nameof(result))
        };
        var failure = result.Status switch
        {
            SyncProfileWriteStatus.Succeeded or SyncProfileWriteStatus.AlreadyApplied => null,
            SyncProfileWriteStatus.NotFound => NotFoundFailure("sync.profile.not_found"),
            SyncProfileWriteStatus.RevisionConflict => ConflictFailure("sync.profile.revision_conflict"),
            _ => ConflictFailure("sync.profile.constraint_conflict")
        };
        return AgentIpcCommandResponse.Create(
            responseType,
            new SyncProfileMutationResponse(
                SyncManagementIpcContract.CurrentVersion,
                profileId,
                outcome,
                result.Profile is null ? null : MapProfile(result.Profile),
                result.ActualRevision,
                failure));
    }

    private static bool TryDecodeOffset(string? token, int maximum, out int offset)
    {
        offset = 0;
        if (token is null)
        {
            return true;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(token));
            return int.TryParse(decoded, NumberStyles.None, CultureInfo.InvariantCulture, out offset) &&
                   offset is >= 0 && offset <= maximum;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string EncodeOffset(int offset) => Convert.ToBase64String(
        Encoding.UTF8.GetBytes(offset.ToString(CultureInfo.InvariantCulture)));

    private static AgentIpcCommandResponse? ValidateRequest(int version, bool hasValidBounds)
    {
        if (!SyncManagementIpcContract.IsSupported(version))
        {
            return AgentIpcCommandResponse.Error(
                "sync.contract.unsupported",
                "The sync management contract version is not supported.");
        }

        return hasValidBounds
            ? null
            : AgentIpcCommandResponse.Error(
                "sync.request.invalid",
                "The sync management request is invalid.");
    }

    private static StorageIpcFailure SanitizeFailure(StorageFailure failure)
    {
        var category = failure.Kind switch
        {
            StorageFailureKind.Validation => StorageIpcFailureCategory.Validation,
            StorageFailureKind.NotFound => StorageIpcFailureCategory.NotFound,
            StorageFailureKind.Conflict => StorageIpcFailureCategory.Conflict,
            StorageFailureKind.Unsupported => StorageIpcFailureCategory.Unsupported,
            StorageFailureKind.Unauthorized => StorageIpcFailureCategory.Unauthorized,
            StorageFailureKind.Unavailable => StorageIpcFailureCategory.Unavailable,
            StorageFailureKind.Timeout => StorageIpcFailureCategory.Timeout,
            StorageFailureKind.Cancelled => StorageIpcFailureCategory.Cancelled,
            StorageFailureKind.Integrity => StorageIpcFailureCategory.Integrity,
            StorageFailureKind.Security => StorageIpcFailureCategory.Security,
            StorageFailureKind.Provider => StorageIpcFailureCategory.Provider,
            _ => StorageIpcFailureCategory.Unexpected
        };
        var code = IsSafeFailureCode(failure.Code) ? failure.Code : "sync.operation.failed";
        return new StorageIpcFailure(code, category, SafeFailureMessage(category), failure.IsTransient);
    }

    private static bool IsSafeFailureCode(string code) =>
        !string.IsNullOrWhiteSpace(code) &&
        code.Length <= StorageIpcLimits.MaximumFailureCodeLength &&
        code.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static string SafeFailureMessage(StorageIpcFailureCategory category) => category switch
    {
        StorageIpcFailureCategory.Validation => "The sync request was invalid.",
        StorageIpcFailureCategory.NotFound => "The requested sync profile or run was not found.",
        StorageIpcFailureCategory.Conflict => "The sync profile, preview, or approval changed before the request was applied.",
        StorageIpcFailureCategory.Unsupported => "The requested sync operation is not supported safely.",
        StorageIpcFailureCategory.Unauthorized => "A saved endpoint rejected its configured credentials.",
        StorageIpcFailureCategory.Unavailable => "The sync service or an endpoint is temporarily unavailable.",
        StorageIpcFailureCategory.Timeout => "A sync endpoint did not respond in time.",
        StorageIpcFailureCategory.Cancelled => "The sync operation was cancelled.",
        StorageIpcFailureCategory.Integrity => "The immutable sync preview failed an integrity check.",
        StorageIpcFailureCategory.Security => "A sync endpoint requires a security or trust decision.",
        StorageIpcFailureCategory.Provider => "A sync endpoint could not complete the requested operation.",
        _ => "The sync operation could not be completed."
    };

    private static StorageIpcFailure ValidationFailure() => new(
        "sync.request.invalid",
        StorageIpcFailureCategory.Validation,
        "The sync management request is invalid.",
        IsTransient: false);

    private static StorageIpcFailure NotFoundFailure(string code) => new(
        code,
        StorageIpcFailureCategory.NotFound,
        "The requested sync profile or run was not found.",
        IsTransient: false);

    private static StorageIpcFailure ConflictFailure(string code) => new(
        code,
        StorageIpcFailureCategory.Conflict,
        "The sync resource changed before the request was applied.",
        IsTransient: true);

    private static StorageIpcFailure IntegrityFailure() => new(
        "sync.response.integrity_failed",
        StorageIpcFailureCategory.Integrity,
        "The immutable sync data could not be exposed safely.",
        IsTransient: false);

    private static StorageIpcFailure UnavailableFailure() => new(
        "sync.service.unavailable",
        StorageIpcFailureCategory.Unavailable,
        "The sync management service is temporarily unavailable.",
        IsTransient: true);

    private static StorageIpcFailure PreviewBusyFailure() => new(
        "sync.preview.busy",
        StorageIpcFailureCategory.Unavailable,
        "The agent is already generating the maximum number of sync previews. Try again shortly.",
        IsTransient: true);

    private bool TryEnterManualPreview()
    {
        while (true)
        {
            var active = Volatile.Read(ref _activeManualPreviews);
            if (active >= MaximumConcurrentManualPreviews)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _activeManualPreviews, active + 1, active) == active)
            {
                return true;
            }
        }
    }

    private static AgentIpcCommandResponse ProfileListFailure(StorageIpcFailure failure) =>
        AgentIpcCommandResponse.Create(
            SyncManagementIpcMessageTypes.ProfileListResponse,
            new SyncProfileListResponse(SyncManagementIpcContract.CurrentVersion, [], failure));

    private static AgentIpcCommandResponse ProfileGetFailure(Guid profileId, StorageIpcFailure failure) =>
        AgentIpcCommandResponse.Create(
            SyncManagementIpcMessageTypes.ProfileGetResponse,
            new SyncProfileGetResponse(
                SyncManagementIpcContract.CurrentVersion,
                profileId,
                Profile: null,
                failure));

    private static AgentIpcCommandResponse ProfileMutationFailure(
        string responseType,
        Guid profileId,
        SyncProfileMutationOutcome outcome,
        StorageIpcFailure failure) => AgentIpcCommandResponse.Create(
            responseType,
            new SyncProfileMutationResponse(
                SyncManagementIpcContract.CurrentVersion,
                profileId,
                outcome,
                Failure: failure));

    private static AgentIpcCommandResponse PreviewFailure(Guid profileId, StorageIpcFailure failure) =>
        AgentIpcCommandResponse.Create(
            SyncManagementIpcMessageTypes.PreviewGenerateResponse,
            new SyncPreviewGenerateResponse(
                SyncManagementIpcContract.CurrentVersion,
                profileId,
                Run: null,
                Plan: null,
                failure));

    private static AgentIpcCommandResponse RunStatusFailure(Guid runId, StorageIpcFailure failure) =>
        AgentIpcCommandResponse.Create(
            SyncManagementIpcMessageTypes.RunStatusResponse,
            new SyncRunStatusResponse(
                SyncManagementIpcContract.CurrentVersion,
                runId,
                Run: null,
                failure));

    private static AgentIpcCommandResponse PlanPageFailure(Guid runId, StorageIpcFailure failure) =>
        AgentIpcCommandResponse.Create(
            SyncManagementIpcMessageTypes.PlanPageResponse,
            new SyncPlanPageResponse(
                SyncManagementIpcContract.CurrentVersion,
                runId,
                Guid.Empty,
                string.Empty,
                TotalOperations: 0,
                Operations: [],
                ContinuationToken: null,
                failure));

    private static AgentIpcCommandResponse ConflictPageFailure(Guid runId, StorageIpcFailure failure) =>
        AgentIpcCommandResponse.Create(
            SyncManagementIpcMessageTypes.ConflictPageResponse,
            new SyncConflictPageResponse(
                SyncManagementIpcContract.CurrentVersion,
                runId,
                ReportedConflictCount: 0,
                Conflicts: [],
                ContinuationToken: null,
                IsTruncatedAtSource: false,
                failure));

    private static AgentIpcCommandResponse ApproveFailure(Guid runId, StorageIpcFailure failure) =>
        AgentIpcCommandResponse.Create(
            SyncManagementIpcMessageTypes.ApproveDispatchResponse,
            new SyncApproveDispatchResponse(
                SyncManagementIpcContract.CurrentVersion,
                runId,
                DurablyDispatched: false,
                Run: null,
                failure));

    private static SyncDirection Map(SyncIpcDirection value) => value switch
    {
        SyncIpcDirection.LeftToRight => SyncDirection.LeftToRight,
        SyncIpcDirection.RightToLeft => SyncDirection.RightToLeft,
        SyncIpcDirection.TwoWay => SyncDirection.TwoWay,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static SyncIpcDirection Map(SyncDirection value) => value switch
    {
        SyncDirection.LeftToRight => SyncIpcDirection.LeftToRight,
        SyncDirection.RightToLeft => SyncIpcDirection.RightToLeft,
        SyncDirection.TwoWay => SyncIpcDirection.TwoWay,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static SyncDeletionMode Map(SyncIpcDeletionMode value) => value switch
    {
        SyncIpcDeletionMode.Disabled => SyncDeletionMode.Disabled,
        SyncIpcDeletionMode.Mirror => SyncDeletionMode.Mirror,
        SyncIpcDeletionMode.Propagate => SyncDeletionMode.Propagate,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static SyncIpcDeletionMode Map(SyncDeletionMode value) => value switch
    {
        SyncDeletionMode.Disabled => SyncIpcDeletionMode.Disabled,
        SyncDeletionMode.Mirror => SyncIpcDeletionMode.Mirror,
        SyncDeletionMode.Propagate => SyncIpcDeletionMode.Propagate,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static SyncConflictPolicy Map(SyncIpcConflictPolicy value) => value switch
    {
        SyncIpcConflictPolicy.Block => SyncConflictPolicy.Block,
        SyncIpcConflictPolicy.KeepBoth => SyncConflictPolicy.KeepBoth,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static SyncIpcConflictPolicy Map(SyncConflictPolicy value) => value switch
    {
        SyncConflictPolicy.Block => SyncIpcConflictPolicy.Block,
        SyncConflictPolicy.KeepBoth => SyncIpcConflictPolicy.KeepBoth,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static SyncBehavior Map(SyncIpcBehavior value) => value switch
    {
        SyncIpcBehavior.CopyNewFilesAToB => SyncBehavior.CopyNewFilesAToB,
        SyncIpcBehavior.UpdateAToB => SyncBehavior.UpdateAToB,
        SyncIpcBehavior.MirrorAToB => SyncBehavior.MirrorAToB,
        SyncIpcBehavior.CopyNewFilesBToA => SyncBehavior.CopyNewFilesBToA,
        SyncIpcBehavior.UpdateBToA => SyncBehavior.UpdateBToA,
        SyncIpcBehavior.MirrorBToA => SyncBehavior.MirrorBToA,
        SyncIpcBehavior.TwoWaySync => SyncBehavior.TwoWaySync,
        SyncIpcBehavior.TwoWayWithDeletionPropagation => SyncBehavior.TwoWayWithDeletionPropagation,
        SyncIpcBehavior.CompareOnly => SyncBehavior.CompareOnly,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static SyncIpcBehavior Map(SyncBehavior value) => value switch
    {
        SyncBehavior.CopyNewFilesAToB => SyncIpcBehavior.CopyNewFilesAToB,
        SyncBehavior.UpdateAToB => SyncIpcBehavior.UpdateAToB,
        SyncBehavior.MirrorAToB => SyncIpcBehavior.MirrorAToB,
        SyncBehavior.CopyNewFilesBToA => SyncIpcBehavior.CopyNewFilesBToA,
        SyncBehavior.UpdateBToA => SyncIpcBehavior.UpdateBToA,
        SyncBehavior.MirrorBToA => SyncIpcBehavior.MirrorBToA,
        SyncBehavior.TwoWaySync => SyncIpcBehavior.TwoWaySync,
        SyncBehavior.TwoWayWithDeletionPropagation => SyncIpcBehavior.TwoWayWithDeletionPropagation,
        SyncBehavior.CompareOnly => SyncIpcBehavior.CompareOnly,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static SyncIpcRunPhase Map(SyncRunPhase value) =>
        Enum.Parse<SyncIpcRunPhase>(value.ToString(), ignoreCase: false);

    private static SyncIpcStatusCode Map(SyncStatusCode value) =>
        Enum.Parse<SyncIpcStatusCode>(value.ToString(), ignoreCase: false);

    private static SyncIpcPlanOperationKind Map(SyncPlanOperationKind value) =>
        Enum.Parse<SyncIpcPlanOperationKind>(value.ToString(), ignoreCase: false);

    private static SyncConflictState Map(SyncIpcConflictState value) =>
        Enum.Parse<SyncConflictState>(value.ToString(), ignoreCase: false);

    private static SyncIpcConflictState Map(SyncConflictState value) =>
        Enum.Parse<SyncIpcConflictState>(value.ToString(), ignoreCase: false);
}
