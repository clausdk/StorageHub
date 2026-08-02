using System.Globalization;
using StorageHub.Agent.Scheduling;
using StorageHub.Domain.Identifiers;
using StorageHub.Persistence.Scheduling;
using Xunit;

namespace StorageHub.Persistence.Tests.Scheduling;

public sealed class SqliteSyncScheduleManagementRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"storagehub-schedule-management-{Guid.NewGuid():N}");

    [Fact]
    public async Task Create_list_get_and_idempotent_retry_calculate_the_next_occurrence()
    {
        var fixture = await CreateFixtureAsync();
        var profileId = await SeedSyncProfileAsync(fixture, enabled: true);
        var scheduleId = ScheduledSyncJobId.New();
        var draft = Draft(profileId, "*/15 * * * *", enabled: true);

        var created = await fixture.Repository.CreateAsync(scheduleId, draft);
        var retry = await fixture.Repository.CreateAsync(scheduleId, draft);
        var listed = await fixture.Repository.ListAsync(includeDisabled: true, maximumCount: 10);
        var fetched = await fixture.Repository.GetAsync(scheduleId);

        Assert.Equal(SyncScheduleManagementMutationStatus.Applied, created.Status);
        Assert.Equal(1, created.Schedule?.Revision);
        Assert.Equal(Now.AddMinutes(15), created.Schedule?.NextOccurrenceUtc);
        Assert.Equal(SyncScheduleManagementMutationStatus.AlreadyApplied, retry.Status);
        Assert.Equal(scheduleId, Assert.Single(listed).ScheduleId);
        Assert.Equal("Sync profile", fetched?.ProfileDisplayName);
        Assert.False(fetched?.IsBusy);
    }

    [Fact]
    public async Task Update_and_enable_disable_use_revision_cas_and_recalculate_due_state()
    {
        var fixture = await CreateFixtureAsync();
        var profileId = await SeedSyncProfileAsync(fixture, enabled: true);
        var scheduleId = ScheduledSyncJobId.New();
        var created = await fixture.Repository.CreateAsync(
            scheduleId,
            Draft(profileId, "*/15 * * * *", enabled: true));

        var updated = await fixture.Repository.UpdateAsync(
            scheduleId,
            expectedRevision: created.Schedule!.Revision,
            Draft(profileId, "*/20 * * * *", enabled: true) with
            {
                QueueOneWhileRunning = false,
                MisfireGrace = TimeSpan.FromHours(2)
            });
        var stale = await fixture.Repository.UpdateAsync(
            scheduleId,
            expectedRevision: created.Schedule.Revision,
            Draft(profileId, "0 * * * *", enabled: true));
        var disabled = await fixture.Repository.SetEnabledAsync(
            scheduleId,
            updated.Schedule!.Revision,
            enabled: false);
        var enabled = await fixture.Repository.SetEnabledAsync(
            scheduleId,
            disabled.Schedule!.Revision,
            enabled: true);

        Assert.Equal(SyncScheduleManagementMutationStatus.Applied, updated.Status);
        Assert.Equal(Now.AddMinutes(20), updated.Schedule?.NextOccurrenceUtc);
        Assert.False(updated.Schedule?.QueueOneWhileRunning);
        Assert.Equal(SyncScheduleManagementMutationStatus.RevisionConflict, stale.Status);
        Assert.Equal(updated.Schedule?.Revision, stale.ActualRevision);
        Assert.Null(disabled.Schedule?.NextOccurrenceUtc);
        Assert.Equal(Now.AddMinutes(20), enabled.Schedule?.NextOccurrenceUtc);
    }

    [Fact]
    public async Task Live_run_ownership_blocks_update_disable_and_delete_without_exposing_ownership_ids()
    {
        var fixture = await CreateFixtureAsync();
        var profileId = await SeedSyncProfileAsync(fixture, enabled: true);
        var scheduleId = ScheduledSyncJobId.New();
        _ = await fixture.Repository.CreateAsync(scheduleId, Draft(profileId, "* * * * *", enabled: true));
        var schedulerStore = new SqliteScheduledSyncJobStore(fixture.Database, fixture.TimeProvider);
        var job = Assert.Single(await schedulerStore.GetJobsAsync());
        var acquired = await schedulerStore.TryAcquireLeaseAsync(new ScheduledSyncLeaseRequest(
            job.JobId,
            job.ProfileId,
            job.Revision,
            job.NextOccurrenceUtc!.Value,
            isQueuedOccurrence: false,
            Now,
            Now.AddMinutes(2),
            TimeSpan.FromMinutes(10)));
        Assert.Equal(ScheduledSyncLeaseAcquisitionStatus.Acquired, acquired.Status);
        var current = await fixture.Repository.GetAsync(scheduleId);

        var update = await fixture.Repository.UpdateAsync(
            scheduleId,
            current!.Revision,
            Draft(profileId, "*/5 * * * *", enabled: true));
        var disable = await fixture.Repository.SetEnabledAsync(
            scheduleId,
            current.Revision,
            enabled: false);
        var delete = await fixture.Repository.DeleteAsync(scheduleId, current.Revision);

        Assert.True(current.IsBusy);
        Assert.Equal(SyncScheduleManagementMutationStatus.ActiveRun, update.Status);
        Assert.Equal(SyncScheduleManagementMutationStatus.ActiveRun, disable.Status);
        Assert.Equal(SyncScheduleManagementMutationStatus.ActiveRun, delete.Status);
        Assert.DoesNotContain(
            typeof(SyncScheduleManagementRecord).GetProperties(),
            property => property.Name.Contains("Lease", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Contains("Fence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Expired_ownership_is_reaped_and_forces_refresh_before_delete()
    {
        var fixture = await CreateFixtureAsync();
        var profileId = await SeedSyncProfileAsync(fixture, enabled: true);
        var scheduleId = ScheduledSyncJobId.New();
        _ = await fixture.Repository.CreateAsync(scheduleId, Draft(profileId, "* * * * *", enabled: true));
        var schedulerStore = new SqliteScheduledSyncJobStore(fixture.Database, fixture.TimeProvider);
        var job = Assert.Single(await schedulerStore.GetJobsAsync());
        _ = await schedulerStore.TryAcquireLeaseAsync(new ScheduledSyncLeaseRequest(
            job.JobId,
            job.ProfileId,
            job.Revision,
            job.NextOccurrenceUtc!.Value,
            isQueuedOccurrence: false,
            Now,
            Now.AddMinutes(2),
            TimeSpan.FromMinutes(1)));
        var leased = await fixture.Repository.GetAsync(scheduleId);
        fixture.TimeProvider.Advance(TimeSpan.FromMinutes(2));

        var staleDelete = await fixture.Repository.DeleteAsync(scheduleId, leased!.Revision);
        var refreshed = await fixture.Repository.GetAsync(scheduleId);
        var deleted = await fixture.Repository.DeleteAsync(scheduleId, refreshed!.Revision);

        Assert.Equal(SyncScheduleManagementMutationStatus.RevisionConflict, staleDelete.Status);
        Assert.False(refreshed.IsBusy);
        Assert.Equal("lease-expired", refreshed.LastRunOutcome);
        Assert.Equal(SyncScheduleManagementMutationStatus.Applied, deleted.Status);
        Assert.Null(await fixture.Repository.GetAsync(scheduleId));
    }

    [Fact]
    public async Task Invalid_cron_time_zone_and_disabled_profile_enablement_are_rejected()
    {
        var fixture = await CreateFixtureAsync();
        var disabledProfile = await SeedSyncProfileAsync(fixture, enabled: false);

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Repository.CreateAsync(
            ScheduledSyncJobId.New(),
            Draft(disabledProfile, "not cron", enabled: false)).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Repository.CreateAsync(
            ScheduledSyncJobId.New(),
            Draft(disabledProfile, "* * * * *", enabled: false) with
            {
                TimeZoneId = "missing/time-zone"
            }).AsTask());
        var rejected = await fixture.Repository.CreateAsync(
            ScheduledSyncJobId.New(),
            Draft(disabledProfile, "* * * * *", enabled: true));

        Assert.Equal(SyncScheduleManagementMutationStatus.ConstraintConflict, rejected.Status);
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
        var initialized = await new StorageHubDatabaseInitializer(options).InitializeAsync();
        Assert.True(initialized.IsReady, initialized.Message);
        var database = new SingleWriterSqliteDatabase(options);
        var timeProvider = new MutableTimeProvider(Now);
        return new Fixture(
            database,
            timeProvider,
            new SqliteSyncScheduleManagementRepository(database, timeProvider));
    }

    private static SyncScheduleManagementDraft Draft(
        SyncProfileId profileId,
        string cron,
        bool enabled) => new(
        profileId,
        cron,
        "UTC",
        TimeSpan.FromHours(1),
        QueueOneWhileRunning: true,
        enabled);

    private static async Task<SyncProfileId> SeedSyncProfileAsync(Fixture fixture, bool enabled)
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
                ($left, 'local', 'Left', '[]', '{}', '{}', '{}', '{}', 0, 1, 1, $now, $now),
                ($right, 'local', 'Right', '[]', '{}', '{}', '{}', '{}', 0, 1, 1, $now, $now);

            INSERT INTO sync_profiles
            (
                sync_profile_id, display_name, left_profile_id, right_profile_id,
                left_root, right_root, direction, policy_hash, enabled, created_utc, updated_utc
            )
            VALUES
                ($profile, 'Sync profile', $left, $right, '', '', 'two-way', $hash, $enabled, $now, $now);
            """;
        command.Parameters.AddWithValue("$left", left.ToString());
        command.Parameters.AddWithValue("$right", right.ToString());
        command.Parameters.AddWithValue("$profile", profile.ToString());
        command.Parameters.AddWithValue("$hash", new string('c', 64));
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$now", Now.ToString("O", CultureInfo.InvariantCulture));
        _ = await command.ExecuteNonQueryAsync();
        return profile;
    }

    private sealed record Fixture(
        SingleWriterSqliteDatabase Database,
        MutableTimeProvider TimeProvider,
        SqliteSyncScheduleManagementRepository Repository);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private long _utcTicks = now.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(
            Interlocked.Read(ref _utcTicks),
            TimeSpan.Zero);

        public void Advance(TimeSpan duration) =>
            _ = Interlocked.Add(ref _utcTicks, duration.Ticks);
    }
}
