using System.Globalization;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Persistence.Transfers;
using StorageHub.Storage.Abstractions;
using StorageHub.Transfers;
using Xunit;

namespace StorageHub.Persistence.Tests.Transfers;

public sealed class SqliteTransferJobStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"storagehub-transfers-{Guid.NewGuid():N}");

    [Fact]
    public async Task Enqueue_round_trips_complete_immutable_intent_and_rejects_duplicate()
    {
        var fixture = await CreateFixtureAsync();
        var intent = await CreateIntentAsync(
            fixture,
            expectedDestinationVersionId: "approved-destination-version",
            expectedDestinationEntityTag: "approved-destination-etag");

        Assert.True(await fixture.Store.TryEnqueueAsync(intent, priority: 17));
        Assert.False(await fixture.Store.TryEnqueueAsync(intent, priority: 99));

        var stored = Assert.IsType<DurableTransferJob>(await fixture.Store.FindAsync(intent.TransferJobId));
        Assert.Equal(intent, stored.Intent);
        Assert.Equal("source-root", stored.Intent.Source.RootIdentity);
        Assert.Equal("source-native-id", stored.Intent.Source.NativeItemId);
        Assert.Equal("source-version", stored.Intent.Source.VersionId);
        Assert.Equal("source-etag", stored.Intent.Source.EntityTag);
        Assert.Equal("approved-destination-version", stored.Intent.ExpectedDestinationVersionId);
        Assert.Equal("approved-destination-etag", stored.Intent.ExpectedDestinationEntityTag);
        Assert.Equal(new string('a', 64), stored.Intent.ExpectedSourceDigest!.Value);
        Assert.Equal(new string('b', 64), stored.Intent.ExpectedDestinationDigest!.Value);
        Assert.Equal(new string('a', 64), stored.Intent.RequiredDestinationDigest!.Value);
        Assert.Equal(TransferVerificationPolicy.StrongHashRequired, stored.Intent.VerificationPolicy);
        Assert.Equal(17, stored.Priority);
        Assert.Equal(TransferState.Pending, stored.State.State);
        Assert.Equal(0, stored.State.Revision);
        Assert.Equal(0, stored.State.Attempt);
        Assert.Null(stored.ActiveLease);
    }

    [Fact]
    public async Task Queue_query_uses_bounded_stable_keyset_pages_without_duplicates()
    {
        var fixture = await CreateFixtureAsync();
        var intents = new List<TransferIntent>();
        for (var index = 0; index < 5; index++)
        {
            var intent = await CreateIntentAsync(fixture);
            intents.Add(intent);
            Assert.True(await fixture.Store.TryEnqueueAsync(intent, priority: index));
        }

        var first = await fixture.Store.ListAsync(new TransferQueueQuery(
            [TransferState.Pending],
            pageSize: 2));
        var second = await fixture.Store.ListAsync(new TransferQueueQuery(
            [TransferState.Pending],
            pageSize: 2,
            Assert.IsType<TransferQueueCursor>(first.Continuation)));
        var third = await fixture.Store.ListAsync(new TransferQueueQuery(
            [TransferState.Pending],
            pageSize: 2,
            Assert.IsType<TransferQueueCursor>(second.Continuation)));
        var observed = first.Jobs.Concat(second.Jobs).Concat(third.Jobs).ToArray();

        Assert.Equal(2, first.Jobs.Count);
        Assert.Equal(2, second.Jobs.Count);
        Assert.Single(third.Jobs);
        Assert.Null(third.Continuation);
        Assert.Equal(5, observed.Select(static job => job.Intent.TransferJobId).Distinct().Count());
        Assert.Equal(
            intents.Select(static intent => intent.TransferJobId).OrderBy(static id => id.ToString()),
            observed.Select(static job => job.Intent.TransferJobId).OrderBy(static id => id.ToString()));
    }

    [Fact]
    public async Task Queue_query_applies_state_filter_before_pagination()
    {
        var fixture = await CreateFixtureAsync();
        var pending = await CreateIntentAsync(fixture);
        var cancelled = await CreateIntentAsync(fixture);
        Assert.True(await fixture.Store.TryEnqueueAsync(pending));
        Assert.True(await fixture.Store.TryEnqueueAsync(cancelled));
        var transition = await fixture.Store.TryTransitionControlStateAsync(
            new TransferControlStateTransitionRequest(
                cancelled.TransferJobId,
                expectedRevision: 0,
                TransferState.Cancelled,
                Now.AddSeconds(1)));
        Assert.Equal(TransferStoreMutationStatus.Applied, transition.Status);

        var page = await fixture.Store.ListAsync(new TransferQueueQuery(
            [TransferState.Pending],
            pageSize: 10));

        Assert.Single(page.Jobs);
        Assert.Equal(pending.TransferJobId, page.Jobs[0].Intent.TransferJobId);
    }

    [Fact]
    public async Task Version_two_in_flight_job_is_preserved_but_fail_closed_for_reconciliation()
    {
        var options = new SqliteDatabaseOptions(
            Path.Combine(_directory, "legacy.db"),
            pooling: false);
        var legacyInitialization = await new StorageHubDatabaseInitializer(
            options,
            [new InitialSchemaMigration(), new SchedulerSchemaMigration()]).InitializeAsync();
        Assert.True(legacyInitialization.IsReady, legacyInitialization.Message);
        var database = new SingleWriterSqliteDatabase(options);
        var source = ConnectionProfileId.New();
        var destination = ConnectionProfileId.New();
        await SeedProfileAsync(database, source, $"Legacy-source-{source}");
        await SeedProfileAsync(database, destination, $"Legacy-destination-{destination}");
        var jobId = TransferJobId.New();
        await using (var writer = await database.AcquireWriterAsync())
        await using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO transfer_jobs
                (
                    transfer_job_id, source_profile_id, destination_profile_id,
                    source_path, destination_path, operation_kind, state,
                    expected_size, created_utc, updated_utc, owner_epoch,
                    claimed_by, claim_expires_utc
                )
                VALUES
                (
                    $jobId, $source, $destination, 'legacy/source.bin',
                    'legacy/destination.bin', 'Copy', 'Transferring', 100,
                    $now, $now, 7, 'legacy-worker', $expires
                );
                INSERT INTO transfer_attempts
                (
                    transfer_attempt_id, transfer_job_id, attempt_number, started_utc
                )
                VALUES ($attemptId, $jobId, 1, $now);
                """;
            command.Parameters.AddWithValue("$jobId", jobId.ToString());
            command.Parameters.AddWithValue("$source", source.ToString());
            command.Parameters.AddWithValue("$destination", destination.ToString());
            command.Parameters.AddWithValue("$now", Now.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue(
                "$expires",
                Now.AddHours(1).ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue(
                "$attemptId",
                Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture));
            _ = await command.ExecuteNonQueryAsync();
        }

        var upgraded = await new StorageHubDatabaseInitializer(options).InitializeAsync();
        var store = new SqliteTransferJobStore(database);
        var legacyJob = Assert.IsType<DurableTransferJob>(await store.FindAsync(jobId));

        Assert.True(upgraded.IsReady, upgraded.Message);
        Assert.Equal(NonAtomicSyncWritesSchemaMigration.SchemaVersion, upgraded.SchemaVersion);
        Assert.Equal(TransferState.NeedsReconciliation, legacyJob.State.State);
        Assert.Equal(TransferStatusCode.StateUncertain, legacyJob.State.StatusCode);
        Assert.Equal(1, legacyJob.State.Attempt);
        Assert.Null(legacyJob.ActiveLease);
        Assert.StartsWith("legacy-unverified:", legacyJob.Intent.Source.RootIdentity, StringComparison.Ordinal);
        Assert.Null(await store.TryClaimNextAsync(Claim("new-worker", Now.AddHours(2))));
    }

    [Fact]
    public async Task Atomic_claim_selects_priority_and_creates_one_fenced_attempt()
    {
        var fixture = await CreateFixtureAsync();
        var low = await CreateIntentAsync(fixture);
        var high = await CreateIntentAsync(fixture);
        Assert.True(await fixture.Store.TryEnqueueAsync(low, priority: 1));
        Assert.True(await fixture.Store.TryEnqueueAsync(high, priority: 50));
        var competingStore = new SqliteTransferJobStore(new SingleWriterSqliteDatabase(fixture.Options));

        var claims = await Task.WhenAll(
            fixture.Store.TryClaimNextAsync(Claim("owner-a", Now)).AsTask(),
            competingStore.TryClaimNextAsync(Claim("owner-b", Now)).AsTask());

        Assert.Equal(2, claims.Count(claim => claim is not null));
        Assert.Contains(claims, claim => claim!.Job.Intent.TransferJobId == high.TransferJobId);
        Assert.All(claims, claim =>
        {
            Assert.Equal(TransferState.Preparing, claim!.Job.State.State);
            Assert.Equal(1, claim.Job.State.Attempt);
            Assert.Equal(1, claim.Job.State.Revision);
            Assert.Equal(1, claim.Lease.FencingToken);
        });
        Assert.Equal(2L, await ScalarInt64Async(
            fixture.Database,
            "SELECT COUNT(*) FROM transfer_attempts WHERE attempt_number = 1 AND completed_utc IS NULL;"));
    }

    [Fact]
    public async Task Claim_is_exclusive_for_a_single_job()
    {
        var fixture = await CreateFixtureAsync();
        var intent = await CreateIntentAsync(fixture);
        Assert.True(await fixture.Store.TryEnqueueAsync(intent));
        var competingStore = new SqliteTransferJobStore(new SingleWriterSqliteDatabase(fixture.Options));

        var claims = await Task.WhenAll(
            fixture.Store.TryClaimNextAsync(Claim("owner-a", Now)).AsTask(),
            competingStore.TryClaimNextAsync(Claim("owner-b", Now)).AsTask());

        Assert.Single(claims, claim => claim is not null);
        Assert.Single(claims, claim => claim is null);
        Assert.Equal(1L, await ScalarInt64Async(
            fixture.Database,
            "SELECT COUNT(*) FROM transfer_attempts;"));
    }

    [Fact]
    public async Task Transition_is_revision_CAS_and_releases_attempt_on_retry()
    {
        var fixture = await CreateFixtureAsync();
        var intent = await CreateIntentAsync(fixture);
        Assert.True(await fixture.Store.TryEnqueueAsync(intent));
        var claim = Assert.IsType<TransferJobClaim>(await fixture.Store.TryClaimNextAsync(Claim("worker", Now)));
        var connected = await TransitionAsync(
            fixture.Store,
            claim.Lease,
            claim.Job.State.Revision,
            TransferState.Connecting,
            Now.AddSeconds(1));
        var stale = await fixture.Store.TryTransitionAsync(new TransferStateTransitionRequest(
            claim.Lease,
            claim.Job.State.Revision,
            TransferState.Transferring,
            Now.AddSeconds(2)));
        var retryAt = Now.AddMinutes(5);
        var retry = await fixture.Store.TryTransitionAsync(new TransferStateTransitionRequest(
            claim.Lease,
            connected.State.Revision,
            TransferState.Retrying,
            Now.AddSeconds(3),
            error: new TransferSafeError("transfer.network.transient", "The endpoint was unavailable."),
            retryAvailableAtUtc: retryAt));

        Assert.Equal(TransferStoreMutationStatus.Conflict, stale.Status);
        Assert.Equal(TransferStoreMutationStatus.Applied, retry.Status);
        Assert.Equal(TransferState.Retrying, retry.Value!.State.State);
        Assert.Equal(retryAt, retry.Value.RetryAvailableAtUtc);
        Assert.Null(retry.Value.ActiveLease);
        Assert.Null(await fixture.Store.TryClaimNextAsync(Claim("early-worker", retryAt.AddTicks(-1))));
        var secondAttempt = Assert.IsType<TransferJobClaim>(
            await fixture.Store.TryClaimNextAsync(Claim("next-worker", retryAt)));
        Assert.Equal(2, secondAttempt.Lease.Attempt);
        Assert.Equal(2, secondAttempt.Lease.FencingToken);
        Assert.Equal("retrying", await ScalarTextAsync(
            fixture.Database,
            "SELECT outcome FROM transfer_attempts WHERE attempt_number = 1;"));
    }

    [Fact]
    public async Task Lease_renewal_and_all_owner_writes_are_fenced()
    {
        var fixture = await CreateFixtureAsync();
        var intent = await CreateIntentAsync(fixture);
        Assert.True(await fixture.Store.TryEnqueueAsync(intent));
        var claim = Assert.IsType<TransferJobClaim>(await fixture.Store.TryClaimNextAsync(Claim("worker", Now)));
        var renewal = await fixture.Store.TryRenewLeaseAsync(new TransferLeaseRenewal(
            claim.Lease,
            Now.AddMinutes(1),
            Now.AddMinutes(40)));
        var wrongFence = new TransferJobLease(
            claim.Lease.TransferJobId,
            claim.Lease.OwnerId,
            claim.Lease.FencingToken + 1,
            claim.Lease.Attempt,
            claim.Lease.AcquiredAtUtc,
            Now.AddMinutes(40));
        var rejected = await fixture.Store.TryTransitionAsync(new TransferStateTransitionRequest(
            wrongFence,
            claim.Job.State.Revision,
            TransferState.Connecting,
            Now.AddMinutes(2)));

        Assert.Equal(TransferStoreMutationStatus.Applied, renewal.Status);
        Assert.Equal(Now.AddMinutes(40), renewal.Value!.ExpiresAtUtc);
        Assert.Equal(TransferStoreMutationStatus.LeaseLost, rejected.Status);
        var stored = Assert.IsType<DurableTransferJob>(await fixture.Store.FindAsync(intent.TransferJobId));
        Assert.Equal(TransferState.Preparing, stored.State.State);
    }

    [Fact]
    public async Task Checkpoint_round_trip_is_fenced_versioned_and_monotonic()
    {
        var fixture = await CreateFixtureAsync();
        var intent = await CreateIntentAsync(fixture);
        Assert.True(await fixture.Store.TryEnqueueAsync(intent));
        var claim = Assert.IsType<TransferJobClaim>(await fixture.Store.TryClaimNextAsync(Claim("worker", Now)));
        var checkpoint = CreateCheckpoint(intent, claim.Lease.Attempt, 10, Now.AddSeconds(1));

        var created = await fixture.Store.TrySaveCheckpointAsync(
            new TransferCheckpointWriteRequest(claim.Lease, checkpoint, expectedVersion: null));
        var duplicateCreate = await fixture.Store.TrySaveCheckpointAsync(
            new TransferCheckpointWriteRequest(claim.Lease, checkpoint, expectedVersion: null));
        var advancedCheckpoint = CreateCheckpoint(intent, claim.Lease.Attempt, 25, Now.AddSeconds(2));
        var advanced = await fixture.Store.TrySaveCheckpointAsync(
            new TransferCheckpointWriteRequest(claim.Lease, advancedCheckpoint, created.Value!.Version));
        var staleUpdate = await fixture.Store.TrySaveCheckpointAsync(
            new TransferCheckpointWriteRequest(claim.Lease, advancedCheckpoint, created.Value.Version));

        Assert.Equal(TransferStoreMutationStatus.Applied, created.Status);
        Assert.Equal(1, created.Value.Version);
        Assert.Equal(TransferStoreMutationStatus.Conflict, duplicateCreate.Status);
        Assert.Equal(TransferStoreMutationStatus.Applied, advanced.Status);
        Assert.Equal(2, advanced.Value!.Version);
        Assert.Equal(TransferStoreMutationStatus.Conflict, staleUpdate.Status);
        var loaded = Assert.IsType<PersistedTransferCheckpoint>(
            await fixture.Store.FindCheckpointAsync(intent.TransferJobId));
        Assert.Equal(advancedCheckpoint, loaded.Checkpoint);

        var backwards = CreateCheckpoint(intent, claim.Lease.Attempt, 24, Now.AddSeconds(3));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.TrySaveCheckpointAsync(
            new TransferCheckpointWriteRequest(claim.Lease, backwards, advanced.Value.Version)).AsTask());
    }

    [Fact]
    public async Task Expired_running_claim_is_interrupted_but_live_claim_is_preserved()
    {
        var fixture = await CreateFixtureAsync();
        var expiredIntent = await CreateIntentAsync(fixture);
        var liveIntent = await CreateIntentAsync(fixture);
        Assert.True(await fixture.Store.TryEnqueueAsync(expiredIntent, priority: 10));
        Assert.True(await fixture.Store.TryEnqueueAsync(liveIntent, priority: 1));
        var expired = Assert.IsType<TransferJobClaim>(await fixture.Store.TryClaimNextAsync(
            new TransferClaimRequest("expired-worker", Now, TimeSpan.FromMinutes(1))));
        var live = Assert.IsType<TransferJobClaim>(await fixture.Store.TryClaimNextAsync(
            new TransferClaimRequest("live-worker", Now, TimeSpan.FromMinutes(30))));
        Assert.Equal(expiredIntent.TransferJobId, expired.Job.Intent.TransferJobId);
        Assert.Equal(liveIntent.TransferJobId, live.Job.Intent.TransferJobId);

        var recovered = await fixture.Store.RecoverInterruptedAsync(Now.AddMinutes(2));

        Assert.Equal(1, recovered);
        var interrupted = Assert.IsType<DurableTransferJob>(
            await fixture.Store.FindAsync(expiredIntent.TransferJobId));
        var stillLive = Assert.IsType<DurableTransferJob>(
            await fixture.Store.FindAsync(liveIntent.TransferJobId));
        Assert.Equal(TransferState.Interrupted, interrupted.State.State);
        Assert.Equal(TransferStatusCode.Interrupted, interrupted.State.StatusCode);
        Assert.Null(interrupted.ActiveLease);
        Assert.NotNull(stillLive.ActiveLease);
        Assert.Equal("interrupted", await ScalarTextAsync(
            fixture.Database,
            "SELECT outcome FROM transfer_attempts WHERE transfer_job_id = $jobId;",
            ("$jobId", expiredIntent.TransferJobId.ToString())));

        var restartRequired = await fixture.Store.TryTransitionControlStateAsync(
            new TransferControlStateTransitionRequest(
                interrupted.Intent.TransferJobId,
                interrupted.State.Revision,
                TransferState.RestartRequired,
                Now.AddMinutes(3)));
        var pending = await fixture.Store.TryTransitionControlStateAsync(
            new TransferControlStateTransitionRequest(
                interrupted.Intent.TransferJobId,
                restartRequired.Value!.State.Revision,
                TransferState.Pending,
                Now.AddMinutes(4)));
        var reclaimed = Assert.IsType<TransferJobClaim>(
            await fixture.Store.TryClaimNextAsync(Claim("recovery-worker", Now.AddMinutes(5))));
        Assert.Equal(TransferStoreMutationStatus.Applied, restartRequired.Status);
        Assert.Equal(TransferStoreMutationStatus.Applied, pending.Status);
        Assert.Equal(expiredIntent.TransferJobId, reclaimed.Job.Intent.TransferJobId);
        Assert.Equal(2, reclaimed.Lease.Attempt);
        Assert.Equal(2, reclaimed.Lease.FencingToken);
    }

    [Fact]
    public async Task Successful_completion_atomically_closes_attempt_and_removes_checkpoint()
    {
        var fixture = await CreateFixtureAsync();
        var intent = await CreateIntentAsync(fixture);
        Assert.True(await fixture.Store.TryEnqueueAsync(intent));
        var claim = Assert.IsType<TransferJobClaim>(await fixture.Store.TryClaimNextAsync(Claim("worker", Now)));
        var saved = await fixture.Store.TrySaveCheckpointAsync(new TransferCheckpointWriteRequest(
            claim.Lease,
            CreateCheckpoint(intent, claim.Lease.Attempt, 50, Now.AddSeconds(1)),
            expectedVersion: null));
        Assert.Equal(TransferStoreMutationStatus.Applied, saved.Status);
        var job = claim.Job;
        job = await TransitionAsync(fixture.Store, claim.Lease, job.State.Revision, TransferState.Connecting, Now.AddSeconds(2));
        job = await TransitionAsync(fixture.Store, claim.Lease, job.State.Revision, TransferState.Transferring, Now.AddSeconds(3));
        job = await TransitionAsync(fixture.Store, claim.Lease, job.State.Revision, TransferState.Verifying, Now.AddSeconds(4));
        job = await TransitionAsync(fixture.Store, claim.Lease, job.State.Revision, TransferState.Finalizing, Now.AddSeconds(5));
        job = await TransitionAsync(fixture.Store, claim.Lease, job.State.Revision, TransferState.Completed, Now.AddSeconds(6));

        Assert.Equal(TransferState.Completed, job.State.State);
        Assert.Null(job.ActiveLease);
        Assert.Null(await fixture.Store.FindCheckpointAsync(intent.TransferJobId));
        Assert.Equal("completed", await ScalarTextAsync(
            fixture.Database,
            "SELECT outcome FROM transfer_attempts WHERE transfer_job_id = $jobId;",
            ("$jobId", intent.TransferJobId.ToString())));
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
            Path.Combine(_directory, "storagehub.db"),
            pooling: false);
        var initialization = await new StorageHubDatabaseInitializer(options).InitializeAsync();
        Assert.True(initialization.IsReady, initialization.Message);
        var database = new SingleWriterSqliteDatabase(options);
        return new Fixture(options, database, new SqliteTransferJobStore(database));
    }

    private static async Task<TransferIntent> CreateIntentAsync(
        Fixture fixture,
        string? expectedDestinationVersionId = null,
        string? expectedDestinationEntityTag = null)
    {
        var sourceProfile = ConnectionProfileId.New();
        var destinationProfile = ConnectionProfileId.New();
        await SeedProfileAsync(fixture.Database, sourceProfile, $"Source-{sourceProfile}");
        await SeedProfileAsync(fixture.Database, destinationProfile, $"Destination-{destinationProfile}");
        var source = Address(
            sourceProfile,
            "source-root",
            "folder/source.bin",
            nativeId: "source-native-id",
            versionId: "source-version",
            entityTag: "source-etag");
        var destination = Address(
            destinationProfile,
            "destination-root",
            "archive/destination.bin",
            nativeId: "destination-native-id",
            versionId: "observed-destination-version",
            entityTag: "observed-destination-etag");
        return new TransferIntent(
            TransferJobId.New(),
            TransferOperationKind.Copy,
            source,
            destination,
            expectedLength: 100,
            TransferVerificationPolicy.StrongHashRequired,
            Now,
            expectedDestinationVersionId,
            expectedDestinationEntityTag,
            new PortableContentDigest(PortableChecksumAlgorithm.Sha256, new string('a', 64)),
            new PortableContentDigest(PortableChecksumAlgorithm.Sha256, new string('b', 64)),
            new PortableContentDigest(PortableChecksumAlgorithm.Sha256, new string('a', 64)));
    }

    private static TransferCheckpoint CreateCheckpoint(
        TransferIntent intent,
        int attempt,
        long verifiedBytes,
        DateTimeOffset recordedAtUtc) =>
        TransferCheckpoint.Create(
            intent.TransferJobId,
            attempt,
            verifiedBytes,
            intent.ExpectedLength,
            intent.Source,
            Address(
                intent.Destination.ProfileId,
                intent.Destination.RootIdentity,
                ".storagehub-partials/destination.part"),
            TransferResumeMode.Offset,
            new TransferContentDigest("SHA256", new string('a', 64)),
            providerResumeId: null,
            completedParts: [],
            recordedAtUtc);

    private static async Task<DurableTransferJob> TransitionAsync(
        SqliteTransferJobStore store,
        TransferJobLease lease,
        long expectedRevision,
        TransferState next,
        DateTimeOffset atUtc)
    {
        var result = await store.TryTransitionAsync(new TransferStateTransitionRequest(
            lease,
            expectedRevision,
            next,
            atUtc));
        Assert.Equal(TransferStoreMutationStatus.Applied, result.Status);
        return Assert.IsType<DurableTransferJob>(result.Value);
    }

    private static TransferClaimRequest Claim(string owner, DateTimeOffset atUtc) =>
        new(owner, atUtc, TimeSpan.FromMinutes(30));

    private static StorageAddress Address(
        ConnectionProfileId profileId,
        string root,
        string path,
        string? nativeId = null,
        string? versionId = null,
        string? entityTag = null)
    {
        var result = StorageAddress.Create(profileId, root, path, nativeId, versionId, entityTag);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        return result.Value;
    }

    private static async Task SeedProfileAsync(
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

    private static async Task<long> ScalarInt64Async(
        SingleWriterSqliteDatabase database,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = await database.OpenReadConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarTextAsync(
        SingleWriterSqliteDatabase database,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = await database.OpenReadConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture)!;
    }

    private sealed record Fixture(
        SqliteDatabaseOptions Options,
        SingleWriterSqliteDatabase Database,
        SqliteTransferJobStore Store);
}
