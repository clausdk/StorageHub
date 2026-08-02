using System.Globalization;
using Cronos;
using Microsoft.Data.Sqlite;
using StorageHub.Agent.Scheduling;
using StorageHub.Domain.Identifiers;

namespace StorageHub.Persistence.Scheduling;

/// <summary>
/// Optimistic schedule management over the durable scheduler row. Mutations reap expired
/// ownership first and reject any still-active run without returning ownership identifiers.
/// </summary>
public sealed class SqliteSyncScheduleManagementRepository : ISyncScheduleManagementRepository
{
    public const int MaximumResultCount = 100;
    public const int MaximumCronExpressionLength = 128;
    public const int MaximumTimeZoneIdLength = 256;
    public static readonly TimeSpan MinimumMisfireGrace = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaximumMisfireGrace = TimeSpan.FromDays(30);

    private readonly SingleWriterSqliteDatabase _database;
    private readonly TimeProvider _timeProvider;

    public SqliteSyncScheduleManagementRepository(
        SingleWriterSqliteDatabase database,
        TimeProvider? timeProvider = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<IReadOnlyList<SyncScheduleManagementRecord>> ListAsync(
        bool includeDisabled,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > MaximumResultCount)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        var observedAtUtc = _timeProvider.GetUtcNow();
        await using var connection = await _database.OpenReadConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Projection}
            FROM sync_schedules AS schedule
            JOIN sync_profiles AS profile
              ON profile.sync_profile_id = schedule.sync_profile_id
            WHERE $includeDisabled = 1 OR schedule.enabled = 1
            ORDER BY profile.display_name COLLATE NOCASE, schedule.sync_schedule_id
            LIMIT $maximumCount;
            """;
        command.Parameters.AddWithValue("$includeDisabled", includeDisabled ? 1 : 0);
        command.Parameters.AddWithValue("$maximumCount", maximumCount);
        command.Parameters.AddWithValue("$observedAt", FormatTimestamp(observedAtUtc));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var schedules = new List<SyncScheduleManagementRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            schedules.Add(Read(reader));
        }

        return schedules;
    }

    public async ValueTask<SyncScheduleManagementRecord?> GetAsync(
        ScheduledSyncJobId scheduleId,
        CancellationToken cancellationToken = default)
    {
        RequireScheduleId(scheduleId);
        var observedAtUtc = _timeProvider.GetUtcNow();
        await using var connection = await _database.OpenReadConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ReadAsync(
            connection,
            transaction: null,
            scheduleId,
            observedAtUtc,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SyncScheduleManagementMutationResult> CreateAsync(
        ScheduledSyncJobId scheduleId,
        SyncScheduleManagementDraft draft,
        CancellationToken cancellationToken = default)
    {
        RequireScheduleId(scheduleId);
        var normalized = ValidateAndNormalize(draft);
        await using var writer = await _database.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = writer.Connection.BeginTransaction(deferred: false);
        try
        {
            var observedAtUtc = _timeProvider.GetUtcNow();
            var existing = await ReadAsync(
                writer.Connection,
                transaction,
                scheduleId,
                observedAtUtc,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return ConfigurationEquals(existing, normalized)
                    ? new SyncScheduleManagementMutationResult(
                        SyncScheduleManagementMutationStatus.AlreadyApplied,
                        existing,
                        existing.Revision)
                    : new SyncScheduleManagementMutationResult(
                        SyncScheduleManagementMutationStatus.ConstraintConflict,
                        ActualRevision: existing.Revision);
            }

            if (!await ProfileCanBeScheduledAsync(
                    writer.Connection,
                    transaction,
                    normalized.ProfileId,
                    normalized.Enabled,
                    cancellationToken).ConfigureAwait(false))
            {
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return new SyncScheduleManagementMutationResult(
                    SyncScheduleManagementMutationStatus.ConstraintConflict);
            }

            var nextOccurrence = CalculateNextOccurrence(normalized, observedAtUtc);
            await using var command = writer.Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO sync_schedules
                (
                    sync_schedule_id, sync_profile_id, cron_expression, time_zone_id,
                    time_zone_rule_version, enabled, next_due_utc, last_due_utc,
                    misfire_policy, misfire_grace_seconds, queue_one_while_running,
                    queued_due_utc, revision
                )
                VALUES
                (
                    $scheduleId, $profileId, $cron, $timeZone, NULL, $enabled, $nextDue, NULL,
                    'coalesce-one', $misfireGrace, $queueOne, NULL, 1
                );
                """;
            AddDefinitionParameters(command, scheduleId, normalized, nextOccurrence);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            var created = await ReadAsync(
                writer.Connection,
                transaction,
                scheduleId,
                observedAtUtc,
                cancellationToken).ConfigureAwait(false) ??
                throw new InvalidDataException("The created schedule could not be read back.");
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return new SyncScheduleManagementMutationResult(
                SyncScheduleManagementMutationStatus.Applied,
                created,
                created.Revision);
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            return new SyncScheduleManagementMutationResult(
                SyncScheduleManagementMutationStatus.ConstraintConflict);
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<SyncScheduleManagementMutationResult> UpdateAsync(
        ScheduledSyncJobId scheduleId,
        long expectedRevision,
        SyncScheduleManagementDraft draft,
        CancellationToken cancellationToken = default)
    {
        RequireScheduleId(scheduleId);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        var normalized = ValidateAndNormalize(draft);
        await using var writer = await _database.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = writer.Connection.BeginTransaction(deferred: false);
        try
        {
            var observedAtUtc = _timeProvider.GetUtcNow();
            await ReapExpiredTargetAsync(
                writer.Connection,
                transaction,
                scheduleId,
                observedAtUtc,
                cancellationToken).ConfigureAwait(false);
            var current = await ReadAsync(
                writer.Connection,
                transaction,
                scheduleId,
                observedAtUtc,
                cancellationToken).ConfigureAwait(false);
            var precondition = EvaluateMutationPrecondition(current, expectedRevision);
            if (precondition is not null)
            {
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return precondition;
            }

            if (ConfigurationEquals(current!, normalized))
            {
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return new SyncScheduleManagementMutationResult(
                    SyncScheduleManagementMutationStatus.AlreadyApplied,
                    current,
                    current!.Revision);
            }

            if (current!.IsBusy)
            {
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return new SyncScheduleManagementMutationResult(
                    SyncScheduleManagementMutationStatus.ActiveRun,
                    ActualRevision: current.Revision);
            }

            if (!await ProfileCanBeScheduledAsync(
                    writer.Connection,
                    transaction,
                    normalized.ProfileId,
                    normalized.Enabled,
                    cancellationToken).ConfigureAwait(false))
            {
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return new SyncScheduleManagementMutationResult(
                    SyncScheduleManagementMutationStatus.ConstraintConflict,
                    ActualRevision: current.Revision);
            }

            var nextOccurrence = CalculateNextOccurrence(normalized, observedAtUtc);
            await using var command = writer.Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE sync_schedules
                SET sync_profile_id = $profileId,
                    cron_expression = $cron,
                    time_zone_id = $timeZone,
                    time_zone_rule_version = NULL,
                    enabled = $enabled,
                    next_due_utc = $nextDue,
                    queued_due_utc = NULL,
                    misfire_policy = 'coalesce-one',
                    misfire_grace_seconds = $misfireGrace,
                    queue_one_while_running = $queueOne,
                    revision = revision + 1
                WHERE sync_schedule_id = $scheduleId
                  AND revision = $expectedRevision
                  AND active_lease_id IS NULL;
                """;
            AddDefinitionParameters(command, scheduleId, normalized, nextOccurrence);
            command.Parameters.AddWithValue("$expectedRevision", expectedRevision);
            var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (changed != 1)
            {
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return new SyncScheduleManagementMutationResult(
                    SyncScheduleManagementMutationStatus.RevisionConflict,
                    ActualRevision: current.Revision);
            }

            var updated = await ReadAsync(
                writer.Connection,
                transaction,
                scheduleId,
                observedAtUtc,
                cancellationToken).ConfigureAwait(false) ??
                throw new InvalidDataException("The updated schedule could not be read back.");
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return new SyncScheduleManagementMutationResult(
                SyncScheduleManagementMutationStatus.Applied,
                updated,
                updated.Revision);
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            return new SyncScheduleManagementMutationResult(
                SyncScheduleManagementMutationStatus.ConstraintConflict);
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<SyncScheduleManagementMutationResult> SetEnabledAsync(
        ScheduledSyncJobId scheduleId,
        long expectedRevision,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        RequireScheduleId(scheduleId);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        await using var writer = await _database.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = writer.Connection.BeginTransaction(deferred: false);
        try
        {
            var observedAtUtc = _timeProvider.GetUtcNow();
            await ReapExpiredTargetAsync(
                writer.Connection,
                transaction,
                scheduleId,
                observedAtUtc,
                cancellationToken).ConfigureAwait(false);
            var current = await ReadAsync(
                writer.Connection,
                transaction,
                scheduleId,
                observedAtUtc,
                cancellationToken).ConfigureAwait(false);
            var precondition = EvaluateMutationPrecondition(current, expectedRevision);
            if (precondition is not null)
            {
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return precondition;
            }

            if (current!.Enabled == enabled)
            {
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return new SyncScheduleManagementMutationResult(
                    SyncScheduleManagementMutationStatus.AlreadyApplied,
                    current,
                    current.Revision);
            }

            if (current.IsBusy)
            {
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return new SyncScheduleManagementMutationResult(
                    SyncScheduleManagementMutationStatus.ActiveRun,
                    ActualRevision: current.Revision);
            }

            if (enabled && !await ProfileCanBeScheduledAsync(
                    writer.Connection,
                    transaction,
                    current.ProfileId,
                    requireEnabled: true,
                    cancellationToken).ConfigureAwait(false))
            {
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return new SyncScheduleManagementMutationResult(
                    SyncScheduleManagementMutationStatus.ConstraintConflict,
                    ActualRevision: current.Revision);
            }

            var nextOccurrence = enabled
                ? CalculateNextOccurrence(ToDraft(current, enabled: true), observedAtUtc)
                : null;
            await using var command = writer.Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE sync_schedules
                SET enabled = $enabled,
                    next_due_utc = $nextDue,
                    queued_due_utc = NULL,
                    revision = revision + 1
                WHERE sync_schedule_id = $scheduleId
                  AND revision = $expectedRevision
                  AND active_lease_id IS NULL;
                """;
            command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            command.Parameters.AddWithValue(
                "$nextDue",
                nextOccurrence is { } next ? FormatTimestamp(next) : DBNull.Value);
            command.Parameters.AddWithValue("$scheduleId", scheduleId.ToString());
            command.Parameters.AddWithValue("$expectedRevision", expectedRevision);
            var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (changed != 1)
            {
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return new SyncScheduleManagementMutationResult(
                    SyncScheduleManagementMutationStatus.RevisionConflict,
                    ActualRevision: current.Revision);
            }

            var updated = await ReadAsync(
                writer.Connection,
                transaction,
                scheduleId,
                observedAtUtc,
                cancellationToken).ConfigureAwait(false) ??
                throw new InvalidDataException("The updated schedule could not be read back.");
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return new SyncScheduleManagementMutationResult(
                SyncScheduleManagementMutationStatus.Applied,
                updated,
                updated.Revision);
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<SyncScheduleManagementMutationResult> DeleteAsync(
        ScheduledSyncJobId scheduleId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        RequireScheduleId(scheduleId);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        await using var writer = await _database.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = writer.Connection.BeginTransaction(deferred: false);
        try
        {
            var observedAtUtc = _timeProvider.GetUtcNow();
            await ReapExpiredTargetAsync(
                writer.Connection,
                transaction,
                scheduleId,
                observedAtUtc,
                cancellationToken).ConfigureAwait(false);
            var current = await ReadAsync(
                writer.Connection,
                transaction,
                scheduleId,
                observedAtUtc,
                cancellationToken).ConfigureAwait(false);
            var precondition = EvaluateMutationPrecondition(current, expectedRevision);
            if (precondition is not null)
            {
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return precondition;
            }

            if (current!.IsBusy)
            {
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return new SyncScheduleManagementMutationResult(
                    SyncScheduleManagementMutationStatus.ActiveRun,
                    ActualRevision: current.Revision);
            }

            await using var command = writer.Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM sync_schedules
                WHERE sync_schedule_id = $scheduleId
                  AND revision = $expectedRevision
                  AND active_lease_id IS NULL;
                """;
            command.Parameters.AddWithValue("$scheduleId", scheduleId.ToString());
            command.Parameters.AddWithValue("$expectedRevision", expectedRevision);
            var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return changed == 1
                ? new SyncScheduleManagementMutationResult(
                    SyncScheduleManagementMutationStatus.Applied,
                    ActualRevision: expectedRevision)
                : new SyncScheduleManagementMutationResult(
                    SyncScheduleManagementMutationStatus.RevisionConflict,
                    ActualRevision: current.Revision);
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private static SyncScheduleManagementMutationResult? EvaluateMutationPrecondition(
        SyncScheduleManagementRecord? current,
        long expectedRevision)
    {
        if (current is null)
        {
            return new SyncScheduleManagementMutationResult(
                SyncScheduleManagementMutationStatus.NotFound);
        }

        return current.Revision != expectedRevision
            ? new SyncScheduleManagementMutationResult(
                SyncScheduleManagementMutationStatus.RevisionConflict,
                ActualRevision: current.Revision)
            : null;
    }

    private static SyncScheduleManagementDraft ValidateAndNormalize(SyncScheduleManagementDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.ProfileId.IsEmpty ||
            !IsSafeText(draft.CronExpression, MaximumCronExpressionLength) ||
            !IsSafeText(draft.TimeZoneId, MaximumTimeZoneIdLength) ||
            draft.MisfireGrace < MinimumMisfireGrace ||
            draft.MisfireGrace > MaximumMisfireGrace ||
            draft.MisfireGrace.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentException("The schedule definition is outside safe bounds.", nameof(draft));
        }

        var expression = draft.CronExpression.Trim();
        var timeZoneId = draft.TimeZoneId.Trim();
        if (!CronScheduleDefinition.TryCreate(
                expression,
                timeZoneId,
                out _,
                out _,
                draft.MisfireGrace))
        {
            throw new ArgumentException("The cron expression or time-zone identifier is invalid.", nameof(draft));
        }

        return draft with { CronExpression = expression, TimeZoneId = timeZoneId };
    }

    private static DateTimeOffset? CalculateNextOccurrence(
        SyncScheduleManagementDraft draft,
        DateTimeOffset observedAtUtc)
    {
        if (!draft.Enabled)
        {
            return null;
        }

        var expression = CronExpression.Parse(draft.CronExpression, CronFormat.Standard);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(draft.TimeZoneId);
        return expression.GetNextOccurrence(observedAtUtc, timeZone, inclusive: false) ??
            throw new ArgumentException("The cron expression has no future occurrence.", nameof(draft));
    }

    private static bool ConfigurationEquals(
        SyncScheduleManagementRecord current,
        SyncScheduleManagementDraft draft) =>
        current.ProfileId == draft.ProfileId &&
        string.Equals(current.CronExpression, draft.CronExpression, StringComparison.Ordinal) &&
        string.Equals(current.TimeZoneId, draft.TimeZoneId, StringComparison.Ordinal) &&
        current.MisfireGrace == draft.MisfireGrace &&
        current.QueueOneWhileRunning == draft.QueueOneWhileRunning &&
        current.Enabled == draft.Enabled;

    private static SyncScheduleManagementDraft ToDraft(
        SyncScheduleManagementRecord current,
        bool enabled) => new(
        current.ProfileId,
        current.CronExpression,
        current.TimeZoneId,
        current.MisfireGrace,
        current.QueueOneWhileRunning,
        enabled);

    private static async Task<bool> ProfileCanBeScheduledAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncProfileId profileId,
        bool requireEnabled,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT enabled FROM sync_profiles WHERE sync_profile_id = $profileId;
            """;
        command.Parameters.AddWithValue("$profileId", profileId.ToString());
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is not null && value != DBNull.Value &&
            (!requireEnabled || Convert.ToInt64(value, CultureInfo.InvariantCulture) == 1);
    }

    private static async Task ReapExpiredTargetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ScheduledSyncJobId scheduleId,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE sync_schedules
            SET active_lease_id = NULL,
                active_lease_acquired_utc = NULL,
                active_lease_expires_utc = NULL,
                active_lease_fencing_token = NULL,
                last_run_completed_utc = $observedAt,
                last_run_outcome = 'lease-expired',
                last_error_code = 'scheduler.lease.expired',
                last_error_message = 'The prior scheduler lease expired before completion.',
                revision = revision + 1
            WHERE sync_schedule_id = $scheduleId
              AND active_lease_id IS NOT NULL
              AND active_lease_expires_utc IS NOT NULL
              AND active_lease_expires_utc <= $observedAt;
            """;
        command.Parameters.AddWithValue("$scheduleId", scheduleId.ToString());
        command.Parameters.AddWithValue("$observedAt", FormatTimestamp(observedAtUtc));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SyncScheduleManagementRecord?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ScheduledSyncJobId scheduleId,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {Projection}
            FROM sync_schedules AS schedule
            JOIN sync_profiles AS profile
              ON profile.sync_profile_id = schedule.sync_profile_id
            WHERE schedule.sync_schedule_id = $scheduleId;
            """;
        command.Parameters.AddWithValue("$scheduleId", scheduleId.ToString());
        command.Parameters.AddWithValue("$observedAt", FormatTimestamp(observedAtUtc));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    private static SyncScheduleManagementRecord Read(SqliteDataReader reader)
    {
        if (!Guid.TryParse(reader.GetString(0), out var scheduleId) || scheduleId == Guid.Empty ||
            !SyncProfileId.TryParse(reader.GetString(1), out var profileId))
        {
            throw new InvalidDataException("A stored schedule identity is invalid.");
        }

        var profileName = RequireSafeStoredText(reader.GetString(2), 256, "profile name");
        var cron = RequireSafeStoredText(reader.GetString(3), MaximumCronExpressionLength, "cron expression");
        var timeZone = RequireSafeStoredText(reader.GetString(4), MaximumTimeZoneIdLength, "time-zone identifier");
        var graceSeconds = reader.GetInt64(5);
        if (graceSeconds < MinimumMisfireGrace.TotalSeconds ||
            graceSeconds > MaximumMisfireGrace.TotalSeconds)
        {
            throw new InvalidDataException("A stored schedule misfire grace is outside safe bounds.");
        }

        var revision = reader.GetInt64(13);
        if (revision < 0)
        {
            throw new InvalidDataException("A stored schedule revision is invalid.");
        }

        return new SyncScheduleManagementRecord(
            new ScheduledSyncJobId(scheduleId),
            profileId,
            profileName,
            cron,
            timeZone,
            TimeSpan.FromSeconds(graceSeconds),
            reader.GetInt64(6) == 1,
            reader.GetInt64(7) == 1,
            ReadNullableTimestamp(reader, 8),
            ReadNullableTimestamp(reader, 9),
            reader.GetInt64(10) == 1,
            ReadSafeNullableText(reader, 11, 64),
            ReadSafeNullableText(reader, 12, 256),
            revision);
    }

    private static void AddDefinitionParameters(
        SqliteCommand command,
        ScheduledSyncJobId scheduleId,
        SyncScheduleManagementDraft draft,
        DateTimeOffset? nextOccurrence)
    {
        command.Parameters.AddWithValue("$scheduleId", scheduleId.ToString());
        command.Parameters.AddWithValue("$profileId", draft.ProfileId.ToString());
        command.Parameters.AddWithValue("$cron", draft.CronExpression);
        command.Parameters.AddWithValue("$timeZone", draft.TimeZoneId);
        command.Parameters.AddWithValue("$enabled", draft.Enabled ? 1 : 0);
        command.Parameters.AddWithValue(
            "$nextDue",
            nextOccurrence is { } next ? FormatTimestamp(next) : DBNull.Value);
        command.Parameters.AddWithValue(
            "$misfireGrace",
            checked((long)draft.MisfireGrace.TotalSeconds));
        command.Parameters.AddWithValue("$queueOne", draft.QueueOneWhileRunning ? 1 : 0);
    }

    private static DateTimeOffset? ReadNullableTimestamp(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        if (!DateTimeOffset.TryParseExact(
                reader.GetString(ordinal),
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var value) ||
            value.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("A stored schedule timestamp is invalid.");
        }

        return value;
    }

    private static string? ReadSafeNullableText(SqliteDataReader reader, int ordinal, int maximumLength)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetString(ordinal);
        return IsSafeText(value, maximumLength) ? value : null;
    }

    private static string RequireSafeStoredText(string value, int maximumLength, string fieldName) =>
        IsSafeText(value, maximumLength)
            ? value
            : throw new InvalidDataException($"The stored schedule {fieldName} is invalid.");

    private static bool IsSafeText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        !value.Any(char.IsControl);

    private static void RequireScheduleId(ScheduledSyncJobId scheduleId)
    {
        if (scheduleId.IsEmpty)
        {
            throw new ArgumentException("A schedule ID is required.", nameof(scheduleId));
        }
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static async Task TryRollbackAsync(SqliteTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Preserve the primary failure.
        }
    }

    private const string Projection = """
        schedule.sync_schedule_id,
        schedule.sync_profile_id,
        profile.display_name,
        schedule.cron_expression,
        schedule.time_zone_id,
        schedule.misfire_grace_seconds,
        schedule.queue_one_while_running,
        schedule.enabled,
        schedule.next_due_utc,
        schedule.queued_due_utc,
        CASE
            WHEN schedule.active_lease_id IS NOT NULL AND
                 (schedule.active_lease_expires_utc IS NULL OR
                  schedule.active_lease_expires_utc > $observedAt)
            THEN 1 ELSE 0
        END,
        schedule.last_run_outcome,
        schedule.last_error_code,
        schedule.revision
        """;
}
