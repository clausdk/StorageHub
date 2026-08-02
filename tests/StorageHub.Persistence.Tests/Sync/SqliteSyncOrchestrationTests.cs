using StorageHub.Contracts.Results;
using StorageHub.Domain.Capabilities;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Persistence.Scheduling;
using StorageHub.Persistence.Sync;
using StorageHub.Storage.Abstractions;
using StorageHub.Storage.Models;
using StorageHub.Sync;
using StorageHub.Sync.Persistence;
using StorageHub.Transfers;
using Xunit;

namespace StorageHub.Persistence.Tests.Sync;

public sealed class SqliteSyncOrchestrationTests : IAsyncLifetime, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 16, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"storagehub-sync-orchestration-{Guid.NewGuid():N}");
    private readonly ConnectionProfileId _leftId = ConnectionProfileId.New();
    private readonly ConnectionProfileId _rightId = ConnectionProfileId.New();
    private readonly SyncProfileId _profileId = SyncProfileId.New();
    private SingleWriterSqliteDatabase _database = null!;
    private SyncProfile _profile = null!;

    public async Task InitializeAsync()
    {
        var options = new SqliteDatabaseOptions(
            Path.Combine(_directory, "storagehub.db"),
            pooling: false);
        var initialized = await new StorageHubDatabaseInitializer(options).InitializeAsync();
        Assert.True(initialized.IsReady, initialized.Message);
        _database = new SingleWriterSqliteDatabase(options);
        await SeedConnectionAsync(_leftId, "Left");
        await SeedConnectionAsync(_rightId, "Right");
        _profile = CreateProfile();
        var created = await new SqliteSyncProfileRepository(
            _database,
            new FixedTimeProvider(Now)).CreateAsync(_profile);
        Assert.Equal(SyncProfileWriteStatus.Succeeded, created.Status);
    }

    [Fact]
    public async Task Policy_update_is_revisioned_and_atomically_invalidates_the_baseline()
    {
        var baselineStore = new SqliteSyncBaselineStore(_database);
        var seeded = await baselineStore.ReplaceAsync(new SyncBaselineReplaceRequest(
            _profileId,
            ExpectedRevision: 0,
            Generation: 1,
            new Dictionary<string, SyncBaselineObservation>
            {
                ["old.txt"] = SyncBaselineObservation.Present(4, null, "left-v1", "right-v1")
            },
            Now));
        Assert.Equal(SyncPersistenceMutationStatus.Applied, seeded.Status);
        var desired = new SyncProfile(
            _profile.ProfileId,
            _profile.DisplayName,
            _profile.LeftConnectionProfileId,
            "new-root",
            _profile.RightConnectionProfileId,
            _profile.RightRoot,
            _profile.Direction,
            _profile.DeletionMode,
            _profile.ConflictPolicy,
            _profile.DeletionSafetyPolicy,
            _profile.TransferOptions,
            _profile.Enabled,
            _profile.Revision,
            _profile.CreatedAtUtc,
            _profile.UpdatedAtUtc);

        var repository = new SqliteSyncProfileRepository(
            _database,
            new FixedTimeProvider(Now.AddMinutes(1)));
        var updated = await repository.UpdateAsync(desired, expectedRevision: 1);
        var stale = await repository.UpdateAsync(_profile, expectedRevision: 1);
        var baseline = await baselineStore.GetAsync(_profileId);

        Assert.Equal(SyncProfileWriteStatus.Succeeded, updated.Status);
        Assert.Equal(2, updated.Profile!.Revision);
        Assert.NotEqual(_profile.PolicySha256, updated.Profile.PolicySha256);
        Assert.Equal(SyncProfileWriteStatus.RevisionConflict, stale.Status);
        Assert.NotNull(baseline);
        Assert.Equal(0, baseline.Generation);
        Assert.Equal(2, baseline.Revision);
        Assert.Empty(baseline.Items);
    }

    [Fact]
    public async Task Preview_and_explicit_approval_persist_plan_run_and_exactly_one_apply_dispatch()
    {
        var left = new ReadOnlyEndpointSession(
            _leftId,
            "left-root-identity",
            [File(_leftId, "left-root-identity", "hello.txt", 5, "left-v1", "left-etag")]);
        var right = new ReadOnlyEndpointSession(_rightId, "right-root-identity", []);
        var service = CreateService(new TestConnector(left, right));

        var previewResult = await service.GeneratePreviewAsync(
            _profileId,
            triggerIdempotencyKey: "manual:test-preview");

        Assert.True(previewResult.IsSuccess, previewResult.Error?.Message);
        var preview = previewResult.Value.Preview;
        Assert.Equal(SyncRunPhase.AwaitingApproval, preview.State.Phase);
        Assert.Single(previewResult.Value.Plan.Operations);
        Assert.Equal(SyncPlanOperationKind.Copy, previewResult.Value.Plan.Operations[0].Kind);
        Assert.Equal("left-etag", previewResult.Value.Plan.Operations[0].SourceOrTarget.EntityTag);
        Assert.Equal(0, left.MutationCalls + right.MutationCalls);

        var approved = await service.ApproveAndDispatchAsync(
            preview.SyncRunId,
            preview.State.Revision,
            preview.ApprovalChallengeSha256);
        var retry = await service.ApproveAndDispatchAsync(
            preview.SyncRunId,
            preview.State.Revision,
            preview.ApprovalChallengeSha256);

        Assert.True(approved.IsSuccess, approved.Error?.Message);
        Assert.True(retry.IsSuccess, retry.Error?.Message);
        Assert.Equal(SyncRunPhase.Ready, approved.Value.State.Phase);
        Assert.True(approved.Value.ApprovedForExecution);
        Assert.Equal(approved.Value.DispatchEventId, retry.Value.DispatchEventId);
        Assert.Equal(0, left.MutationCalls + right.MutationCalls);
        var outbox = await new SqliteReliableOutboxStore(_database).GetAsync(preview.SyncRunId.Value);
        Assert.NotNull(outbox);
        Assert.Equal(SyncOutboxEventKinds.ApplyRequested, outbox.EventKind);

        var replayedPreview = await service.GeneratePreviewAsync(
            _profileId,
            triggerIdempotencyKey: "manual:test-preview");
        Assert.True(replayedPreview.IsSuccess);
        Assert.Equal(preview.SyncRunId, replayedPreview.Value.Preview.SyncRunId);
    }

    [Fact]
    public async Task Persisted_conflict_or_deletion_guard_cannot_be_approved()
    {
        var plan = ImmutableSyncPlan.Create(
            OperationPlanId.New(),
            _profileId,
            0,
            [],
            Now);
        Assert.Equal(
            SyncPersistenceMutationStatus.Applied,
            (await new SqliteSyncPlanStore(_database).PutAsync(plan)).Status);
        var snapshots = new SyncExecutionSnapshots(
            SnapshotCompleteness.Complete(1),
            SnapshotCompleteness.Complete(1),
            1,
            new Dictionary<ConnectionProfileId, string>
            {
                [_leftId] = "left-root-identity",
                [_rightId] = "right-root-identity",
            });
        var created = await new SqliteSyncRunStore(_database).CreatePreviewAsync(new SyncPreviewDraft(
            SyncRunId.New(),
            _profileId,
            1,
            _profile.PolicySha256,
            plan.PlanId,
            plan.Digest,
            snapshots,
            new string('a', 64),
            SyncPreviewTrigger.Manual,
            "manual:conflict",
            [new SyncPlanningConflict("shared.txt", SyncChangeKind.ConflictBothModified, "Both sides changed.")],
            Now,
            DeletionGuardBlocked: false));

        Assert.Equal(SyncPersistenceMutationStatus.Applied, created.Status);
        Assert.Equal(SyncRunPhase.BlockedConflict, created.Value!.State.Phase);
        Assert.Equal(1, created.Value.ConflictCount);
        Assert.Single(await new SqliteSyncConflictStore(_database).ListForRunAsync(created.Value.SyncRunId));
        var rejected = await new SqliteSyncRunStore(_database).ApproveAndDispatchAsync(
            new SyncApplyDispatchRequest(
                created.Value.SyncRunId,
                created.Value.State.Revision,
                1,
                _profile.PolicySha256,
                new string('a', 64),
                Guid.NewGuid(),
                Now));
        Assert.Equal(SyncPersistenceMutationStatus.Conflict, rejected.Status);
    }

    [Fact]
    public async Task Scheduled_dispatch_requires_the_exact_live_fence_and_is_idempotent()
    {
        var jobId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();
        await SeedScheduleLeaseAsync(jobId, leaseId, fencingToken: 7);
        var store = new SqliteScheduledSyncDispatchStore(
            _database,
            new FixedTimeProvider(Now.AddMinutes(1)));
        var request = new ScheduledSyncDispatchRequest(
            jobId,
            _profileId,
            leaseId,
            7,
            Now,
            Now,
            Now.AddMinutes(10));

        Assert.Equal(SyncPersistenceMutationStatus.Applied, await store.TryDispatchAsync(request));
        Assert.Equal(
            SyncPersistenceMutationStatus.AlreadyApplied,
            await store.TryDispatchAsync(request));
        Assert.Equal(
            SyncPersistenceMutationStatus.StaleLease,
            await store.TryDispatchAsync(request with { FencingToken = 6, LeaseId = Guid.NewGuid() }));
        Assert.Equal(
            SyncPersistenceMutationStatus.StaleLease,
            await new SqliteScheduledSyncDispatchStore(
                    _database,
                    new FixedTimeProvider(Now.AddMinutes(11)))
                .TryDispatchAsync(request));

        var outbox = await new SqliteReliableOutboxStore(_database).GetAsync(leaseId);
        Assert.NotNull(outbox);
        Assert.Equal(SyncOutboxEventKinds.PreviewRequested, outbox.EventKind);
        Assert.Equal(7, outbox.SequenceNumber);
    }

    [Fact]
    public async Task Apply_execution_is_bound_to_outbox_fence_and_expired_owner_recovers_to_reconciliation()
    {
        var left = new ReadOnlyEndpointSession(
            _leftId,
            "left-root-identity",
            [File(_leftId, "left-root-identity", "hello.txt", 5, "left-v1", "left-etag")]);
        var right = new ReadOnlyEndpointSession(_rightId, "right-root-identity", []);
        var service = CreateService(new TestConnector(left, right));
        var preview = (await service.GeneratePreviewAsync(
            _profileId,
            triggerIdempotencyKey: "manual:fenced-execution")).Value.Preview;
        var ready = (await service.ApproveAndDispatchAsync(
            preview.SyncRunId,
            preview.State.Revision,
            preview.ApprovalChallengeSha256)).Value;
        var outbox = new SqliteReliableOutboxStore(_database);
        var first = Assert.Single(await outbox.ClaimPendingByKindsAsync(
            "worker-a",
            [SyncOutboxEventKinds.ApplyRequested],
            1,
            Now.AddSeconds(1),
            TimeSpan.FromMinutes(1)));
        var execution = new SqliteSyncExecutionStore(
            _database,
            new FixedTimeProvider(Now.AddSeconds(2)));
        var acquired = await execution.BeginAsync(new SyncExecutionBeginRequest(
            first,
            ready.SyncRunId,
            ready.ProfileId,
            ready.PlanId,
            ready.PlanDigest,
            first.Event.SequenceNumber,
            ready.ProfileRevision,
            ready.ProfilePolicySha256,
            ready.ApprovalChallengeSha256,
            Now.AddSeconds(2)));

        Assert.Equal(SyncExecutionBeginStatus.Acquired, acquired.Status);
        Assert.Equal(SyncRunPhase.Executing, acquired.Context!.Preview.State.Phase);

        var replacement = Assert.Single(await outbox.ClaimPendingByKindsAsync(
            "worker-b",
            [SyncOutboxEventKinds.ApplyRequested],
            1,
            first.ExpiresAtUtc,
            TimeSpan.FromMinutes(1)));
        Assert.Equal(
            SyncPersistenceMutationStatus.StaleLease,
            (await execution.TransitionAsync(new SyncExecutionTransitionRequest(
                first,
                ready.SyncRunId,
                acquired.Context.Preview.State.Revision,
                SyncRunPhase.Executing,
                SyncRunPhase.Verifying,
                Now.AddMinutes(1)))).Status);

        var recoveryStore = new SqliteSyncExecutionStore(
            _database,
            new FixedTimeProvider(first.ExpiresAtUtc.AddSeconds(1)));
        var recovered = await recoveryStore.BeginAsync(new SyncExecutionBeginRequest(
            replacement,
            ready.SyncRunId,
            ready.ProfileId,
            ready.PlanId,
            ready.PlanDigest,
            replacement.Event.SequenceNumber,
            ready.ProfileRevision,
            ready.ProfilePolicySha256,
            ready.ApprovalChallengeSha256,
            first.ExpiresAtUtc.AddSeconds(1)));

        Assert.Equal(SyncExecutionBeginStatus.ReconciliationRequired, recovered.Status);
        Assert.Equal(
            SyncRunPhase.NeedsReconciliation,
            (await new SqliteSyncRunStore(_database).GetAsync(ready.SyncRunId))!.State.Phase);
    }

    [Fact]
    public async Task Apply_processor_executes_once_rescans_and_atomically_commits_last_known_good_baseline()
    {
        var left = new MutableEndpointSession(
            _leftId,
            "left-root-identity",
            new Dictionary<string, byte[]> { ["hello.txt"] = "hello"u8.ToArray() },
            canWrite: false);
        var right = new MutableEndpointSession(
            _rightId,
            "right-root-identity",
            new Dictionary<string, byte[]>(),
            canWrite: true);
        var connector = new TestConnector(left, right);
        var service = CreateService(connector);
        var preview = (await service.GeneratePreviewAsync(
            _profileId,
            triggerIdempotencyKey: "manual:execute-once")).Value.Preview;
        var ready = (await service.ApproveAndDispatchAsync(
            preview.SyncRunId,
            preview.State.Revision,
            preview.ApprovalChallengeSha256)).Value;
        var outbox = new SqliteReliableOutboxStore(_database);
        var lease = Assert.Single(await outbox.ClaimPendingByKindsAsync(
            "apply-worker",
            [SyncOutboxEventKinds.ApplyRequested],
            1,
            Now.AddSeconds(1),
            TimeSpan.FromMinutes(5)));
        var time = new FixedTimeProvider(Now.AddSeconds(2));
        var processor = new SyncOutboxEventProcessor(
            service,
            new SqliteSyncProfileRepository(_database, time),
            new SqliteSyncPlanStore(_database),
            new SqliteSyncExecutionStore(_database, time),
            connector,
            timeProvider: time);

        var first = await processor.ProcessAsync(lease);
        var replay = await processor.ProcessAsync(lease);

        Assert.Equal(SyncOutboxProcessingOutcome.Completed, first.Outcome);
        Assert.Equal(SyncOutboxProcessingOutcome.Completed, replay.Outcome);
        Assert.Equal(1, right.CommitCount);
        Assert.Equal("hello"u8.ToArray(), right.ReadBytes("hello.txt"));
        var run = await new SqliteSyncRunStore(_database).GetAsync(ready.SyncRunId);
        Assert.Equal(SyncRunPhase.Completed, run!.State.Phase);
        var baseline = await new SqliteSyncBaselineStore(_database).GetAsync(_profileId);
        Assert.Equal(1, baseline!.Generation);
        Assert.True(baseline.Items.ContainsKey("hello.txt"));
        Assert.Equal(
            SyncPersistenceMutationStatus.Applied,
            await outbox.CompleteAsync(lease, Now.AddSeconds(3)));
    }

    [Fact]
    public async Task Preview_outbox_replay_uses_one_stable_trigger_identity()
    {
        var left = new ReadOnlyEndpointSession(
            _leftId,
            "left-root-identity",
            [File(_leftId, "left-root-identity", "hello.txt", 5, "left-v1", "left-etag")]);
        var right = new ReadOnlyEndpointSession(_rightId, "right-root-identity", []);
        var connector = new TestConnector(left, right);
        var service = CreateService(connector);
        var jobId = Guid.NewGuid();
        var schedulerLeaseId = Guid.NewGuid();
        await SeedScheduleLeaseAsync(jobId, schedulerLeaseId, fencingToken: 11);
        var dispatched = await new SqliteScheduledSyncDispatchStore(
            _database,
            new FixedTimeProvider(Now.AddSeconds(1))).TryDispatchAsync(
            new ScheduledSyncDispatchRequest(
                jobId,
                _profileId,
                schedulerLeaseId,
                11,
                Now,
                Now,
                Now.AddMinutes(10)));
        Assert.Equal(SyncPersistenceMutationStatus.Applied, dispatched);
        var outbox = new SqliteReliableOutboxStore(_database);
        var lease = Assert.Single(await outbox.ClaimPendingByKindsAsync(
            "preview-worker",
            [SyncOutboxEventKinds.PreviewRequested],
            1,
            Now.AddSeconds(2),
            TimeSpan.FromMinutes(5)));
        var time = new FixedTimeProvider(Now.AddSeconds(3));
        var processor = new SyncOutboxEventProcessor(
            service,
            new SqliteSyncProfileRepository(_database, time),
            new SqliteSyncPlanStore(_database),
            new SqliteSyncExecutionStore(_database, time),
            connector,
            timeProvider: time);

        var first = await processor.ProcessAsync(lease);
        var replay = await processor.ProcessAsync(lease);
        var preview = await new SqliteSyncRunStore(_database).GetByTriggerAsync(
            _profileId,
            $"outbox:{schedulerLeaseId:D}");

        Assert.Equal(SyncOutboxProcessingOutcome.Completed, first.Outcome);
        Assert.Equal(SyncOutboxProcessingOutcome.Completed, replay.Outcome);
        Assert.NotNull(preview);
        Assert.Equal(SyncPreviewTrigger.Scheduled, preview.Trigger);
    }

    [Fact]
    public async Task Ambiguous_provider_failure_after_mutation_requires_reconciliation_and_never_commits_baseline()
    {
        var left = new MutableEndpointSession(
            _leftId,
            "left-root-identity",
            new Dictionary<string, byte[]> { ["hello.txt"] = "hello"u8.ToArray() },
            canWrite: false);
        var right = new MutableEndpointSession(
            _rightId,
            "right-root-identity",
            new Dictionary<string, byte[]>(),
            canWrite: true,
            ambiguousCommitFailure: true);
        var connector = new TestConnector(left, right);
        var service = CreateService(connector);
        var preview = (await service.GeneratePreviewAsync(
            _profileId,
            triggerIdempotencyKey: "manual:ambiguous-failure")).Value.Preview;
        var ready = (await service.ApproveAndDispatchAsync(
            preview.SyncRunId,
            preview.State.Revision,
            preview.ApprovalChallengeSha256)).Value;
        var outbox = new SqliteReliableOutboxStore(_database);
        var lease = Assert.Single(await outbox.ClaimPendingByKindsAsync(
            "apply-worker",
            [SyncOutboxEventKinds.ApplyRequested],
            1,
            Now.AddSeconds(1),
            TimeSpan.FromMinutes(5)));
        var time = new FixedTimeProvider(Now.AddSeconds(2));
        var processor = new SyncOutboxEventProcessor(
            service,
            new SqliteSyncProfileRepository(_database, time),
            new SqliteSyncPlanStore(_database),
            new SqliteSyncExecutionStore(_database, time),
            connector,
            timeProvider: time);

        var result = await processor.ProcessAsync(lease);

        Assert.Equal(SyncOutboxProcessingOutcome.DeadLetter, result.Outcome);
        Assert.Equal(1, right.CommitCount);
        Assert.Equal(
            SyncRunPhase.NeedsReconciliation,
            (await new SqliteSyncRunStore(_database).GetAsync(ready.SyncRunId))!.State.Phase);
        Assert.Equal(0, (await new SqliteSyncBaselineStore(_database).GetAsync(_profileId))!.Generation);
    }

    private SyncOrchestrationService CreateService(ISyncEndpointConnector connector) => new(
        new SqliteSyncProfileRepository(_database, new FixedTimeProvider(Now)),
        new SqliteSyncBaselineStore(_database),
        new SqliteSyncPlanStore(_database),
        new SqliteSyncRunStore(_database),
        new SqliteSyncConflictStore(_database),
        connector,
        timeProvider: new FixedTimeProvider(Now));

    private SyncProfile CreateProfile() => new(
        _profileId,
        "Documents mirror",
        _leftId,
        string.Empty,
        _rightId,
        string.Empty,
        SyncDirection.LeftToRight,
        SyncDeletionMode.Disabled,
        SyncConflictPolicy.Block,
        DeletionSafetyPolicy.Default,
        new TransferExecutionOptions(),
        enabled: true,
        revision: 1,
        Now,
        Now);

    private async Task SeedConnectionAsync(ConnectionProfileId id, string name)
    {
        await using var writer = await _database.AcquireWriterAsync();
        await using var command = writer.Connection.CreateCommand();
        command.CommandText = """
            INSERT INTO connection_profiles
            (profile_id, provider, display_name, tags_json, metadata_json, endpoint_json,
             authentication_json, operational_options_json, is_favorite, is_enabled, version,
             created_utc, updated_utc)
            VALUES ($id, 'local', $name, '[]', '{}', '{}', '{}', '{}', 0, 1, 1, $now, $now);
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$now", Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        _ = await command.ExecuteNonQueryAsync();
    }

    private async Task SeedScheduleLeaseAsync(Guid jobId, Guid leaseId, long fencingToken)
    {
        await using var writer = await _database.AcquireWriterAsync();
        await using var command = writer.Connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_schedules
            (sync_schedule_id, sync_profile_id, cron_expression, time_zone_id, enabled,
             next_due_utc, misfire_policy, revision, fencing_counter,
             active_lease_id, active_lease_acquired_utc, active_lease_expires_utc,
             active_lease_fencing_token)
            VALUES ($job, $profile, '* * * * *', 'UTC', 1, $due, 'coalesce-one', 1, $fence,
                    $lease, $acquired, $expires, $fence);
            """;
        command.Parameters.AddWithValue("$job", jobId.ToString("D"));
        command.Parameters.AddWithValue("$profile", _profileId.ToString());
        command.Parameters.AddWithValue("$due", Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$lease", leaseId.ToString("D"));
        command.Parameters.AddWithValue("$acquired", Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$expires", Now.AddMinutes(10).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$fence", fencingToken);
        _ = await command.ExecuteNonQueryAsync();
    }

    private static StorageEntry File(
        ConnectionProfileId profileId,
        string rootIdentity,
        string path,
        long size,
        string version,
        string entityTag)
    {
        var address = StorageAddress.Create(
            profileId,
            rootIdentity,
            path,
            versionId: version,
            entityTag: entityTag).Value;
        return StorageEntry.Create(
            address,
            StorageEntryKind.File,
            size,
            eTag: entityTag).Value;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestConnector(params IStorageEndpointSession[] sessions) : ISyncEndpointConnector
    {
        private readonly Dictionary<ConnectionProfileId, IStorageEndpointSession> _sessions =
            sessions.ToDictionary(session => session.ProfileId);

        public ValueTask<StorageResult<ISyncEndpointConnection>> OpenAsync(
            ConnectionProfileId profileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_sessions.TryGetValue(profileId, out var session)
                ? StorageResult<ISyncEndpointConnection>.Success(new TestConnection(session))
                : StorageResult<ISyncEndpointConnection>.Fail(new StorageFailure(
                    "test.session.not_found",
                    StorageFailureKind.NotFound,
                    "Test session not found.")));
        }

        private sealed class TestConnection(IStorageEndpointSession session) : ISyncEndpointConnection
        {
            public IStorageEndpointSession Session { get; } = session;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ReadOnlyEndpointSession : IStorageEndpointSession
    {
        private readonly StorageEntry[] _entries;

        public ReadOnlyEndpointSession(
            ConnectionProfileId profileId,
            string rootIdentity,
            StorageEntry[] entries)
        {
            ProfileId = profileId;
            RootIdentity = rootIdentity;
            _entries = entries;
            Capabilities = new EffectiveStorageCapabilities(
                [
                    new(StorageFeature.List, FeatureSupport.Native()),
                    new(StorageFeature.PaginatedList, FeatureSupport.Native()),
                    new(StorageFeature.ReadStream, FeatureSupport.Native()),
                    new(StorageFeature.WriteStream, FeatureSupport.Native()),
                ],
                StorageCaseSensitivity.Sensitive);
        }

        public ConnectionProfileId ProfileId { get; }
        public string RootIdentity { get; }
        public EffectiveStorageCapabilities Capabilities { get; }
        public int MutationCalls { get; private set; }

        public ValueTask<StorageResult> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageResult.Success());

        public ValueTask<StorageResult<StorageEntry>> GetEntryAsync(
            StorageAddress address,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageResult<StorageEntry>.Fail(Unsupported()));

        public ValueTask<StorageResult<StoragePage>> ListAsync(
            StorageAddress address,
            StorageListRequest? request = null,
            CancellationToken cancellationToken = default)
        {
            request ??= new StorageListRequest();
            var entries = _entries
                .Where(entry => entry.Address.Parent.CanonicalRelativePath == address.CanonicalRelativePath)
                .Take(request.PageSize)
                .ToArray();
            return ValueTask.FromResult(StorageResult<StoragePage>.Success(new StoragePage(entries, null)));
        }

        public ValueTask<StorageResult<Stream>> OpenReadAsync(
            StorageReadRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageResult<Stream>.Fail(Unsupported()));

        public ValueTask<StorageResult<IStorageWriteHandle>> OpenWriteAsync(
            StorageWriteRequest request,
            CancellationToken cancellationToken = default)
        {
            MutationCalls++;
            return ValueTask.FromResult(StorageResult<IStorageWriteHandle>.Fail(Unsupported()));
        }

        public ValueTask<StorageResult<StorageEntry>> CreateDirectoryAsync(
            StorageAddress address,
            CancellationToken cancellationToken = default)
        {
            MutationCalls++;
            return ValueTask.FromResult(StorageResult<StorageEntry>.Fail(Unsupported()));
        }

        public ValueTask<StorageResult> DeleteAsync(
            StorageDeleteRequest request,
            CancellationToken cancellationToken = default)
        {
            MutationCalls++;
            return ValueTask.FromResult(StorageResult.Fail(Unsupported()));
        }

        public ValueTask<StorageResult<StorageEntry>> CopyAsync(
            StorageCopyRequest request,
            CancellationToken cancellationToken = default)
        {
            MutationCalls++;
            return ValueTask.FromResult(StorageResult<StorageEntry>.Fail(Unsupported()));
        }

        public ValueTask<StorageResult<StorageEntry>> MoveAsync(
            StorageMoveRequest request,
            CancellationToken cancellationToken = default)
        {
            MutationCalls++;
            return ValueTask.FromResult(StorageResult<StorageEntry>.Fail(Unsupported()));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static StorageFailure Unsupported() => new(
            "test.unsupported",
            StorageFailureKind.Unsupported,
            "The test session does not implement provider mutation.");
    }

    private sealed class MutableEndpointSession : IStorageEndpointSession
    {
        private readonly Dictionary<string, Item> _items = new(StringComparer.Ordinal);
        private readonly bool _canWrite;
        private long _nextVersion;

        public MutableEndpointSession(
            ConnectionProfileId profileId,
            string rootIdentity,
            IReadOnlyDictionary<string, byte[]> items,
            bool canWrite,
            bool ambiguousCommitFailure = false)
        {
            ProfileId = profileId;
            RootIdentity = rootIdentity;
            _canWrite = canWrite;
            AmbiguousCommitFailure = ambiguousCommitFailure;
            foreach (var (path, bytes) in items)
            {
                _items.Add(path, new Item(bytes.ToArray(), $"v{++_nextVersion}"));
            }

            var features = new List<KeyValuePair<StorageFeature, FeatureSupport>>
            {
                new(StorageFeature.List, FeatureSupport.Native()),
                new(StorageFeature.PaginatedList, FeatureSupport.Native()),
                new(StorageFeature.ReadStream, FeatureSupport.Native()),
            };
            if (canWrite)
            {
                features.Add(new(StorageFeature.WriteStream, FeatureSupport.Native()));
                features.Add(new(StorageFeature.ConditionalCreate, FeatureSupport.Native()));
            }

            Capabilities = new EffectiveStorageCapabilities(features, StorageCaseSensitivity.Sensitive);
        }

        public ConnectionProfileId ProfileId { get; }
        public string RootIdentity { get; }
        public EffectiveStorageCapabilities Capabilities { get; }
        public int CommitCount { get; private set; }
        private bool AmbiguousCommitFailure { get; }

        public byte[] ReadBytes(string path) => _items[path].Content.ToArray();

        public ValueTask<StorageResult> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageResult.Success());

        public ValueTask<StorageResult<StorageEntry>> GetEntryAsync(
            StorageAddress address,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_items.TryGetValue(address.CanonicalRelativePath, out var item)
                ? StorageResult<StorageEntry>.Success(ToEntry(address.CanonicalRelativePath, item))
                : StorageResult<StorageEntry>.Fail(new StorageFailure(
                    "test.not_found",
                    StorageFailureKind.NotFound,
                    "The in-memory item was not found.")));
        }

        public ValueTask<StorageResult<StoragePage>> ListAsync(
            StorageAddress address,
            StorageListRequest? request = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            request ??= new StorageListRequest();
            var entries = _items
                .Where(pair => StorageAddress.Create(ProfileId, RootIdentity, pair.Key).Value.Parent == address)
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Take(request.PageSize)
                .Select(pair => ToEntry(pair.Key, pair.Value))
                .ToArray();
            return ValueTask.FromResult(StorageResult<StoragePage>.Success(new StoragePage(entries, null)));
        }

        public ValueTask<StorageResult<Stream>> OpenReadAsync(
            StorageReadRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_items.TryGetValue(request.Address.CanonicalRelativePath, out var item) ||
                request.ExpectedVersionId is not null && request.ExpectedVersionId != item.Version)
            {
                return ValueTask.FromResult(StorageResult<Stream>.Fail(new StorageFailure(
                    "test.source_changed",
                    StorageFailureKind.Conflict,
                    "The in-memory source changed.")));
            }

            return ValueTask.FromResult(StorageResult<Stream>.Success(
                new MemoryStream(item.Content, writable: false)));
        }

        public ValueTask<StorageResult<IStorageWriteHandle>> OpenWriteAsync(
            StorageWriteRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_canWrite || request.Mode != StorageWriteMode.CreateNew ||
                _items.ContainsKey(request.Destination.CanonicalRelativePath))
            {
                return ValueTask.FromResult(StorageResult<IStorageWriteHandle>.Fail(new StorageFailure(
                    "test.write_rejected",
                    StorageFailureKind.Conflict,
                    "The in-memory destination rejected the write.")));
            }

            return ValueTask.FromResult(StorageResult<IStorageWriteHandle>.Success(
                new MemoryWriteHandle(this, request.Destination)));
        }

        public ValueTask<StorageResult<StorageEntry>> CreateDirectoryAsync(
            StorageAddress address,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageResult<StorageEntry>.Fail(Unsupported()));

        public ValueTask<StorageResult> DeleteAsync(
            StorageDeleteRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageResult.Fail(Unsupported()));

        public ValueTask<StorageResult<StorageEntry>> CopyAsync(
            StorageCopyRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageResult<StorageEntry>.Fail(Unsupported()));

        public ValueTask<StorageResult<StorageEntry>> MoveAsync(
            StorageMoveRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageResult<StorageEntry>.Fail(Unsupported()));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private StorageEntry Commit(StorageAddress destination, byte[] content)
        {
            var item = new Item(content, $"v{++_nextVersion}");
            _items.Add(destination.CanonicalRelativePath, item);
            CommitCount++;
            return ToEntry(destination.CanonicalRelativePath, item);
        }

        private StorageEntry ToEntry(string path, Item item)
        {
            var address = StorageAddress.Create(
                ProfileId,
                RootIdentity,
                path,
                versionId: item.Version,
                entityTag: item.Version).Value;
            return StorageEntry.Create(
                address,
                StorageEntryKind.File,
                item.Content.LongLength,
                eTag: item.Version).Value;
        }

        private static StorageFailure Unsupported() => new(
            "test.unsupported",
            StorageFailureKind.Unsupported,
            "The operation is not supported by the in-memory endpoint.");

        private sealed record Item(byte[] Content, string Version);

        private sealed class MemoryWriteHandle(
            MutableEndpointSession owner,
            StorageAddress destination) : IStorageWriteHandle
        {
            private readonly MemoryStream _content = new();

            public StorageAddress Destination { get; } = destination;
            public Stream Content => _content;
            public long AcceptedOffset => 0;
            public string? ResumeToken => null;
            public StorageWriteHandleState State { get; private set; } = StorageWriteHandleState.Open;

            public ValueTask<StorageResult<StorageEntry>> CommitAsync(
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (State != StorageWriteHandleState.Open)
                {
                    return ValueTask.FromResult(StorageResult<StorageEntry>.Fail(Unsupported()));
                }

                State = StorageWriteHandleState.Committed;
                var committed = owner.Commit(Destination, _content.ToArray());
                return ValueTask.FromResult(owner.AmbiguousCommitFailure
                    ? StorageResult<StorageEntry>.Fail(new StorageFailure(
                        "test.commit_ambiguous",
                        StorageFailureKind.Provider,
                        "The provider persisted the object but did not confirm commit."))
                    : StorageResult<StorageEntry>.Success(committed));
            }

            public ValueTask<StorageResult> AbortAsync(CancellationToken cancellationToken = default)
            {
                State = StorageWriteHandleState.Aborted;
                return ValueTask.FromResult(StorageResult.Success());
            }

            public ValueTask DisposeAsync()
            {
                _content.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
