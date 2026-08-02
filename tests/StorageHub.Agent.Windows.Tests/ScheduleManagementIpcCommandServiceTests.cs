using System.Globalization;
using System.Text.Json;
using StorageHub.Agent.Scheduling;
using StorageHub.Contracts.Ipc;
using StorageHub.Domain.Identifiers;
using StorageHub.Persistence;
using StorageHub.Persistence.Scheduling;

namespace StorageHub.Agent.Windows.Tests;

public sealed class ScheduleManagementIpcCommandServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"storagehub-schedule-ipc-{Guid.NewGuid():N}");

    [Fact]
    public async Task Lifecycle_uses_bounded_documents_and_revision_cas()
    {
        var fixture = await CreateFixtureAsync();
        var profileId = await SeedSyncProfileAsync(fixture);
        var scheduleId = Guid.NewGuid();
        var created = await SendAsync<ScheduleCreateRequest, ScheduleMutationResponse>(
            fixture.Service,
            ScheduleManagementIpcMessageTypes.CreateRequest,
            new ScheduleCreateRequest(
                ScheduleManagementIpcContract.CurrentVersion,
                scheduleId,
                Draft(profileId, "*/15 * * * *", enabled: false)));
        var listed = await SendAsync<ScheduleListRequest, ScheduleListResponse>(
            fixture.Service,
            ScheduleManagementIpcMessageTypes.ListRequest,
            new ScheduleListRequest());
        var fetched = await SendAsync<ScheduleGetRequest, ScheduleGetResponse>(
            fixture.Service,
            ScheduleManagementIpcMessageTypes.GetRequest,
            new ScheduleGetRequest(ScheduleManagementIpcContract.CurrentVersion, scheduleId));
        var updated = await SendAsync<ScheduleUpdateRequest, ScheduleMutationResponse>(
            fixture.Service,
            ScheduleManagementIpcMessageTypes.UpdateRequest,
            new ScheduleUpdateRequest(
                ScheduleManagementIpcContract.CurrentVersion,
                scheduleId,
                ExpectedRevision: 1,
                Draft(profileId, "*/20 * * * *", enabled: true)));
        var stale = await SendAsync<ScheduleSetEnabledRequest, ScheduleMutationResponse>(
            fixture.Service,
            ScheduleManagementIpcMessageTypes.SetEnabledRequest,
            new ScheduleSetEnabledRequest(
                ScheduleManagementIpcContract.CurrentVersion,
                scheduleId,
                ExpectedRevision: 1,
                Enabled: false));
        var disabled = await SendAsync<ScheduleSetEnabledRequest, ScheduleMutationResponse>(
            fixture.Service,
            ScheduleManagementIpcMessageTypes.SetEnabledRequest,
            new ScheduleSetEnabledRequest(
                ScheduleManagementIpcContract.CurrentVersion,
                scheduleId,
                ExpectedRevision: 2,
                Enabled: false));
        var deleted = await SendAsync<ScheduleDeleteRequest, ScheduleMutationResponse>(
            fixture.Service,
            ScheduleManagementIpcMessageTypes.DeleteRequest,
            new ScheduleDeleteRequest(
                ScheduleManagementIpcContract.CurrentVersion,
                scheduleId,
                ExpectedRevision: 3));

        Assert.Equal(ScheduleMutationOutcome.Succeeded, created.Outcome);
        Assert.Equal(ScheduleIpcExecutionMode.PreviewOnly, created.Schedule?.ExecutionMode);
        Assert.Null(created.Schedule?.NextOccurrenceUtc);
        Assert.Equal(scheduleId, Assert.Single(listed.Schedules).ScheduleId);
        Assert.Equal("Sync profile", fetched.Schedule?.ProfileDisplayName);
        Assert.Equal(Now.AddMinutes(20), updated.Schedule?.NextOccurrenceUtc);
        Assert.Equal(ScheduleMutationOutcome.RevisionConflict, stale.Outcome);
        Assert.Equal(2, stale.ActualRevision);
        Assert.Null(disabled.Schedule?.NextOccurrenceUtc);
        Assert.Equal(ScheduleMutationOutcome.Succeeded, deleted.Outcome);
        Assert.Null(deleted.Schedule);
    }

    [Fact]
    public async Task Invalid_cron_is_rejected_with_a_safe_validation_failure()
    {
        var fixture = await CreateFixtureAsync();
        var profileId = await SeedSyncProfileAsync(fixture);

        var response = await SendAsync<ScheduleCreateRequest, ScheduleMutationResponse>(
            fixture.Service,
            ScheduleManagementIpcMessageTypes.CreateRequest,
            new ScheduleCreateRequest(
                ScheduleManagementIpcContract.CurrentVersion,
                Guid.NewGuid(),
                Draft(profileId, "not cron", enabled: true)));

        Assert.Equal(ScheduleMutationOutcome.ConstraintConflict, response.Outcome);
        Assert.Equal(StorageIpcFailureCategory.Validation, response.Failure?.Category);
        Assert.DoesNotContain("Cronos", response.Failure?.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Active_run_is_coarsely_reported_and_blocks_mutation_without_ownership_evidence()
    {
        var fixture = await CreateFixtureAsync();
        var profileId = await SeedSyncProfileAsync(fixture);
        var scheduleId = Guid.NewGuid();
        _ = await SendAsync<ScheduleCreateRequest, ScheduleMutationResponse>(
            fixture.Service,
            ScheduleManagementIpcMessageTypes.CreateRequest,
            new ScheduleCreateRequest(
                ScheduleManagementIpcContract.CurrentVersion,
                scheduleId,
                Draft(profileId, "* * * * *", enabled: true)));
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
            TimeSpan.FromMinutes(10)));
        var getCommand = await fixture.Service.HandleAsync(CreateEnvelope(
            ScheduleManagementIpcMessageTypes.GetRequest,
            new ScheduleGetRequest(
                ScheduleManagementIpcContract.CurrentVersion,
                scheduleId)));
        var current = getCommand.Payload.Deserialize<ScheduleGetResponse>()!.Schedule!;
        var update = await SendAsync<ScheduleUpdateRequest, ScheduleMutationResponse>(
            fixture.Service,
            ScheduleManagementIpcMessageTypes.UpdateRequest,
            new ScheduleUpdateRequest(
                ScheduleManagementIpcContract.CurrentVersion,
                scheduleId,
                current.Revision,
                Draft(profileId, "*/5 * * * *", enabled: true)));
        var json = getCommand.Payload.GetRawText();

        Assert.True(current.IsBusy);
        Assert.Equal(ScheduleMutationOutcome.ActiveRun, update.Outcome);
        Assert.Equal("schedule.active_run", update.Failure?.Code);
        Assert.DoesNotContain("lease", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fenc", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("active_", json, StringComparison.OrdinalIgnoreCase);
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
        var timeProvider = new FixedTimeProvider(Now);
        var repository = new SqliteSyncScheduleManagementRepository(database, timeProvider);
        return new Fixture(
            database,
            timeProvider,
            new ScheduleManagementIpcCommandService(repository));
    }

    private static ScheduleDraftDocument Draft(Guid profileId, string cron, bool enabled) => new(
        profileId,
        cron,
        "UTC",
        MisfireGraceSeconds: 3_600,
        QueueOneWhileRunning: true,
        enabled,
        ScheduleIpcExecutionMode.PreviewOnly);

    private static async Task<Guid> SeedSyncProfileAsync(Fixture fixture)
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
                ($profile, 'Sync profile', $left, $right, '', '', 'two-way', $hash, 1, $now, $now);
            """;
        command.Parameters.AddWithValue("$left", left.ToString());
        command.Parameters.AddWithValue("$right", right.ToString());
        command.Parameters.AddWithValue("$profile", profile.ToString());
        command.Parameters.AddWithValue("$hash", new string('d', 64));
        command.Parameters.AddWithValue("$now", Now.ToString("O", CultureInfo.InvariantCulture));
        _ = await command.ExecuteNonQueryAsync();
        return profile.Value;
    }

    private static async Task<TResponse> SendAsync<TRequest, TResponse>(
        ScheduleManagementIpcCommandService service,
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
        FixedTimeProvider TimeProvider,
        ScheduleManagementIpcCommandService Service);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
