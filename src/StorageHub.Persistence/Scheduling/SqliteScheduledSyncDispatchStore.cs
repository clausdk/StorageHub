using System.Text.Json;
using Microsoft.Data.Sqlite;
using StorageHub.Persistence.Sync;
using StorageHub.Sync.Persistence;

namespace StorageHub.Persistence.Scheduling;

/// <summary>
/// Bridges a scheduler lease to the reliable outbox in one immediate SQLite transaction. This
/// adapter deliberately has no storage-provider dependency.
/// </summary>
public sealed class SqliteScheduledSyncDispatchStore : IScheduledSyncDispatchStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SingleWriterSqliteDatabase _database;
    private readonly TimeProvider _timeProvider;

    public SqliteScheduledSyncDispatchStore(
        SingleWriterSqliteDatabase database,
        TimeProvider? timeProvider = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<SyncPersistenceMutationStatus> TryDispatchAsync(
        ScheduledSyncDispatchRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        await using var writer = await _database.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = writer.Connection.BeginTransaction(deferred: false);
        try
        {
            var observedAtUtc = _timeProvider.GetUtcNow();
            if (!await ExactLeaseIsCurrentAsync(
                    writer.Connection,
                    transaction,
                    request,
                    observedAtUtc,
                    cancellationToken).ConfigureAwait(false))
            {
                var exists = await ScheduleExistsAsync(
                    writer.Connection,
                    transaction,
                    request.JobId,
                    request.ProfileId.ToString(),
                    cancellationToken).ConfigureAwait(false);
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return exists
                    ? SyncPersistenceMutationStatus.StaleLease
                    : SyncPersistenceMutationStatus.NotFound;
            }

            var authorization = await ReadAuthorizationAsync(
                writer.Connection,
                transaction,
                request,
                cancellationToken).ConfigureAwait(false);
            if (authorization is null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return SyncPersistenceMutationStatus.StaleLease;
            }

            var payload = JsonSerializer.Serialize(
                new ScheduledSyncPreviewOutboxPayload(
                    request.JobId.ToString("D"),
                    request.ProfileId.ToString(),
                    request.LeaseId.ToString("D"),
                    request.FencingToken,
                    request.ScheduledForUtc,
                    authorization.Value.ExecutionMode,
                    authorization.Value.ProfileRevision,
                    authorization.Value.ProfilePolicySha256),
                JsonOptions);
            var enqueue = await SqliteReliableOutboxStore.EnqueueCoreAsync(
                writer.Connection,
                transaction,
                new OutboxEventDraft(
                    request.LeaseId,
                    SyncOutboxEventKinds.PreviewRequested,
                    $"sync-schedule:{request.JobId:D}",
                    request.FencingToken,
                    payload,
                    request.LeaseAcquiredAtUtc),
                cancellationToken).ConfigureAwait(false);
            if (enqueue.Status is not (
                    SyncPersistenceMutationStatus.Applied or
                    SyncPersistenceMutationStatus.AlreadyApplied))
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return SyncPersistenceMutationStatus.Conflict;
            }

            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return enqueue.Status;
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask<bool> ExactLeaseIsCurrentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ScheduledSyncDispatchRequest request,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM sync_schedules AS schedule
            JOIN sync_profiles AS profile
              ON profile.sync_profile_id = schedule.sync_profile_id
            WHERE schedule.sync_schedule_id = $jobId
              AND schedule.sync_profile_id = $profileId
              AND schedule.enabled = 1
              AND profile.enabled = 1
              AND schedule.active_lease_id = $leaseId
              AND schedule.active_lease_fencing_token = $fence
              AND schedule.active_lease_acquired_utc = $acquiredAt
              AND schedule.active_lease_expires_utc > $observedAt;
            """;
        command.Parameters.AddWithValue("$jobId", request.JobId.ToString("D"));
        command.Parameters.AddWithValue("$profileId", request.ProfileId.ToString());
        command.Parameters.AddWithValue("$leaseId", request.LeaseId.ToString("D"));
        command.Parameters.AddWithValue("$fence", request.FencingToken);
        command.Parameters.AddWithValue(
            "$acquiredAt",
            SyncPersistenceUtilities.FormatTimestamp(request.LeaseAcquiredAtUtc));
        command.Parameters.AddWithValue(
            "$observedAt",
            SyncPersistenceUtilities.FormatTimestamp(observedAtUtc));
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async ValueTask<(string ExecutionMode, long ProfileRevision, string ProfilePolicySha256)?>
        ReadAuthorizationAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            ScheduledSyncDispatchRequest request,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT schedule.execution_mode, profile.profile_revision, profile.policy_hash
            FROM sync_schedules AS schedule
            JOIN sync_profiles AS profile ON profile.sync_profile_id = schedule.sync_profile_id
            WHERE schedule.sync_schedule_id = $jobId
              AND schedule.sync_profile_id = $profileId;
            """;
        command.Parameters.AddWithValue("$jobId", request.JobId.ToString("D"));
        command.Parameters.AddWithValue("$profileId", request.ProfileId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetString(0), reader.GetInt64(1), reader.GetString(2))
            : null;
    }

    private static async ValueTask<bool> ScheduleExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid jobId,
        string profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM sync_schedules
            WHERE sync_schedule_id = $jobId AND sync_profile_id = $profileId;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
        command.Parameters.AddWithValue("$profileId", profileId);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static void Validate(ScheduledSyncDispatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.JobId == Guid.Empty || request.ProfileId.IsEmpty || request.LeaseId == Guid.Empty)
        {
            throw new ArgumentException("The schedule, profile, and lease IDs are required.", nameof(request));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.FencingToken);
        SyncPersistenceUtilities.ValidateUtc(request.ScheduledForUtc, nameof(request));
        SyncPersistenceUtilities.ValidateUtc(request.LeaseAcquiredAtUtc, nameof(request));
        SyncPersistenceUtilities.ValidateUtc(request.LeaseExpiresAtUtc, nameof(request));
        if (request.LeaseExpiresAtUtc <= request.LeaseAcquiredAtUtc)
        {
            throw new ArgumentException("The supplied scheduler lease interval is invalid.", nameof(request));
        }
    }

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
}
