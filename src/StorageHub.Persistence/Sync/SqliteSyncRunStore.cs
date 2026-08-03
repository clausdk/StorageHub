using System.Text.Json;
using Microsoft.Data.Sqlite;
using StorageHub.Domain.Identifiers;
using StorageHub.Sync;
using StorageHub.Sync.Persistence;

namespace StorageHub.Persistence.Sync;

public sealed class SqliteSyncRunStore(SingleWriterSqliteDatabase database) : ISyncRunStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SingleWriterSqliteDatabase _database =
        database ?? throw new ArgumentNullException(nameof(database));

    public async ValueTask<SyncPreviewRecord?> GetAsync(
        SyncRunId syncRunId,
        CancellationToken cancellationToken = default)
    {
        EnsureRunId(syncRunId);
        await using var connection = await _database.OpenReadConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ReadAsync(connection, null, syncRunId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<SyncPreviewRecord>> ListAsync(
        SyncProfileId? profileId,
        int offset,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumCount, 101);
        await using var connection = await _database.OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Projection}
            FROM sync_runs AS runs
            WHERE $profileId IS NULL OR sync_profile_id = $profileId
            ORDER BY started_utc DESC, sync_run_id DESC
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$profileId", profileId is { } id ? id.ToString() : DBNull.Value);
        command.Parameters.AddWithValue("$limit", maximumCount);
        command.Parameters.AddWithValue("$offset", offset);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var runs = new List<SyncPreviewRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            runs.Add(ReadRecord(reader));
        }
        return runs;
    }

    public async ValueTask<SyncPreviewRecord?> GetByTriggerAsync(
        SyncProfileId profileId,
        string triggerIdempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (profileId.IsEmpty)
        {
            throw new ArgumentException("A sync profile ID is required.", nameof(profileId));
        }

        SyncPersistenceUtilities.ValidateText(
            triggerIdempotencyKey,
            nameof(triggerIdempotencyKey),
            512);
        await using var connection = await _database.OpenReadConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Projection}
            FROM sync_runs AS runs
            WHERE sync_profile_id = $profileId AND trigger_idempotency_key = $key;
            """;
        command.Parameters.AddWithValue("$profileId", profileId.ToString());
        command.Parameters.AddWithValue("$key", triggerIdempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadRecord(reader)
            : null;
    }

    public async ValueTask<SyncPersistenceResult<SyncPreviewRecord>> CreatePreviewAsync(
        SyncPreviewDraft draft,
        CancellationToken cancellationToken = default)
    {
        ValidateDraft(draft);
        await using var writer = await _database.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = writer.Connection.BeginTransaction(deferred: false);
        try
        {
            var idempotent = await ReadByTriggerAsync(
                writer.Connection,
                transaction,
                draft.ProfileId,
                draft.TriggerIdempotencyKey,
                cancellationToken).ConfigureAwait(false);
            if (idempotent is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                var equivalent = idempotent.PlanId == draft.PlanId &&
                    idempotent.PlanDigest == draft.PlanDigest &&
                    idempotent.ProfileRevision == draft.ExpectedProfileRevision &&
                    StringComparer.OrdinalIgnoreCase.Equals(
                        idempotent.ProfilePolicySha256,
                        draft.ExpectedPolicySha256);
                return new SyncPersistenceResult<SyncPreviewRecord>(
                    equivalent
                        ? SyncPersistenceMutationStatus.AlreadyApplied
                        : SyncPersistenceMutationStatus.Conflict,
                    equivalent ? idempotent : null);
            }

            if (!await ProfileMatchesAsync(
                    writer.Connection,
                    transaction,
                    draft.ProfileId,
                    draft.ExpectedProfileRevision,
                    draft.ExpectedPolicySha256,
                    cancellationToken).ConfigureAwait(false) ||
                !await PlanMatchesAsync(
                    writer.Connection,
                    transaction,
                    draft,
                    cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Rejected(SyncPersistenceMutationStatus.Conflict);
            }

            if (await ReadAsync(
                    writer.Connection,
                    transaction,
                    draft.SyncRunId,
                    cancellationToken).ConfigureAwait(false) is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Rejected(SyncPersistenceMutationStatus.Conflict);
            }

            var generation = await NextGenerationAsync(
                writer.Connection,
                transaction,
                draft.ProfileId,
                cancellationToken).ConfigureAwait(false);
            var state = CreatePreviewState(draft);
            var snapshotJson = SerializeSnapshots(draft.Snapshots);
            await InsertRunAsync(
                writer.Connection,
                transaction,
                draft,
                generation,
                state,
                snapshotJson,
                cancellationToken).ConfigureAwait(false);
            foreach (var conflict in draft.Conflicts)
            {
                await InsertConflictAsync(
                    writer.Connection,
                    transaction,
                    draft.SyncRunId,
                    conflict,
                    draft.CreatedAtUtc,
                    cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            var record = new SyncPreviewRecord(
                draft.SyncRunId,
                draft.ProfileId,
                generation,
                state,
                draft.ExpectedProfileRevision,
                draft.ExpectedPolicySha256.ToLowerInvariant(),
                draft.PlanId,
                draft.PlanDigest,
                draft.Snapshots,
                draft.ApprovalChallengeSha256.ToLowerInvariant(),
                draft.Trigger,
                draft.TriggerIdempotencyKey,
                draft.Conflicts.Count,
                false,
                null,
                null,
                draft.CreatedAtUtc);
            return new SyncPersistenceResult<SyncPreviewRecord>(
                SyncPersistenceMutationStatus.Applied,
                record);
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            return Rejected(SyncPersistenceMutationStatus.Conflict);
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<SyncPersistenceResult<SyncPreviewRecord>> ApproveAndDispatchAsync(
        SyncApplyDispatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateApply(request);
        await using var writer = await _database.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = writer.Connection.BeginTransaction(deferred: false);
        try
        {
            var current = await ReadAsync(
                writer.Connection,
                transaction,
                request.SyncRunId,
                cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Rejected(SyncPersistenceMutationStatus.NotFound);
            }

            if (current.ApprovedForExecution && current.DispatchEventId == request.DispatchEventId &&
                StringComparer.OrdinalIgnoreCase.Equals(
                    current.ApprovalChallengeSha256,
                    request.ApprovalSha256))
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new SyncPersistenceResult<SyncPreviewRecord>(
                    SyncPersistenceMutationStatus.AlreadyApplied,
                    current);
            }

            if (current.State.Phase != SyncRunPhase.AwaitingApproval ||
                current.State.Revision != request.ExpectedRunRevision ||
                current.ProfileRevision != request.ExpectedProfileRevision ||
                !StringComparer.OrdinalIgnoreCase.Equals(
                    current.ProfilePolicySha256,
                    request.ExpectedPolicySha256) ||
                !StringComparer.OrdinalIgnoreCase.Equals(
                    current.ApprovalChallengeSha256,
                    request.ApprovalSha256) ||
                current.ConflictCount != 0 ||
                !await ProfileMatchesAsync(
                    writer.Connection,
                    transaction,
                    current.ProfileId,
                    request.ExpectedProfileRevision,
                    request.ExpectedPolicySha256,
                    cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Rejected(SyncPersistenceMutationStatus.Conflict);
            }

            var nextState = SyncStateMachine.Transition(
                current.State,
                SyncRunPhase.Ready,
                request.ApprovedAtUtc);
            var payload = JsonSerializer.Serialize(
                new SyncApplyOutboxPayload(
                    current.SyncRunId.ToString(),
                    current.ProfileId.ToString(),
                    current.PlanId.ToString(),
                    current.PlanDigest.Sha256Hex,
                    request.ApprovalSha256.ToLowerInvariant(),
                    current.ProfileRevision,
                    current.ProfilePolicySha256),
                JsonOptions);
            var outbox = await SqliteReliableOutboxStore.EnqueueCoreAsync(
                writer.Connection,
                transaction,
                new OutboxEventDraft(
                    request.DispatchEventId,
                    SyncOutboxEventKinds.ApplyRequested,
                    $"sync-run:{current.SyncRunId}",
                    nextState.Revision,
                    payload,
                    request.ApprovedAtUtc),
                cancellationToken).ConfigureAwait(false);
            if (outbox.Status is not (
                    SyncPersistenceMutationStatus.Applied or
                    SyncPersistenceMutationStatus.AlreadyApplied))
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Rejected(SyncPersistenceMutationStatus.Conflict);
            }

            await using (var update = writer.Connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE sync_runs
                    SET state = $state,
                        status_code = $status,
                        run_revision = $nextRevision,
                        transitioned_utc = $transitioned,
                        approved_execution = 1,
                        approved_utc = $approved,
                        dispatch_event_id = $eventId
                    WHERE sync_run_id = $runId
                      AND run_revision = $expectedRevision
                      AND state = $expectedState
                      AND approved_execution = 0;
                    """;
                update.Parameters.AddWithValue("$state", nextState.Phase.ToString());
                update.Parameters.AddWithValue("$status", nextState.StatusCode.ToString());
                update.Parameters.AddWithValue("$nextRevision", nextState.Revision);
                update.Parameters.AddWithValue(
                    "$transitioned",
                    SyncPersistenceUtilities.FormatTimestamp(nextState.TransitionedAtUtc));
                update.Parameters.AddWithValue(
                    "$approved",
                    SyncPersistenceUtilities.FormatTimestamp(request.ApprovedAtUtc));
                update.Parameters.AddWithValue("$eventId", request.DispatchEventId.ToString("D"));
                update.Parameters.AddWithValue("$runId", current.SyncRunId.ToString());
                update.Parameters.AddWithValue("$expectedRevision", current.State.Revision);
                update.Parameters.AddWithValue("$expectedState", SyncRunPhase.AwaitingApproval.ToString());
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return Rejected(SyncPersistenceMutationStatus.Conflict);
                }
            }

            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return new SyncPersistenceResult<SyncPreviewRecord>(
                SyncPersistenceMutationStatus.Applied,
                current with
                {
                    State = nextState,
                    ApprovedForExecution = true,
                    ApprovedAtUtc = request.ApprovedAtUtc,
                    DispatchEventId = request.DispatchEventId,
                });
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    internal static async ValueTask<SyncPreviewRecord?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        SyncRunId runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Projection} FROM sync_runs AS runs WHERE sync_run_id = $id;";
        command.Parameters.AddWithValue("$id", runId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadRecord(reader)
            : null;
    }

    private static async ValueTask<SyncPreviewRecord?> ReadByTriggerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncProfileId profileId,
        string triggerIdempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {Projection}
            FROM sync_runs AS runs
            WHERE sync_profile_id = $profileId AND trigger_idempotency_key = $key;
            """;
        command.Parameters.AddWithValue("$profileId", profileId.ToString());
        command.Parameters.AddWithValue("$key", triggerIdempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadRecord(reader)
            : null;
    }

    private static SyncPreviewRecord ReadRecord(SqliteDataReader reader)
    {
        if (!SyncRunId.TryParse(reader.GetString(0), out var runId) ||
            !SyncProfileId.TryParse(reader.GetString(1), out var profileId) ||
            !OperationPlanId.TryParse(reader.GetString(8), out var planId) ||
            !SyncPlanDigest.TryParse(reader.GetString(9), out var planDigest) ||
            !Enum.TryParse<SyncRunPhase>(reader.GetString(3), false, out var phase) ||
            !Enum.TryParse<SyncStatusCode>(reader.GetString(5), false, out var status) ||
            !Enum.TryParse<SyncPreviewTrigger>(reader.GetString(13), false, out var trigger))
        {
            throw new InvalidDataException("A persisted sync preview contains invalid identifiers or enums.");
        }

        var transitioned = SyncPersistenceUtilities.ParseTimestamp(
            reader.GetString(6),
            "sync run transition time");
        var created = SyncPersistenceUtilities.ParseTimestamp(reader.GetString(17), "sync run creation time");
        return new SyncPreviewRecord(
            runId,
            profileId,
            reader.GetInt64(2),
            new SyncRunState(runId, phase, reader.GetInt64(4), transitioned, status),
            reader.GetInt64(7),
            reader.GetString(10),
            planId,
            planDigest,
            DeserializeSnapshots(reader.GetString(11)),
            reader.GetString(12),
            trigger,
            reader.GetString(14),
            reader.GetInt32(15),
            reader.GetInt64(16) == 1,
            reader.IsDBNull(18)
                ? null
                : SyncPersistenceUtilities.ParseTimestamp(reader.GetString(18), "sync approval time"),
            reader.IsDBNull(19)
                ? null
                : SyncPersistenceUtilities.ParseGuid(reader.GetString(19), "sync dispatch event ID"),
            created);
    }

    private static async Task InsertRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncPreviewDraft draft,
        long generation,
        SyncRunState state,
        string snapshotJson,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sync_runs
            (sync_run_id, sync_profile_id, generation, trigger_kind, state, plan_digest,
             started_utc, run_revision, status_code, transitioned_utc, operation_plan_id,
             profile_revision, profile_policy_hash, execution_snapshot_json,
             approval_challenge, trigger_idempotency_key)
            VALUES
            ($runId, $profileId, $generation, $trigger, $state, $planDigest,
             $created, $runRevision, $status, $transitioned, $planId,
             $profileRevision, $policyHash, $snapshots, $approval, $triggerKey);
            """;
        command.Parameters.AddWithValue("$runId", draft.SyncRunId.ToString());
        command.Parameters.AddWithValue("$profileId", draft.ProfileId.ToString());
        command.Parameters.AddWithValue("$generation", generation);
        command.Parameters.AddWithValue("$trigger", draft.Trigger.ToString());
        command.Parameters.AddWithValue("$state", state.Phase.ToString());
        command.Parameters.AddWithValue("$planDigest", draft.PlanDigest.Sha256Hex);
        command.Parameters.AddWithValue("$created", SyncPersistenceUtilities.FormatTimestamp(draft.CreatedAtUtc));
        command.Parameters.AddWithValue("$runRevision", state.Revision);
        command.Parameters.AddWithValue("$status", state.StatusCode.ToString());
        command.Parameters.AddWithValue(
            "$transitioned",
            SyncPersistenceUtilities.FormatTimestamp(state.TransitionedAtUtc));
        command.Parameters.AddWithValue("$planId", draft.PlanId.ToString());
        command.Parameters.AddWithValue("$profileRevision", draft.ExpectedProfileRevision);
        command.Parameters.AddWithValue("$policyHash", draft.ExpectedPolicySha256.ToLowerInvariant());
        command.Parameters.AddWithValue("$snapshots", snapshotJson);
        command.Parameters.AddWithValue("$approval", draft.ApprovalChallengeSha256.ToLowerInvariant());
        command.Parameters.AddWithValue("$triggerKey", draft.TriggerIdempotencyKey);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertConflictAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncRunId runId,
        SyncPlanningConflict conflict,
        DateTimeOffset detectedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        SyncPersistenceUtilities.ValidateText(conflict.RelativePath, nameof(conflict), 32_768);
        SyncPersistenceUtilities.ValidateText(conflict.SafeReason, nameof(conflict), 2_048);
        var details = JsonSerializer.Serialize(new
        {
            reason = conflict.SafeReason,
            changeKind = conflict.Kind.ToString(),
        }, JsonOptions);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO conflict_records
            (conflict_id, sync_run_id, relative_path, conflict_kind, state, detected_utc,
             safe_details_json, record_revision, updated_utc)
            VALUES ($id, $runId, $path, $kind, 'Unresolved', $detected, $details, 1, $detected);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$runId", runId.ToString());
        command.Parameters.AddWithValue("$path", conflict.RelativePath);
        command.Parameters.AddWithValue("$kind", conflict.Kind.ToString());
        command.Parameters.AddWithValue("$detected", SyncPersistenceUtilities.FormatTimestamp(detectedAtUtc));
        command.Parameters.AddWithValue("$details", details);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<bool> ProfileMatchesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncProfileId profileId,
        long revision,
        string policySha256,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM sync_profiles
            WHERE sync_profile_id = $id AND profile_revision = $revision
              AND lower(policy_hash) = lower($hash) AND enabled = 1;
            """;
        command.Parameters.AddWithValue("$id", profileId.ToString());
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$hash", policySha256);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async ValueTask<bool> PlanMatchesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncPreviewDraft draft,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM sync_plans
            WHERE plan_id = $planId AND sync_profile_id = $profileId
              AND lower(plan_digest) = lower($digest);
            """;
        command.Parameters.AddWithValue("$planId", draft.PlanId.ToString());
        command.Parameters.AddWithValue("$profileId", draft.ProfileId.ToString());
        command.Parameters.AddWithValue("$digest", draft.PlanDigest.Sha256Hex);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async ValueTask<long> NextGenerationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncProfileId profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(MAX(generation), 0) + 1
            FROM sync_runs WHERE sync_profile_id = $profileId;
            """;
        command.Parameters.AddWithValue("$profileId", profileId.ToString());
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static SyncRunState CreatePreviewState(SyncPreviewDraft draft)
    {
        var state = SyncRunState.Create(draft.SyncRunId, draft.CreatedAtUtc);
        state = SyncStateMachine.Transition(state, SyncRunPhase.Scanning, draft.CreatedAtUtc);
        state = SyncStateMachine.Transition(state, SyncRunPhase.Planning, draft.CreatedAtUtc);
        return SyncStateMachine.Transition(
            state,
            draft.Conflicts.Count > 0
                ? SyncRunPhase.BlockedConflict
                : draft.DeletionGuardBlocked
                    ? SyncRunPhase.BlockedDeletionGuard
                    : SyncRunPhase.AwaitingApproval,
            draft.CreatedAtUtc);
    }

    private static string SerializeSnapshots(SyncExecutionSnapshots snapshots) => JsonSerializer.Serialize(
        new SnapshotDocument(
            ToDocument(snapshots.Left),
            ToDocument(snapshots.Right),
            snapshots.BaselineItemCount,
            snapshots.VerifiedRootIdentities.ToDictionary(
                pair => pair.Key.ToString(),
                pair => pair.Value,
                StringComparer.Ordinal)),
        JsonOptions);

    private static SyncExecutionSnapshots DeserializeSnapshots(string json)
    {
        SnapshotDocument document;
        try
        {
            document = JsonSerializer.Deserialize<SnapshotDocument>(json, JsonOptions)
                ?? throw new JsonException();
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The persisted sync execution snapshot is invalid.", error);
        }

        var roots = new Dictionary<ConnectionProfileId, string>();
        foreach (var (id, root) in document.VerifiedRoots)
        {
            if (!ConnectionProfileId.TryParse(id, out var parsed) || !roots.TryAdd(parsed, root))
            {
                throw new InvalidDataException("The persisted sync execution roots are invalid.");
            }
        }

        return new SyncExecutionSnapshots(
            FromDocument(document.Left),
            FromDocument(document.Right),
            document.BaselineItemCount,
            roots);
    }

    private static CompletenessDocument ToDocument(SnapshotCompleteness value) => new(
        value.EndpointAvailable,
        value.RootIdentityVerified,
        value.EnumerationCompleted,
        value.PaginationCompleted,
        value.PermissionsIntact,
        value.UnexpectedlyEmpty,
        value.TotalItemCount);

    private static SnapshotCompleteness FromDocument(CompletenessDocument value) => new(
        value.EndpointAvailable,
        value.RootIdentityVerified,
        value.EnumerationCompleted,
        value.PaginationCompleted,
        value.PermissionsIntact,
        value.UnexpectedlyEmpty,
        value.TotalItemCount);

    private static void ValidateDraft(SyncPreviewDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        EnsureRunId(draft.SyncRunId);
        if (draft.ProfileId.IsEmpty || draft.PlanId.IsEmpty ||
            string.IsNullOrWhiteSpace(draft.PlanDigest.Sha256Hex))
        {
            throw new ArgumentException("The preview identifiers and digest are required.", nameof(draft));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(draft.ExpectedProfileRevision);
        ValidateSha256(draft.ExpectedPolicySha256, nameof(draft));
        ValidateSha256(draft.ApprovalChallengeSha256, nameof(draft));
        ArgumentNullException.ThrowIfNull(draft.Snapshots);
        ArgumentNullException.ThrowIfNull(draft.Conflicts);
        if (!Enum.IsDefined(draft.Trigger))
        {
            throw new ArgumentOutOfRangeException(nameof(draft));
        }

        SyncPersistenceUtilities.ValidateText(draft.TriggerIdempotencyKey, nameof(draft), 512);
        SyncPersistenceUtilities.ValidateUtc(draft.CreatedAtUtc, nameof(draft));
    }

    private static void ValidateApply(SyncApplyDispatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureRunId(request.SyncRunId);
        ArgumentOutOfRangeException.ThrowIfNegative(request.ExpectedRunRevision);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.ExpectedProfileRevision);
        ValidateSha256(request.ExpectedPolicySha256, nameof(request));
        ValidateSha256(request.ApprovalSha256, nameof(request));
        if (request.DispatchEventId == Guid.Empty)
        {
            throw new ArgumentException("A dispatch event ID is required.", nameof(request));
        }

        SyncPersistenceUtilities.ValidateUtc(request.ApprovedAtUtc, nameof(request));
    }

    private static void ValidateSha256(string value, string parameterName)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A SHA-256 value must contain 64 hexadecimal characters.", parameterName);
        }
    }

    private static void EnsureRunId(SyncRunId runId)
    {
        if (runId.IsEmpty)
        {
            throw new ArgumentException("A sync run ID is required.", nameof(runId));
        }
    }

    private static SyncPersistenceResult<SyncPreviewRecord> Rejected(
        SyncPersistenceMutationStatus status) => new(status, null);

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

    private sealed record SnapshotDocument(
        CompletenessDocument Left,
        CompletenessDocument Right,
        long BaselineItemCount,
        Dictionary<string, string> VerifiedRoots);

    private sealed record CompletenessDocument(
        bool EndpointAvailable,
        bool RootIdentityVerified,
        bool EnumerationCompleted,
        bool PaginationCompleted,
        bool PermissionsIntact,
        bool UnexpectedlyEmpty,
        long TotalItemCount);

    private const string Projection = """
        runs.sync_run_id,
        runs.sync_profile_id,
        runs.generation,
        runs.state,
        runs.run_revision,
        runs.status_code,
        runs.transitioned_utc,
        runs.profile_revision,
        runs.operation_plan_id,
        runs.plan_digest,
        runs.profile_policy_hash,
        runs.execution_snapshot_json,
        runs.approval_challenge,
        runs.trigger_kind,
        runs.trigger_idempotency_key,
        (SELECT COUNT(*) FROM conflict_records AS conflicts
         WHERE conflicts.sync_run_id = runs.sync_run_id AND conflicts.state = 'Unresolved'),
        runs.approved_execution,
        runs.started_utc,
        runs.approved_utc,
        runs.dispatch_event_id
        """;
}
