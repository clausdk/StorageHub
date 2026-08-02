using Microsoft.Data.Sqlite;
using StorageHub.Sync.Persistence;

namespace StorageHub.Persistence.Sync;

public sealed class SqliteReliableOutboxStore(SingleWriterSqliteDatabase database) : IReliableOutboxStore
{
    private const int MaximumClaimCount = 100;
    private static readonly TimeSpan MaximumLeaseDuration = TimeSpan.FromHours(1);
    private readonly SingleWriterSqliteDatabase _database =
        database ?? throw new ArgumentNullException(nameof(database));

    public async ValueTask<OutboxEventRecord?> GetAsync(
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        EnsureEventId(eventId);
        await using var connection = await _database
            .OpenReadConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ReadAsync(connection, null, eventId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SyncPersistenceResult<OutboxEventRecord>> EnqueueAsync(
        OutboxEventDraft draft,
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
            var result = await EnqueueCoreAsync(
                writer.Connection,
                transaction,
                draft,
                cancellationToken).ConfigureAwait(false);
            if (result.Status == SyncPersistenceMutationStatus.Applied)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }

            return result;
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<IReadOnlyList<OutboxDeliveryLease>> ClaimPendingAsync(
        string ownerId,
        int maximumCount,
        DateTimeOffset observedAtUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default) =>
        await ClaimPendingCoreAsync(
            ownerId,
            eventKinds: null,
            maximumCount,
            observedAtUtc,
            leaseDuration,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<OutboxDeliveryLease>> ClaimPendingByKindsAsync(
        string ownerId,
        IReadOnlyCollection<string> eventKinds,
        int maximumCount,
        DateTimeOffset observedAtUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventKinds);
        if (eventKinds.Count is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(eventKinds));
        }

        var kinds = eventKinds.Distinct(StringComparer.Ordinal).ToArray();
        if (kinds.Length != eventKinds.Count)
        {
            throw new ArgumentException("Outbox event kinds must be unique.", nameof(eventKinds));
        }

        foreach (var kind in kinds)
        {
            SyncPersistenceUtilities.ValidateText(
                kind,
                nameof(eventKinds),
                SyncPersistenceUtilities.MaximumKindLength);
        }

        return await ClaimPendingCoreAsync(
            ownerId,
            kinds,
            maximumCount,
            observedAtUtc,
            leaseDuration,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<OutboxDeliveryLease>> ClaimPendingCoreAsync(
        string ownerId,
        string[]? eventKinds,
        int maximumCount,
        DateTimeOffset observedAtUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        SyncPersistenceUtilities.ValidateText(
            ownerId,
            nameof(ownerId),
            SyncPersistenceUtilities.MaximumOwnerLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumCount, MaximumClaimCount);

        SyncPersistenceUtilities.ValidateUtc(observedAtUtc, nameof(observedAtUtc));
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > MaximumLeaseDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        var expiresAtUtc = observedAtUtc.Add(leaseDuration);
        await using var writer = await _database
            .AcquireWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await writer.Connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var eventIds = new List<Guid>(maximumCount);
            await using (var select = writer.Connection.CreateCommand())
            {
                select.Transaction = transaction;
                var kindParameters = eventKinds is null
                    ? string.Empty
                    : string.Join(", ", eventKinds.Select((_, index) => $"$kind{index}"));
                select.CommandText = $"""
                    SELECT outbox_event_id
                    FROM outbox_events
                    WHERE dispatched_utc IS NULL AND dead_lettered_utc IS NULL
                      AND (next_attempt_utc IS NULL OR next_attempt_utc <= $observedAt)
                      AND (claim_id IS NULL OR claim_expires_utc <= $observedAt)
                      {(eventKinds is null ? string.Empty : $"AND event_kind IN ({kindParameters})")}
                    ORDER BY COALESCE(next_attempt_utc, created_utc), created_utc, outbox_event_id
                    LIMIT $limit;
                    """;
                if (eventKinds is not null)
                {
                    for (var index = 0; index < eventKinds.Length; index++)
                    {
                        select.Parameters.AddWithValue($"$kind{index}", eventKinds[index]);
                    }
                }

                select.Parameters.AddWithValue(
                    "$observedAt",
                    SyncPersistenceUtilities.FormatTimestamp(observedAtUtc));
                select.Parameters.AddWithValue("$limit", maximumCount);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    eventIds.Add(SyncPersistenceUtilities.ParseGuid(reader.GetString(0), "outbox event ID"));
                }
            }

            var leases = new List<OutboxDeliveryLease>(eventIds.Count);
            foreach (var eventId in eventIds)
            {
                var claimId = Guid.NewGuid();
                await using var update = writer.Connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = $"""
                    UPDATE outbox_events
                    SET delivery_revision = delivery_revision + 1,
                        attempt_count = attempt_count + 1,
                        claim_id = $claimId,
                        claimed_by = $owner,
                        claim_acquired_utc = $observedAt,
                        claim_expires_utc = $expiresAt
                    WHERE outbox_event_id = $eventId
                      AND dispatched_utc IS NULL AND dead_lettered_utc IS NULL
                      AND (next_attempt_utc IS NULL OR next_attempt_utc <= $observedAt)
                      AND (claim_id IS NULL OR claim_expires_utc <= $observedAt)
                    RETURNING {Projection};
                    """;
                update.Parameters.AddWithValue("$claimId", claimId.ToString("D"));
                update.Parameters.AddWithValue("$owner", ownerId);
                update.Parameters.AddWithValue(
                    "$observedAt",
                    SyncPersistenceUtilities.FormatTimestamp(observedAtUtc));
                update.Parameters.AddWithValue(
                    "$expiresAt",
                    SyncPersistenceUtilities.FormatTimestamp(expiresAtUtc));
                update.Parameters.AddWithValue("$eventId", eventId.ToString("D"));
                await using var reader = await update.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var record = Read(reader);
                    leases.Add(new OutboxDeliveryLease(
                        record,
                        claimId,
                        ownerId,
                        record.DeliveryRevision,
                        observedAtUtc,
                        expiresAtUtc));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return leases;
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<SyncPersistenceResult<OutboxDeliveryLease>> RenewAsync(
        OutboxDeliveryLease lease,
        DateTimeOffset renewedAtUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ValidateLease(lease);
        SyncPersistenceUtilities.ValidateUtc(renewedAtUtc, nameof(renewedAtUtc));
        if (renewedAtUtc < lease.AcquiredAtUtc ||
            leaseDuration <= TimeSpan.Zero || leaseDuration > MaximumLeaseDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        var expiresAtUtc = renewedAtUtc.Add(leaseDuration);
        await using var writer = await _database.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = writer.Connection.BeginTransaction(deferred: false);
        try
        {
            OutboxEventRecord? record;
            await using (var command = writer.Connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"""
                    UPDATE outbox_events
                    SET claim_expires_utc = $expiresAt
                    WHERE outbox_event_id = $eventId
                      AND claim_id = $claimId AND claimed_by = $owner
                      AND delivery_revision = $fence
                      AND claim_expires_utc > $renewedAt
                      AND claim_expires_utc < $expiresAt
                      AND dispatched_utc IS NULL AND dead_lettered_utc IS NULL
                    RETURNING {Projection};
                    """;
                command.Parameters.AddWithValue(
                    "$renewedAt",
                    SyncPersistenceUtilities.FormatTimestamp(renewedAtUtc));
                command.Parameters.AddWithValue(
                    "$expiresAt",
                    SyncPersistenceUtilities.FormatTimestamp(expiresAtUtc));
                AddLeaseParameters(command, lease);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                record = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                    ? Read(reader)
                    : null;
            }

            if (record is null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new SyncPersistenceResult<OutboxDeliveryLease>(
                    SyncPersistenceMutationStatus.StaleLease,
                    null);
            }

            var renewed = new OutboxDeliveryLease(
                record,
                lease.ClaimId,
                lease.OwnerId,
                lease.FencingToken,
                lease.AcquiredAtUtc,
                expiresAtUtc);
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return new SyncPersistenceResult<OutboxDeliveryLease>(
                SyncPersistenceMutationStatus.Applied,
                renewed);
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<SyncPersistenceMutationStatus> CompleteAsync(
        OutboxDeliveryLease lease,
        DateTimeOffset dispatchedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateLease(lease);
        SyncPersistenceUtilities.ValidateUtc(dispatchedAtUtc, nameof(dispatchedAtUtc));
        if (dispatchedAtUtc < lease.AcquiredAtUtc)
        {
            throw new ArgumentException("Dispatch completion cannot precede lease acquisition.", nameof(dispatchedAtUtc));
        }
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
                lease.Event.EventId,
                cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return SyncPersistenceMutationStatus.NotFound;
            }

            if (current.DispatchedAtUtc is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return current.DispatchedAtUtc == dispatchedAtUtc
                    ? SyncPersistenceMutationStatus.AlreadyApplied
                    : SyncPersistenceMutationStatus.Conflict;
            }

            if (current.DeadLetteredAtUtc is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return SyncPersistenceMutationStatus.Conflict;
            }

            var affected = await CompleteCoreAsync(
                writer.Connection,
                transaction,
                lease,
                dispatchedAtUtc,
                cancellationToken).ConfigureAwait(false);
            if (affected != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return SyncPersistenceMutationStatus.StaleLease;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return SyncPersistenceMutationStatus.Applied;
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<SyncPersistenceMutationStatus> FailAsync(
        OutboxDeliveryLease lease,
        DateTimeOffset failedAtUtc,
        DateTimeOffset nextAttemptAtUtc,
        string errorCode,
        string safeErrorSummary,
        bool deadLetter,
        CancellationToken cancellationToken = default)
    {
        ValidateLease(lease);
        SyncPersistenceUtilities.ValidateUtc(failedAtUtc, nameof(failedAtUtc));
        SyncPersistenceUtilities.ValidateUtc(nextAttemptAtUtc, nameof(nextAttemptAtUtc));
        if (failedAtUtc < lease.AcquiredAtUtc || nextAttemptAtUtc < failedAtUtc)
        {
            throw new ArgumentException(
                "Failure and retry timestamps must follow lease acquisition in order.",
                nameof(nextAttemptAtUtc));
        }

        SyncPersistenceUtilities.ValidateText(
            errorCode,
            nameof(errorCode),
            SyncPersistenceUtilities.MaximumKindLength);
        SyncPersistenceUtilities.ValidateText(safeErrorSummary, nameof(safeErrorSummary), 2_048);
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
                lease.Event.EventId,
                cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return SyncPersistenceMutationStatus.NotFound;
            }

            if (current.DeadLetteredAtUtc is not null && deadLetter &&
                string.Equals(current.LastErrorCode, errorCode, StringComparison.Ordinal) &&
                string.Equals(current.LastErrorSummary, safeErrorSummary, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return SyncPersistenceMutationStatus.AlreadyApplied;
            }

            if (current.DispatchedAtUtc is not null || current.DeadLetteredAtUtc is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return SyncPersistenceMutationStatus.Conflict;
            }

            await using var command = writer.Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE outbox_events
                SET next_attempt_utc = CASE WHEN $deadLetter = 1 THEN NULL ELSE $nextAttempt END,
                    dead_lettered_utc = CASE WHEN $deadLetter = 1 THEN $failedAt ELSE NULL END,
                    last_error_code = $errorCode,
                    last_error_summary = $errorSummary,
                    claim_id = NULL,
                    claimed_by = NULL,
                    claim_acquired_utc = NULL,
                    claim_expires_utc = NULL
                WHERE outbox_event_id = $eventId
                  AND claim_id = $claimId AND claimed_by = $owner
                  AND delivery_revision = $fence
                  AND claim_expires_utc > $failedAt
                  AND dispatched_utc IS NULL AND dead_lettered_utc IS NULL;
                """;
            command.Parameters.AddWithValue("$deadLetter", deadLetter ? 1 : 0);
            command.Parameters.AddWithValue(
                "$nextAttempt",
                SyncPersistenceUtilities.FormatTimestamp(nextAttemptAtUtc));
            command.Parameters.AddWithValue("$failedAt", SyncPersistenceUtilities.FormatTimestamp(failedAtUtc));
            command.Parameters.AddWithValue("$errorCode", errorCode);
            command.Parameters.AddWithValue("$errorSummary", safeErrorSummary);
            AddLeaseParameters(command, lease);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return SyncPersistenceMutationStatus.StaleLease;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return SyncPersistenceMutationStatus.Applied;
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    internal static async ValueTask<SyncPersistenceResult<OutboxEventRecord>> EnqueueCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        OutboxEventDraft draft,
        CancellationToken cancellationToken)
    {
        ValidateDraft(draft);
        var existing = await ReadAsync(connection, transaction, draft.EventId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            var identical = IsEquivalent(existing, draft);
            return new SyncPersistenceResult<OutboxEventRecord>(
                identical
                    ? SyncPersistenceMutationStatus.AlreadyApplied
                    : SyncPersistenceMutationStatus.Conflict,
                identical ? existing : null);
        }

        if (await AggregateSequenceExistsAsync(
                connection,
                transaction,
                draft.AggregateId,
                draft.SequenceNumber,
                cancellationToken).ConfigureAwait(false))
        {
            return new SyncPersistenceResult<OutboxEventRecord>(
                SyncPersistenceMutationStatus.Conflict,
                null);
        }

        await InsertAsync(connection, transaction, draft, cancellationToken).ConfigureAwait(false);
        return new SyncPersistenceResult<OutboxEventRecord>(
            SyncPersistenceMutationStatus.Applied,
            FromDraft(draft));
    }

    internal static async ValueTask<OutboxEventRecord?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Projection} FROM outbox_events WHERE outbox_event_id = $eventId;";
        command.Parameters.AddWithValue("$eventId", eventId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    internal static bool IsEquivalent(OutboxEventRecord record, OutboxEventDraft draft) =>
        record.EventId == draft.EventId &&
        string.Equals(record.EventKind, draft.EventKind, StringComparison.Ordinal) &&
        string.Equals(record.AggregateId, draft.AggregateId, StringComparison.Ordinal) &&
        record.SequenceNumber == draft.SequenceNumber &&
        string.Equals(record.SafePayloadJson, draft.SafePayloadJson, StringComparison.Ordinal) &&
        record.CreatedAtUtc == draft.CreatedAtUtc;

    internal static void ValidateDraft(OutboxEventDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        EnsureEventId(draft.EventId);
        SyncPersistenceUtilities.ValidateText(
            draft.EventKind,
            nameof(draft),
            SyncPersistenceUtilities.MaximumKindLength);
        SyncPersistenceUtilities.ValidateText(draft.AggregateId, nameof(draft));
        ArgumentOutOfRangeException.ThrowIfNegative(draft.SequenceNumber);
        SyncPersistenceUtilities.ValidateSafeJsonObject(draft.SafePayloadJson, nameof(draft));
        SyncPersistenceUtilities.ValidateUtc(draft.CreatedAtUtc, nameof(draft));
    }

    private static async Task<int> CompleteCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        OutboxDeliveryLease lease,
        DateTimeOffset dispatchedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE outbox_events
            SET dispatched_utc = $dispatchedAt,
                claim_id = NULL,
                claimed_by = NULL,
                claim_acquired_utc = NULL,
                claim_expires_utc = NULL,
                next_attempt_utc = NULL,
                last_error_code = NULL,
                last_error_summary = NULL
            WHERE outbox_event_id = $eventId
              AND claim_id = $claimId AND claimed_by = $owner
              AND delivery_revision = $fence
              AND claim_expires_utc > $dispatchedAt
              AND dispatched_utc IS NULL AND dead_lettered_utc IS NULL;
            """;
        command.Parameters.AddWithValue(
            "$dispatchedAt",
            SyncPersistenceUtilities.FormatTimestamp(dispatchedAtUtc));
        AddLeaseParameters(command, lease);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        OutboxEventDraft draft,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO outbox_events
            (outbox_event_id, event_kind, aggregate_id, safe_payload_json, sequence_number, created_utc)
            VALUES ($eventId, $kind, $aggregate, $payload, $sequence, $createdAt);
            """;
        command.Parameters.AddWithValue("$eventId", draft.EventId.ToString("D"));
        command.Parameters.AddWithValue("$kind", draft.EventKind);
        command.Parameters.AddWithValue("$aggregate", draft.AggregateId);
        command.Parameters.AddWithValue("$payload", draft.SafePayloadJson);
        command.Parameters.AddWithValue("$sequence", draft.SequenceNumber);
        command.Parameters.AddWithValue(
            "$createdAt",
            SyncPersistenceUtilities.FormatTimestamp(draft.CreatedAtUtc));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<bool> AggregateSequenceExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string aggregateId,
        long sequenceNumber,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM outbox_events
            WHERE aggregate_id = $aggregate AND sequence_number = $sequence;
            """;
        command.Parameters.AddWithValue("$aggregate", aggregateId);
        command.Parameters.AddWithValue("$sequence", sequenceNumber);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static OutboxEventRecord Read(SqliteDataReader reader) => new(
        SyncPersistenceUtilities.ParseGuid(reader.GetString(0), "outbox event ID"),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetInt64(3),
        reader.GetString(4),
        SyncPersistenceUtilities.ParseTimestamp(reader.GetString(5), "outbox creation time"),
        ReadNullableTimestamp(reader, 6, "outbox dispatch time"),
        ReadNullableTimestamp(reader, 7, "outbox dead-letter time"),
        reader.GetInt32(8),
        reader.GetInt64(9),
        ReadNullableTimestamp(reader, 10, "outbox next-attempt time"),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetString(12));

    private static OutboxEventRecord FromDraft(OutboxEventDraft draft) => new(
        draft.EventId,
        draft.EventKind,
        draft.AggregateId,
        draft.SequenceNumber,
        draft.SafePayloadJson,
        draft.CreatedAtUtc,
        null,
        null,
        0,
        0,
        null,
        null,
        null);

    private static DateTimeOffset? ReadNullableTimestamp(
        SqliteDataReader reader,
        int ordinal,
        string description) => reader.IsDBNull(ordinal)
        ? null
        : SyncPersistenceUtilities.ParseTimestamp(reader.GetString(ordinal), description);

    private static void AddLeaseParameters(SqliteCommand command, OutboxDeliveryLease lease)
    {
        command.Parameters.AddWithValue("$eventId", lease.Event.EventId.ToString("D"));
        command.Parameters.AddWithValue("$claimId", lease.ClaimId.ToString("D"));
        command.Parameters.AddWithValue("$owner", lease.OwnerId);
        command.Parameters.AddWithValue("$fence", lease.FencingToken);
    }

    private static void ValidateLease(OutboxDeliveryLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(lease.Event);
        EnsureEventId(lease.Event.EventId);
        if (lease.ClaimId == Guid.Empty || lease.FencingToken <= 0)
        {
            throw new ArgumentException("The outbox lease identity is invalid.", nameof(lease));
        }

        SyncPersistenceUtilities.ValidateText(
            lease.OwnerId,
            nameof(lease),
            SyncPersistenceUtilities.MaximumOwnerLength);
        SyncPersistenceUtilities.ValidateUtc(lease.AcquiredAtUtc, nameof(lease));
        SyncPersistenceUtilities.ValidateUtc(lease.ExpiresAtUtc, nameof(lease));
        if (lease.ExpiresAtUtc <= lease.AcquiredAtUtc ||
            lease.Event.DeliveryRevision != lease.FencingToken)
        {
            throw new ArgumentException("The outbox lease interval or fencing token is invalid.", nameof(lease));
        }
    }

    private static void EnsureEventId(Guid eventId)
    {
        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("An outbox event ID is required.", nameof(eventId));
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

    private const string Projection = """
        outbox_event_id, event_kind, aggregate_id, sequence_number, safe_payload_json,
        created_utc, dispatched_utc, dead_lettered_utc, attempt_count, delivery_revision,
        next_attempt_utc, last_error_code, last_error_summary
        """;
}
