using System.Globalization;
using Microsoft.Data.Sqlite;
using StorageHub.Agent.Scheduling;
using StorageHub.Domain.Identifiers;

namespace StorageHub.Persistence;

/// <summary>
/// SQLite implementation of the durable scheduler store. All mutation paths use revision CAS or
/// lease ID plus fencing-token predicates; the partial unique index enforces one active lease per
/// sync profile across schedules and processes.
/// </summary>
public sealed class SqliteScheduledSyncJobStore : IScheduledSyncJobStore
{
    private readonly SingleWriterSqliteDatabase _database;
    private readonly TimeProvider _timeProvider;

    public SqliteScheduledSyncJobStore(
        SingleWriterSqliteDatabase database,
        TimeProvider? timeProvider = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<IReadOnlyList<ScheduledSyncJobSnapshot>> GetJobsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database
            .OpenReadConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                sync_schedule_id,
                sync_profile_id,
                cron_expression,
                time_zone_id,
                enabled,
                next_due_utc,
                misfire_policy,
                misfire_grace_seconds,
                queue_one_while_running,
                queued_due_utc,
                revision
            FROM sync_schedules
            ORDER BY sync_schedule_id;
            """;
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var jobs = new List<ScheduledSyncJobSnapshot>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var jobId = ParseJobId(reader.GetString(0));
            var profileId = ParseProfileId(reader.GetString(1));
            var expression = reader.GetString(2);
            var timeZoneId = reader.GetString(3);
            var enabled = reader.GetInt64(4) == 1;
            var nextOccurrenceUtc = ReadNullableTimestamp(reader, 5);
            var misfirePolicy = reader.GetString(6);
            if (!string.Equals(misfirePolicy, "coalesce-one", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The stored scheduler misfire policy is unsupported.");
            }

            var misfireGraceSeconds = reader.GetInt64(7);
            TimeSpan misfireGrace;
            try
            {
                misfireGrace = TimeSpan.FromSeconds(misfireGraceSeconds);
            }
            catch (OverflowException error)
            {
                throw new InvalidDataException("The stored scheduler misfire grace is invalid.", error);
            }

            if (!CronScheduleDefinition.TryCreate(
                    expression,
                    timeZoneId,
                    out var schedule,
                    out _,
                    misfireGrace))
            {
                throw new InvalidDataException("The stored cron schedule is invalid.");
            }

            jobs.Add(new ScheduledSyncJobSnapshot(
                jobId,
                profileId,
                schedule!,
                enabled,
                reader.GetInt64(8) == 1,
                nextOccurrenceUtc,
                ReadNullableTimestamp(reader, 9),
                reader.GetInt64(10)));
        }

        return jobs;
    }

    public async ValueTask<ScheduledSyncLeaseAcquisition> TryAcquireLeaseAsync(
        ScheduledSyncLeaseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var leaseId = Guid.NewGuid();
        await using var writer = await _database
            .AcquireWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await writer.Connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            // Sample time only after the cross-process writer is held. The caller may have waited
            // for this transaction, so its observation cannot safely establish lease validity.
            var observedAtUtc = _timeProvider.GetUtcNow();
            var expiresAtUtc = observedAtUtc.Add(request.LeaseDuration);
            await ReapExpiredLeasesAsync(
                writer.Connection,
                transaction,
                observedAtUtc,
                cancellationToken).ConfigureAwait(false);

            await using var command = writer.Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE sync_schedules AS target
                SET
                    next_due_utc = $nextDue,
                    queued_due_utc = CASE WHEN $isQueued = 1 THEN NULL ELSE queued_due_utc END,
                    last_due_utc = $scheduledFor,
                    active_lease_id = $leaseId,
                    active_lease_acquired_utc = $observedAt,
                    active_lease_expires_utc = $expiresAt,
                    fencing_counter = fencing_counter + 1,
                    active_lease_fencing_token = fencing_counter + 1,
                    last_run_started_utc = $observedAt,
                    last_run_completed_utc = NULL,
                    last_run_outcome = NULL,
                    last_error_code = NULL,
                    last_error_message = NULL,
                    revision = revision + 1
                WHERE
                    sync_schedule_id = $jobId
                    AND sync_profile_id = $profileId
                    AND revision = $expectedRevision
                    AND enabled = 1
                    AND active_lease_id IS NULL
                    AND
                    (
                        ($isQueued = 1 AND queued_due_utc = $scheduledFor)
                        OR
                        ($isQueued = 0 AND queued_due_utc IS NULL AND next_due_utc = $scheduledFor)
                    )
                    AND NOT EXISTS
                    (
                        SELECT 1
                        FROM sync_schedules AS active
                        WHERE active.sync_profile_id = target.sync_profile_id
                            AND active.active_lease_id IS NOT NULL
                            AND active.active_lease_expires_utc > $observedAt
                    );
                """;
            AddLeaseRequestParameters(command, request, leaseId, observedAtUtc, expiresAtUtc);
            var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (changed == 1)
            {
                var fencingToken = await ReadFencingTokenAsync(
                    writer.Connection,
                    transaction,
                    request.JobId,
                    leaseId,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return ScheduledSyncLeaseAcquisition.Acquired(new ScheduledSyncJobLease(
                    leaseId,
                    request.JobId,
                    request.ProfileId,
                    request.ScheduledForUtc,
                    observedAtUtc,
                    expiresAtUtc,
                    fencingToken));
            }

            var status = await DetermineAcquisitionFailureAsync(
                writer.Connection,
                transaction,
                request,
                observedAtUtc,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return status;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<bool> TryRenewLeaseAsync(
        ScheduledSyncLeaseRenewal renewal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(renewal);
        var requestedDuration = renewal.ExpiresAtUtc - renewal.RenewedAtUtc;
        await using var writer = await _database
            .AcquireWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = writer.Connection.BeginTransaction(deferred: false);
        try
        {
            var renewedAtUtc = _timeProvider.GetUtcNow();
            var expiresAtUtc = renewedAtUtc.Add(requestedDuration);
            await using var command = writer.Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE sync_schedules
                SET
                    active_lease_expires_utc = $expiresAt,
                    revision = revision + 1
                WHERE
                    sync_schedule_id = $jobId
                    AND sync_profile_id = $profileId
                    AND active_lease_id = $leaseId
                    AND active_lease_fencing_token = $fencingToken
                    AND active_lease_expires_utc > $renewedAt
                    AND active_lease_expires_utc < $expiresAt;
                """;
            command.Parameters.AddWithValue("$expiresAt", FormatTimestamp(expiresAtUtc));
            command.Parameters.AddWithValue("$renewedAt", FormatTimestamp(renewedAtUtc));
            command.Parameters.AddWithValue("$jobId", renewal.Lease.JobId.ToString());
            command.Parameters.AddWithValue("$profileId", renewal.Lease.ProfileId.ToString());
            command.Parameters.AddWithValue(
                "$leaseId",
                renewal.Lease.LeaseId.ToString("D", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$fencingToken", renewal.Lease.FencingToken);
            var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return changed == 1;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<bool> TryRecordOccurrenceDispositionAsync(
        ScheduledOccurrenceDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disposition);
        var (queuedValue, additionalPredicate) = disposition.Disposition switch
        {
            ScheduledOccurrenceDispositionKind.ExpiredMisfireSkipped =>
                ("NULL", string.Empty),
            ScheduledOccurrenceDispositionKind.OverlapSkipped =>
                ("NULL", "AND queue_one_while_running = 0 AND " + ActiveProfileLeaseExistsSql),
            ScheduledOccurrenceDispositionKind.OverlapQueued =>
                ("$scheduledFor", "AND queue_one_while_running = 1 AND " + ActiveProfileLeaseExistsSql),
            _ => throw new ArgumentOutOfRangeException(nameof(disposition)),
        };

        await using var writer = await _database
            .AcquireWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = writer.Connection.BeginTransaction(deferred: false);
        try
        {
            var observedAtUtc = _timeProvider.GetUtcNow();
            await using var command = writer.Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                UPDATE sync_schedules AS target
                SET
                    queued_due_utc = {queuedValue},
                    next_due_utc = $nextDue,
                    last_due_utc = $scheduledFor,
                    revision = revision + 1
                WHERE
                    sync_schedule_id = $jobId
                    AND sync_profile_id = $profileId
                    AND revision = $expectedRevision
                    AND enabled = 1
                    AND queued_due_utc IS NULL
                    AND next_due_utc = $scheduledFor
                    {additionalPredicate};
                """;
            command.Parameters.AddWithValue("$jobId", disposition.JobId.ToString());
            command.Parameters.AddWithValue("$profileId", disposition.ProfileId.ToString());
            command.Parameters.AddWithValue("$expectedRevision", disposition.ExpectedRevision);
            command.Parameters.AddWithValue("$scheduledFor", FormatTimestamp(disposition.ScheduledForUtc));
            command.Parameters.AddWithValue("$observedAt", FormatTimestamp(observedAtUtc));
            command.Parameters.AddWithValue(
                "$nextDue",
                disposition.NextOccurrenceUtc is { } next
                    ? FormatTimestamp(next)
                    : DBNull.Value);
            var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return changed == 1;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask RecordCompletionAsync(
        ScheduledSyncJobCompletion completion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        var outcome = completion.Result.Outcome switch
        {
            ScheduledSyncRunOutcome.Completed => "completed",
            ScheduledSyncRunOutcome.Failed => "failed",
            ScheduledSyncRunOutcome.Cancelled => "cancelled",
            _ => throw new ArgumentOutOfRangeException(nameof(completion)),
        };

        await using var writer = await _database
            .AcquireWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = writer.Connection.BeginTransaction(deferred: false);
        try
        {
            var observedAtUtc = _timeProvider.GetUtcNow();
            if (await IsExactRecordedCompletionAsync(
                    writer.Connection,
                    transaction,
                    completion,
                    outcome,
                    cancellationToken).ConfigureAwait(false))
            {
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return;
            }

            await using var command = writer.Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE sync_schedules
                SET
                    active_lease_id = NULL,
                    active_lease_acquired_utc = NULL,
                    active_lease_expires_utc = NULL,
                    active_lease_fencing_token = NULL,
                    last_run_started_utc = $startedAt,
                    last_run_completed_utc = $completedAt,
                    last_run_outcome = $outcome,
                    last_error_code = $errorCode,
                    last_error_message = $errorMessage,
                    revision = revision + 1
                WHERE
                    sync_schedule_id = $jobId
                    AND sync_profile_id = $profileId
                    AND active_lease_id = $leaseId
                    AND active_lease_fencing_token = $fencingToken
                    AND active_lease_expires_utc > $observedAt;
                """;
            command.Parameters.AddWithValue("$startedAt", FormatTimestamp(completion.StartedAtUtc));
            command.Parameters.AddWithValue("$completedAt", FormatTimestamp(completion.CompletedAtUtc));
            command.Parameters.AddWithValue("$observedAt", FormatTimestamp(observedAtUtc));
            command.Parameters.AddWithValue("$outcome", outcome);
            command.Parameters.AddWithValue("$errorCode", (object?)completion.Result.Code ?? DBNull.Value);
            command.Parameters.AddWithValue("$errorMessage", (object?)completion.Result.Message ?? DBNull.Value);
            command.Parameters.AddWithValue("$jobId", completion.Lease.JobId.ToString());
            command.Parameters.AddWithValue("$profileId", completion.Lease.ProfileId.ToString());
            command.Parameters.AddWithValue(
                "$leaseId",
                completion.Lease.LeaseId.ToString("D", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$fencingToken", completion.Lease.FencingToken);
            var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (changed != 1)
            {
                throw new InvalidOperationException(
                    "The scheduler completion lease is no longer current.");
            }

            await InsertCompletionAsync(
                writer.Connection,
                transaction,
                completion,
                outcome,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private const string ActiveProfileLeaseExistsSql = """
        EXISTS
        (
            SELECT 1
            FROM sync_schedules AS active
            WHERE active.sync_profile_id = target.sync_profile_id
                AND active.active_lease_id IS NOT NULL
                AND active.active_lease_expires_utc > $observedAt
        )
        """;

    private static async Task ReapExpiredLeasesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE sync_schedules
            SET
                active_lease_id = NULL,
                active_lease_acquired_utc = NULL,
                active_lease_expires_utc = NULL,
                active_lease_fencing_token = NULL,
                last_run_completed_utc = $observedAt,
                last_run_outcome = 'lease-expired',
                last_error_code = 'scheduler.lease.expired',
                last_error_message = 'The prior scheduler lease expired before completion.',
                revision = revision + 1
            WHERE
                active_lease_id IS NOT NULL
                AND active_lease_expires_utc <= $observedAt;
            """;
        command.Parameters.AddWithValue("$observedAt", FormatTimestamp(observedAtUtc));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddLeaseRequestParameters(
        SqliteCommand command,
        ScheduledSyncLeaseRequest request,
        Guid leaseId,
        DateTimeOffset observedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        command.Parameters.AddWithValue("$nextDue", request.NextOccurrenceUtc is { } next
            ? FormatTimestamp(next)
            : DBNull.Value);
        command.Parameters.AddWithValue("$isQueued", request.IsQueuedOccurrence ? 1 : 0);
        command.Parameters.AddWithValue("$scheduledFor", FormatTimestamp(request.ScheduledForUtc));
        command.Parameters.AddWithValue(
            "$leaseId",
            leaseId.ToString("D", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$observedAt", FormatTimestamp(observedAtUtc));
        command.Parameters.AddWithValue("$expiresAt", FormatTimestamp(expiresAtUtc));
        command.Parameters.AddWithValue("$jobId", request.JobId.ToString());
        command.Parameters.AddWithValue("$profileId", request.ProfileId.ToString());
        command.Parameters.AddWithValue("$expectedRevision", request.ExpectedRevision);
    }

    private static async Task<long> ReadFencingTokenAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ScheduledSyncJobId jobId,
        Guid leaseId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT active_lease_fencing_token
            FROM sync_schedules
            WHERE sync_schedule_id = $jobId AND active_lease_id = $leaseId;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        command.Parameters.AddWithValue("$leaseId", leaseId.ToString("D", CultureInfo.InvariantCulture));
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static async Task<bool> IsExactRecordedCompletionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ScheduledSyncJobCompletion completion,
        string outcome,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                sync_schedule_id,
                sync_profile_id,
                fencing_token,
                scheduled_for_utc,
                started_utc,
                completed_utc,
                outcome,
                error_code,
                error_message
            FROM sync_schedule_completions
            WHERE lease_id = $leaseId;
            """;
        command.Parameters.AddWithValue(
            "$leaseId",
            completion.Lease.LeaseId.ToString("D", CultureInfo.InvariantCulture));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var matches =
            string.Equals(reader.GetString(0), completion.Lease.JobId.ToString(), StringComparison.Ordinal) &&
            string.Equals(reader.GetString(1), completion.Lease.ProfileId.ToString(), StringComparison.Ordinal) &&
            reader.GetInt64(2) == completion.Lease.FencingToken &&
            string.Equals(reader.GetString(3), FormatTimestamp(completion.Lease.ScheduledForUtc), StringComparison.Ordinal) &&
            string.Equals(reader.GetString(4), FormatTimestamp(completion.StartedAtUtc), StringComparison.Ordinal) &&
            string.Equals(reader.GetString(5), FormatTimestamp(completion.CompletedAtUtc), StringComparison.Ordinal) &&
            string.Equals(reader.GetString(6), outcome, StringComparison.Ordinal) &&
            string.Equals(ReadNullableString(reader, 7), completion.Result.Code, StringComparison.Ordinal) &&
            string.Equals(ReadNullableString(reader, 8), completion.Result.Message, StringComparison.Ordinal);
        if (!matches)
        {
            throw new InvalidOperationException(
                "The scheduler completion lease was already recorded with a different payload.");
        }

        return true;
    }

    private static async Task InsertCompletionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ScheduledSyncJobCompletion completion,
        string outcome,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sync_schedule_completions
            (
                lease_id,
                sync_schedule_id,
                sync_profile_id,
                fencing_token,
                scheduled_for_utc,
                started_utc,
                completed_utc,
                outcome,
                error_code,
                error_message
            )
            VALUES
            (
                $leaseId,
                $jobId,
                $profileId,
                $fencingToken,
                $scheduledFor,
                $startedAt,
                $completedAt,
                $outcome,
                $errorCode,
                $errorMessage
            );
            """;
        command.Parameters.AddWithValue(
            "$leaseId",
            completion.Lease.LeaseId.ToString("D", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$jobId", completion.Lease.JobId.ToString());
        command.Parameters.AddWithValue("$profileId", completion.Lease.ProfileId.ToString());
        command.Parameters.AddWithValue("$fencingToken", completion.Lease.FencingToken);
        command.Parameters.AddWithValue("$scheduledFor", FormatTimestamp(completion.Lease.ScheduledForUtc));
        command.Parameters.AddWithValue("$startedAt", FormatTimestamp(completion.StartedAtUtc));
        command.Parameters.AddWithValue("$completedAt", FormatTimestamp(completion.CompletedAtUtc));
        command.Parameters.AddWithValue("$outcome", outcome);
        command.Parameters.AddWithValue("$errorCode", (object?)completion.Result.Code ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", (object?)completion.Result.Message ?? DBNull.Value);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ScheduledSyncLeaseAcquisition> DetermineAcquisitionFailureAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ScheduledSyncLeaseRequest request,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT enabled, revision, next_due_utc, queued_due_utc
                FROM sync_schedules
                WHERE sync_schedule_id = $jobId AND sync_profile_id = $profileId;
                """;
            command.Parameters.AddWithValue("$jobId", request.JobId.ToString());
            command.Parameters.AddWithValue("$profileId", request.ProfileId.ToString());
            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return ScheduledSyncLeaseAcquisition.StaleSnapshot();
            }

            if (reader.GetInt64(0) != 1)
            {
                return ScheduledSyncLeaseAcquisition.Disabled();
            }

            if (reader.GetInt64(1) != request.ExpectedRevision)
            {
                return ScheduledSyncLeaseAcquisition.StaleSnapshot();
            }

            var nextDue = ReadNullableTimestamp(reader, 2);
            var queuedDue = ReadNullableTimestamp(reader, 3);
            var occurrenceMatches = request.IsQueuedOccurrence
                ? queuedDue == request.ScheduledForUtc
                : queuedDue is null && nextDue == request.ScheduledForUtc;
            if (!occurrenceMatches)
            {
                return ScheduledSyncLeaseAcquisition.StaleSnapshot();
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT COUNT(*)
                FROM sync_schedules
                WHERE sync_profile_id = $profileId
                    AND active_lease_id IS NOT NULL
                    AND active_lease_expires_utc > $observedAt;
                """;
            command.Parameters.AddWithValue("$profileId", request.ProfileId.ToString());
            command.Parameters.AddWithValue("$observedAt", FormatTimestamp(observedAtUtc));
            var activeCount = Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            return activeCount > 0
                ? ScheduledSyncLeaseAcquisition.ProfileBusy()
                : ScheduledSyncLeaseAcquisition.StaleSnapshot();
        }
    }

    private static ScheduledSyncJobId ParseJobId(string value)
    {
        if (!Guid.TryParse(value, out var parsed) || parsed == Guid.Empty)
        {
            throw new InvalidDataException("The stored scheduled job ID is invalid.");
        }

        return new ScheduledSyncJobId(parsed);
    }

    private static SyncProfileId ParseProfileId(string value)
    {
        if (!SyncProfileId.TryParse(value, out var parsed))
        {
            throw new InvalidDataException("The stored sync profile ID is invalid.");
        }

        return parsed;
    }

    private static DateTimeOffset? ReadNullableTimestamp(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetString(ordinal);
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed) ||
            parsed.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("A stored scheduler timestamp is invalid.");
        }

        return parsed;
    }

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);
}
