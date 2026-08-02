using System.Globalization;
using System.Text.Json;
using StorageHub.Contracts.Ipc;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Persistence;
using StorageHub.Persistence.Sync;
using StorageHub.Sync;
using StorageHub.Sync.Persistence;
using StorageHub.Transfers;

namespace StorageHub.Agent.Windows.Tests;

public sealed class SyncManagementIpcCommandServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 13, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"storagehub-sync-ipc-{Guid.NewGuid():N}");

    [Fact]
    public async Task Profile_create_get_list_and_update_use_repository_revision_cas()
    {
        var fixture = await CreateFixtureAsync();
        var profileId = Guid.NewGuid();
        var draft = await CreateDraftAsync(fixture);

        var created = await SendAsync<SyncProfileCreateRequest, SyncProfileMutationResponse>(
            fixture.Service,
            SyncManagementIpcMessageTypes.ProfileCreateRequest,
            new SyncProfileCreateRequest(
                SyncManagementIpcContract.CurrentVersion,
                profileId,
                draft));
        var listed = await SendAsync<SyncProfileListRequest, SyncProfileListResponse>(
            fixture.Service,
            SyncManagementIpcMessageTypes.ProfileListRequest,
            new SyncProfileListRequest());
        var fetched = await SendAsync<SyncProfileGetRequest, SyncProfileGetResponse>(
            fixture.Service,
            SyncManagementIpcMessageTypes.ProfileGetRequest,
            new SyncProfileGetRequest(SyncManagementIpcContract.CurrentVersion, profileId));
        var updated = await SendAsync<SyncProfileUpdateRequest, SyncProfileMutationResponse>(
            fixture.Service,
            SyncManagementIpcMessageTypes.ProfileUpdateRequest,
            new SyncProfileUpdateRequest(
                SyncManagementIpcContract.CurrentVersion,
                profileId,
                ExpectedRevision: 1,
                draft with { DisplayName = "Updated sync" }));
        var stale = await SendAsync<SyncProfileUpdateRequest, SyncProfileMutationResponse>(
            fixture.Service,
            SyncManagementIpcMessageTypes.ProfileUpdateRequest,
            new SyncProfileUpdateRequest(
                SyncManagementIpcContract.CurrentVersion,
                profileId,
                ExpectedRevision: 1,
                draft with { DisplayName = "Stale write" }));

        Assert.Equal(SyncProfileMutationOutcome.Succeeded, created.Outcome);
        Assert.Equal(1, created.Profile?.Revision);
        Assert.Equal(profileId, Assert.Single(listed.Profiles).ProfileId);
        Assert.Equal(draft, fetched.Profile?.Draft);
        Assert.Equal(SyncProfileMutationOutcome.Succeeded, updated.Outcome);
        Assert.Equal(2, updated.Profile?.Revision);
        Assert.Equal(SyncProfileMutationOutcome.RevisionConflict, stale.Outcome);
        Assert.Equal(2, stale.ActualRevision);
    }

    [Fact]
    public async Task Profile_handler_rejects_noncanonical_roots_without_persisting()
    {
        var fixture = await CreateFixtureAsync();
        var draft = await CreateDraftAsync(fixture) with { LeftRoot = "folder\\child" };

        var response = await SendAsync<SyncProfileCreateRequest, SyncProfileMutationResponse>(
            fixture.Service,
            SyncManagementIpcMessageTypes.ProfileCreateRequest,
            new SyncProfileCreateRequest(
                SyncManagementIpcContract.CurrentVersion,
                Guid.NewGuid(),
                draft));

        Assert.Equal(SyncProfileMutationOutcome.ConstraintConflict, response.Outcome);
        Assert.Equal(StorageIpcFailureCategory.Validation, response.Failure?.Category);
        Assert.Empty(await fixture.Profiles.ListAsync());
    }

    [Fact]
    public async Task Plan_pages_are_bounded_and_omit_root_version_and_entity_tag_evidence()
    {
        var fixture = await CreateFixtureAsync();
        var seeded = await SeedPreviewAsync(fixture, conflictCount: 0);
        var firstCommand = await fixture.Service.HandleAsync(CreateEnvelope(
            SyncManagementIpcMessageTypes.PlanPageRequest,
            new SyncPlanPageRequest(
                SyncManagementIpcContract.CurrentVersion,
                seeded.Run.SyncRunId.Value,
                PageSize: 2)));
        var first = firstCommand.Payload.Deserialize<SyncPlanPageResponse>()!;
        var second = await SendAsync<SyncPlanPageRequest, SyncPlanPageResponse>(
            fixture.Service,
            SyncManagementIpcMessageTypes.PlanPageRequest,
            new SyncPlanPageRequest(
                SyncManagementIpcContract.CurrentVersion,
                seeded.Run.SyncRunId.Value,
                PageSize: 2,
                first.ContinuationToken));
        var payload = firstCommand.Payload.GetRawText();

        Assert.Equal(2, first.Operations.Length);
        Assert.Single(second.Operations);
        Assert.NotNull(first.ContinuationToken);
        Assert.Null(second.ContinuationToken);
        Assert.Equal(seeded.Plan.Digest.Sha256Hex, first.PlanSha256);
        Assert.DoesNotContain("private-left-root-identity", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("left-version", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("left-etag", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("NativeItem", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Conflict_pages_extract_only_bounded_safe_reason_and_page_by_offset()
    {
        var fixture = await CreateFixtureAsync();
        var seeded = await SeedPreviewAsync(fixture, conflictCount: 2);

        var first = await SendAsync<SyncConflictPageRequest, SyncConflictPageResponse>(
            fixture.Service,
            SyncManagementIpcMessageTypes.ConflictPageRequest,
            new SyncConflictPageRequest(
                SyncManagementIpcContract.CurrentVersion,
                seeded.Run.SyncRunId.Value,
                PageSize: 1));
        var second = await SendAsync<SyncConflictPageRequest, SyncConflictPageResponse>(
            fixture.Service,
            SyncManagementIpcMessageTypes.ConflictPageRequest,
            new SyncConflictPageRequest(
                SyncManagementIpcContract.CurrentVersion,
                seeded.Run.SyncRunId.Value,
                PageSize: 1,
                ContinuationToken: first.ContinuationToken));

        Assert.Single(first.Conflicts);
        Assert.Single(second.Conflicts);
        Assert.NotNull(first.ContinuationToken);
        Assert.Null(second.ContinuationToken);
        Assert.All(first.Conflicts.Concat(second.Conflicts), conflict =>
        {
            Assert.Contains("safely", conflict.SafeReason, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain('{', conflict.SafeReason);
        });
    }

    [Fact]
    public async Task Manual_preview_is_idempotency_bound_and_maps_immutable_run_summary()
    {
        var fixture = await CreateFixtureAsync();
        var seeded = await SeedPreviewAsync(fixture, conflictCount: 0);
        fixture.Orchestration.GenerateResult = StorageResult<SyncPreviewResult>.Success(
            new SyncPreviewResult(seeded.Run, seeded.Plan, []));
        var requestId = Guid.NewGuid();

        var response = await SendAsync<SyncPreviewGenerateRequest, SyncPreviewGenerateResponse>(
            fixture.Service,
            SyncManagementIpcMessageTypes.PreviewGenerateRequest,
            new SyncPreviewGenerateRequest(
                SyncManagementIpcContract.CurrentVersion,
                seeded.Profile.ProfileId.Value,
                requestId));

        Assert.Equal($"ipc-manual:{requestId:D}", fixture.Orchestration.TriggerKey);
        Assert.Equal(SyncIpcRunPhase.AwaitingApproval, response.Run?.Phase);
        Assert.Equal(SyncIpcDispatchState.NotDispatched, response.Run?.DispatchState);
        Assert.Equal(seeded.Plan.Operations.Length, response.Plan?.OperationCount);
    }

    [Fact]
    public async Task Manual_preview_gate_rejects_a_third_scan_and_releases_cancelled_permits()
    {
        var fixture = await CreateFixtureAsync();
        var firstWaveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        fixture.Orchestration.GenerateOverride = async (_, _, cancellationToken) =>
        {
            if (Interlocked.Increment(ref entered) == 2)
            {
                firstWaveEntered.TrySetResult();
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        };
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        var profileId = Guid.NewGuid();
        var first = fixture.Service.HandleAsync(CreateEnvelope(
            SyncManagementIpcMessageTypes.PreviewGenerateRequest,
            new SyncPreviewGenerateRequest(
                SyncManagementIpcContract.CurrentVersion,
                profileId,
                Guid.NewGuid())), firstCancellation.Token).AsTask();
        var second = fixture.Service.HandleAsync(CreateEnvelope(
            SyncManagementIpcMessageTypes.PreviewGenerateRequest,
            new SyncPreviewGenerateRequest(
                SyncManagementIpcContract.CurrentVersion,
                profileId,
                Guid.NewGuid())), secondCancellation.Token).AsTask();
        await firstWaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var busy = await SendAsync<SyncPreviewGenerateRequest, SyncPreviewGenerateResponse>(
            fixture.Service,
            SyncManagementIpcMessageTypes.PreviewGenerateRequest,
            new SyncPreviewGenerateRequest(
                SyncManagementIpcContract.CurrentVersion,
                profileId,
                Guid.NewGuid()));

        Assert.Equal("sync.preview.busy", busy.Failure?.Code);
        Assert.Equal(StorageIpcFailureCategory.Unavailable, busy.Failure?.Category);
        Assert.True(busy.Failure?.IsTransient);
        Assert.Null(busy.Run);
        firstCancellation.Cancel();
        secondCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);

        var secondWaveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWaveRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        entered = 0;
        fixture.Orchestration.GenerateOverride = async (_, _, cancellationToken) =>
        {
            if (Interlocked.Increment(ref entered) == 2)
            {
                secondWaveEntered.TrySetResult();
            }

            await secondWaveRelease.Task.WaitAsync(cancellationToken);
            return fixture.Orchestration.GenerateResult;
        };
        var fourth = fixture.Service.HandleAsync(CreateEnvelope(
            SyncManagementIpcMessageTypes.PreviewGenerateRequest,
            new SyncPreviewGenerateRequest(
                SyncManagementIpcContract.CurrentVersion,
                profileId,
                Guid.NewGuid()))).AsTask();
        var fifth = fixture.Service.HandleAsync(CreateEnvelope(
            SyncManagementIpcMessageTypes.PreviewGenerateRequest,
            new SyncPreviewGenerateRequest(
                SyncManagementIpcContract.CurrentVersion,
                profileId,
                Guid.NewGuid()))).AsTask();

        await secondWaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        secondWaveRelease.TrySetResult();
        _ = await Task.WhenAll(fourth, fifth);
    }

    [Fact]
    public async Task Approval_response_claims_only_durable_dispatch_and_uses_exact_revision_and_sha()
    {
        var fixture = await CreateFixtureAsync();
        var seeded = await SeedPreviewAsync(fixture, conflictCount: 0);
        var readyState = SyncStateMachine.Transition(
            seeded.Run.State,
            SyncRunPhase.Ready,
            Now.AddSeconds(1));
        fixture.Orchestration.ApproveResult = StorageResult<SyncPreviewRecord>.Success(
            seeded.Run with
            {
                State = readyState,
                ApprovedForExecution = true,
                ApprovedAtUtc = Now.AddSeconds(1),
                DispatchEventId = seeded.Run.SyncRunId.Value
            });

        var response = await SendAsync<SyncApproveDispatchRequest, SyncApproveDispatchResponse>(
            fixture.Service,
            SyncManagementIpcMessageTypes.ApproveDispatchRequest,
            new SyncApproveDispatchRequest(
                SyncManagementIpcContract.CurrentVersion,
                seeded.Run.SyncRunId.Value,
                seeded.Run.State.Revision,
                seeded.Run.ApprovalChallengeSha256));
        var json = JsonSerializer.Serialize(response);

        Assert.True(response.DurablyDispatched);
        Assert.Equal(SyncIpcDispatchState.DurablyDispatched, response.Run?.DispatchState);
        Assert.Equal(SyncIpcRunPhase.Ready, response.Run?.Phase);
        Assert.Equal(seeded.Run.State.Revision, fixture.Orchestration.ExpectedRevision);
        Assert.Equal(seeded.Run.ApprovalChallengeSha256, fixture.Orchestration.ApprovalSha256);
        Assert.DoesNotContain("ProviderExecutionCompleted", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Orchestration_failure_messages_are_sanitized_before_normal_ipc()
    {
        var fixture = await CreateFixtureAsync();
        var draft = await CreateDraftAsync(fixture);
        var profile = await CreateProfileAsync(fixture, draft);
        fixture.Orchestration.GenerateResult = StorageResult<SyncPreviewResult>.Fail(new StorageFailure(
            "sync.endpoint.failed",
            StorageFailureKind.Provider,
            "https://example.invalid?password=super-secret"));

        var command = await fixture.Service.HandleAsync(CreateEnvelope(
            SyncManagementIpcMessageTypes.PreviewGenerateRequest,
            new SyncPreviewGenerateRequest(
                SyncManagementIpcContract.CurrentVersion,
                profile.ProfileId.Value,
                Guid.NewGuid())));
        var response = command.Payload.Deserialize<SyncPreviewGenerateResponse>()!;

        Assert.Equal(StorageIpcFailureCategory.Provider, response.Failure?.Category);
        Assert.DoesNotContain("super-secret", command.Payload.GetRawText(), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private async Task<Fixture> CreateFixtureAsync()
    {
        var options = new SqliteDatabaseOptions(
            Path.Combine(_directory, $"{Guid.NewGuid():N}.db"),
            pooling: false);
        var initialized = await new StorageHubDatabaseInitializer(options).InitializeAsync();
        Assert.True(initialized.IsReady, initialized.Message);
        var database = new SingleWriterSqliteDatabase(options);
        var profiles = new SqliteSyncProfileRepository(database, new FixedTimeProvider(Now));
        var runs = new SqliteSyncRunStore(database);
        var plans = new SqliteSyncPlanStore(database);
        var conflicts = new SqliteSyncConflictStore(database);
        var orchestration = new RecordingOrchestrationService();
        return new Fixture(
            database,
            profiles,
            runs,
            plans,
            conflicts,
            orchestration,
            new SyncManagementIpcCommandService(
                profiles,
                orchestration,
                runs,
                plans,
                conflicts,
                new FixedTimeProvider(Now)));
    }

    private static async Task<SyncProfileDraftDocument> CreateDraftAsync(Fixture fixture)
    {
        var left = ConnectionProfileId.New();
        var right = ConnectionProfileId.New();
        await SeedConnectionAsync(fixture.Database, left, $"left-{left}");
        await SeedConnectionAsync(fixture.Database, right, $"right-{right}");
        return new SyncProfileDraftDocument(
            "Documents mirror",
            left.Value,
            "documents",
            right.Value,
            "backup/documents",
            SyncIpcDirection.LeftToRight,
            SyncIpcDeletionMode.Mirror,
            SyncIpcConflictPolicy.Block,
            MaximumDeletionCount: 100,
            MaximumDeletionPercentage: 10,
            Overwrite: true,
            TransferBufferSize: 65_536,
            Enabled: true);
    }

    private static async Task<SyncProfile> CreateProfileAsync(
        Fixture fixture,
        SyncProfileDraftDocument draft)
    {
        var profile = new SyncProfile(
            SyncProfileId.New(),
            draft.DisplayName,
            new ConnectionProfileId(draft.LeftConnectionId),
            draft.LeftRoot,
            new ConnectionProfileId(draft.RightConnectionId),
            draft.RightRoot,
            SyncDirection.LeftToRight,
            SyncDeletionMode.Mirror,
            SyncConflictPolicy.Block,
            new DeletionSafetyPolicy(
                draft.MaximumDeletionCount,
                draft.MaximumDeletionPercentage),
            new TransferExecutionOptions(draft.Overwrite, draft.TransferBufferSize),
            draft.Enabled,
            revision: 1,
            Now,
            Now);
        var written = await fixture.Profiles.CreateAsync(profile);
        Assert.Equal(SyncProfileWriteStatus.Succeeded, written.Status);
        return profile;
    }

    private static async Task<SeededPreview> SeedPreviewAsync(Fixture fixture, int conflictCount)
    {
        var draft = await CreateDraftAsync(fixture);
        var profile = await CreateProfileAsync(fixture, draft);
        var left = StorageAddress.Create(
            profile.LeftConnectionProfileId,
            "private-left-root-identity",
            "documents/file.txt",
            nativeItemId: "left-native",
            versionId: "left-version",
            entityTag: "left-etag").Value;
        var right = StorageAddress.Create(
            profile.RightConnectionProfileId,
            "private-right-root-identity",
            "backup/documents/file.txt",
            nativeItemId: "right-native",
            versionId: "right-version",
            entityTag: "right-etag").Value;
        var plan = ImmutableSyncPlan.Create(
            OperationPlanId.New(),
            profile.ProfileId,
            baselineGeneration: 0,
            [
                SyncPlanOperation.Copy(0, left, right, expectedLength: 42),
                SyncPlanOperation.Delete(1, right),
                SyncPlanOperation.CreateDirectory(2, StorageAddress.Create(
                    profile.RightConnectionProfileId,
                    "private-right-root-identity",
                    "backup/documents/new-folder").Value)
            ],
            Now);
        var planWrite = await fixture.Plans.PutAsync(plan);
        Assert.Equal(SyncPersistenceMutationStatus.Applied, planWrite.Status);
        var snapshots = new SyncExecutionSnapshots(
            SnapshotCompleteness.Complete(10),
            SnapshotCompleteness.Complete(11),
            baselineItemCount: 9,
            new Dictionary<ConnectionProfileId, string>
            {
                [profile.LeftConnectionProfileId] = "private-left-root-identity",
                [profile.RightConnectionProfileId] = "private-right-root-identity"
            });
        var conflicts = Enumerable.Range(0, conflictCount)
            .Select(index => new SyncPlanningConflict(
                $"documents/conflict-{index}.txt",
                SyncChangeKind.ConflictBothModified,
                "Both endpoints changed incompatibly and could not be compared safely."))
            .ToArray();
        var runWrite = await fixture.Runs.CreatePreviewAsync(new SyncPreviewDraft(
            SyncRunId.New(),
            profile.ProfileId,
            profile.Revision,
            profile.PolicySha256,
            plan.PlanId,
            plan.Digest,
            snapshots,
            new string('a', 64),
            SyncPreviewTrigger.Manual,
            $"test:{Guid.NewGuid():D}",
            conflicts,
            Now,
            DeletionGuardBlocked: false));
        Assert.Equal(SyncPersistenceMutationStatus.Applied, runWrite.Status);
        return new SeededPreview(profile, plan, Assert.IsType<SyncPreviewRecord>(runWrite.Value));
    }

    private static async Task SeedConnectionAsync(
        SingleWriterSqliteDatabase database,
        ConnectionProfileId profileId,
        string name)
    {
        await using var writer = await database.AcquireWriterAsync();
        await using var command = writer.Connection.CreateCommand();
        command.CommandText = """
            INSERT INTO connection_profiles
            (
                profile_id, provider, display_name, tags_json, metadata_json, endpoint_json,
                authentication_json, operational_options_json, is_favorite, is_enabled,
                version, created_utc, updated_utc
            )
            VALUES ($id, 'local', $name, '[]', '{}', '{}', '{}', '{}', 0, 1, 1, $now, $now);
            """;
        command.Parameters.AddWithValue("$id", profileId.ToString());
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$now", Now.ToString("O", CultureInfo.InvariantCulture));
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<TResponse> SendAsync<TRequest, TResponse>(
        SyncManagementIpcCommandService service,
        string messageType,
        TRequest request)
    {
        var response = await service.HandleAsync(CreateEnvelope(messageType, request));
        return response.Payload.Deserialize<TResponse>()!;
    }

    private static IpcEnvelope CreateEnvelope<TRequest>(string messageType, TRequest request) =>
        IpcEnvelope.Create(messageType, Guid.NewGuid(), sequence: 1, request);

    private sealed record Fixture(
        SingleWriterSqliteDatabase Database,
        SqliteSyncProfileRepository Profiles,
        SqliteSyncRunStore Runs,
        SqliteSyncPlanStore Plans,
        SqliteSyncConflictStore Conflicts,
        RecordingOrchestrationService Orchestration,
        SyncManagementIpcCommandService Service);

    private sealed record SeededPreview(
        SyncProfile Profile,
        ImmutableSyncPlan Plan,
        SyncPreviewRecord Run);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingOrchestrationService : ISyncOrchestrationService
    {
        public StorageResult<SyncPreviewResult> GenerateResult { get; set; } =
            StorageResult<SyncPreviewResult>.Fail(new StorageFailure(
                "sync.preview.unconfigured",
                StorageFailureKind.Unavailable,
                "Preview not configured."));

        public StorageResult<SyncPreviewRecord> ApproveResult { get; set; } =
            StorageResult<SyncPreviewRecord>.Fail(new StorageFailure(
                "sync.approval.unconfigured",
                StorageFailureKind.Unavailable,
                "Approval not configured."));

        public string? TriggerKey { get; private set; }
        public long ExpectedRevision { get; private set; } = -1;
        public string? ApprovalSha256 { get; private set; }
        public Func<SyncProfileId, string?, CancellationToken, ValueTask<StorageResult<SyncPreviewResult>>>?
            GenerateOverride
        { get; set; }

        public ValueTask<StorageResult<SyncPreviewResult>> GeneratePreviewAsync(
            SyncProfileId profileId,
            SyncPreviewTrigger trigger = SyncPreviewTrigger.Manual,
            string? triggerIdempotencyKey = null,
            CancellationToken cancellationToken = default)
        {
            TriggerKey = triggerIdempotencyKey;
            if (GenerateOverride is not null)
            {
                return GenerateOverride(profileId, triggerIdempotencyKey, cancellationToken);
            }

            return ValueTask.FromResult(GenerateResult);
        }

        public ValueTask<StorageResult<SyncPreviewRecord>> ApproveAndDispatchAsync(
            SyncRunId syncRunId,
            long expectedRevision,
            string approvalSha256,
            CancellationToken cancellationToken = default)
        {
            ExpectedRevision = expectedRevision;
            ApprovalSha256 = approvalSha256;
            return ValueTask.FromResult(ApproveResult);
        }
    }
}
