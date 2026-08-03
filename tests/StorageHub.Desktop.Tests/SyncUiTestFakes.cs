using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

internal sealed class FakeSyncManagementClient : ISyncManagementAgentClient
{
    internal static readonly DateTimeOffset Now = new(2026, 8, 2, 13, 0, 0, TimeSpan.Zero);
    internal static readonly string Digest = new('b', 64);
    private readonly Dictionary<Guid, SyncProfileDocument> _profiles = [];

    public int ListProfilesCount { get; private set; }
    public int GetProfileCount { get; private set; }
    public int CreateProfileCount { get; private set; }
    public int UpdateProfileCount { get; private set; }
    public int PreviewCount { get; private set; }
    public int RunStatusCount { get; private set; }
    public int RunListCount { get; private set; }
    public int PlanPageCount { get; private set; }
    public int ConflictPageCount { get; private set; }
    public int ApprovalCount { get; private set; }
    public int DisposeCount { get; private set; }
    public Exception? ListProfilesError { get; set; }
    public SyncApproveDispatchRequest? LastApproval { get; private set; }
    public SyncRunSummary Run { get; set; } = CreateRun(Guid.NewGuid(), Guid.NewGuid());

    public void SeedProfile(SyncProfileDocument profile) => _profiles.Add(profile.ProfileId, profile);

    public Task<SyncProfileListResponse> ListProfilesAsync(
        SyncProfileListRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ListProfilesCount++;
        if (ListProfilesError is not null)
        {
            return Task.FromException<SyncProfileListResponse>(ListProfilesError);
        }

        return Task.FromResult(new SyncProfileListResponse(
            SyncManagementIpcContract.CurrentVersion,
            _profiles.Values
                .Where(profile => request.IncludeDisabled || profile.Draft.Enabled)
                .Take(request.MaximumCount)
                .Select(profile => new SyncProfileSummary(
                    profile.ProfileId,
                    profile.Draft.DisplayName,
                    profile.Draft.LeftConnectionId,
                    profile.Draft.RightConnectionId,
                    profile.Draft.Direction,
                    profile.Draft.DeletionMode,
                    profile.Draft.Enabled,
                    profile.Revision,
                    profile.UpdatedUtc))
                .ToArray()));
    }

    public Task<SyncProfileGetResponse> GetProfileAsync(
        SyncProfileGetRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GetProfileCount++;
        _profiles.TryGetValue(request.ProfileId, out var profile);
        return Task.FromResult(new SyncProfileGetResponse(
            SyncManagementIpcContract.CurrentVersion,
            request.ProfileId,
            profile,
            profile is null ? NotFound() : null));
    }

    public Task<SyncProfileMutationResponse> CreateProfileAsync(
        SyncProfileCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CreateProfileCount++;
        var profile = new SyncProfileDocument(request.ProfileId, request.Draft, 1, Now, Now);
        _profiles.Add(profile.ProfileId, profile);
        Run = Run with { ProfileId = profile.ProfileId };
        return Task.FromResult(new SyncProfileMutationResponse(
            SyncManagementIpcContract.CurrentVersion,
            request.ProfileId,
            SyncProfileMutationOutcome.Succeeded,
            profile,
            ActualRevision: 1));
    }

    public Task<SyncProfileMutationResponse> UpdateProfileAsync(
        SyncProfileUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UpdateProfileCount++;
        var current = _profiles[request.ProfileId];
        if (current.Revision != request.ExpectedRevision)
        {
            return Task.FromResult(new SyncProfileMutationResponse(
                SyncManagementIpcContract.CurrentVersion,
                request.ProfileId,
                SyncProfileMutationOutcome.RevisionConflict,
                ActualRevision: current.Revision,
                Failure: Conflict()));
        }

        var updated = current with
        {
            Draft = request.Draft,
            Revision = current.Revision + 1,
            UpdatedUtc = Now.AddSeconds(current.Revision)
        };
        _profiles[request.ProfileId] = updated;
        return Task.FromResult(new SyncProfileMutationResponse(
            SyncManagementIpcContract.CurrentVersion,
            request.ProfileId,
            SyncProfileMutationOutcome.Succeeded,
            updated,
            updated.Revision));
    }

    public Task<SyncPreviewGenerateResponse> GeneratePreviewAsync(
        SyncPreviewGenerateRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PreviewCount++;
        Run = Run with { ProfileId = request.ProfileId };
        return Task.FromResult(new SyncPreviewGenerateResponse(
            SyncManagementIpcContract.CurrentVersion,
            request.ProfileId,
            Run,
            new SyncPlanOverview(
                Run.SyncRunId,
                Run.PlanId,
                Run.PlanSha256,
                BaselineGeneration: 0,
                OperationCount: 1,
                CopyCount: 1,
                DeleteCount: 0,
                CreateDirectoryCount: 0,
                Now)));
    }

    public Task<SyncRunStatusResponse> GetRunStatusAsync(
        SyncRunStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RunStatusCount++;
        if (Run.DispatchState == SyncIpcDispatchState.DurablyDispatched &&
            Run.Phase == SyncIpcRunPhase.Ready)
        {
            Run = Run with
            {
                Phase = SyncIpcRunPhase.Completed,
                Revision = Run.Revision + 1,
                UpdatedUtc = Now.AddSeconds(2)
            };
        }

        return Task.FromResult(new SyncRunStatusResponse(
            SyncManagementIpcContract.CurrentVersion,
            request.SyncRunId,
            Run.SyncRunId == request.SyncRunId ? Run : null,
            Run.SyncRunId == request.SyncRunId ? null : NotFound()));
    }

    public Task<SyncRunListResponse> ListRunsAsync(
        SyncRunListRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RunListCount++;
        return Task.FromResult(new SyncRunListResponse(
            SyncManagementIpcContract.CurrentVersion,
            [Run],
            ContinuationToken: null));
    }

    public Task<SyncPlanPageResponse> GetPlanPageAsync(
        SyncPlanPageRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PlanPageCount++;
        return Task.FromResult(new SyncPlanPageResponse(
            SyncManagementIpcContract.CurrentVersion,
            request.SyncRunId,
            Run.PlanId,
            Run.PlanSha256,
            TotalOperations: 1,
            [new SyncPlanOperationSummary(
                0,
                SyncIpcPlanOperationKind.Copy,
                Guid.NewGuid(),
                "documents/source.txt",
                Guid.NewGuid(),
                "backup/source.txt",
                ExpectedLength: 42,
                IsDestructive: false)],
            ContinuationToken: null));
    }

    public Task<SyncConflictPageResponse> GetConflictPageAsync(
        SyncConflictPageRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConflictPageCount++;
        return Task.FromResult(new SyncConflictPageResponse(
            SyncManagementIpcContract.CurrentVersion,
            request.SyncRunId,
            ReportedConflictCount: 0,
            Conflicts: [],
            ContinuationToken: null,
            IsTruncatedAtSource: false));
    }

    public Task<SyncApproveDispatchResponse> ApproveAndDispatchAsync(
        SyncApproveDispatchRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApprovalCount++;
        LastApproval = request;
        Run = Run with
        {
            Phase = SyncIpcRunPhase.Ready,
            Revision = Run.Revision + 1,
            UpdatedUtc = Now.AddSeconds(1),
            DispatchState = SyncIpcDispatchState.DurablyDispatched,
            DispatchedUtc = Now.AddSeconds(1)
        };
        return Task.FromResult(new SyncApproveDispatchResponse(
            SyncManagementIpcContract.CurrentVersion,
            request.SyncRunId,
            DurablyDispatched: true,
            Run));
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }

    public static SyncRunSummary CreateRun(Guid runId, Guid profileId) => new(
        runId,
        profileId,
        Generation: 1,
        SyncIpcRunPhase.AwaitingApproval,
        SyncIpcStatusCode.None,
        Revision: 4,
        Now,
        Guid.NewGuid(),
        Digest,
        Digest,
        ConflictCount: 0,
        SyncIpcDispatchState.NotDispatched,
        DispatchedUtc: null,
        Now,
        BaselineItemCount: 0,
        LeftItemCount: 1,
        RightItemCount: 0,
        LeftSnapshotComplete: true,
        RightSnapshotComplete: true);

    private static StorageIpcFailure NotFound() => new(
        "sync.not_found",
        StorageIpcFailureCategory.NotFound,
        "The requested sync item was not found.",
        IsTransient: false);

    private static StorageIpcFailure Conflict() => new(
        "sync.revision_conflict",
        StorageIpcFailureCategory.Conflict,
        "The sync profile changed before it was saved.",
        IsTransient: true);
}

internal sealed class FakeRemoteStorageClient(ConnectionSummary[] connections) : IRemoteStorageAgentClient
{
    public int ListCount { get; private set; }
    public int DisposeCount { get; private set; }

    public Task<ConnectionListResponse> ListConnectionsAsync(
        ConnectionListRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ListCount++;
        return Task.FromResult(new ConnectionListResponse(
            StorageIpcContract.CurrentVersion,
            connections.Take(request.Limit).ToArray()));
    }

    public Task<ConnectionTestResponse> TestConnectionAsync(
        ConnectionTestRequest request,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<StorageListPageResponse> ListStorageAsync(
        StorageListPageRequest request,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }

    public static ConnectionSummary CreateConnection(string name) => new(
        Guid.NewGuid(),
        name,
        StorageConnectionProvider.Local,
        FolderPath: null,
        Tags: [],
        IsFavorite: false,
        IsEnabled: true,
        IconKey: "folder",
        AccentColor: null,
        Version: 1);
}
