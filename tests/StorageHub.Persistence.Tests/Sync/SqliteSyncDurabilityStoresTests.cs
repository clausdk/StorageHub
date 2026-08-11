using Microsoft.Data.Sqlite;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Persistence.Sync;
using StorageHub.Storage.Abstractions;
using StorageHub.Sync;
using StorageHub.Sync.Persistence;
using Xunit;

namespace StorageHub.Persistence.Tests.Sync;

public sealed class SqliteSyncDurabilityStoresTests : IAsyncLifetime, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"storagehub-sync-durability-{Guid.NewGuid():N}");
    private readonly SyncProfileId _profileId = SyncProfileId.New();
    private readonly SyncRunId _runId = SyncRunId.New();
    private SqliteDatabaseOptions _options = null!;
    private SingleWriterSqliteDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _options = new SqliteDatabaseOptions(Path.Combine(_directory, "storagehub.db"), pooling: false);
        var initialized = await new StorageHubDatabaseInitializer(_options).InitializeAsync();
        Assert.True(initialized.IsReady, initialized.Message);
        _database = new SingleWriterSqliteDatabase(_options);
        await SeedSyncProfileAndRunAsync();
    }

    [Fact]
    public async Task Run_history_query_uses_the_durable_run_timestamp()
    {
        var runs = await new SqliteSyncRunStore(_database).ListAsync(SyncProfileId.New(), 0, 100);

        Assert.Empty(runs);
    }

    [Fact]
    public async Task Baseline_replace_is_atomic_revisioned_and_idempotent()
    {
        var store = new SqliteSyncBaselineStore(_database);
        var items = new Dictionary<string, SyncBaselineObservation>(StringComparer.Ordinal)
        {
            ["folder/a.txt"] = SyncBaselineObservation.Present(
                12,
                new ContentDigest("sha256", new string('A', 64)),
                "left-v1",
                "right-v1"),
            ["removed.txt"] = SyncBaselineObservation.Missing
        };
        var request = new SyncBaselineReplaceRequest(_profileId, 0, 1, items, Now);

        var first = await store.ReplaceAsync(request);
        var retry = await store.ReplaceAsync(request);
        var loaded = await store.GetAsync(_profileId);

        Assert.Equal(SyncPersistenceMutationStatus.Applied, first.Status);
        Assert.Equal(1, first.Value!.Revision);
        Assert.Equal(SyncPersistenceMutationStatus.AlreadyApplied, retry.Status);
        Assert.Equal(first.Value.Sha256Digest, retry.Value!.Sha256Digest);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Items.Count);
        Assert.Equal(items["folder/a.txt"], loaded.Items["folder/a.txt"]);
        Assert.Equal(items["removed.txt"], loaded.Items["removed.txt"]);
    }

    [Fact]
    public async Task Concurrent_baseline_writers_cannot_both_advance_the_same_revision()
    {
        var firstStore = new SqliteSyncBaselineStore(_database);
        var secondStore = new SqliteSyncBaselineStore(new SingleWriterSqliteDatabase(_options));
        var firstRequest = new SyncBaselineReplaceRequest(
            _profileId,
            0,
            1,
            new Dictionary<string, SyncBaselineObservation>
            {
                ["first.txt"] = SyncBaselineObservation.Present(1, null, null, null)
            },
            Now);
        var secondRequest = new SyncBaselineReplaceRequest(
            _profileId,
            0,
            2,
            new Dictionary<string, SyncBaselineObservation>
            {
                ["second.txt"] = SyncBaselineObservation.Present(2, null, null, null)
            },
            Now.AddSeconds(1));

        var results = await Task.WhenAll(
            firstStore.ReplaceAsync(firstRequest).AsTask(),
            secondStore.ReplaceAsync(secondRequest).AsTask());

        Assert.Single(results, result => result.Status == SyncPersistenceMutationStatus.Applied);
        Assert.Single(results, result => result.Status == SyncPersistenceMutationStatus.Conflict);
        var loaded = await firstStore.GetAsync(_profileId);
        Assert.NotNull(loaded);
        Assert.Single(loaded.Items);
        Assert.Equal(1, loaded.Revision);
    }

    [Fact]
    public async Task Baseline_failure_after_profile_update_rolls_back_revision_and_items()
    {
        var store = new SqliteSyncBaselineStore(_database);
        var initial = new SyncBaselineReplaceRequest(
            _profileId,
            0,
            1,
            new Dictionary<string, SyncBaselineObservation>
            {
                ["stable.txt"] = SyncBaselineObservation.Present(5, null, null, null)
            },
            Now);
        Assert.Equal(SyncPersistenceMutationStatus.Applied, (await store.ReplaceAsync(initial)).Status);
        var throwingItems = new ThrowOnSecondEnumerationDictionary(
            new Dictionary<string, SyncBaselineObservation>
            {
                ["new-a.txt"] = SyncBaselineObservation.Present(1, null, null, null),
                ["new-b.txt"] = SyncBaselineObservation.Present(2, null, null, null)
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReplaceAsync(
            new SyncBaselineReplaceRequest(_profileId, 1, 2, throwingItems, Now.AddSeconds(1))).AsTask());

        var loaded = await store.GetAsync(_profileId);
        Assert.NotNull(loaded);
        Assert.Equal(1, loaded.Revision);
        Assert.Equal(1, loaded.Generation);
        Assert.Equal(["stable.txt"], loaded.Items.Keys);
    }

    [Fact]
    public async Task Immutable_plan_round_trips_and_rejects_substitution_or_sql_mutation()
    {
        var store = new SqliteSyncPlanStore(_database);
        var plan = CreatePlan(OperationPlanId.New(), "a.txt");

        var inserted = await store.PutAsync(plan);
        var retry = await store.PutAsync(plan);
        var substituted = await store.PutAsync(CreatePlan(plan.PlanId, "different.txt"));
        var loaded = await store.GetAsync(plan.PlanId);

        Assert.Equal(SyncPersistenceMutationStatus.Applied, inserted.Status);
        Assert.Equal(SyncPersistenceMutationStatus.AlreadyApplied, retry.Status);
        Assert.Equal(SyncPersistenceMutationStatus.Conflict, substituted.Status);
        Assert.NotNull(loaded);
        Assert.Equal(plan.Digest, loaded.Plan.Digest);
        Assert.True(loaded.Plan.HasValidDigest);
        Assert.Equal(plan.Operations.ToArray(), loaded.Plan.Operations.ToArray());
        Assert.Equal("left-etag", loaded.Plan.Operations[0].SourceOrTarget.EntityTag);
        Assert.Equal("right-etag", loaded.Plan.Operations[0].Destination!.EntityTag);
        Assert.Equal(new string('a', 64), loaded.Plan.Operations[0].SourceDigest!.Value);
        Assert.Equal(new string('b', 64), loaded.Plan.Operations[0].DestinationDigest!.Value);
        Assert.True(loaded.Plan.Operations[0].DestinationExisted);
        Assert.Equal(4, loaded.Plan.DigestSchemaVersion);

        await using var writer = await _database.AcquireWriterAsync();
        await using var command = writer.Connection.CreateCommand();
        command.CommandText = "UPDATE sync_plans SET plan_digest = $digest WHERE plan_id = $id;";
        command.Parameters.AddWithValue("$digest", new string('0', 64));
        command.Parameters.AddWithValue("$id", plan.PlanId.ToString());
        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Digest_schema_two_plan_without_portable_evidence_remains_readable()
    {
        var planId = OperationPlanId.New();
        var source = StorageAddress.Create(ConnectionProfileId.New(), "left-root", "legacy.bin").Value;
        var destination = StorageAddress.Create(ConnectionProfileId.New(), "right-root", "legacy.bin").Value;
        var legacy = ImmutableSyncPlan.Restore(
            planId,
            _profileId,
            baselineGeneration: 0,
            [SyncPlanOperation.Copy(0, source, destination, 12)],
            Now,
            digestSchemaVersion: 2);
        var store = new SqliteSyncPlanStore(_database);

        Assert.Equal(SyncPersistenceMutationStatus.Applied, (await store.PutAsync(legacy)).Status);
        var loaded = Assert.IsType<PersistedSyncPlan>(await store.GetAsync(planId));

        Assert.Equal(2, loaded.Plan.DigestSchemaVersion);
        Assert.Equal(legacy.Digest, loaded.Plan.Digest);
        Assert.True(loaded.Plan.HasValidDigest);
    }

    [Fact]
    public async Task Conflict_resolution_uses_optimistic_revision_and_is_idempotent()
    {
        var store = new SqliteSyncConflictStore(_database);
        var conflictId = Guid.NewGuid();
        var draft = new SyncConflictDraft(
            conflictId,
            _runId,
            "shared.txt",
            "both-modified",
            "{\"summary\":\"Both sides changed\"}",
            Now);
        Assert.Equal(SyncPersistenceMutationStatus.Applied, (await store.AddAsync(draft)).Status);
        Assert.Equal(SyncPersistenceMutationStatus.AlreadyApplied, (await store.AddAsync(draft)).Status);

        var firstResolution = new SyncConflictResolution(
            SyncConflictState.Resolved,
            "{\"choice\":\"left\"}",
            Now.AddMinutes(1));
        var secondResolution = firstResolution with { SafeResolutionJson = "{\"choice\":\"right\"}" };
        var resolutions = await Task.WhenAll(
            store.ResolveAsync(conflictId, 1, firstResolution).AsTask(),
            new SqliteSyncConflictStore(new SingleWriterSqliteDatabase(_options))
                .ResolveAsync(conflictId, 1, secondResolution).AsTask());

        Assert.Single(resolutions, result => result.Status == SyncPersistenceMutationStatus.Applied);
        Assert.Single(resolutions, result => result.Status == SyncPersistenceMutationStatus.Conflict);
        var winner = resolutions.Single(result => result.Status == SyncPersistenceMutationStatus.Applied).Value!;
        var retry = await store.ResolveAsync(
            conflictId,
            expectedRevision: 1,
            new SyncConflictResolution(winner.State, winner.SafeResolutionJson!, winner.ResolvedAtUtc!.Value));
        Assert.Equal(SyncPersistenceMutationStatus.AlreadyApplied, retry.Status);
        Assert.Equal(2, retry.Value!.Revision);
    }

    [Fact]
    public async Task Audit_and_outbox_append_is_one_crash_safe_idempotent_transaction()
    {
        var auditStore = new SqliteAuditEventStore(_database);
        var outboxStore = new SqliteReliableOutboxStore(_database);
        var request = AuditRequest(Guid.NewGuid(), Guid.NewGuid(), "sync-1", 1, "audit-once");

        var inserted = await auditStore.AppendAsync(request);
        var retry = await auditStore.AppendAsync(request);

        Assert.Equal(SyncPersistenceMutationStatus.Applied, inserted.Status);
        Assert.Equal(SyncPersistenceMutationStatus.AlreadyApplied, retry.Status);
        Assert.NotNull(await outboxStore.GetAsync(request.OutboxEvent!.EventId));

        var occupied = new OutboxEventDraft(
            Guid.NewGuid(), "sync.changed", "sync-2", 7, "{}", Now.AddSeconds(1));
        Assert.Equal(SyncPersistenceMutationStatus.Applied, (await outboxStore.EnqueueAsync(occupied)).Status);
        var rolledBackAuditId = Guid.NewGuid();
        var colliding = AuditRequest(
            rolledBackAuditId,
            Guid.NewGuid(),
            occupied.AggregateId,
            occupied.SequenceNumber,
            "must-roll-back");

        var rejected = await auditStore.AppendAsync(colliding);

        Assert.Equal(SyncPersistenceMutationStatus.Conflict, rejected.Status);
        Assert.Null(await auditStore.GetAsync(rolledBackAuditId));
    }

    [Fact]
    public async Task Audit_sequence_is_ordered_idempotent_and_database_immutable()
    {
        var store = new SqliteAuditEventStore(_database);
        var firstDraft = AuditRequest(
            Guid.NewGuid(), Guid.NewGuid(), "sync-a", 20, "ordered-a").AuditEvent;
        var secondDraft = AuditRequest(
            Guid.NewGuid(), Guid.NewGuid(), "sync-b", 21, "ordered-b").AuditEvent with
        {
            OccurredAtUtc = Now.AddSeconds(1)
        };
        var first = await store.AppendAsync(new AuditAppendRequest(firstDraft, null));
        var second = await store.AppendAsync(new AuditAppendRequest(secondDraft, null));

        Assert.Equal(first.Value!.SequenceNumber + 1, second.Value!.SequenceNumber);
        Assert.Equal([second.Value], await store.ReadAfterAsync(first.Value.SequenceNumber, 10));
        var reusedKey = secondDraft with { EventId = Guid.NewGuid() };
        Assert.Equal(
            SyncPersistenceMutationStatus.Conflict,
            (await store.AppendAsync(new AuditAppendRequest(reusedKey, null))).Status);

        await using var writer = await _database.AcquireWriterAsync();
        await using var command = writer.Connection.CreateCommand();
        command.CommandText = "UPDATE audit_events SET event_kind = 'rewritten' WHERE audit_event_id = $id;";
        command.Parameters.AddWithValue("$id", firstDraft.EventId.ToString("D"));
        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Outbox_claims_are_fenced_recoverable_and_at_least_once()
    {
        var store = new SqliteReliableOutboxStore(_database);
        var draft = new OutboxEventDraft(
            Guid.NewGuid(), "sync.completed", "sync-1", 10, "{\"ok\":true}", Now);
        Assert.Equal(SyncPersistenceMutationStatus.Applied, (await store.EnqueueAsync(draft)).Status);

        var first = Assert.Single(await store.ClaimPendingAsync(
            "worker-a", 1, Now.AddSeconds(1), TimeSpan.FromSeconds(10)));
        Assert.Empty(await new SqliteReliableOutboxStore(new SingleWriterSqliteDatabase(_options))
            .ClaimPendingAsync("worker-b", 1, Now.AddSeconds(2), TimeSpan.FromSeconds(10)));

        var replacement = Assert.Single(await store.ClaimPendingAsync(
            "worker-b", 1, first.ExpiresAtUtc, TimeSpan.FromSeconds(10)));
        Assert.True(replacement.FencingToken > first.FencingToken);
        Assert.Equal(
            SyncPersistenceMutationStatus.StaleLease,
            await store.CompleteAsync(first, first.ExpiresAtUtc));
        Assert.Equal(
            SyncPersistenceMutationStatus.Applied,
            await store.CompleteAsync(replacement, replacement.AcquiredAtUtc.AddSeconds(1)));
        Assert.Equal(
            SyncPersistenceMutationStatus.AlreadyApplied,
            await store.CompleteAsync(replacement, replacement.AcquiredAtUtc.AddSeconds(1)));
    }

    [Fact]
    public async Task Failed_outbox_delivery_respects_retry_time_and_dead_letter_state()
    {
        var store = new SqliteReliableOutboxStore(_database);
        var draft = new OutboxEventDraft(
            Guid.NewGuid(), "sync.failed", "sync-1", 11, "{}", Now);
        _ = await store.EnqueueAsync(draft);
        var first = Assert.Single(await store.ClaimPendingAsync(
            "worker", 1, Now, TimeSpan.FromMinutes(1)));
        var retryAt = Now.AddMinutes(5);

        Assert.Equal(SyncPersistenceMutationStatus.Applied, await store.FailAsync(
            first, Now.AddSeconds(1), retryAt, "delivery.timeout", "Delivery timed out.", false));
        Assert.Empty(await store.ClaimPendingAsync(
            "worker", 1, retryAt.AddTicks(-1), TimeSpan.FromMinutes(1)));
        var retry = Assert.Single(await store.ClaimPendingAsync(
            "worker", 1, retryAt, TimeSpan.FromMinutes(1)));
        Assert.Equal(SyncPersistenceMutationStatus.Applied, await store.FailAsync(
            retry, retryAt.AddSeconds(1), retryAt.AddHours(1), "delivery.rejected", "Delivery rejected.", true));
        Assert.Empty(await store.ClaimPendingAsync(
            "worker", 1, retryAt.AddDays(1), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task Selective_outbox_claim_and_renewal_never_hide_unowned_events_and_preserve_fence()
    {
        var store = new SqliteReliableOutboxStore(_database);
        var unrelated = new OutboxEventDraft(
            Guid.NewGuid(), "notifications.send", "notification:1", 1, "{}", Now);
        var owned = new OutboxEventDraft(
            Guid.NewGuid(), SyncOutboxEventKinds.PreviewRequested, "sync-schedule:1", 1, "{}", Now);
        _ = await store.EnqueueAsync(unrelated);
        _ = await store.EnqueueAsync(owned);

        var claim = Assert.Single(await store.ClaimPendingByKindsAsync(
            "sync-worker",
            [SyncOutboxEventKinds.PreviewRequested, SyncOutboxEventKinds.ApplyRequested],
            1,
            Now.AddSeconds(1),
            TimeSpan.FromSeconds(10)));
        Assert.Equal(owned.EventId, claim.Event.EventId);

        var unownedClaim = Assert.Single(await store.ClaimPendingAsync(
            "notification-worker",
            1,
            Now.AddSeconds(2),
            TimeSpan.FromSeconds(10)));
        Assert.Equal(unrelated.EventId, unownedClaim.Event.EventId);

        var renewed = await store.RenewAsync(
            claim,
            Now.AddSeconds(5),
            TimeSpan.FromSeconds(10));
        Assert.Equal(SyncPersistenceMutationStatus.Applied, renewed.Status);
        Assert.Equal(claim.FencingToken, renewed.Value!.FencingToken);
        Assert.Equal(Now.AddSeconds(15), renewed.Value.ExpiresAtUtc);
        Assert.Empty(await store.ClaimPendingByKindsAsync(
            "replacement",
            [SyncOutboxEventKinds.PreviewRequested],
            1,
            claim.ExpiresAtUtc,
            TimeSpan.FromSeconds(10)));

        var replacement = Assert.Single(await store.ClaimPendingByKindsAsync(
            "replacement",
            [SyncOutboxEventKinds.PreviewRequested],
            1,
            renewed.Value.ExpiresAtUtc,
            TimeSpan.FromSeconds(10)));
        Assert.True(replacement.FencingToken > claim.FencingToken);
        Assert.Equal(
            SyncPersistenceMutationStatus.StaleLease,
            (await store.RenewAsync(claim, Now.AddSeconds(6), TimeSpan.FromSeconds(10))).Status);
    }

    [Fact]
    public async Task Bounded_json_rejects_oversize_duplicate_and_deep_payloads()
    {
        var store = new SqliteReliableOutboxStore(_database);
        var oversized = "{\"value\":\"" + new string('x', 70_000) + "\"}";
        var duplicate = "{\"value\":1,\"value\":2}";
        var deep = string.Concat(Enumerable.Repeat("{\"x\":", 40)) + "0" +
            string.Concat(Enumerable.Repeat("}", 40));

        await Assert.ThrowsAsync<ArgumentException>(() => store.EnqueueAsync(
            new OutboxEventDraft(Guid.NewGuid(), "event", "aggregate", 1, oversized, Now)).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => store.EnqueueAsync(
            new OutboxEventDraft(Guid.NewGuid(), "event", "aggregate", 2, duplicate, Now)).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => store.EnqueueAsync(
            new OutboxEventDraft(Guid.NewGuid(), "event", "aggregate", 3, deep, Now)).AsTask());
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private async Task SeedSyncProfileAndRunAsync()
    {
        var left = ConnectionProfileId.New();
        var right = ConnectionProfileId.New();
        await using var writer = await _database.AcquireWriterAsync();
        await using var transaction = (SqliteTransaction)await writer.Connection.BeginTransactionAsync();
        await InsertConnectionAsync(writer.Connection, transaction, left, "Left");
        await InsertConnectionAsync(writer.Connection, transaction, right, "Right");
        await using (var profile = writer.Connection.CreateCommand())
        {
            profile.Transaction = transaction;
            profile.CommandText = """
                INSERT INTO sync_profiles
                (sync_profile_id, display_name, left_profile_id, right_profile_id, left_root, right_root,
                 direction, deletion_policy, conflict_policy, policy_hash, enabled, created_utc, updated_utc)
                VALUES ($id, 'Test sync', $left, $right, '', '', 'bidirectional', 'disabled', 'block',
                        $hash, 1, $now, $now);
                """;
            profile.Parameters.AddWithValue("$id", _profileId.ToString());
            profile.Parameters.AddWithValue("$left", left.ToString());
            profile.Parameters.AddWithValue("$right", right.ToString());
            profile.Parameters.AddWithValue("$hash", new string('A', 64));
            profile.Parameters.AddWithValue("$now", Format(Now));
            _ = await profile.ExecuteNonQueryAsync();
        }

        await using (var run = writer.Connection.CreateCommand())
        {
            run.Transaction = transaction;
            run.CommandText = """
                INSERT INTO sync_runs
                (sync_run_id, sync_profile_id, generation, trigger_kind, state, started_utc)
                VALUES ($id, $profile, 1, 'manual', 'planning', $now);
                """;
            run.Parameters.AddWithValue("$id", _runId.ToString());
            run.Parameters.AddWithValue("$profile", _profileId.ToString());
            run.Parameters.AddWithValue("$now", Format(Now));
            _ = await run.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task InsertConnectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ConnectionProfileId id,
        string name)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO connection_profiles
            (profile_id, provider, display_name, tags_json, metadata_json, endpoint_json,
             authentication_json, operational_options_json, is_favorite, is_enabled, version,
             created_utc, updated_utc)
            VALUES ($id, 'local', $name, '[]', '{}', '{}', '{}', '{}', 0, 1, 1, $now, $now);
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$now", Format(Now));
        _ = await command.ExecuteNonQueryAsync();
    }

    private ImmutableSyncPlan CreatePlan(OperationPlanId planId, string relativePath)
    {
        var connectionId = ConnectionProfileId.New();
        var source = StorageAddress.Create(
            connectionId,
            "left-root",
            relativePath,
            versionId: "left-v1",
            entityTag: "left-etag").Value;
        var destination = StorageAddress.Create(
            connectionId,
            "right-root",
            relativePath,
            versionId: "right-v1",
            entityTag: "right-etag").Value;
        return ImmutableSyncPlan.Create(
            planId,
            _profileId,
            0,
            [SyncPlanOperation.Copy(
                0,
                source,
                destination,
                12,
                new PortableContentDigest(PortableChecksumAlgorithm.Sha256, new string('a', 64)),
                new PortableContentDigest(PortableChecksumAlgorithm.Sha256, new string('b', 64)),
                destinationExisted: true)],
            Now);
    }

    private static AuditAppendRequest AuditRequest(
        Guid auditId,
        Guid outboxId,
        string aggregate,
        long sequence,
        string idempotencyKey) => new(
        new AuditEventDraft(
            auditId,
            "sync.changed",
            "test-user",
            "sync-profile",
            aggregate,
            "{\"safe\":true}",
            Now,
            "correlation-1",
            idempotencyKey),
        new OutboxEventDraft(
            outboxId,
            "sync.changed",
            aggregate,
            sequence,
            "{\"safe\":true}",
            Now));

    private static string Format(DateTimeOffset value) => value.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private sealed class ThrowOnSecondEnumerationDictionary(
        IReadOnlyDictionary<string, SyncBaselineObservation> inner)
        : IReadOnlyDictionary<string, SyncBaselineObservation>
    {
        private int _enumerations;

        public int Count => inner.Count;
        public IEnumerable<string> Keys => inner.Keys;
        public IEnumerable<SyncBaselineObservation> Values => inner.Values;
        public SyncBaselineObservation this[string key] => inner[key];
        public bool ContainsKey(string key) => inner.ContainsKey(key);
        public bool TryGetValue(string key, out SyncBaselineObservation value) => inner.TryGetValue(key, out value!);

        public IEnumerator<KeyValuePair<string, SyncBaselineObservation>> GetEnumerator()
        {
            if (Interlocked.Increment(ref _enumerations) > 1)
            {
                throw new InvalidOperationException("Simulated materialization failure.");
            }

            return inner.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

public sealed class SyncDurabilityMigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"storagehub-sync-migration-{Guid.NewGuid():N}");

    [Fact]
    public async Task Version_one_database_upgrades_to_sync_durability_schema_without_losing_legacy_rows()
    {
        var options = new SqliteDatabaseOptions(Path.Combine(_directory, "storagehub.db"), pooling: false);
        Assert.True((await new StorageHubDatabaseInitializer(options, [new InitialSchemaMigration()])
            .InitializeAsync()).IsReady);
        await using (var connection = new SqliteConnection($"Data Source={options.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO audit_events
                (audit_event_id, event_kind, safe_payload_json, occurred_utc)
                VALUES ('11111111-1111-1111-1111-111111111111', 'legacy', '{}',
                        '2026-08-02T12:00:00.0000000+00:00');
                """;
            _ = await command.ExecuteNonQueryAsync();
        }

        var upgraded = await new StorageHubDatabaseInitializer(options).InitializeAsync();

        Assert.True(upgraded.IsReady, upgraded.Message);
        Assert.Equal(NonAtomicSyncWritesSchemaMigration.SchemaVersion, upgraded.SchemaVersion);
        await using var read = new SqliteConnection($"Data Source={options.DatabasePath};Mode=ReadOnly;Pooling=False");
        await read.OpenAsync();
        await using var count = read.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM audit_events WHERE event_kind = 'legacy' AND sequence_number IS NOT NULL;";
        Assert.Equal(1L, Convert.ToInt64(await count.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
