using Microsoft.Data.Sqlite;
using StorageHub.Sync.Persistence;

namespace StorageHub.Persistence.Sync;

public sealed class SqliteAuditEventStore(SingleWriterSqliteDatabase database) : IAuditEventStore
{
    private readonly SingleWriterSqliteDatabase _database =
        database ?? throw new ArgumentNullException(nameof(database));

    public async ValueTask<AuditEventRecord?> GetAsync(
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        EnsureEventId(eventId);
        await using var connection = await _database
            .OpenReadConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ReadByIdAsync(connection, null, eventId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<AuditEventRecord>> ReadAfterAsync(
        long sequenceNumber,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequenceNumber);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            maximumCount,
            SyncPersistenceUtilities.MaximumPageSize);

        await using var connection = await _database
            .OpenReadConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Projection}
            FROM audit_events
            WHERE sequence_number > $sequence
            ORDER BY sequence_number
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$sequence", sequenceNumber);
        command.Parameters.AddWithValue("$limit", maximumCount);
        var events = new List<AuditEventRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(Read(reader));
        }

        return events;
    }

    public async ValueTask<SyncPersistenceResult<AuditEventRecord>> AppendAsync(
        AuditAppendRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateDraft(request.AuditEvent);
        if (request.OutboxEvent is not null)
        {
            SqliteReliableOutboxStore.ValidateDraft(request.OutboxEvent);
        }

        await using var writer = await _database
            .AcquireWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = writer.Connection.BeginTransaction(deferred: false);
        try
        {
            var existingById = await ReadByIdAsync(
                writer.Connection,
                transaction,
                request.AuditEvent.EventId,
                cancellationToken).ConfigureAwait(false);
            if (existingById is not null)
            {
                var equivalent = IsEquivalent(existingById, request.AuditEvent) &&
                    await HasEquivalentOutboxAsync(
                        writer.Connection,
                        transaction,
                        request.OutboxEvent,
                        cancellationToken).ConfigureAwait(false);
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new SyncPersistenceResult<AuditEventRecord>(
                    equivalent
                        ? SyncPersistenceMutationStatus.AlreadyApplied
                        : SyncPersistenceMutationStatus.Conflict,
                    equivalent ? existingById : null);
            }

            if (await ReadByIdempotencyKeyAsync(
                    writer.Connection,
                    transaction,
                    request.AuditEvent.IdempotencyKey,
                    cancellationToken).ConfigureAwait(false) is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Rejected(SyncPersistenceMutationStatus.Conflict);
            }

            var sequenceNumber = await NextSequenceAsync(
                writer.Connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            var record = FromDraft(request.AuditEvent, sequenceNumber);
            await InsertAsync(writer.Connection, transaction, record, cancellationToken).ConfigureAwait(false);

            if (request.OutboxEvent is not null)
            {
                var outboxResult = await SqliteReliableOutboxStore.EnqueueCoreAsync(
                    writer.Connection,
                    transaction,
                    request.OutboxEvent,
                    cancellationToken).ConfigureAwait(false);
                if (outboxResult.Status == SyncPersistenceMutationStatus.Conflict)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return Rejected(SyncPersistenceMutationStatus.Conflict);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return new SyncPersistenceResult<AuditEventRecord>(
                SyncPersistenceMutationStatus.Applied,
                record);
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask<bool> HasEquivalentOutboxAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        OutboxEventDraft? draft,
        CancellationToken cancellationToken)
    {
        if (draft is null)
        {
            return true;
        }

        var record = await SqliteReliableOutboxStore.ReadAsync(
            connection,
            transaction,
            draft.EventId,
            cancellationToken).ConfigureAwait(false);
        return record is not null && SqliteReliableOutboxStore.IsEquivalent(record, draft);
    }

    private static async ValueTask<AuditEventRecord?> ReadByIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Projection} FROM audit_events WHERE audit_event_id = $eventId;";
        command.Parameters.AddWithValue("$eventId", eventId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    private static async ValueTask<AuditEventRecord?> ReadByIdempotencyKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Projection} FROM audit_events WHERE idempotency_key = $key;";
        command.Parameters.AddWithValue("$key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    private static async ValueTask<long> NextSequenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(sequence_number), 0) + 1 FROM audit_events;";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuditEventRecord record,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO audit_events
            (audit_event_id, event_kind, actor_id, subject_type, subject_id,
             safe_payload_json, occurred_utc, sequence_number, correlation_id, idempotency_key)
            VALUES ($eventId, $kind, $actor, $subjectType, $subjectId,
                    $payload, $occurredAt, $sequence, $correlation, $idempotencyKey);
            """;
        command.Parameters.AddWithValue("$eventId", record.EventId.ToString("D"));
        command.Parameters.AddWithValue("$kind", record.EventKind);
        command.Parameters.AddWithValue("$actor", (object?)record.ActorId ?? DBNull.Value);
        command.Parameters.AddWithValue("$subjectType", (object?)record.SubjectType ?? DBNull.Value);
        command.Parameters.AddWithValue("$subjectId", (object?)record.SubjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload", record.SafePayloadJson);
        command.Parameters.AddWithValue(
            "$occurredAt",
            SyncPersistenceUtilities.FormatTimestamp(record.OccurredAtUtc));
        command.Parameters.AddWithValue("$sequence", record.SequenceNumber);
        command.Parameters.AddWithValue("$correlation", (object?)record.CorrelationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$idempotencyKey", record.IdempotencyKey);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static AuditEventRecord Read(SqliteDataReader reader) => new(
        SyncPersistenceUtilities.ParseGuid(reader.GetString(0), "audit event ID"),
        reader.GetInt64(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetString(6),
        SyncPersistenceUtilities.ParseTimestamp(reader.GetString(7), "audit occurrence time"),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.GetString(9));

    private static AuditEventRecord FromDraft(AuditEventDraft draft, long sequenceNumber) => new(
        draft.EventId,
        sequenceNumber,
        draft.EventKind,
        draft.ActorId,
        draft.SubjectType,
        draft.SubjectId,
        draft.SafePayloadJson,
        draft.OccurredAtUtc,
        draft.CorrelationId,
        draft.IdempotencyKey);

    private static bool IsEquivalent(AuditEventRecord record, AuditEventDraft draft) =>
        record.EventId == draft.EventId &&
        string.Equals(record.EventKind, draft.EventKind, StringComparison.Ordinal) &&
        string.Equals(record.ActorId, draft.ActorId, StringComparison.Ordinal) &&
        string.Equals(record.SubjectType, draft.SubjectType, StringComparison.Ordinal) &&
        string.Equals(record.SubjectId, draft.SubjectId, StringComparison.Ordinal) &&
        string.Equals(record.SafePayloadJson, draft.SafePayloadJson, StringComparison.Ordinal) &&
        record.OccurredAtUtc == draft.OccurredAtUtc &&
        string.Equals(record.CorrelationId, draft.CorrelationId, StringComparison.Ordinal) &&
        string.Equals(record.IdempotencyKey, draft.IdempotencyKey, StringComparison.Ordinal);

    private static void ValidateDraft(AuditEventDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        EnsureEventId(draft.EventId);
        SyncPersistenceUtilities.ValidateText(
            draft.EventKind,
            nameof(draft),
            SyncPersistenceUtilities.MaximumKindLength);
        _ = SyncPersistenceUtilities.ValidateOptionalText(draft.ActorId, nameof(draft), 256);
        _ = SyncPersistenceUtilities.ValidateOptionalText(draft.SubjectType, nameof(draft), 128);
        _ = SyncPersistenceUtilities.ValidateOptionalText(draft.SubjectId, nameof(draft));
        SyncPersistenceUtilities.ValidateSafeJsonObject(draft.SafePayloadJson, nameof(draft));
        SyncPersistenceUtilities.ValidateUtc(draft.OccurredAtUtc, nameof(draft));
        _ = SyncPersistenceUtilities.ValidateOptionalText(draft.CorrelationId, nameof(draft), 256);
        SyncPersistenceUtilities.ValidateText(draft.IdempotencyKey, nameof(draft), 256);
    }

    private static void EnsureEventId(Guid eventId)
    {
        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("An audit event ID is required.", nameof(eventId));
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

    private static SyncPersistenceResult<AuditEventRecord> Rejected(
        SyncPersistenceMutationStatus status) => new(status, null);

    private const string Projection = """
        audit_event_id, sequence_number, event_kind, actor_id, subject_type,
        subject_id, safe_payload_json, occurred_utc, correlation_id, idempotency_key
        """;
}
