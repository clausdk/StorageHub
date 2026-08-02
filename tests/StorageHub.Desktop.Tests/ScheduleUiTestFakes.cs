using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

internal sealed class FakeScheduleManagementClient : IScheduleManagementAgentClient
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly Dictionary<Guid, ScheduleDocument> _schedules = [];

    public int ListCount { get; private set; }
    public int GetCount { get; private set; }
    public int CreateCount { get; private set; }
    public int UpdateCount { get; private set; }
    public int SetEnabledCount { get; private set; }
    public int DeleteCount { get; private set; }

    public Task<ScheduleListResponse> ListAsync(
        ScheduleListRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ListCount++;
        return Task.FromResult(new ScheduleListResponse(
            ScheduleManagementIpcContract.CurrentVersion,
            _schedules.Values
                .Where(schedule => request.IncludeDisabled || schedule.Enabled)
                .Take(request.MaximumCount)
                .ToArray()));
    }

    public Task<ScheduleGetResponse> GetAsync(
        ScheduleGetRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GetCount++;
        _schedules.TryGetValue(request.ScheduleId, out var schedule);
        return Task.FromResult(new ScheduleGetResponse(
            ScheduleManagementIpcContract.CurrentVersion,
            request.ScheduleId,
            schedule,
            schedule is null ? NotFound() : null));
    }

    public Task<ScheduleMutationResponse> CreateAsync(
        ScheduleCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CreateCount++;
        var schedule = FromDraft(request.ScheduleId, request.Draft, revision: 1);
        _schedules.Add(schedule.ScheduleId, schedule);
        return Task.FromResult(Succeeded(schedule));
    }

    public Task<ScheduleMutationResponse> UpdateAsync(
        ScheduleUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UpdateCount++;
        var current = _schedules[request.ScheduleId];
        if (current.Revision != request.ExpectedRevision)
        {
            return Task.FromResult(Conflict(current));
        }

        var updated = FromDraft(request.ScheduleId, request.Draft, current.Revision + 1);
        _schedules[request.ScheduleId] = updated;
        return Task.FromResult(Succeeded(updated));
    }

    public Task<ScheduleMutationResponse> SetEnabledAsync(
        ScheduleSetEnabledRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetEnabledCount++;
        var current = _schedules[request.ScheduleId];
        if (current.Revision != request.ExpectedRevision)
        {
            return Task.FromResult(Conflict(current));
        }

        var updated = current with
        {
            Enabled = request.Enabled,
            NextOccurrenceUtc = request.Enabled ? Now.AddHours(1) : null,
            QueuedOccurrenceUtc = null,
            Revision = current.Revision + 1
        };
        _schedules[request.ScheduleId] = updated;
        return Task.FromResult(Succeeded(updated));
    }

    public Task<ScheduleMutationResponse> DeleteAsync(
        ScheduleDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeleteCount++;
        var current = _schedules[request.ScheduleId];
        if (current.Revision != request.ExpectedRevision)
        {
            return Task.FromResult(Conflict(current));
        }

        _schedules.Remove(request.ScheduleId);
        return Task.FromResult(new ScheduleMutationResponse(
            ScheduleManagementIpcContract.CurrentVersion,
            request.ScheduleId,
            ScheduleMutationOutcome.Succeeded,
            Schedule: null,
            ActualRevision: request.ExpectedRevision));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static ScheduleDocument FromDraft(Guid scheduleId, ScheduleDraftDocument draft, long revision) => new(
        scheduleId,
        draft.ProfileId,
        "Documents sync",
        draft.CronExpression,
        draft.TimeZoneId,
        draft.MisfireGraceSeconds,
        draft.QueueOneWhileRunning,
        draft.Enabled,
        draft.Enabled ? Now.AddHours(1) : null,
        QueuedOccurrenceUtc: null,
        IsBusy: false,
        LastRunOutcome: null,
        LastErrorCode: null,
        revision,
        ScheduleIpcExecutionMode.PreviewOnly);

    private static ScheduleMutationResponse Succeeded(ScheduleDocument schedule) => new(
        ScheduleManagementIpcContract.CurrentVersion,
        schedule.ScheduleId,
        ScheduleMutationOutcome.Succeeded,
        schedule,
        schedule.Revision);

    private static ScheduleMutationResponse Conflict(ScheduleDocument schedule) => new(
        ScheduleManagementIpcContract.CurrentVersion,
        schedule.ScheduleId,
        ScheduleMutationOutcome.RevisionConflict,
        Schedule: null,
        ActualRevision: schedule.Revision,
        Failure: new StorageIpcFailure(
            "schedule.revision_conflict",
            StorageIpcFailureCategory.Conflict,
            "The schedule changed before the request was applied.",
            IsTransient: true));

    private static StorageIpcFailure NotFound() => new(
        "schedule.not_found",
        StorageIpcFailureCategory.NotFound,
        "The requested schedule was not found.",
        IsTransient: false);
}
