using Microsoft.Data.Sqlite;
using StorageHub.Domain.Identifiers;
using StorageHub.Sync.Persistence;

namespace StorageHub.Persistence.Sync;

public sealed class SqliteSyncConflictStore(SingleWriterSqliteDatabase database) : ISyncConflictStore
{
    private readonly SingleWriterSqliteDatabase _database =
        database ?? throw new ArgumentNullException(nameof(database));

    public async ValueTask<SyncConflictRecord?> GetAsync(
        Guid conflictId,
        CancellationToken cancellationToken = default)
    {
        EnsureConflictId(conflictId);
        await using var connection = await _database
            .OpenReadConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ReadAsync(connection, null, conflictId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<SyncConflictRecord>> ListForRunAsync(
        SyncRunId syncRunId,
        SyncConflictState? state = null,
        int maximumCount = SyncPersistenceUtilities.MaximumPageSize,
        CancellationToken cancellationToken = default)
    {
        EnsureRunId(syncRunId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            maximumCount,
            SyncPersistenceUtilities.MaximumPageSize);

        if (state is not null && !Enum.IsDefined(state.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        await using var connection = await _database
            .OpenReadConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Projection}
            FROM conflict_records
            WHERE sync_run_id = $runId AND ($state IS NULL OR state = $state)
            ORDER BY detected_utc, conflict_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$runId", syncRunId.ToString());
        command.Parameters.AddWithValue("$state", state is null ? DBNull.Value : state.Value.ToString());
        command.Parameters.AddWithValue("$limit", maximumCount);
        var result = new List<SyncConflictRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(Read(reader));
        }

        return result;
    }

    public async ValueTask<SyncPersistenceResult<SyncConflictRecord>> AddAsync(
        SyncConflictDraft draft,
        CancellationToken cancellationToken = default)
    {
        ValidateDraft(draft);
        await using var writer = await _database
            .AcquireWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await writer.Connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var existing = await ReadAsync(
                writer.Connection,
                transaction,
                draft.ConflictId,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                var identical = existing.SyncRunId == draft.SyncRunId &&
                    string.Equals(existing.RelativePath, draft.RelativePath, StringComparison.Ordinal) &&
                    string.Equals(existing.ConflictKind, draft.ConflictKind, StringComparison.Ordinal) &&
                    string.Equals(existing.SafeDetailsJson, draft.SafeDetailsJson, StringComparison.Ordinal) &&
                    existing.DetectedAtUtc == draft.DetectedAtUtc;
                return new SyncPersistenceResult<SyncConflictRecord>(
                    identical
                        ? SyncPersistenceMutationStatus.AlreadyApplied
                        : SyncPersistenceMutationStatus.Conflict,
                    identical ? existing : null);
            }

            if (!await RunExistsAsync(
                    writer.Connection,
                    transaction,
                    draft.SyncRunId,
                    cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Rejected(SyncPersistenceMutationStatus.NotFound);
            }

            var record = new SyncConflictRecord(
                draft.ConflictId,
                draft.SyncRunId,
                draft.RelativePath,
                draft.ConflictKind,
                SyncConflictState.Unresolved,
                draft.SafeDetailsJson,
                draft.DetectedAtUtc,
                null,
                null,
                1);
            await InsertAsync(writer.Connection, transaction, record, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return new SyncPersistenceResult<SyncConflictRecord>(
                SyncPersistenceMutationStatus.Applied,
                record);
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<SyncPersistenceResult<SyncConflictRecord>> ResolveAsync(
        Guid conflictId,
        long expectedRevision,
        SyncConflictResolution resolution,
        CancellationToken cancellationToken = default)
    {
        EnsureConflictId(conflictId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedRevision);
        ValidateResolution(resolution);
        await using var writer = await _database
            .AcquireWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await writer.Connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var current = await ReadAsync(
                writer.Connection,
                transaction,
                conflictId,
                cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Rejected(SyncPersistenceMutationStatus.NotFound);
            }

            if (current.State == resolution.State &&
                current.ResolvedAtUtc == resolution.ResolvedAtUtc &&
                string.Equals(current.SafeResolutionJson, resolution.SafeResolutionJson, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new SyncPersistenceResult<SyncConflictRecord>(
                    SyncPersistenceMutationStatus.AlreadyApplied,
                    current);
            }

            if (current.State != SyncConflictState.Unresolved || current.Revision != expectedRevision)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Rejected(SyncPersistenceMutationStatus.Conflict);
            }

            var next = current with
            {
                State = resolution.State,
                ResolvedAtUtc = resolution.ResolvedAtUtc,
                SafeResolutionJson = resolution.SafeResolutionJson,
                Revision = checked(current.Revision + 1)
            };
            await using (var command = writer.Connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE conflict_records
                    SET state = $state, resolved_utc = $resolvedAt,
                        resolution = NULL, safe_resolution_json = $resolution,
                        record_revision = $nextRevision, updated_utc = $resolvedAt
                    WHERE conflict_id = $id AND state = 'Unresolved' AND record_revision = $expectedRevision;
                    """;
                command.Parameters.AddWithValue("$state", resolution.State.ToString());
                command.Parameters.AddWithValue(
                    "$resolvedAt",
                    SyncPersistenceUtilities.FormatTimestamp(resolution.ResolvedAtUtc));
                command.Parameters.AddWithValue("$resolution", resolution.SafeResolutionJson);
                command.Parameters.AddWithValue("$nextRevision", next.Revision);
                command.Parameters.AddWithValue("$id", conflictId.ToString("D"));
                command.Parameters.AddWithValue("$expectedRevision", expectedRevision);
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidDataException("The conflict revision changed inside the writer transaction.");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return new SyncPersistenceResult<SyncConflictRecord>(SyncPersistenceMutationStatus.Applied, next);
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask<SyncConflictRecord?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid conflictId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Projection} FROM conflict_records WHERE conflict_id = $id;";
        command.Parameters.AddWithValue("$id", conflictId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    private static SyncConflictRecord Read(SqliteDataReader reader)
    {
        if (!SyncRunId.TryParse(reader.GetString(1), out var runId))
        {
            throw new InvalidDataException("The persisted conflict sync run ID is invalid.");
        }

        return new SyncConflictRecord(
            SyncPersistenceUtilities.ParseGuid(reader.GetString(0), "conflict ID"),
            runId,
            reader.GetString(2),
            reader.GetString(3),
            SyncPersistenceUtilities.ParseEnum<SyncConflictState>(reader.GetString(4), "conflict state"),
            reader.GetString(5),
            SyncPersistenceUtilities.ParseTimestamp(reader.GetString(6), "conflict detection time"),
            reader.IsDBNull(7)
                ? null
                : SyncPersistenceUtilities.ParseTimestamp(reader.GetString(7), "conflict resolution time"),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetInt64(9));
    }

    private static async Task InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncConflictRecord record,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO conflict_records
            (conflict_id, sync_run_id, relative_path, conflict_kind, state,
             detected_utc, safe_details_json, record_revision, updated_utc)
            VALUES ($id, $runId, $path, $kind, $state, $detectedAt, $details, $revision, $detectedAt);
            """;
        command.Parameters.AddWithValue("$id", record.ConflictId.ToString("D"));
        command.Parameters.AddWithValue("$runId", record.SyncRunId.ToString());
        command.Parameters.AddWithValue("$path", record.RelativePath);
        command.Parameters.AddWithValue("$kind", record.ConflictKind);
        command.Parameters.AddWithValue("$state", record.State.ToString());
        command.Parameters.AddWithValue("$detectedAt", SyncPersistenceUtilities.FormatTimestamp(record.DetectedAtUtc));
        command.Parameters.AddWithValue("$details", record.SafeDetailsJson);
        command.Parameters.AddWithValue("$revision", record.Revision);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<bool> RunExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncRunId runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM sync_runs WHERE sync_run_id = $runId;";
        command.Parameters.AddWithValue("$runId", runId.ToString());
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static void ValidateDraft(SyncConflictDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        EnsureConflictId(draft.ConflictId);
        EnsureRunId(draft.SyncRunId);
        SyncPersistenceUtilities.ValidateRelativePath(draft.RelativePath, nameof(draft));
        SyncPersistenceUtilities.ValidateText(
            draft.ConflictKind,
            nameof(draft),
            SyncPersistenceUtilities.MaximumKindLength);
        SyncPersistenceUtilities.ValidateSafeJsonObject(draft.SafeDetailsJson, nameof(draft));
        SyncPersistenceUtilities.ValidateUtc(draft.DetectedAtUtc, nameof(draft));
    }

    private static void ValidateResolution(SyncConflictResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        if (resolution.State is not (SyncConflictState.Resolved or SyncConflictState.Dismissed))
        {
            throw new ArgumentException("A conflict resolution must resolve or dismiss the conflict.", nameof(resolution));
        }

        SyncPersistenceUtilities.ValidateSafeJsonObject(resolution.SafeResolutionJson, nameof(resolution));
        SyncPersistenceUtilities.ValidateUtc(resolution.ResolvedAtUtc, nameof(resolution));
    }

    private static void EnsureConflictId(Guid conflictId)
    {
        if (conflictId == Guid.Empty)
        {
            throw new ArgumentException("A conflict ID is required.", nameof(conflictId));
        }
    }

    private static void EnsureRunId(SyncRunId runId)
    {
        if (runId.IsEmpty)
        {
            throw new ArgumentException("A sync run ID is required.", nameof(runId));
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
            // Preserve the primary mutation or commit failure.
        }
    }

    private static SyncPersistenceResult<SyncConflictRecord> Rejected(
        SyncPersistenceMutationStatus status) => new(status, null);

    private const string Projection = """
        conflict_id, sync_run_id, relative_path, conflict_kind, state,
        safe_details_json, detected_utc, resolved_utc, safe_resolution_json, record_revision
        """;
}
