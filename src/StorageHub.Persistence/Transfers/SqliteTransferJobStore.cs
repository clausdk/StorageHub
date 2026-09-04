using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Abstractions;
using StorageHub.Transfers;

namespace StorageHub.Persistence.Transfers;

/// <summary>
/// Durable SQLite transfer queue. Every owner mutation is fenced by the monotonically increasing
/// owner epoch and by lease expiry; state and checkpoint updates additionally use revision CAS.
/// </summary>
public sealed class SqliteTransferJobStore : ITransferJobStore, ITransferQueueQueryStore, ITransferHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SingleWriterSqliteDatabase _database;

    public SqliteTransferJobStore(SingleWriterSqliteDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async ValueTask<bool> TryEnqueueAsync(
        TransferIntent intent,
        int priority = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var intentJson = SerializeIntent(intent);
        await using var writer = await _database
            .AcquireWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = writer.Connection.CreateCommand();
        command.CommandText = """
            INSERT INTO transfer_jobs
            (
                transfer_job_id, source_profile_id, destination_profile_id,
                source_path, destination_path, operation_kind, state, priority,
                expected_size, created_utc, updated_utc, intent_json,
                state_revision, attempt_number, owner_epoch
            )
            VALUES
            (
                $jobId, $sourceProfile, $destinationProfile,
                $sourcePath, $destinationPath, $operation, $state, $priority,
                $expectedSize, $createdAt, $createdAt, $intentJson,
                0, 0, 0
            )
            ON CONFLICT(transfer_job_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$jobId", intent.TransferJobId.ToString());
        command.Parameters.AddWithValue("$sourceProfile", intent.Source.ProfileId.ToString());
        command.Parameters.AddWithValue("$destinationProfile", intent.Destination.ProfileId.ToString());
        command.Parameters.AddWithValue("$sourcePath", intent.Source.CanonicalRelativePath);
        command.Parameters.AddWithValue("$destinationPath", intent.Destination.CanonicalRelativePath);
        command.Parameters.AddWithValue("$operation", FormatEnum(intent.Operation));
        command.Parameters.AddWithValue("$state", FormatEnum(TransferState.Pending));
        command.Parameters.AddWithValue("$priority", priority);
        command.Parameters.AddWithValue("$expectedSize", (object?)intent.ExpectedLength ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", FormatTimestamp(intent.CreatedAtUtc));
        command.Parameters.AddWithValue("$intentJson", intentJson);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async ValueTask<DurableTransferJob?> FindAsync(
        TransferJobId transferJobId,
        CancellationToken cancellationToken = default)
    {
        EnsureJobId(transferJobId);
        await using var connection = await _database
            .OpenReadConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await FindJobCoreAsync(
            connection,
            transaction: null,
            transferJobId,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<TransferQueuePage> ListAsync(
        TransferQueueQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var connection = await _database
            .OpenReadConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var stateParameters = new string[query.States.Count];
        for (var index = 0; index < query.States.Count; index++)
        {
            var parameterName = $"$state{index.ToString(CultureInfo.InvariantCulture)}";
            stateParameters[index] = parameterName;
            command.Parameters.AddWithValue(parameterName, FormatEnum(query.States[index]));
        }

        command.CommandText = $"""
            SELECT {JobProjection}
            FROM transfer_jobs
            WHERE state IN ({string.Join(", ", stateParameters)})
              AND
              (
                  $cursorTime IS NULL
                  OR updated_utc < $cursorTime
                  OR (updated_utc = $cursorTime AND transfer_job_id < $cursorId)
              )
            ORDER BY updated_utc DESC, transfer_job_id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue(
            "$cursorTime",
            query.Cursor is null ? DBNull.Value : FormatTimestamp(query.Cursor.TransitionedAtUtc));
        command.Parameters.AddWithValue(
            "$cursorId",
            query.Cursor is null ? DBNull.Value : query.Cursor.TransferJobId.ToString());
        command.Parameters.AddWithValue("$limit", checked(query.PageSize + 1));
        var jobs = new List<DurableTransferJob>(query.PageSize + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            jobs.Add(ReadJob(reader));
        }

        TransferQueueCursor? continuation = null;
        if (jobs.Count > query.PageSize)
        {
            jobs.RemoveAt(jobs.Count - 1);
            var last = jobs[^1];
            continuation = new TransferQueueCursor(
                last.State.TransitionedAtUtc,
                last.Intent.TransferJobId);
        }

        return new TransferQueuePage(jobs, continuation);
    }

    public async ValueTask<IReadOnlyDictionary<TransferState, int>> CountByStateAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT state, COUNT(*) FROM transfer_jobs GROUP BY state;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var counts = new Dictionary<TransferState, int>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var state = ParseEnum<TransferState>(reader.GetString(0), "transfer state");
            counts[state] = reader.GetInt32(1);
        }
        return counts;
    }

    public async ValueTask<int> ClearTerminalHistoryAsync(
        IReadOnlyCollection<TransferJobId>? transferJobIds = null,
        CancellationToken cancellationToken = default)
    {
        if (transferJobIds?.Any(id => id.IsEmpty) == true)
            throw new ArgumentException("Transfer history IDs cannot be empty.", nameof(transferJobIds));
        await using var writer = await _database.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);
        await using var command = writer.Connection.CreateCommand();
        var idFilter = string.Empty;
        if (transferJobIds is { Count: > 0 })
        {
            var parameters = transferJobIds.Select((id, index) =>
            {
                var name = $"$id{index.ToString(CultureInfo.InvariantCulture)}";
                command.Parameters.AddWithValue(name, id.ToString());
                return name;
            }).ToArray();
            idFilter = $" AND transfer_job_id IN ({string.Join(", ", parameters)})";
        }
        command.CommandText = $"""
            DELETE FROM transfer_jobs
            WHERE state IN ($completed, $cancelled, $failed){idFilter};
            """;
        command.Parameters.AddWithValue("$completed", FormatEnum(TransferState.Completed));
        command.Parameters.AddWithValue("$cancelled", FormatEnum(TransferState.Cancelled));
        command.Parameters.AddWithValue("$failed", FormatEnum(TransferState.Failed));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<TransferJobClaim?> TryClaimNextAsync(
        TransferClaimRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var expiresAtUtc = request.ObservedAtUtc.Add(request.LeaseDuration);
        await using var writer = await _database
            .AcquireWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = writer.Connection.BeginTransaction(deferred: false);
        try
        {
            DurableTransferJob? job;
            await using (var command = writer.Connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"""
                    UPDATE transfer_jobs
                    SET
                        state = $preparing,
                        state_revision = state_revision + 1,
                        attempt_number = attempt_number + 1,
                        status_code = NULL,
                        retry_not_before_utc = NULL,
                        updated_utc = $observedAt,
                        owner_epoch = owner_epoch + 1,
                        claimed_by = $owner,
                        claim_acquired_utc = $observedAt,
                        claim_expires_utc = $expiresAt,
                        last_error_code = NULL,
                        last_error_summary = NULL
                    WHERE transfer_job_id =
                    (
                        SELECT transfer_job_id
                        FROM transfer_jobs
                        WHERE
                            claimed_by IS NULL
                            AND
                            (
                                state = $pending
                                OR
                                (
                                    state = $retrying
                                    AND retry_not_before_utc IS NOT NULL
                                    AND retry_not_before_utc <= $observedAt
                                )
                            )
                        ORDER BY priority DESC, created_utc, transfer_job_id
                        LIMIT 1
                    )
                    RETURNING {JobProjection};
                    """;
                command.Parameters.AddWithValue("$preparing", FormatEnum(TransferState.Preparing));
                command.Parameters.AddWithValue("$pending", FormatEnum(TransferState.Pending));
                command.Parameters.AddWithValue("$retrying", FormatEnum(TransferState.Retrying));
                command.Parameters.AddWithValue("$observedAt", FormatTimestamp(request.ObservedAtUtc));
                command.Parameters.AddWithValue("$expiresAt", FormatTimestamp(expiresAtUtc));
                command.Parameters.AddWithValue("$owner", request.OwnerId);
                await using var reader = await command
                    .ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return null;
                }

                job = ReadJob(reader);
            }

            var lease = job.ActiveLease ?? throw Corrupt("A claimed transfer has no lease metadata.");
            await InsertAttemptAsync(
                writer.Connection,
                transaction,
                lease,
                request.ObservedAtUtc,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new TransferJobClaim(job, lease);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<TransferStoreResult<TransferJobLease>> TryRenewLeaseAsync(
        TransferLeaseRenewal renewal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(renewal);
        await using var writer = await _database
            .AcquireWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = writer.Connection.CreateCommand();
        command.CommandText = """
            UPDATE transfer_jobs
            SET claim_expires_utc = $expiresAt
            WHERE
                transfer_job_id = $jobId
                AND claimed_by = $owner
                AND owner_epoch = $fence
                AND attempt_number = $attempt
                AND claim_expires_utc > $renewedAt
                AND claim_expires_utc < $expiresAt
            RETURNING claim_acquired_utc;
            """;
        command.Parameters.AddWithValue("$expiresAt", FormatTimestamp(renewal.ExpiresAtUtc));
        command.Parameters.AddWithValue("$renewedAt", FormatTimestamp(renewal.RenewedAtUtc));
        AddLeaseIdentityParameters(command, renewal.Lease);
        var acquiredValue = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (acquiredValue is string acquiredText)
        {
            return Applied(new TransferJobLease(
                renewal.Lease.TransferJobId,
                renewal.Lease.OwnerId,
                renewal.Lease.FencingToken,
                renewal.Lease.Attempt,
                ParseTimestamp(acquiredText, "claim acquisition"),
                renewal.ExpiresAtUtc));
        }

        return await JobExistsAsync(writer.Connection, null, renewal.Lease.TransferJobId, cancellationToken)
            .ConfigureAwait(false)
            ? Rejected<TransferJobLease>(TransferStoreMutationStatus.LeaseLost)
            : Rejected<TransferJobLease>(TransferStoreMutationStatus.NotFound);
    }

    public async ValueTask<TransferStoreResult<DurableTransferJob>> TryTransitionAsync(
        TransferStateTransitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var writer = await _database
            .AcquireWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = writer.Connection.BeginTransaction(deferred: false);
        try
        {
            var current = await FindJobCoreAsync(
                writer.Connection,
                transaction,
                request.Lease.TransferJobId,
                cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Rejected<DurableTransferJob>(TransferStoreMutationStatus.NotFound);
            }

            if (!LeaseMatches(current.ActiveLease, request.Lease, request.TransitionedAtUtc))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Rejected<DurableTransferJob>(TransferStoreMutationStatus.LeaseLost);
            }

            if (current.State.Revision != request.ExpectedRevision)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Rejected<DurableTransferJob>(TransferStoreMutationStatus.Conflict);
            }

            var nextState = TransferStateMachine.Transition(
                current.State,
                request.NextState,
                request.TransitionedAtUtc,
                request.StatusCode);
            var releaseLease = !RequiresLease(nextState.State);
            await using (var command = writer.Connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE transfer_jobs
                    SET
                        state = $state,
                        state_revision = $nextRevision,
                        status_code = $statusCode,
                        retry_not_before_utc = $retryAt,
                        updated_utc = $transitionedAt,
                        claimed_by = CASE WHEN $releaseLease = 1 THEN NULL ELSE claimed_by END,
                        claim_acquired_utc = CASE WHEN $releaseLease = 1 THEN NULL ELSE claim_acquired_utc END,
                        claim_expires_utc = CASE WHEN $releaseLease = 1 THEN NULL ELSE claim_expires_utc END,
                        last_error_code = $errorCode,
                        last_error_summary = $errorSummary
                    WHERE
                        transfer_job_id = $jobId
                        AND state_revision = $expectedRevision
                        AND claimed_by = $owner
                        AND owner_epoch = $fence
                        AND attempt_number = $attempt
                        AND claim_expires_utc > $transitionedAt;
                    """;
                command.Parameters.AddWithValue("$state", FormatEnum(nextState.State));
                command.Parameters.AddWithValue("$nextRevision", nextState.Revision);
                command.Parameters.AddWithValue(
                    "$statusCode",
                    nextState.StatusCode == TransferStatusCode.None
                        ? DBNull.Value
                        : FormatEnum(nextState.StatusCode));
                command.Parameters.AddWithValue(
                    "$retryAt",
                    request.RetryAvailableAtUtc is { } retryAt
                        ? FormatTimestamp(retryAt)
                        : DBNull.Value);
                command.Parameters.AddWithValue("$transitionedAt", FormatTimestamp(request.TransitionedAtUtc));
                command.Parameters.AddWithValue("$releaseLease", releaseLease ? 1 : 0);
                command.Parameters.AddWithValue("$errorCode", (object?)request.Error?.Code ?? DBNull.Value);
                command.Parameters.AddWithValue("$errorSummary", (object?)request.Error?.Summary ?? DBNull.Value);
                command.Parameters.AddWithValue("$expectedRevision", request.ExpectedRevision);
                AddLeaseIdentityParameters(command, request.Lease);
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return Rejected<DurableTransferJob>(TransferStoreMutationStatus.Conflict);
                }
            }

            if (releaseLease)
            {
                await CompleteAttemptAsync(
                    writer.Connection,
                    transaction,
                    request.Lease,
                    nextState.State,
                    request.TransitionedAtUtc,
                    request.Error,
                    cancellationToken).ConfigureAwait(false);
            }

            if (nextState.State is TransferState.Completed or TransferState.Cancelled)
            {
                await DeleteCheckpointCoreAsync(
                    writer.Connection,
                    transaction,
                    request.Lease.TransferJobId,
                    cancellationToken).ConfigureAwait(false);
            }

            var activeLease = releaseLease ? null : current.ActiveLease;
            var result = new DurableTransferJob(
                current.Intent,
                nextState,
                current.Priority,
                request.RetryAvailableAtUtc,
                activeLease,
                request.Error);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Applied(result);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<TransferStoreResult<DurableTransferJob>> TryTransitionControlStateAsync(
        TransferControlStateTransitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var writer = await _database
            .AcquireWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = writer.Connection.BeginTransaction(deferred: false);
        try
        {
            var current = await FindJobCoreAsync(
                writer.Connection,
                transaction,
                request.TransferJobId,
                cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Rejected<DurableTransferJob>(TransferStoreMutationStatus.NotFound);
            }

            if (current.ActiveLease is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Rejected<DurableTransferJob>(TransferStoreMutationStatus.Conflict);
            }

            if (current.State.Revision != request.ExpectedRevision)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Rejected<DurableTransferJob>(TransferStoreMutationStatus.Conflict);
            }

            var nextState = TransferStateMachine.Transition(
                current.State,
                request.NextState,
                request.TransitionedAtUtc,
                request.StatusCode);
            if (RequiresLease(nextState.State))
            {
                throw new InvalidOperationException(
                    "A control transition cannot enter an execution-owned state.");
            }

            var effectiveError = request.Error ??
                (nextState.StatusCode == TransferStatusCode.None ? null : current.LastError);
            await using (var command = writer.Connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE transfer_jobs
                    SET
                        state = $state,
                        state_revision = $nextRevision,
                        status_code = $statusCode,
                        retry_not_before_utc = NULL,
                        updated_utc = $transitionedAt,
                        last_error_code = $errorCode,
                        last_error_summary = $errorSummary
                    WHERE
                        transfer_job_id = $jobId
                        AND state_revision = $expectedRevision
                        AND claimed_by IS NULL;
                    """;
                command.Parameters.AddWithValue("$state", FormatEnum(nextState.State));
                command.Parameters.AddWithValue("$nextRevision", nextState.Revision);
                command.Parameters.AddWithValue(
                    "$statusCode",
                    nextState.StatusCode == TransferStatusCode.None
                        ? DBNull.Value
                        : FormatEnum(nextState.StatusCode));
                command.Parameters.AddWithValue("$transitionedAt", FormatTimestamp(request.TransitionedAtUtc));
                command.Parameters.AddWithValue("$errorCode", (object?)effectiveError?.Code ?? DBNull.Value);
                command.Parameters.AddWithValue("$errorSummary", (object?)effectiveError?.Summary ?? DBNull.Value);
                command.Parameters.AddWithValue("$jobId", request.TransferJobId.ToString());
                command.Parameters.AddWithValue("$expectedRevision", request.ExpectedRevision);
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return Rejected<DurableTransferJob>(TransferStoreMutationStatus.Conflict);
                }
            }

            if (nextState.State is TransferState.Completed or TransferState.Cancelled)
            {
                await DeleteCheckpointCoreAsync(
                    writer.Connection,
                    transaction,
                    request.TransferJobId,
                    cancellationToken).ConfigureAwait(false);
            }

            var result = new DurableTransferJob(
                current.Intent,
                nextState,
                current.Priority,
                retryAvailableAtUtc: null,
                activeLease: null,
                effectiveError);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Applied(result);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<PersistedTransferCheckpoint?> FindCheckpointAsync(
        TransferJobId transferJobId,
        CancellationToken cancellationToken = default)
    {
        EnsureJobId(transferJobId);
        await using var connection = await _database
            .OpenReadConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await FindCheckpointCoreAsync(
            connection,
            null,
            transferJobId,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<TransferStoreResult<PersistedTransferCheckpoint>> TrySaveCheckpointAsync(
        TransferCheckpointWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var writer = await _database
            .AcquireWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = writer.Connection.BeginTransaction(deferred: false);
        try
        {
            var job = await FindJobCoreAsync(
                writer.Connection,
                transaction,
                request.Lease.TransferJobId,
                cancellationToken).ConfigureAwait(false);
            if (job is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Rejected<PersistedTransferCheckpoint>(TransferStoreMutationStatus.NotFound);
            }

            if (!LeaseMatches(job.ActiveLease, request.Lease, request.Checkpoint.RecordedAtUtc))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Rejected<PersistedTransferCheckpoint>(TransferStoreMutationStatus.LeaseLost);
            }

            var existing = await FindCheckpointCoreAsync(
                writer.Connection,
                transaction,
                request.Lease.TransferJobId,
                cancellationToken).ConfigureAwait(false);
            if (!CheckpointExpectationMatches(existing, request.ExpectedVersion))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Rejected<PersistedTransferCheckpoint>(TransferStoreMutationStatus.Conflict);
            }

            EnsureCheckpointProgressIsMonotonic(existing, request.Checkpoint);
            var nextVersion = checked((request.ExpectedVersion ?? 0) + 1);
            var completedPartsJson = SerializeCompletedParts(request.Checkpoint.CompletedParts);
            var stateJson = SerializeCheckpointState(request.Checkpoint);
            await using (var command = writer.Connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = existing is null
                    ? """
                        INSERT INTO transfer_checkpoints
                        (
                            transfer_job_id, checkpoint_version, source_identity,
                            destination_identity, expected_size, verified_offset,
                            temporary_name, multipart_upload_id, completed_parts_json,
                            non_secret_state_json, updated_utc
                        )
                        VALUES
                        (
                            $jobId, $version, $sourceIdentity,
                            $destinationIdentity, $expectedSize, $verifiedOffset,
                            $temporaryName, $resumeId, $completedParts,
                            $state, $updatedAt
                        );
                        """
                    : """
                        UPDATE transfer_checkpoints
                        SET
                            checkpoint_version = $version,
                            source_identity = $sourceIdentity,
                            destination_identity = $destinationIdentity,
                            expected_size = $expectedSize,
                            verified_offset = $verifiedOffset,
                            temporary_name = $temporaryName,
                            multipart_upload_id = $resumeId,
                            completed_parts_json = $completedParts,
                            non_secret_state_json = $state,
                            updated_utc = $updatedAt
                        WHERE transfer_job_id = $jobId AND checkpoint_version = $expectedVersion;
                        """;
                AddCheckpointParameters(command, request.Checkpoint, nextVersion, completedPartsJson, stateJson);
                command.Parameters.AddWithValue("$expectedVersion", (object?)request.ExpectedVersion ?? DBNull.Value);
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return Rejected<PersistedTransferCheckpoint>(TransferStoreMutationStatus.Conflict);
                }
            }

            var saved = new PersistedTransferCheckpoint(nextVersion, request.Checkpoint);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Applied(saved);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<TransferStoreMutationStatus> TryClearCheckpointAsync(
        TransferCheckpointClearRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var writer = await _database
            .AcquireWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = writer.Connection.BeginTransaction(deferred: false);
        try
        {
            var job = await FindJobCoreAsync(
                writer.Connection,
                transaction,
                request.Lease.TransferJobId,
                cancellationToken).ConfigureAwait(false);
            if (job is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return TransferStoreMutationStatus.NotFound;
            }

            if (!LeaseMatches(job.ActiveLease, request.Lease, request.ObservedAtUtc))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return TransferStoreMutationStatus.LeaseLost;
            }

            await using var command = writer.Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM transfer_checkpoints
                WHERE transfer_job_id = $jobId AND checkpoint_version = $version;
                """;
            command.Parameters.AddWithValue("$jobId", request.Lease.TransferJobId.ToString());
            command.Parameters.AddWithValue("$version", request.ExpectedVersion);
            var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            var result = changed == 1
                ? TransferStoreMutationStatus.Applied
                : await CheckpointExistsAsync(
                    writer.Connection,
                    transaction,
                    request.Lease.TransferJobId,
                    cancellationToken).ConfigureAwait(false)
                    ? TransferStoreMutationStatus.Conflict
                    : TransferStoreMutationStatus.NotFound;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<int> RecoverInterruptedAsync(
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateUtc(observedAtUtc, nameof(observedAtUtc));
        await using var writer = await _database
            .AcquireWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = writer.Connection.BeginTransaction(deferred: false);
        try
        {
            var interrupted = new List<(TransferJobId JobId, int Attempt)>();
            await using (var command = writer.Connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE transfer_jobs
                    SET
                        state = $interrupted,
                        state_revision = state_revision + 1,
                        status_code = $status,
                        updated_utc = $observedAt,
                        claimed_by = NULL,
                        claim_acquired_utc = NULL,
                        claim_expires_utc = NULL,
                        retry_not_before_utc = NULL,
                        last_error_code = 'transfer.owner.interrupted',
                        last_error_summary = 'The prior transfer owner stopped before recording completion.'
                    WHERE
                        state IN ($preparing, $connecting, $transferring, $verifying, $finalizing)
                        AND updated_utc <= $observedAt
                        AND
                        (
                            claimed_by IS NULL
                            OR claim_expires_utc IS NULL
                            OR claim_expires_utc <= $observedAt
                        )
                    RETURNING transfer_job_id, attempt_number;
                    """;
                command.Parameters.AddWithValue("$interrupted", FormatEnum(TransferState.Interrupted));
                command.Parameters.AddWithValue("$status", FormatEnum(TransferStatusCode.Interrupted));
                command.Parameters.AddWithValue("$observedAt", FormatTimestamp(observedAtUtc));
                command.Parameters.AddWithValue("$preparing", FormatEnum(TransferState.Preparing));
                command.Parameters.AddWithValue("$connecting", FormatEnum(TransferState.Connecting));
                command.Parameters.AddWithValue("$transferring", FormatEnum(TransferState.Transferring));
                command.Parameters.AddWithValue("$verifying", FormatEnum(TransferState.Verifying));
                command.Parameters.AddWithValue("$finalizing", FormatEnum(TransferState.Finalizing));
                await using var reader = await command
                    .ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    interrupted.Add((ParseJobId(reader.GetString(0)), checked((int)reader.GetInt64(1))));
                }
            }

            foreach (var (jobId, attempt) in interrupted)
            {
                await CompleteAttemptByIdentityAsync(
                    writer.Connection,
                    transaction,
                    jobId,
                    attempt,
                    observedAtUtc,
                    "interrupted",
                    "transfer.owner.interrupted",
                    "The prior transfer owner stopped before recording completion.",
                    cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return interrupted.Count;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask<DurableTransferJob?> FindJobCoreAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        TransferJobId transferJobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {JobProjection}
            FROM transfer_jobs
            WHERE transfer_job_id = $jobId;
            """;
        command.Parameters.AddWithValue("$jobId", transferJobId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadJob(reader)
            : null;
    }

    private static DurableTransferJob ReadJob(SqliteDataReader reader)
    {
        var jobId = ParseJobId(reader.GetString(0));
        var intent = DeserializeIntent(jobId, reader.GetString(1));
        var state = ParseEnum<TransferState>(reader.GetString(2), "transfer state");
        var revision = reader.GetInt64(3);
        var attempt = checked((int)reader.GetInt64(4));
        var priority = reader.GetInt32(5);
        var retryAt = ReadNullableTimestamp(reader, 6, "retry availability");
        var createdAt = ParseTimestamp(reader.GetString(7), "creation time");
        var updatedAt = ParseTimestamp(reader.GetString(8), "transition time");
        var status = reader.IsDBNull(9)
            ? TransferStatusCode.None
            : ParseEnum<TransferStatusCode>(reader.GetString(9), "transfer status");

        if (intent.CreatedAtUtc != createdAt)
        {
            throw Corrupt("The transfer intent creation time does not match its queue row.");
        }

        var snapshot = new TransferStateSnapshot(jobId, state, revision, attempt, updatedAt, status);
        TransferJobLease? lease = null;
        if (!reader.IsDBNull(11))
        {
            if (reader.IsDBNull(12) || reader.IsDBNull(13))
            {
                throw Corrupt("A transfer claim is missing acquisition or expiry metadata.");
            }

            lease = new TransferJobLease(
                jobId,
                reader.GetString(11),
                reader.GetInt64(10),
                attempt,
                ParseTimestamp(reader.GetString(12), "claim acquisition"),
                ParseTimestamp(reader.GetString(13), "claim expiry"));
        }

        TransferSafeError? error = null;
        if (!reader.IsDBNull(14) || !reader.IsDBNull(15))
        {
            if (reader.IsDBNull(14) || reader.IsDBNull(15))
            {
                throw Corrupt("The transfer error code and safe summary must be stored together.");
            }

            error = new TransferSafeError(reader.GetString(14), reader.GetString(15));
        }

        ValidateDenormalizedIntent(reader, intent);
        return new DurableTransferJob(intent, snapshot, priority, retryAt, lease, error);
    }

    private static void ValidateDenormalizedIntent(SqliteDataReader reader, TransferIntent intent)
    {
        if (!StringComparer.Ordinal.Equals(reader.GetString(16), intent.Source.ProfileId.ToString()) ||
            !StringComparer.Ordinal.Equals(reader.GetString(17), intent.Destination.ProfileId.ToString()) ||
            !StringComparer.Ordinal.Equals(reader.GetString(18), intent.Source.CanonicalRelativePath) ||
            !StringComparer.Ordinal.Equals(reader.GetString(19), intent.Destination.CanonicalRelativePath) ||
            ParseEnum<TransferOperationKind>(reader.GetString(20), "operation kind") != intent.Operation ||
            ReadNullableInt64(reader, 21) != intent.ExpectedLength)
        {
            throw Corrupt("The immutable transfer intent does not match its indexed queue columns.");
        }
    }

    private static async ValueTask<PersistedTransferCheckpoint?> FindCheckpointCoreAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        TransferJobId transferJobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                checkpoint_version, expected_size, verified_offset,
                multipart_upload_id, completed_parts_json, non_secret_state_json, updated_utc
            FROM transfer_checkpoints
            WHERE transfer_job_id = $jobId;
            """;
        command.Parameters.AddWithValue("$jobId", transferJobId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var state = DeserializeCheckpointState(reader.GetString(5));
        var source = RestoreAddress(state.Source);
        var destination = RestoreAddress(state.DestinationTemporaryAddress);
        var digest = state.SourceDigest is null
            ? null
            : new TransferContentDigest(state.SourceDigest.Algorithm, state.SourceDigest.Value);
        var parts = DeserializeCompletedParts(reader.GetString(4));
        var checkpoint = TransferCheckpoint.Create(
            transferJobId,
            state.Attempt,
            reader.GetInt64(2),
            ReadNullableInt64(reader, 1),
            source,
            destination,
            ParseEnum<TransferResumeMode>(state.ResumeMode, "checkpoint resume mode"),
            digest,
            reader.IsDBNull(3) ? null : reader.GetString(3),
            parts,
            ParseTimestamp(reader.GetString(6), "checkpoint update time"));
        return new PersistedTransferCheckpoint(reader.GetInt64(0), checkpoint);
    }

    private static async Task InsertAttemptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TransferJobLease lease,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO transfer_attempts
            (
                transfer_attempt_id, transfer_job_id, attempt_number, started_utc
            )
            VALUES ($attemptId, $jobId, $attempt, $startedAt);
            """;
        command.Parameters.AddWithValue("$attemptId", Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$jobId", lease.TransferJobId.ToString());
        command.Parameters.AddWithValue("$attempt", lease.Attempt);
        command.Parameters.AddWithValue("$startedAt", FormatTimestamp(startedAtUtc));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Task CompleteAttemptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TransferJobLease lease,
        TransferState state,
        DateTimeOffset completedAtUtc,
        TransferSafeError? error,
        CancellationToken cancellationToken) =>
        CompleteAttemptByIdentityAsync(
            connection,
            transaction,
            lease.TransferJobId,
            lease.Attempt,
            completedAtUtc,
            FormatAttemptOutcome(state),
            error?.Code,
            error?.Summary,
            cancellationToken);

    private static async Task CompleteAttemptByIdentityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TransferJobId jobId,
        int attempt,
        DateTimeOffset completedAtUtc,
        string outcome,
        string? errorCode,
        string? errorSummary,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE transfer_attempts
            SET completed_utc = $completedAt, outcome = $outcome,
                error_code = $errorCode, safe_error_summary = $errorSummary
            WHERE transfer_job_id = $jobId AND attempt_number = $attempt AND completed_utc IS NULL;
            """;
        command.Parameters.AddWithValue("$completedAt", FormatTimestamp(completedAtUtc));
        command.Parameters.AddWithValue("$outcome", outcome);
        command.Parameters.AddWithValue("$errorCode", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorSummary", (object?)errorSummary ?? DBNull.Value);
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        command.Parameters.AddWithValue("$attempt", attempt);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw Corrupt("The active transfer attempt could not be completed atomically.");
        }
    }

    private static async ValueTask DeleteCheckpointCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TransferJobId jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM transfer_checkpoints WHERE transfer_job_id = $jobId;";
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<bool> JobExistsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        TransferJobId jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM transfer_jobs WHERE transfer_job_id = $jobId;";
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;
    }

    private static async ValueTask<bool> CheckpointExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TransferJobId jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM transfer_checkpoints WHERE transfer_job_id = $jobId;";
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;
    }

    private static void AddLeaseIdentityParameters(SqliteCommand command, TransferJobLease lease)
    {
        command.Parameters.AddWithValue("$jobId", lease.TransferJobId.ToString());
        command.Parameters.AddWithValue("$owner", lease.OwnerId);
        command.Parameters.AddWithValue("$fence", lease.FencingToken);
        command.Parameters.AddWithValue("$attempt", lease.Attempt);
    }

    private static void AddCheckpointParameters(
        SqliteCommand command,
        TransferCheckpoint checkpoint,
        long version,
        string completedPartsJson,
        string stateJson)
    {
        command.Parameters.AddWithValue("$jobId", checkpoint.TransferJobId.ToString());
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$sourceIdentity", BuildAddressIdentity(checkpoint.Source));
        command.Parameters.AddWithValue(
            "$destinationIdentity",
            BuildAddressIdentity(checkpoint.DestinationTemporaryAddress));
        command.Parameters.AddWithValue("$expectedSize", (object?)checkpoint.ExpectedLength ?? DBNull.Value);
        command.Parameters.AddWithValue("$verifiedOffset", checkpoint.VerifiedBytes);
        command.Parameters.AddWithValue(
            "$temporaryName",
            checkpoint.DestinationTemporaryAddress.CanonicalRelativePath);
        command.Parameters.AddWithValue("$resumeId", (object?)checkpoint.ProviderResumeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$completedParts", completedPartsJson);
        command.Parameters.AddWithValue("$state", stateJson);
        command.Parameters.AddWithValue("$updatedAt", FormatTimestamp(checkpoint.RecordedAtUtc));
    }

    private static bool LeaseMatches(
        TransferJobLease? current,
        TransferJobLease expected,
        DateTimeOffset observedAtUtc) =>
        current is not null &&
        current.TransferJobId == expected.TransferJobId &&
        StringComparer.Ordinal.Equals(current.OwnerId, expected.OwnerId) &&
        current.FencingToken == expected.FencingToken &&
        current.Attempt == expected.Attempt &&
        current.AcquiredAtUtc <= observedAtUtc &&
        current.ExpiresAtUtc > observedAtUtc;

    private static bool RequiresLease(TransferState state) => state is
        TransferState.Preparing or
        TransferState.Connecting or
        TransferState.Transferring or
        TransferState.Verifying or
        TransferState.Finalizing or
        TransferState.CleanupPending;

    private static bool CheckpointExpectationMatches(
        PersistedTransferCheckpoint? existing,
        long? expectedVersion) =>
        expectedVersion is null ? existing is null : existing?.Version == expectedVersion;

    private static void EnsureCheckpointProgressIsMonotonic(
        PersistedTransferCheckpoint? existing,
        TransferCheckpoint next)
    {
        if (existing is null)
        {
            return;
        }

        var prior = existing.Checkpoint;
        if (next.Attempt < prior.Attempt ||
            (next.Attempt == prior.Attempt &&
             (next.VerifiedBytes < prior.VerifiedBytes || next.RecordedAtUtc < prior.RecordedAtUtc)))
        {
            throw new ArgumentException(
                "A checkpoint update cannot move an attempt, byte offset, or timestamp backwards.",
                nameof(next));
        }
    }

    private static string FormatAttemptOutcome(TransferState state) => state switch
    {
        TransferState.Completed => "completed",
        TransferState.Cancelled => "cancelled",
        TransferState.Retrying => "retrying",
        TransferState.Paused => "paused",
        TransferState.BlockedCredential => "blocked-credential",
        TransferState.BlockedTrust => "blocked-trust",
        TransferState.Interrupted => "interrupted",
        TransferState.NeedsReconciliation => "needs-reconciliation",
        TransferState.RestartRequired => "restart-required",
        TransferState.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "The state does not close an attempt."),
    };

    private static string SerializeIntent(TransferIntent intent) => JsonSerializer.Serialize(
        new TransferIntentDocument(
            3,
            FormatEnum(intent.Operation),
            AddressDocument.From(intent.Source),
            AddressDocument.From(intent.Destination),
            intent.ExpectedLength,
            FormatEnum(intent.VerificationPolicy),
            FormatTimestamp(intent.CreatedAtUtc),
            intent.ExpectedDestinationVersionId,
            intent.ExpectedDestinationEntityTag,
            ToDocument(intent.ExpectedSourceDigest),
            ToDocument(intent.ExpectedDestinationDigest),
            ToDocument(intent.RequiredDestinationDigest)),
        JsonOptions);

    private static TransferIntent DeserializeIntent(TransferJobId jobId, string json)
    {
        TransferIntentDocument document;
        try
        {
            document = JsonSerializer.Deserialize<TransferIntentDocument>(json, JsonOptions)
                ?? throw Corrupt("The transfer intent JSON is empty.");
        }
        catch (JsonException error)
        {
            throw Corrupt("The transfer intent JSON is invalid.", error);
        }

        if (document.Version is not (1 or 2 or 3))
        {
            throw Corrupt("The transfer intent version is unsupported.");
        }

        return new TransferIntent(
            jobId,
            ParseEnum<TransferOperationKind>(document.Operation, "operation kind"),
            RestoreAddress(document.Source),
            RestoreAddress(document.Destination),
            document.ExpectedLength,
            ParseEnum<TransferVerificationPolicy>(document.VerificationPolicy, "verification policy"),
            ParseTimestamp(document.CreatedAtUtc, "intent creation time"),
            document.ExpectedDestinationVersionId,
            document.ExpectedDestinationEntityTag,
            RestorePortableDigest(document.ExpectedSourceDigest),
            RestorePortableDigest(document.ExpectedDestinationDigest),
            RestorePortableDigest(document.RequiredDestinationDigest));
    }

    private static string SerializeCheckpointState(TransferCheckpoint checkpoint) =>
        JsonSerializer.Serialize(
            new CheckpointStateDocument(
                1,
                checkpoint.Attempt,
                FormatEnum(checkpoint.ResumeMode),
                AddressDocument.From(checkpoint.Source),
                AddressDocument.From(checkpoint.DestinationTemporaryAddress),
                checkpoint.SourceDigest is null
                    ? null
                    : new DigestDocument(
                        checkpoint.SourceDigest.Algorithm,
                        checkpoint.SourceDigest.Value)),
            JsonOptions);

    private static DigestDocument? ToDocument(PortableContentDigest? digest) => digest is null
        ? null
        : new DigestDocument(digest.AlgorithmName, digest.Value);

    private static PortableContentDigest? RestorePortableDigest(DigestDocument? digest)
    {
        if (digest is null)
        {
            return null;
        }

        try
        {
            return PortableContentDigest.Parse(digest.Algorithm, digest.Value);
        }
        catch (FormatException error)
        {
            throw Corrupt("A persisted portable transfer digest is invalid.", error);
        }
    }

    private static CheckpointStateDocument DeserializeCheckpointState(string json)
    {
        try
        {
            var document = JsonSerializer.Deserialize<CheckpointStateDocument>(json, JsonOptions)
                ?? throw Corrupt("The checkpoint state JSON is empty.");
            return document.Version == 1
                ? document
                : throw Corrupt("The checkpoint state version is unsupported.");
        }
        catch (JsonException error)
        {
            throw Corrupt("The checkpoint state JSON is invalid.", error);
        }
    }

    private static string SerializeCompletedParts(IEnumerable<CompletedTransferPart> parts) =>
        JsonSerializer.Serialize(
            parts.Select(part => new CompletedPartDocument(
                part.PartNumber,
                part.Offset,
                part.Length,
                part.ProviderTag)),
            JsonOptions);

    private static CompletedTransferPart[] DeserializeCompletedParts(string json)
    {
        try
        {
            return (JsonSerializer.Deserialize<CompletedPartDocument[]>(json, JsonOptions) ?? [])
                .Select(part => new CompletedTransferPart(
                    part.PartNumber,
                    part.Offset,
                    part.Length,
                    part.ProviderTag))
                .ToArray();
        }
        catch (JsonException error)
        {
            throw Corrupt("The completed-part checkpoint JSON is invalid.", error);
        }
    }

    private static StorageAddress RestoreAddress(AddressDocument document)
    {
        if (document is null)
        {
            throw Corrupt("A persisted storage address is missing.");
        }

        if (!ConnectionProfileId.TryParse(document.ProfileId, out var profileId))
        {
            throw Corrupt("A persisted storage address contains an invalid profile ID.");
        }

        var result = StorageAddress.Create(
            profileId,
            document.RootIdentity,
            document.CanonicalRelativePath,
            document.NativeItemId,
            document.VersionId,
            document.EntityTag);
        return result.IsSuccess
            ? result.Value
            : throw Corrupt("A persisted storage address is not canonical or valid.");
    }

    private static string BuildAddressIdentity(StorageAddress address) =>
        JsonSerializer.Serialize(AddressDocument.From(address), JsonOptions);

    private static string FormatEnum<T>(T value) where T : struct, Enum => value.ToString();

    private static T ParseEnum<T>(string value, string description) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw Corrupt($"The stored {description} is unsupported.");

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value, string description) =>
        DateTimeOffset.TryParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed) && parsed.Offset == TimeSpan.Zero
            ? parsed
            : throw Corrupt($"The stored {description} is not a UTC timestamp.");

    private static DateTimeOffset? ReadNullableTimestamp(
        SqliteDataReader reader,
        int ordinal,
        string description) =>
        reader.IsDBNull(ordinal) ? null : ParseTimestamp(reader.GetString(ordinal), description);

    private static long? ReadNullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static TransferJobId ParseJobId(string value) =>
        TransferJobId.TryParse(value, out var id)
            ? id
            : throw Corrupt("A persisted transfer job ID is invalid.");

    private static void EnsureJobId(TransferJobId jobId)
    {
        if (jobId.IsEmpty)
        {
            throw new ArgumentException("A transfer job ID is required.", nameof(jobId));
        }
    }

    private static void ValidateUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The timestamp must be UTC.", parameterName);
        }
    }

    private static TransferStoreResult<T> Applied<T>(T value) where T : class =>
        new(TransferStoreMutationStatus.Applied, value);

    private static TransferStoreResult<T> Rejected<T>(TransferStoreMutationStatus status) where T : class =>
        new(status, null);

    private static InvalidDataException Corrupt(string message, Exception? innerException = null) =>
        new(message, innerException);

    private const string JobProjection = """
        transfer_job_id, intent_json, state, state_revision, attempt_number,
        priority, retry_not_before_utc, created_utc, updated_utc, status_code,
        owner_epoch, claimed_by, claim_acquired_utc, claim_expires_utc,
        last_error_code, last_error_summary, source_profile_id, destination_profile_id,
        source_path, destination_path, operation_kind, expected_size
        """;

    private sealed record TransferIntentDocument(
        int Version,
        string Operation,
        AddressDocument Source,
        AddressDocument Destination,
        long? ExpectedLength,
        string VerificationPolicy,
        string CreatedAtUtc,
        string? ExpectedDestinationVersionId,
        string? ExpectedDestinationEntityTag,
        DigestDocument? ExpectedSourceDigest,
        DigestDocument? ExpectedDestinationDigest,
        DigestDocument? RequiredDestinationDigest);

    private sealed record AddressDocument(
        string ProfileId,
        string RootIdentity,
        string CanonicalRelativePath,
        string? NativeItemId,
        string? VersionId,
        string? EntityTag)
    {
        public static AddressDocument From(StorageAddress address) => new(
            address.ProfileId.ToString(),
            address.RootIdentity,
            address.CanonicalRelativePath,
            address.NativeItemId,
            address.VersionId,
            address.EntityTag);
    }

    private sealed record CheckpointStateDocument(
        int Version,
        int Attempt,
        string ResumeMode,
        AddressDocument Source,
        AddressDocument DestinationTemporaryAddress,
        DigestDocument? SourceDigest);

    private sealed record DigestDocument(string Algorithm, string Value);

    private sealed record CompletedPartDocument(
        int PartNumber,
        long Offset,
        long Length,
        string? ProviderTag);
}
