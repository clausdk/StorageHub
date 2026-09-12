using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

/// <summary>
/// Hand-written stand-ins for the four agent clients a settings transfer uses. They keep just
/// enough state to answer a listing and record what was written, which is what the import logic
/// is actually judged on.
/// </summary>
internal sealed class FakeSettingsStorageClient : IRemoteStorageAgentClient
{
    private readonly List<ConnectionSummary> _connections = [];

    internal IReadOnlyList<ConnectionSummary> Connections => _connections;

    internal IEnumerable<Guid> Ids => _connections.Select(connection => connection.ConnectionId);

    internal Guid Add(string name, long version = 1)
    {
        var id = Guid.NewGuid();
        _connections.Add(new ConnectionSummary(
            id, name, StorageConnectionProvider.Local, "/", [], false, true, null, null, version));
        return id;
    }

    internal void Replace(Guid id, ConnectionProfileDraft draft)
    {
        var index = _connections.FindIndex(connection => connection.ConnectionId == id);
        if (index < 0) return;
        _connections[index] = _connections[index] with { DisplayName = draft.Metadata.DisplayName };
    }

    public Task<ConnectionListResponse> ListConnectionsAsync(
        ConnectionListRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ConnectionListResponse(request.ContractVersion, [.. _connections]));

    public Task<ConnectionTestResponse> TestConnectionAsync(
        ConnectionTestRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A settings transfer never tests a connection.");

    public Task<StorageListPageResponse> ListStorageAsync(
        StorageListPageRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A settings transfer never browses storage.");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeSettingsProfileClient(FakeSettingsStorageClient storage) : IRemoteConnectionProfileClient
{
    private readonly Dictionary<Guid, ConnectionProfileDraft> _drafts = [];

    internal List<ConnectionProfileDraft> Created { get; } = [];

    internal List<ConnectionProfileDraft> Updated { get; } = [];

    public Task<ConnectionProfileGetResponse> GetAsync(
        ConnectionProfileGetRequest request,
        CancellationToken cancellationToken = default)
    {
        var summary = storage.Connections.FirstOrDefault(c => c.ConnectionId == request.ConnectionId);
        if (summary is null)
        {
            return Task.FromResult(new ConnectionProfileGetResponse(request.ContractVersion, null));
        }

        var draft = _drafts.GetValueOrDefault(request.ConnectionId) ?? new ConnectionProfileDraft(
            new ConnectionProfileMetadataDocument(summary.DisplayName, "/", [], IsFavorite: false),
            new ConnectionEndpointDocument(StorageConnectionProvider.Local, RootPath: @"C:\data"),
            new ConnectionAuthenticationDocument(ConnectionAuthenticationKind.None),
            new ConnectionOperationalOptionsDocument());
        return Task.FromResult(new ConnectionProfileGetResponse(
            request.ContractVersion,
            new ConnectionProfileDocument(
                summary.ConnectionId, summary.Version, draft, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)));
    }

    public Task<ConnectionProfileWriteResponse> CreateAsync(
        ConnectionProfileCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        Created.Add(request.Draft);
        // The agent mints the identifier, which is exactly why imports need a remap table.
        var id = storage.Add(request.Draft.Metadata.DisplayName);
        _drafts[id] = request.Draft;
        return Task.FromResult(new ConnectionProfileWriteResponse(
            request.ContractVersion,
            ConnectionProfileWriteStatus.Succeeded,
            new ConnectionProfileDocument(id, 1, request.Draft, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)));
    }

    public Task<ConnectionProfileWriteResponse> UpdateAsync(
        ConnectionProfileUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        Updated.Add(request.Draft);
        _drafts[request.ConnectionId] = request.Draft;
        storage.Replace(request.ConnectionId, request.Draft);
        return Task.FromResult(new ConnectionProfileWriteResponse(
            request.ContractVersion,
            ConnectionProfileWriteStatus.Succeeded,
            new ConnectionProfileDocument(
                request.ConnectionId,
                request.ExpectedVersion + 1,
                request.Draft,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch)));
    }

    public Task<ConnectionProfileWriteResponse> DeleteAsync(
        ConnectionProfileDeleteRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("An import never deletes.");

    public Task<ConnectionTrustGetResponse> GetTrustAsync(
        ConnectionTrustGetRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Trust decisions are never exported.");

    public Task<ConnectionSshHostKeyDiscoveryResponse> DiscoverSshHostKeyAsync(
        ConnectionSshHostKeyDiscoveryRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A settings transfer never discovers host keys.");

    public Task<ConnectionTrustMutationResponse> DecideTrustAsync(
        ConnectionTrustDecisionRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Trust decisions are never imported.");

    public Task<ConnectionTrustMutationResponse> RolloverTrustAsync(
        ConnectionTrustRolloverRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Trust decisions are never imported.");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeSettingsSyncClient : ISyncManagementAgentClient
{
    private readonly List<SyncProfileSummary> _profiles = [];
    private readonly Dictionary<Guid, SyncProfileDraftDocument> _drafts = [];

    internal List<SyncProfileDraftDocument> Created { get; } = [];

    internal List<SyncProfileDraftDocument> Updated { get; } = [];

    internal Guid Add(string name)
    {
        var id = Guid.NewGuid();
        _profiles.Add(new SyncProfileSummary(
            id, name, Guid.NewGuid(), Guid.NewGuid(),
            SyncIpcDirection.LeftToRight, SyncIpcDeletionMode.Disabled, true, 1, DateTimeOffset.UnixEpoch));
        return id;
    }

    public Task<SyncProfileListResponse> ListProfilesAsync(
        SyncProfileListRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new SyncProfileListResponse(request.ContractVersion, [.. _profiles]));

    public Task<SyncProfileGetResponse> GetProfileAsync(
        SyncProfileGetRequest request,
        CancellationToken cancellationToken = default)
    {
        var summary = _profiles.FirstOrDefault(profile => profile.ProfileId == request.ProfileId);
        if (summary is null)
        {
            return Task.FromResult(new SyncProfileGetResponse(request.ContractVersion, request.ProfileId, null));
        }

        var draft = _drafts.GetValueOrDefault(summary.ProfileId) ?? new SyncProfileDraftDocument(
            summary.DisplayName, summary.LeftConnectionId, "/left", summary.RightConnectionId, "/right",
            SyncIpcDirection.LeftToRight, SyncIpcDeletionMode.Disabled, SyncIpcConflictPolicy.Block,
            10, 5m, true, 65536, true);
        return Task.FromResult(new SyncProfileGetResponse(
            request.ContractVersion,
            summary.ProfileId,
            new SyncProfileDocument(
                summary.ProfileId, draft, summary.Revision, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)));
    }

    public Task<SyncProfileMutationResponse> CreateProfileAsync(
        SyncProfileCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        Created.Add(request.Draft);
        _profiles.Add(new SyncProfileSummary(
            request.ProfileId, request.Draft.DisplayName, request.Draft.LeftConnectionId,
            request.Draft.RightConnectionId, request.Draft.Direction, request.Draft.DeletionMode,
            request.Draft.Enabled, 1, DateTimeOffset.UnixEpoch));
        _drafts[request.ProfileId] = request.Draft;
        return Task.FromResult(new SyncProfileMutationResponse(
            request.ContractVersion, request.ProfileId, SyncProfileMutationOutcome.Succeeded));
    }

    public Task<SyncProfileMutationResponse> UpdateProfileAsync(
        SyncProfileUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        Updated.Add(request.Draft);
        _drafts[request.ProfileId] = request.Draft;
        return Task.FromResult(new SyncProfileMutationResponse(
            request.ContractVersion, request.ProfileId, SyncProfileMutationOutcome.Succeeded));
    }

    public Task<SyncPreviewGenerateResponse> GeneratePreviewAsync(
        SyncPreviewGenerateRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A settings transfer never runs a sync.");

    public Task<SyncPlanPageResponse> GetPlanPageAsync(
        SyncPlanPageRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A settings transfer never runs a sync.");

    public Task<SyncConflictPageResponse> GetConflictPageAsync(
        SyncConflictPageRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A settings transfer never runs a sync.");

    public Task<SyncApproveDispatchResponse> ApproveAndDispatchAsync(
        SyncApproveDispatchRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A settings transfer never runs a sync.");

    public Task<SyncRunStatusResponse> GetRunStatusAsync(
        SyncRunStatusRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A settings transfer never runs a sync.");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeSettingsScheduleClient : IScheduleManagementAgentClient
{
    private readonly List<ScheduleDocument> _schedules = [];

    internal List<ScheduleDraftDocument> Created { get; } = [];

    internal List<ScheduleDraftDocument> Updated { get; } = [];

    public Task<ScheduleListResponse> ListAsync(
        ScheduleListRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ScheduleListResponse(request.ContractVersion, [.. _schedules]));

    public Task<ScheduleGetResponse> GetAsync(
        ScheduleGetRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ScheduleGetResponse(
            request.ContractVersion,
            request.ScheduleId,
            _schedules.FirstOrDefault(schedule => schedule.ScheduleId == request.ScheduleId)));

    public Task<ScheduleMutationResponse> CreateAsync(
        ScheduleCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        Created.Add(request.Draft);
        _schedules.Add(new ScheduleDocument(
            request.ScheduleId, request.Draft.ProfileId, "Profile", request.Draft.CronExpression,
            request.Draft.TimeZoneId, request.Draft.MisfireGraceSeconds, request.Draft.QueueOneWhileRunning,
            request.Draft.Enabled, null, null, false, null, null, 1, request.Draft.ExecutionMode));
        return Task.FromResult(new ScheduleMutationResponse(
            request.ContractVersion, request.ScheduleId, ScheduleMutationOutcome.Succeeded));
    }

    public Task<ScheduleMutationResponse> UpdateAsync(
        ScheduleUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        Updated.Add(request.Draft);
        return Task.FromResult(new ScheduleMutationResponse(
            request.ContractVersion, request.ScheduleId, ScheduleMutationOutcome.Succeeded));
    }

    public Task<ScheduleMutationResponse> SetEnabledAsync(
        ScheduleSetEnabledRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("An import writes the whole schedule rather than toggling it.");

    public Task<ScheduleMutationResponse> DeleteAsync(
        ScheduleDeleteRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("An import never deletes.");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
