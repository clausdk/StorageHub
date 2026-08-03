using StorageHub.Agent.Ipc;
using StorageHub.Agent.Scheduling;
using StorageHub.Contracts.Ipc;
using StorageHub.Domain.Identifiers;
using StorageHub.Persistence.Scheduling;

namespace StorageHub.Agent.Windows;

/// <summary>
/// Bounded normal-pipe schedule management. The scheduler remains preview-only, while ownership
/// IDs and fencing evidence remain exclusively inside persistence.
/// </summary>
public sealed class ScheduleManagementIpcCommandService : IAgentIpcCommandHandler
{
    private readonly ISyncScheduleManagementRepository _repository;

    public ScheduleManagementIpcCommandService(ISyncScheduleManagementRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public bool CanHandle(string messageType) => messageType is
        ScheduleManagementIpcMessageTypes.ListRequest or
        ScheduleManagementIpcMessageTypes.GetRequest or
        ScheduleManagementIpcMessageTypes.CreateRequest or
        ScheduleManagementIpcMessageTypes.UpdateRequest or
        ScheduleManagementIpcMessageTypes.SetEnabledRequest or
        ScheduleManagementIpcMessageTypes.DeleteRequest;

    public ValueTask<AgentIpcCommandResponse> HandleAsync(
        IpcEnvelope request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.MessageType switch
        {
            ScheduleManagementIpcMessageTypes.ListRequest => ListAsync(request, cancellationToken),
            ScheduleManagementIpcMessageTypes.GetRequest => GetAsync(request, cancellationToken),
            ScheduleManagementIpcMessageTypes.CreateRequest => CreateAsync(request, cancellationToken),
            ScheduleManagementIpcMessageTypes.UpdateRequest => UpdateAsync(request, cancellationToken),
            ScheduleManagementIpcMessageTypes.SetEnabledRequest => SetEnabledAsync(request, cancellationToken),
            ScheduleManagementIpcMessageTypes.DeleteRequest => DeleteAsync(request, cancellationToken),
            _ => ValueTask.FromResult(AgentIpcCommandResponse.Error(
                "ipc.message.unsupported",
                "The requested IPC operation is not supported by this agent version."))
        };
    }

    private async ValueTask<AgentIpcCommandResponse> ListAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<ScheduleListRequest>();
        var invalid = ValidateRequest(request.ContractVersion, request.HasValidBounds);
        if (invalid is not null)
        {
            return invalid;
        }

        try
        {
            var schedules = await _repository.ListAsync(
                request.IncludeDisabled,
                request.MaximumCount,
                cancellationToken).ConfigureAwait(false);
            if (schedules.Count > request.MaximumCount ||
                schedules.Count > ScheduleManagementIpcLimits.MaximumScheduleResults)
            {
                return ListFailure(IntegrityFailure());
            }

            return AgentIpcCommandResponse.Create(
                ScheduleManagementIpcMessageTypes.ListResponse,
                new ScheduleListResponse(
                    ScheduleManagementIpcContract.CurrentVersion,
                    schedules.Select(Map).ToArray()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ListFailure(UnavailableFailure());
        }
    }

    private async ValueTask<AgentIpcCommandResponse> GetAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<ScheduleGetRequest>();
        var invalid = ValidateRequest(request.ContractVersion, request.HasValidBounds);
        if (invalid is not null)
        {
            return invalid;
        }

        try
        {
            var schedule = await _repository.GetAsync(
                new ScheduledSyncJobId(request.ScheduleId),
                cancellationToken).ConfigureAwait(false);
            return schedule is null
                ? GetFailure(request.ScheduleId, NotFoundFailure())
                : AgentIpcCommandResponse.Create(
                    ScheduleManagementIpcMessageTypes.GetResponse,
                    new ScheduleGetResponse(
                        ScheduleManagementIpcContract.CurrentVersion,
                        request.ScheduleId,
                        Map(schedule)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return GetFailure(request.ScheduleId, UnavailableFailure());
        }
    }

    private async ValueTask<AgentIpcCommandResponse> CreateAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<ScheduleCreateRequest>();
        var invalid = ValidateRequest(request.ContractVersion, request.HasValidBounds);
        if (invalid is not null)
        {
            return invalid;
        }

        try
        {
            var result = await _repository.CreateAsync(
                new ScheduledSyncJobId(request.ScheduleId),
                Map(request.Draft),
                cancellationToken).ConfigureAwait(false);
            return MapMutation(
                ScheduleManagementIpcMessageTypes.CreateResponse,
                request.ScheduleId,
                result,
                requiresDocumentOnSuccess: true);
        }
        catch (ArgumentException)
        {
            return MutationFailure(
                ScheduleManagementIpcMessageTypes.CreateResponse,
                request.ScheduleId,
                ScheduleMutationOutcome.ConstraintConflict,
                ValidationFailure());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return MutationFailure(
                ScheduleManagementIpcMessageTypes.CreateResponse,
                request.ScheduleId,
                ScheduleMutationOutcome.Unavailable,
                UnavailableFailure());
        }
    }

    private async ValueTask<AgentIpcCommandResponse> UpdateAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<ScheduleUpdateRequest>();
        var invalid = ValidateRequest(request.ContractVersion, request.HasValidBounds);
        if (invalid is not null)
        {
            return invalid;
        }

        try
        {
            var result = await _repository.UpdateAsync(
                new ScheduledSyncJobId(request.ScheduleId),
                request.ExpectedRevision,
                Map(request.Draft),
                cancellationToken).ConfigureAwait(false);
            return MapMutation(
                ScheduleManagementIpcMessageTypes.UpdateResponse,
                request.ScheduleId,
                result,
                requiresDocumentOnSuccess: true);
        }
        catch (ArgumentException)
        {
            return MutationFailure(
                ScheduleManagementIpcMessageTypes.UpdateResponse,
                request.ScheduleId,
                ScheduleMutationOutcome.ConstraintConflict,
                ValidationFailure());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return MutationFailure(
                ScheduleManagementIpcMessageTypes.UpdateResponse,
                request.ScheduleId,
                ScheduleMutationOutcome.Unavailable,
                UnavailableFailure());
        }
    }

    private async ValueTask<AgentIpcCommandResponse> SetEnabledAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<ScheduleSetEnabledRequest>();
        var invalid = ValidateRequest(request.ContractVersion, request.HasValidBounds);
        if (invalid is not null)
        {
            return invalid;
        }

        try
        {
            var result = await _repository.SetEnabledAsync(
                new ScheduledSyncJobId(request.ScheduleId),
                request.ExpectedRevision,
                request.Enabled,
                cancellationToken).ConfigureAwait(false);
            return MapMutation(
                ScheduleManagementIpcMessageTypes.SetEnabledResponse,
                request.ScheduleId,
                result,
                requiresDocumentOnSuccess: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return MutationFailure(
                ScheduleManagementIpcMessageTypes.SetEnabledResponse,
                request.ScheduleId,
                ScheduleMutationOutcome.Unavailable,
                UnavailableFailure());
        }
    }

    private async ValueTask<AgentIpcCommandResponse> DeleteAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<ScheduleDeleteRequest>();
        var invalid = ValidateRequest(request.ContractVersion, request.HasValidBounds);
        if (invalid is not null)
        {
            return invalid;
        }

        try
        {
            var result = await _repository.DeleteAsync(
                new ScheduledSyncJobId(request.ScheduleId),
                request.ExpectedRevision,
                cancellationToken).ConfigureAwait(false);
            return MapMutation(
                ScheduleManagementIpcMessageTypes.DeleteResponse,
                request.ScheduleId,
                result,
                requiresDocumentOnSuccess: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return MutationFailure(
                ScheduleManagementIpcMessageTypes.DeleteResponse,
                request.ScheduleId,
                ScheduleMutationOutcome.Unavailable,
                UnavailableFailure());
        }
    }

    private static AgentIpcCommandResponse MapMutation(
        string responseType,
        Guid scheduleId,
        SyncScheduleManagementMutationResult result,
        bool requiresDocumentOnSuccess)
    {
        var outcome = result.Status switch
        {
            SyncScheduleManagementMutationStatus.Applied => ScheduleMutationOutcome.Succeeded,
            SyncScheduleManagementMutationStatus.AlreadyApplied => ScheduleMutationOutcome.AlreadyApplied,
            SyncScheduleManagementMutationStatus.NotFound => ScheduleMutationOutcome.NotFound,
            SyncScheduleManagementMutationStatus.RevisionConflict => ScheduleMutationOutcome.RevisionConflict,
            SyncScheduleManagementMutationStatus.ActiveRun => ScheduleMutationOutcome.ActiveRun,
            SyncScheduleManagementMutationStatus.ConstraintConflict => ScheduleMutationOutcome.ConstraintConflict,
            _ => throw new ArgumentOutOfRangeException(nameof(result))
        };
        var succeeded = result.Status is
            SyncScheduleManagementMutationStatus.Applied or
            SyncScheduleManagementMutationStatus.AlreadyApplied;
        if (succeeded && requiresDocumentOnSuccess && result.Schedule is null ||
            succeeded && !requiresDocumentOnSuccess && result.Schedule is not null)
        {
            return MutationFailure(
                responseType,
                scheduleId,
                ScheduleMutationOutcome.Unavailable,
                IntegrityFailure());
        }

        var failure = result.Status switch
        {
            SyncScheduleManagementMutationStatus.Applied or
            SyncScheduleManagementMutationStatus.AlreadyApplied => null,
            SyncScheduleManagementMutationStatus.NotFound => NotFoundFailure(),
            SyncScheduleManagementMutationStatus.RevisionConflict => ConflictFailure(
                "schedule.revision_conflict",
                "The schedule changed before the request was applied."),
            SyncScheduleManagementMutationStatus.ActiveRun => ConflictFailure(
                "schedule.active_run",
                "The schedule has an active preview run. Wait for it to finish, then refresh."),
            _ => ConflictFailure(
                "schedule.constraint_conflict",
                "The selected profile or schedule settings cannot be used.")
        };
        return AgentIpcCommandResponse.Create(
            responseType,
            new ScheduleMutationResponse(
                ScheduleManagementIpcContract.CurrentVersion,
                scheduleId,
                outcome,
                result.Schedule is null ? null : Map(result.Schedule),
                result.ActualRevision,
                failure));
    }

    private static SyncScheduleManagementDraft Map(ScheduleDraftDocument draft) => new(
        new SyncProfileId(draft.ProfileId),
        draft.CronExpression,
        draft.TimeZoneId,
        TimeSpan.FromSeconds(draft.MisfireGraceSeconds),
        draft.QueueOneWhileRunning,
        draft.Enabled,
        draft.ExecutionMode == ScheduleIpcExecutionMode.SafeAutomatic
            ? SyncScheduleExecutionMode.SafeAutomatic
            : SyncScheduleExecutionMode.PreviewOnly);

    private static ScheduleDocument Map(SyncScheduleManagementRecord schedule)
    {
        var graceSeconds = checked((int)schedule.MisfireGrace.TotalSeconds);
        return new ScheduleDocument(
            schedule.ScheduleId.Value,
            schedule.ProfileId.Value,
            SafeText(
                schedule.ProfileDisplayName,
                ScheduleManagementIpcLimits.MaximumProfileDisplayNameLength,
                "Sync profile"),
            SafeText(
                schedule.CronExpression,
                ScheduleManagementIpcLimits.MaximumCronExpressionLength,
                "Invalid"),
            SafeText(
                schedule.TimeZoneId,
                ScheduleManagementIpcLimits.MaximumTimeZoneIdLength,
                "Invalid"),
            graceSeconds,
            schedule.QueueOneWhileRunning,
            schedule.Enabled,
            schedule.NextOccurrenceUtc,
            schedule.QueuedOccurrenceUtc,
            schedule.IsBusy,
            SafeNullableText(schedule.LastRunOutcome, ScheduleManagementIpcLimits.MaximumOutcomeLength),
            SafeNullableText(schedule.LastErrorCode, ScheduleManagementIpcLimits.MaximumErrorCodeLength),
            schedule.Revision,
            schedule.ExecutionMode == SyncScheduleExecutionMode.SafeAutomatic
                ? ScheduleIpcExecutionMode.SafeAutomatic
                : ScheduleIpcExecutionMode.PreviewOnly);
    }

    private static AgentIpcCommandResponse? ValidateRequest(int version, bool hasValidBounds)
    {
        if (ScheduleManagementIpcContract.IsSupported(version) && hasValidBounds)
        {
            return null;
        }

        return AgentIpcCommandResponse.Error(
            "schedule.request.invalid",
            "The schedule management request is invalid.");
    }

    private static AgentIpcCommandResponse ListFailure(StorageIpcFailure failure) =>
        AgentIpcCommandResponse.Create(
            ScheduleManagementIpcMessageTypes.ListResponse,
            new ScheduleListResponse(ScheduleManagementIpcContract.CurrentVersion, [], failure));

    private static AgentIpcCommandResponse GetFailure(Guid scheduleId, StorageIpcFailure failure) =>
        AgentIpcCommandResponse.Create(
            ScheduleManagementIpcMessageTypes.GetResponse,
            new ScheduleGetResponse(
                ScheduleManagementIpcContract.CurrentVersion,
                scheduleId,
                Schedule: null,
                failure));

    private static AgentIpcCommandResponse MutationFailure(
        string responseType,
        Guid scheduleId,
        ScheduleMutationOutcome outcome,
        StorageIpcFailure failure) => AgentIpcCommandResponse.Create(
        responseType,
        new ScheduleMutationResponse(
            ScheduleManagementIpcContract.CurrentVersion,
            scheduleId,
            outcome,
            Failure: failure));

    private static StorageIpcFailure ValidationFailure() => new(
        "schedule.definition.invalid",
        StorageIpcFailureCategory.Validation,
        "The cron expression, time zone, or schedule bounds are invalid.",
        IsTransient: false);

    private static StorageIpcFailure NotFoundFailure() => new(
        "schedule.not_found",
        StorageIpcFailureCategory.NotFound,
        "The requested schedule was not found.",
        IsTransient: false);

    private static StorageIpcFailure ConflictFailure(string code, string message) => new(
        code,
        StorageIpcFailureCategory.Conflict,
        message,
        IsTransient: true);

    private static StorageIpcFailure IntegrityFailure() => new(
        "schedule.response.integrity_failed",
        StorageIpcFailureCategory.Integrity,
        "The schedule data could not be exposed safely.",
        IsTransient: false);

    private static StorageIpcFailure UnavailableFailure() => new(
        "schedule.service.unavailable",
        StorageIpcFailureCategory.Unavailable,
        "The schedule management service is temporarily unavailable.",
        IsTransient: true);

    private static string SafeText(string? value, int maximumLength, string fallback) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl)
            ? fallback
            : value;

    private static string? SafeNullableText(string? value, int maximumLength) =>
        value is null ? null : SafeText(value, maximumLength, fallback: string.Empty) is { Length: > 0 } safe
            ? safe
            : null;
}
