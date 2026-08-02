using System.Globalization;
using Microsoft.Data.Sqlite;
using StorageHub.Agent.Scheduling;
using StorageHub.Domain.Identifiers;
using Xunit;

namespace StorageHub.Persistence.Tests.Scheduling;

public sealed class SqliteScheduledSyncJobStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"storagehub-scheduler-{Guid.NewGuid():N}");

    [Fact]
    public async Task Reads_durable_schedule_snapshot_and_policy()
    {
        var fixture = await CreateFixtureAsync();
        var profileId = await SeedSyncProfileAsync(fixture);
        var jobId = await SeedScheduleAsync(
            fixture,
            profileId,
            enabled: true,
            queueOneWhileRunning: false,
            nextOccurrenceUtc: Now,
            misfireGrace: TimeSpan.FromMinutes(7));

        var jobs = await fixture.Store.GetJobsAsync();

        var job = Assert.Single(jobs);
        Assert.Equal(jobId, job.JobId);
        Assert.Equal(profileId, job.ProfileId);
        Assert.True(job.Enabled);
        Assert.False(job.QueueOneWhileRunning);
        Assert.Equal(Now, job.NextOccurrenceUtc);
        Assert.Equal(TimeSpan.FromMinutes(7), job.Schedule.MisfireGrace);
        Assert.Equal("* * * * *", job.Schedule.Expression);
        Assert.Equal("UTC", job.Schedule.TimeZoneId);
        Assert.Equal(0, job.Revision);
    }

    [Fact]
    public async Task Atomic_acquisition_allows_only_one_job_for_a_profile()
    {
        var fixture = await CreateFixtureAsync();
        var profileId = await SeedSyncProfileAsync(fixture);
        _ = await SeedScheduleAsync(fixture, profileId, nextOccurrenceUtc: Now);
        _ = await SeedScheduleAsync(fixture, profileId, nextOccurrenceUtc: Now);
        var snapshots = await fixture.Store.GetJobsAsync();
        var firstStore = fixture.Store;
        var secondStore = new SqliteScheduledSyncJobStore(
            new SingleWriterSqliteDatabase(fixture.Options),
            fixture.TimeProvider);

        var acquisitions = await Task.WhenAll(
            firstStore.TryAcquireLeaseAsync(Request(snapshots[0], Now)).AsTask(),
            secondStore.TryAcquireLeaseAsync(Request(snapshots[1], Now)).AsTask());

        Assert.Equal(1, acquisitions.Count(result =>
            result.Status == ScheduledSyncLeaseAcquisitionStatus.Acquired));
        Assert.Equal(1, acquisitions.Count(result =>
            result.Status == ScheduledSyncLeaseAcquisitionStatus.ProfileBusy));
        Assert.Equal(1, acquisitions.Single(result =>
            result.Status == ScheduledSyncLeaseAcquisitionStatus.Acquired).Lease!.FencingToken);
    }

    [Fact]
    public async Task Acquisition_is_revision_and_occurrence_compare_and_swap()
    {
        var fixture = await CreateFixtureAsync();
        var profileId = await SeedSyncProfileAsync(fixture);
        _ = await SeedScheduleAsync(fixture, profileId, nextOccurrenceUtc: Now);
        var snapshot = Assert.Single(await fixture.Store.GetJobsAsync());
        var request = Request(snapshot, Now);

        var acquired = await fixture.Store.TryAcquireLeaseAsync(request);
        var staleRetry = await fixture.Store.TryAcquireLeaseAsync(request);

        Assert.Equal(ScheduledSyncLeaseAcquisitionStatus.Acquired, acquired.Status);
        Assert.Equal(ScheduledSyncLeaseAcquisitionStatus.StaleSnapshot, staleRetry.Status);
        var updated = Assert.Single(await fixture.Store.GetJobsAsync());
        Assert.Equal(Now.AddMinutes(1), updated.NextOccurrenceUtc);
        Assert.Equal(1, updated.Revision);
    }

    [Fact]
    public async Task Disabled_job_cannot_be_leased_even_from_a_stale_enabled_caller()
    {
        var fixture = await CreateFixtureAsync();
        var profileId = await SeedSyncProfileAsync(fixture);
        _ = await SeedScheduleAsync(
            fixture,
            profileId,
            enabled: false,
            nextOccurrenceUtc: Now);
        var snapshot = Assert.Single(await fixture.Store.GetJobsAsync());

        var result = await fixture.Store.TryAcquireLeaseAsync(Request(snapshot, Now));

        Assert.Equal(ScheduledSyncLeaseAcquisitionStatus.Disabled, result.Status);
    }

    [Fact]
    public async Task Expired_profile_lease_is_reaped_before_another_job_acquires()
    {
        var fixture = await CreateFixtureAsync();
        var profileId = await SeedSyncProfileAsync(fixture);
        _ = await SeedScheduleAsync(fixture, profileId, nextOccurrenceUtc: Now);
        _ = await SeedScheduleAsync(fixture, profileId, nextOccurrenceUtc: Now.AddMinutes(2));
        var snapshots = await fixture.Store.GetJobsAsync();
        var firstRequest = Request(
            snapshots[0],
            Now,
            nextOccurrenceUtc: Now.AddMinutes(1),
            leaseDuration: TimeSpan.FromMinutes(1));
        var first = await fixture.Store.TryAcquireLeaseAsync(firstRequest);

        fixture.TimeProvider.Advance(TimeSpan.FromMinutes(2));
        var second = await fixture.Store.TryAcquireLeaseAsync(Request(
            snapshots[1],
            Now.AddMinutes(2),
            nextOccurrenceUtc: Now.AddMinutes(3)));

        Assert.Equal(ScheduledSyncLeaseAcquisitionStatus.Acquired, second.Status);
        var staleCompletion = new ScheduledSyncJobCompletion(
            first.Lease!,
            ScheduledSyncJobRunResult.Completed(),
            Now,
            Now.AddMinutes(2));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.RecordCompletionAsync(staleCompletion).AsTask());
    }

    [Fact]
    public async Task Renewal_requires_current_unexpired_fencing_token_and_extends_lease()
    {
        var fixture = await CreateFixtureAsync();
        var profileId = await SeedSyncProfileAsync(fixture);
        _ = await SeedScheduleAsync(fixture, profileId, nextOccurrenceUtc: Now);
        var snapshot = Assert.Single(await fixture.Store.GetJobsAsync());
        var acquired = await fixture.Store.TryAcquireLeaseAsync(Request(
            snapshot,
            Now,
            leaseDuration: TimeSpan.FromMinutes(10)));
        var lease = acquired.Lease!;

        fixture.TimeProvider.Advance(TimeSpan.FromMinutes(2));
        var renewed = await fixture.Store.TryRenewLeaseAsync(new ScheduledSyncLeaseRenewal(
            lease,
            Now.AddMinutes(2),
            Now.AddMinutes(12)));
        var wrongFence = new ScheduledSyncJobLease(
            lease.LeaseId,
            lease.JobId,
            lease.ProfileId,
            lease.ScheduledForUtc,
            lease.AcquiredAtUtc,
            lease.ExpiresAtUtc,
            lease.FencingToken + 1);
        var stale = await fixture.Store.TryRenewLeaseAsync(new ScheduledSyncLeaseRenewal(
            wrongFence,
            Now.AddMinutes(3),
            Now.AddMinutes(13)));
        fixture.TimeProvider.Advance(TimeSpan.FromMinutes(11));
        var expired = await fixture.Store.TryRenewLeaseAsync(new ScheduledSyncLeaseRenewal(
            lease,
            Now.AddMinutes(13),
            Now.AddMinutes(23)));

        Assert.True(renewed);
        Assert.False(stale);
        Assert.False(expired);
    }

    [Fact]
    public async Task Renewal_waiting_for_writer_rechecks_authoritative_time_after_wait()
    {
        var fixture = await CreateFixtureAsync();
        var profileId = await SeedSyncProfileAsync(fixture);
        _ = await SeedScheduleAsync(fixture, profileId, nextOccurrenceUtc: Now);
        var snapshot = Assert.Single(await fixture.Store.GetJobsAsync());
        var acquired = await fixture.Store.TryAcquireLeaseAsync(Request(
            snapshot,
            Now,
            leaseDuration: TimeSpan.FromMinutes(1)));
        var lease = acquired.Lease!;
        var heldWriter = await fixture.Database.AcquireWriterAsync();
        try
        {
            var renewal = fixture.Store.TryRenewLeaseAsync(new ScheduledSyncLeaseRenewal(
                lease,
                Now,
                Now.AddMinutes(10))).AsTask();
            Assert.False(renewal.IsCompleted);

            fixture.TimeProvider.Advance(TimeSpan.FromMinutes(2));
            await heldWriter.DisposeAsync();

            Assert.False(await renewal.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            await heldWriter.DisposeAsync();
        }
    }

    [Fact]
    public async Task Renewal_waiting_for_cross_process_writer_rechecks_time_inside_immediate_transaction()
    {
        var fixture = await CreateFixtureAsync();
        var profileId = await SeedSyncProfileAsync(fixture);
        _ = await SeedScheduleAsync(fixture, profileId, nextOccurrenceUtc: Now);
        var snapshot = Assert.Single(await fixture.Store.GetJobsAsync());
        var lease = (await fixture.Store.TryAcquireLeaseAsync(Request(
            snapshot,
            Now,
            leaseDuration: TimeSpan.FromMinutes(1)))).Lease!;
        await using var blocker = await OpenIndependentConnectionAsync(fixture.Options);
        await using var blockingTransaction = blocker.BeginTransaction(deferred: false);

        var renewal = Task.Run(async () => await fixture.Store.TryRenewLeaseAsync(
            new ScheduledSyncLeaseRenewal(
                lease,
                Now,
                Now.AddMinutes(10))));
        await Task.Delay(100);
        Assert.False(renewal.IsCompleted);

        fixture.TimeProvider.Advance(TimeSpan.FromMinutes(2));
        await blockingTransaction.CommitAsync();

        Assert.False(await renewal.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Completion_waiting_for_cross_process_writer_rejects_expired_lease()
    {
        var fixture = await CreateFixtureAsync();
        var profileId = await SeedSyncProfileAsync(fixture);
        _ = await SeedScheduleAsync(fixture, profileId, nextOccurrenceUtc: Now);
        var snapshot = Assert.Single(await fixture.Store.GetJobsAsync());
        var lease = (await fixture.Store.TryAcquireLeaseAsync(Request(
            snapshot,
            Now,
            leaseDuration: TimeSpan.FromMinutes(1)))).Lease!;
        await using var blocker = await OpenIndependentConnectionAsync(fixture.Options);
        await using var blockingTransaction = blocker.BeginTransaction(deferred: false);

        var completion = Task.Run(async () => await fixture.Store.RecordCompletionAsync(
            new ScheduledSyncJobCompletion(
                lease,
                ScheduledSyncJobRunResult.Completed(),
                Now,
                Now.AddSeconds(30))));
        await Task.Delay(100);
        Assert.False(completion.IsCompleted);

        fixture.TimeProvider.Advance(TimeSpan.FromMinutes(2));
        await blockingTransaction.CommitAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => completion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Queue_one_disposition_is_atomic_and_queued_claim_clears_it()
    {
        var fixture = await CreateFixtureAsync();
        var profileId = await SeedSyncProfileAsync(fixture);
        _ = await SeedScheduleAsync(fixture, profileId, nextOccurrenceUtc: Now);
        _ = await SeedScheduleAsync(fixture, profileId, nextOccurrenceUtc: Now);
        var snapshots = await fixture.Store.GetJobsAsync();
        var active = await fixture.Store.TryAcquireLeaseAsync(Request(snapshots[0], Now));
        var queuedJob = snapshots[1];
        var disposition = new ScheduledOccurrenceDisposition(
            queuedJob.JobId,
            queuedJob.ProfileId,
            queuedJob.Revision,
            Now,
            Now,
            Now.AddMinutes(1),
            ScheduledOccurrenceDispositionKind.OverlapQueued);

        Assert.True(await fixture.Store.TryRecordOccurrenceDispositionAsync(disposition));
        Assert.False(await fixture.Store.TryRecordOccurrenceDispositionAsync(disposition));
        var queuedSnapshot = (await fixture.Store.GetJobsAsync()).Single(job =>
            job.JobId == queuedJob.JobId);
        Assert.Equal(Now, queuedSnapshot.QueuedOccurrenceUtc);

        await fixture.Store.RecordCompletionAsync(new ScheduledSyncJobCompletion(
            active.Lease!,
            ScheduledSyncJobRunResult.Completed(),
            Now,
            Now.AddSeconds(1)));
        var queuedAcquisition = await fixture.Store.TryAcquireLeaseAsync(new ScheduledSyncLeaseRequest(
            queuedSnapshot.JobId,
            queuedSnapshot.ProfileId,
            queuedSnapshot.Revision,
            queuedSnapshot.QueuedOccurrenceUtc!.Value,
            isQueuedOccurrence: true,
            Now.AddSeconds(2),
            Now.AddMinutes(1),
            TimeSpan.FromMinutes(30)));

        Assert.Equal(ScheduledSyncLeaseAcquisitionStatus.Acquired, queuedAcquisition.Status);
        Assert.Null((await fixture.Store.GetJobsAsync()).Single(job =>
            job.JobId == queuedJob.JobId).QueuedOccurrenceUtc);
    }

    [Fact]
    public async Task Completion_is_fenced_and_records_only_safe_result_fields()
    {
        var fixture = await CreateFixtureAsync();
        var profileId = await SeedSyncProfileAsync(fixture);
        var jobId = await SeedScheduleAsync(fixture, profileId, nextOccurrenceUtc: Now);
        var snapshot = Assert.Single(await fixture.Store.GetJobsAsync());
        var acquired = await fixture.Store.TryAcquireLeaseAsync(Request(snapshot, Now));
        var lease = acquired.Lease!;
        var wrongLease = new ScheduledSyncJobLease(
            Guid.NewGuid(),
            lease.JobId,
            lease.ProfileId,
            lease.ScheduledForUtc,
            lease.AcquiredAtUtc,
            lease.ExpiresAtUtc,
            lease.FencingToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.RecordCompletionAsync(new ScheduledSyncJobCompletion(
                wrongLease,
                ScheduledSyncJobRunResult.Completed(),
                Now,
                Now.AddSeconds(1))).AsTask());

        var completion = new ScheduledSyncJobCompletion(
            lease,
            ScheduledSyncJobRunResult.Failed("sync.provider.failed", "The provider failed."),
            Now,
            Now.AddSeconds(2));
        await fixture.Store.RecordCompletionAsync(completion);
        await fixture.Store.RecordCompletionAsync(completion);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.RecordCompletionAsync(new ScheduledSyncJobCompletion(
                lease,
                ScheduledSyncJobRunResult.Completed(),
                Now,
                Now.AddSeconds(2))).AsTask());

        await using var connection = await fixture.Database.OpenReadConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT last_run_outcome, last_error_code, last_error_message, active_lease_id
            FROM sync_schedules
            WHERE sync_schedule_id = $jobId;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("failed", reader.GetString(0));
        Assert.Equal("sync.provider.failed", reader.GetString(1));
        Assert.Equal("The provider failed.", reader.GetString(2));
        Assert.True(reader.IsDBNull(3));
        await reader.DisposeAsync();
        Assert.Equal(1L, await ScalarInt64Async(
            connection,
            "SELECT COUNT(*) FROM sync_schedule_completions WHERE lease_id = $leaseId;",
            ("$leaseId", lease.LeaseId.ToString("D", CultureInfo.InvariantCulture))));
    }

    [Fact]
    public async Task Completion_after_unrenewed_lease_expiry_is_rejected()
    {
        var fixture = await CreateFixtureAsync();
        var profileId = await SeedSyncProfileAsync(fixture);
        _ = await SeedScheduleAsync(fixture, profileId, nextOccurrenceUtc: Now);
        var snapshot = Assert.Single(await fixture.Store.GetJobsAsync());
        var acquired = await fixture.Store.TryAcquireLeaseAsync(Request(
            snapshot,
            Now,
            leaseDuration: TimeSpan.FromMinutes(1)));
        fixture.TimeProvider.Advance(TimeSpan.FromMinutes(2));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.RecordCompletionAsync(new ScheduledSyncJobCompletion(
                acquired.Lease!,
                ScheduledSyncJobRunResult.Completed(),
                Now,
                Now.AddMinutes(2))).AsTask());
    }

    [Fact]
    public async Task Acquisition_uses_store_time_instead_of_stale_caller_observation()
    {
        var fixture = await CreateFixtureAsync();
        var profileId = await SeedSyncProfileAsync(fixture);
        _ = await SeedScheduleAsync(fixture, profileId, nextOccurrenceUtc: Now);
        var snapshot = Assert.Single(await fixture.Store.GetJobsAsync());
        fixture.TimeProvider.Advance(TimeSpan.FromMinutes(10));

        var acquisition = await fixture.Store.TryAcquireLeaseAsync(Request(
            snapshot,
            Now,
            leaseDuration: TimeSpan.FromMinutes(1)));

        Assert.Equal(ScheduledSyncLeaseAcquisitionStatus.Acquired, acquisition.Status);
        Assert.Equal(Now.AddMinutes(10), acquisition.Lease!.AcquiredAtUtc);
        Assert.Equal(Now.AddMinutes(11), acquisition.Lease.ExpiresAtUtc);
    }

    [Fact]
    public async Task Real_store_executes_one_due_job_through_scheduler_subsystem()
    {
        var fixture = await CreateFixtureAsync();
        var profileId = await SeedSyncProfileAsync(fixture);
        _ = await SeedScheduleAsync(fixture, profileId, nextOccurrenceUtc: Now);
        var runner = new SuccessfulRunner();
        await using var scheduler = new SchedulerAgentSubsystem(
            fixture.Store,
            runner,
            new SchedulerAgentOptions
            {
                MaximumConcurrency = 1,
                LeaseDuration = TimeSpan.FromMinutes(30),
                LeaseRenewalInterval = TimeSpan.FromMinutes(5),
            },
            new FixedTimeProvider(Now));
        await scheduler.InitializeAsync(CancellationToken.None);

        await scheduler.RunDueOnceAsync();

        Assert.Single(runner.Executions);
        await using var connection = await fixture.Database.OpenReadConnectionAsync();
        Assert.Equal("completed", await ScalarTextAsync(
            connection,
            "SELECT last_run_outcome FROM sync_schedules LIMIT 1;"));
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
        var timeProvider = new MutableTimeProvider(Now);
        return new Fixture(
            options,
            database,
            new SqliteScheduledSyncJobStore(database, timeProvider),
            timeProvider);
    }

    private static async Task<SyncProfileId> SeedSyncProfileAsync(Fixture fixture)
    {
        var left = ConnectionProfileId.New();
        var right = ConnectionProfileId.New();
        var profile = SyncProfileId.New();
        await using var writer = await fixture.Database.AcquireWriterAsync();
        await using var command = writer.Connection.CreateCommand();
        command.CommandText = """
            INSERT INTO connection_profiles
            (
                profile_id, provider, display_name, tags_json, metadata_json, endpoint_json,
                authentication_json, operational_options_json, is_favorite, is_enabled,
                version, created_utc, updated_utc
            )
            VALUES
                ($left, 'local', $leftName, '[]', '{}', '{}', '{}', '{}', 0, 1, 1, $now, $now),
                ($right, 'local', $rightName, '[]', '{}', '{}', '{}', '{}', 0, 1, 1, $now, $now);

            INSERT INTO sync_profiles
            (
                sync_profile_id, display_name, left_profile_id, right_profile_id,
                left_root, right_root, direction, policy_hash, enabled, created_utc, updated_utc
            )
            VALUES
                ($profile, $profileName, $left, $right, '', '', 'two-way', $policyHash, 1, $now, $now);
            """;
        command.Parameters.AddWithValue("$left", left.ToString());
        command.Parameters.AddWithValue("$right", right.ToString());
        command.Parameters.AddWithValue("$leftName", $"Left-{left}");
        command.Parameters.AddWithValue("$rightName", $"Right-{right}");
        command.Parameters.AddWithValue("$profile", profile.ToString());
        command.Parameters.AddWithValue("$profileName", $"Sync-{profile}");
        command.Parameters.AddWithValue("$policyHash", new string('a', 64));
        command.Parameters.AddWithValue("$now", Format(Now));
        _ = await command.ExecuteNonQueryAsync();
        return profile;
    }

    private static async Task<ScheduledSyncJobId> SeedScheduleAsync(
        Fixture fixture,
        SyncProfileId profileId,
        bool enabled = true,
        bool queueOneWhileRunning = true,
        DateTimeOffset? nextOccurrenceUtc = null,
        TimeSpan? misfireGrace = null)
    {
        var jobId = ScheduledSyncJobId.New();
        await using var writer = await fixture.Database.AcquireWriterAsync();
        await using var command = writer.Connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_schedules
            (
                sync_schedule_id, sync_profile_id, cron_expression, time_zone_id,
                enabled, next_due_utc, misfire_policy, misfire_grace_seconds,
                queue_one_while_running, revision
            )
            VALUES
            (
                $jobId, $profileId, '* * * * *', 'UTC', $enabled, $nextDue,
                'coalesce-one', $misfireGrace, $queueOne, 0
            );
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        command.Parameters.AddWithValue("$profileId", profileId.ToString());
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue(
            "$nextDue",
            nextOccurrenceUtc is { } next ? Format(next) : DBNull.Value);
        command.Parameters.AddWithValue(
            "$misfireGrace",
            (long)(misfireGrace ?? TimeSpan.FromHours(1)).TotalSeconds);
        command.Parameters.AddWithValue("$queueOne", queueOneWhileRunning ? 1 : 0);
        _ = await command.ExecuteNonQueryAsync();
        return jobId;
    }

    private static ScheduledSyncLeaseRequest Request(
        ScheduledSyncJobSnapshot snapshot,
        DateTimeOffset observedAtUtc,
        DateTimeOffset? nextOccurrenceUtc = null,
        TimeSpan? leaseDuration = null) =>
        new(
            snapshot.JobId,
            snapshot.ProfileId,
            snapshot.Revision,
            snapshot.DueOccurrenceUtc!.Value,
            snapshot.QueuedOccurrenceUtc.HasValue,
            observedAtUtc,
            nextOccurrenceUtc ?? observedAtUtc.AddMinutes(1),
            leaseDuration ?? TimeSpan.FromMinutes(30));

    private static string Format(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static async Task<string> ScalarTextAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(
            await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture)!;
    }

    private static async Task<long> ScalarInt64Async(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture);
    }

    private static async Task<SqliteConnection> OpenIndependentConnectionAsync(
        SqliteDatabaseOptions options)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = Math.Max(1, options.BusyTimeoutMilliseconds / 1000)
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout = {options.BusyTimeoutMilliseconds};";
        _ = await command.ExecuteNonQueryAsync();
        return connection;
    }

    private sealed record Fixture(
        SqliteDatabaseOptions Options,
        SingleWriterSqliteDatabase Database,
        SqliteScheduledSyncJobStore Store,
        MutableTimeProvider TimeProvider);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private long _utcTicks = utcNow.UtcTicks;

        public override DateTimeOffset GetUtcNow() =>
            new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

        public void Advance(TimeSpan duration) =>
            _ = Interlocked.Add(ref _utcTicks, duration.Ticks);
    }

    private sealed class SuccessfulRunner : IScheduledSyncJobRunner
    {
        public List<ScheduledSyncJobExecution> Executions { get; } = [];

        public ValueTask<ScheduledSyncJobRunResult> RunAsync(
            ScheduledSyncJobExecution execution,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Executions.Add(execution);
            return ValueTask.FromResult(ScheduledSyncJobRunResult.Completed());
        }
    }
}
