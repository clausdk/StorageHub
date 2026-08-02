using Microsoft.Data.Sqlite;
using StorageHub.Domain.Identifiers;
using StorageHub.Sync;
using StorageHub.Sync.Persistence;

namespace StorageHub.Persistence.Sync;

/// <summary>
/// SQLite-backed sync execution state. Every write is made in an immediate transaction and is
/// conditional on both the run-bound execution fence and the live reliable-outbox claim.
/// </summary>
public sealed class SqliteSyncExecutionStore : ISyncExecutionStore
{
    private readonly SingleWriterSqliteDatabase _database;
    private readonly TimeProvider _timeProvider;

    public SqliteSyncExecutionStore(
        SingleWriterSqliteDatabase database,
        TimeProvider? timeProvider = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<SyncExecutionBeginResult> BeginAsync(
        SyncExecutionBeginRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateBegin(request);
        await using var writer = await _database.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = writer.Connection.BeginTransaction(deferred: false);
        try
        {
            var observedAtUtc = _timeProvider.GetUtcNow();
            if (!await LeaseIsCurrentAsync(
                    writer.Connection,
                    transaction,
                    request.Lease,
                    request.SyncRunId,
                    observedAtUtc,
                    cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new SyncExecutionBeginResult(SyncExecutionBeginStatus.StaleLease);
            }

            var current = await SqliteSyncRunStore.ReadAsync(
                writer.Connection,
                transaction,
                request.SyncRunId,
                cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new SyncExecutionBeginResult(SyncExecutionBeginStatus.NotFound);
            }

            if (!RunMatchesImmutableBinding(current, request))
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new SyncExecutionBeginResult(SyncExecutionBeginStatus.Conflict);
            }

            if (current.State.Phase == SyncRunPhase.Completed)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new SyncExecutionBeginResult(SyncExecutionBeginStatus.AlreadyCompleted);
            }

            if (current.State.Phase is SyncRunPhase.Executing or
                SyncRunPhase.Verifying or SyncRunPhase.CommittingBaseline)
            {
                var reconciled = await TransitionInterruptedExecutionAsync(
                    writer.Connection,
                    transaction,
                    current,
                    request.Lease,
                    request.TransitionedAtUtc,
                    cancellationToken).ConfigureAwait(false);
                if (reconciled is null)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return new SyncExecutionBeginResult(SyncExecutionBeginStatus.Conflict);
                }

                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return new SyncExecutionBeginResult(SyncExecutionBeginStatus.ReconciliationRequired);
            }

            if (current.State.Phase != SyncRunPhase.Ready ||
                current.State.Revision != request.ExpectedRunRevision ||
                current.ConflictCount != 0 ||
                !await ProfilePlanAndBaselineMatchAsync(
                    writer.Connection,
                    transaction,
                    request,
                    cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new SyncExecutionBeginResult(SyncExecutionBeginStatus.Conflict);
            }

            var baseline = await SqliteSyncBaselineStore.ReadSnapshotAsync(
                writer.Connection,
                transaction,
                request.ProfileId,
                cancellationToken).ConfigureAwait(false);
            if (baseline is null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new SyncExecutionBeginResult(SyncExecutionBeginStatus.NotFound);
            }

            var next = SyncStateMachine.Transition(
                current.State,
                SyncRunPhase.Executing,
                request.TransitionedAtUtc);
            await using (var update = writer.Connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE sync_runs
                    SET state = $state,
                        status_code = $status,
                        run_revision = $nextRevision,
                        transitioned_utc = $transitioned,
                        safe_error_summary = NULL,
                        execution_claim_id = $claimId,
                        execution_owner_id = $owner,
                        execution_fencing_token = $fence,
                        execution_bound_utc = $transitioned,
                        provider_mutation_may_have_started = 0
                    WHERE sync_run_id = $runId
                      AND state = $expectedState
                      AND run_revision = $expectedRevision
                      AND dispatch_event_id = $eventId;
                    """;
                AddStateParameters(update, current, next);
                AddLeaseParameters(update, request.Lease);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return new SyncExecutionBeginResult(SyncExecutionBeginStatus.Conflict);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return new SyncExecutionBeginResult(
                SyncExecutionBeginStatus.Acquired,
                new SyncExecutionContext(current with { State = next }, baseline, false));
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<SyncPersistenceMutationStatus> ArmProviderMutationAsync(
        OutboxDeliveryLease lease,
        SyncRunId syncRunId,
        long expectedRunRevision,
        DateTimeOffset armedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateLeaseForRun(lease, syncRunId);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRunRevision);
        SyncPersistenceUtilities.ValidateUtc(armedAtUtc, nameof(armedAtUtc));
        await using var writer = await _database.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = writer.Connection.BeginTransaction(deferred: false);
        try
        {
            var observedAtUtc = _timeProvider.GetUtcNow();
            if (!await LeaseIsCurrentAsync(
                    writer.Connection,
                    transaction,
                    lease,
                    syncRunId,
                    observedAtUtc,
                    cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return SyncPersistenceMutationStatus.StaleLease;
            }

            await using var command = writer.Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE sync_runs
                SET provider_mutation_may_have_started = 1
                WHERE sync_run_id = $runId
                  AND state = 'Executing'
                  AND run_revision = $expectedRevision
                  AND dispatch_event_id = $eventId
                  AND execution_claim_id = $claimId
                  AND execution_owner_id = $owner
                  AND execution_fencing_token = $fence
                  AND approved_execution = 1
                  AND NOT EXISTS (
                      SELECT 1 FROM conflict_records
                      WHERE conflict_records.sync_run_id = sync_runs.sync_run_id
                        AND conflict_records.state = 'Unresolved')
                  AND EXISTS (
                      SELECT 1
                      FROM sync_profiles AS profile
                      JOIN sync_plans AS plan
                        ON plan.plan_id = sync_runs.operation_plan_id
                       AND plan.sync_profile_id = profile.sync_profile_id
                      WHERE profile.sync_profile_id = sync_runs.sync_profile_id
                        AND profile.enabled = 1
                        AND profile.profile_revision = sync_runs.profile_revision
                        AND lower(profile.policy_hash) = lower(sync_runs.profile_policy_hash)
                        AND profile.baseline_generation = plan.baseline_generation
                        AND lower(plan.plan_digest) = lower(sync_runs.plan_digest));
                """;
            command.Parameters.AddWithValue("$expectedRevision", expectedRunRevision);
            command.Parameters.AddWithValue("$runId", syncRunId.ToString());
            AddLeaseParameters(command, lease);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return SyncPersistenceMutationStatus.Conflict;
            }

            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return SyncPersistenceMutationStatus.Applied;
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<SyncPersistenceResult<SyncPreviewRecord>> TransitionAsync(
        SyncExecutionTransitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateTransition(request);
        await using var writer = await _database.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = writer.Connection.BeginTransaction(deferred: false);
        try
        {
            if (!await LeaseIsCurrentAsync(
                    writer.Connection,
                    transaction,
                    request.Lease,
                    request.SyncRunId,
                    _timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Rejected(SyncPersistenceMutationStatus.StaleLease);
            }

            var current = await SqliteSyncRunStore.ReadAsync(
                writer.Connection,
                transaction,
                request.SyncRunId,
                cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Rejected(SyncPersistenceMutationStatus.NotFound);
            }

            if (current.State.Phase != request.ExpectedPhase ||
                current.State.Revision != request.ExpectedRunRevision)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Rejected(SyncPersistenceMutationStatus.Conflict);
            }

            var next = SyncStateMachine.Transition(
                current.State,
                request.NextPhase,
                request.TransitionedAtUtc,
                request.StatusCode);
            await using var command = writer.Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE sync_runs
                SET state = $state,
                    status_code = $status,
                    run_revision = $nextRevision,
                    transitioned_utc = $transitioned,
                    safe_error_summary = $safeError
                WHERE sync_run_id = $runId
                  AND state = $expectedState
                  AND run_revision = $expectedRevision
                  AND dispatch_event_id = $eventId
                  AND execution_claim_id = $claimId
                  AND execution_owner_id = $owner
                  AND execution_fencing_token = $fence
                  AND ($requiresMutation = 0 OR provider_mutation_may_have_started = 1);
                """;
            AddStateParameters(command, current, next);
            command.Parameters.AddWithValue("$safeError", (object?)request.SafeErrorSummary ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$requiresMutation",
                request.NextPhase is SyncRunPhase.Verifying or SyncRunPhase.CommittingBaseline ? 1 : 0);
            AddLeaseParameters(command, request.Lease);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Rejected(SyncPersistenceMutationStatus.Conflict);
            }

            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return new SyncPersistenceResult<SyncPreviewRecord>(
                SyncPersistenceMutationStatus.Applied,
                current with { State = next });
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<SyncPersistenceResult<SyncPreviewRecord>> CommitBaselineAndCompleteAsync(
        SyncExecutionBaselineCommitRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateCommit(request);
        var baselineRequest = new SyncBaselineReplaceRequest(
            request.ProfileId,
            request.ExpectedBaselineRevision,
            request.NewBaselineGeneration,
            request.Items,
            request.CommittedAtUtc);
        var digest = SyncPersistenceUtilities.ComputeBaselineDigest(request.Items);

        await using var writer = await _database.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = writer.Connection.BeginTransaction(deferred: false);
        try
        {
            if (!await LeaseIsCurrentAsync(
                    writer.Connection,
                    transaction,
                    request.Lease,
                    request.SyncRunId,
                    _timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Rejected(SyncPersistenceMutationStatus.StaleLease);
            }

            var current = await SqliteSyncRunStore.ReadAsync(
                writer.Connection,
                transaction,
                request.SyncRunId,
                cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Rejected(SyncPersistenceMutationStatus.NotFound);
            }

            if (current.State.Phase != SyncRunPhase.CommittingBaseline ||
                current.State.Revision != request.ExpectedRunRevision ||
                current.ProfileId != request.ProfileId ||
                current.ProfileRevision != request.ExpectedProfileRevision ||
                !StringComparer.OrdinalIgnoreCase.Equals(
                    current.ProfilePolicySha256,
                    request.ExpectedProfilePolicySha256) ||
                !await ExactBaselineHeadMatchesAsync(
                    writer.Connection,
                    transaction,
                    request,
                    cancellationToken).ConfigureAwait(false) ||
                !await ExecutionFenceMatchesAsync(
                    writer.Connection,
                    transaction,
                    request.SyncRunId,
                    request.Lease,
                    requireMutationMarker: true,
                    cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Rejected(SyncPersistenceMutationStatus.Conflict);
            }

            var nextBaselineRevision = checked(request.ExpectedBaselineRevision + 1);
            await SqliteSyncBaselineStore.UpdateProfileAsync(
                writer.Connection,
                transaction,
                baselineRequest,
                digest,
                nextBaselineRevision,
                cancellationToken).ConfigureAwait(false);
            await SqliteSyncBaselineStore.DeleteItemsAsync(
                writer.Connection,
                transaction,
                request.ProfileId,
                cancellationToken).ConfigureAwait(false);
            foreach (var (path, observation) in request.Items.OrderBy(
                         static pair => pair.Key,
                         StringComparer.Ordinal))
            {
                await SqliteSyncBaselineStore.InsertItemAsync(
                    writer.Connection,
                    transaction,
                    baselineRequest,
                    path,
                    observation,
                    nextBaselineRevision,
                    cancellationToken).ConfigureAwait(false);
            }

            var next = SyncStateMachine.Transition(
                current.State,
                SyncRunPhase.Completed,
                request.CommittedAtUtc);
            await using (var update = writer.Connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE sync_runs
                    SET state = $state,
                        status_code = $status,
                        run_revision = $nextRevision,
                        transitioned_utc = $transitioned,
                        completed_utc = $transitioned,
                        safe_error_summary = NULL
                    WHERE sync_run_id = $runId
                      AND state = $expectedState
                      AND run_revision = $expectedRevision
                      AND dispatch_event_id = $eventId
                      AND execution_claim_id = $claimId
                      AND execution_owner_id = $owner
                      AND execution_fencing_token = $fence
                      AND provider_mutation_may_have_started = 1;
                    """;
                AddStateParameters(update, current, next);
                AddLeaseParameters(update, request.Lease);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return Rejected(SyncPersistenceMutationStatus.Conflict);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return new SyncPersistenceResult<SyncPreviewRecord>(
                SyncPersistenceMutationStatus.Applied,
                current with { State = next });
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask<SyncPreviewRecord?> TransitionInterruptedExecutionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncPreviewRecord current,
        OutboxDeliveryLease lease,
        DateTimeOffset transitionedAtUtc,
        CancellationToken cancellationToken)
    {
        var next = SyncStateMachine.Transition(
            current.State,
            SyncRunPhase.NeedsReconciliation,
            transitionedAtUtc,
            SyncStatusCode.StateUncertain);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE sync_runs
            SET state = $state,
                status_code = $status,
                run_revision = $nextRevision,
                transitioned_utc = $transitioned,
                safe_error_summary = $safeError,
                execution_claim_id = $claimId,
                execution_owner_id = $owner,
                execution_fencing_token = $fence,
                execution_bound_utc = $transitioned,
                provider_mutation_may_have_started = 1
            WHERE sync_run_id = $runId
              AND state = $expectedState
              AND run_revision = $expectedRevision
              AND dispatch_event_id = $eventId;
            """;
        AddStateParameters(command, current, next);
        command.Parameters.AddWithValue(
            "$safeError",
            "A prior apply owner stopped after execution began; provider state must be reconciled before retry.");
        AddLeaseParameters(command, lease);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1
            ? current with { State = next }
            : null;
    }

    private static async ValueTask<bool> ProfilePlanAndBaselineMatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncExecutionBeginRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM sync_profiles AS profile
            JOIN sync_plans AS plan ON plan.sync_profile_id = profile.sync_profile_id
            WHERE profile.sync_profile_id = $profileId
              AND profile.enabled = 1
              AND profile.profile_revision = $profileRevision
              AND lower(profile.policy_hash) = lower($policyHash)
              AND plan.plan_id = $planId
              AND lower(plan.plan_digest) = lower($planDigest)
              AND profile.baseline_generation = plan.baseline_generation;
            """;
        command.Parameters.AddWithValue("$profileId", request.ProfileId.ToString());
        command.Parameters.AddWithValue("$profileRevision", request.ExpectedProfileRevision);
        command.Parameters.AddWithValue("$policyHash", request.ExpectedProfilePolicySha256);
        command.Parameters.AddWithValue("$planId", request.PlanId.ToString());
        command.Parameters.AddWithValue("$planDigest", request.PlanDigest.Sha256Hex);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async ValueTask<bool> ExactBaselineHeadMatchesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncExecutionBaselineCommitRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM sync_profiles
            WHERE sync_profile_id = $profileId
              AND enabled = 1
              AND profile_revision = $profileRevision
              AND lower(policy_hash) = lower($policyHash)
              AND baseline_generation = $baselineGeneration
              AND baseline_revision = $baselineRevision;
            """;
        command.Parameters.AddWithValue("$profileId", request.ProfileId.ToString());
        command.Parameters.AddWithValue("$profileRevision", request.ExpectedProfileRevision);
        command.Parameters.AddWithValue("$policyHash", request.ExpectedProfilePolicySha256);
        command.Parameters.AddWithValue("$baselineGeneration", request.ExpectedBaselineGeneration);
        command.Parameters.AddWithValue("$baselineRevision", request.ExpectedBaselineRevision);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async ValueTask<bool> LeaseIsCurrentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        OutboxDeliveryLease lease,
        SyncRunId runId,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM outbox_events
            WHERE outbox_event_id = $eventId
              AND event_kind = $eventKind
              AND aggregate_id = $aggregateId
              AND sequence_number = $sequence
              AND safe_payload_json = $payload
              AND claim_id = $claimId
              AND claimed_by = $owner
              AND delivery_revision = $fence
              AND claim_expires_utc > $observedAt
              AND dispatched_utc IS NULL
              AND dead_lettered_utc IS NULL;
            """;
        command.Parameters.AddWithValue("$eventKind", SyncOutboxEventKinds.ApplyRequested);
        command.Parameters.AddWithValue("$aggregateId", $"sync-run:{runId}");
        command.Parameters.AddWithValue("$sequence", lease.Event.SequenceNumber);
        command.Parameters.AddWithValue("$payload", lease.Event.SafePayloadJson);
        command.Parameters.AddWithValue(
            "$observedAt",
            SyncPersistenceUtilities.FormatTimestamp(observedAtUtc));
        AddLeaseParameters(command, lease);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async ValueTask<bool> ExecutionFenceMatchesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncRunId runId,
        OutboxDeliveryLease lease,
        bool requireMutationMarker,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM sync_runs
            WHERE sync_run_id = $runId
              AND dispatch_event_id = $eventId
              AND execution_claim_id = $claimId
              AND execution_owner_id = $owner
              AND execution_fencing_token = $fence
              AND ($requireMutation = 0 OR provider_mutation_may_have_started = 1);
            """;
        command.Parameters.AddWithValue("$requireMutation", requireMutationMarker ? 1 : 0);
        command.Parameters.AddWithValue("$runId", runId.ToString());
        AddLeaseParameters(command, lease);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static bool RunMatchesImmutableBinding(
        SyncPreviewRecord current,
        SyncExecutionBeginRequest request) =>
        current.SyncRunId == request.SyncRunId &&
        current.ProfileId == request.ProfileId &&
        current.PlanId == request.PlanId &&
        current.PlanDigest == request.PlanDigest &&
        current.ProfileRevision == request.ExpectedProfileRevision &&
        StringComparer.OrdinalIgnoreCase.Equals(
            current.ProfilePolicySha256,
            request.ExpectedProfilePolicySha256) &&
        current.ApprovedForExecution &&
        current.DispatchEventId == request.Lease.Event.EventId &&
        StringComparer.OrdinalIgnoreCase.Equals(
            current.ApprovalChallengeSha256,
            request.ApprovalSha256);

    private static void AddStateParameters(
        SqliteCommand command,
        SyncPreviewRecord current,
        SyncRunState next)
    {
        command.Parameters.AddWithValue("$state", next.Phase.ToString());
        command.Parameters.AddWithValue("$status", next.StatusCode.ToString());
        command.Parameters.AddWithValue("$nextRevision", next.Revision);
        command.Parameters.AddWithValue(
            "$transitioned",
            SyncPersistenceUtilities.FormatTimestamp(next.TransitionedAtUtc));
        command.Parameters.AddWithValue("$runId", current.SyncRunId.ToString());
        command.Parameters.AddWithValue("$expectedState", current.State.Phase.ToString());
        command.Parameters.AddWithValue("$expectedRevision", current.State.Revision);
    }

    private static void AddLeaseParameters(SqliteCommand command, OutboxDeliveryLease lease)
    {
        command.Parameters.AddWithValue("$eventId", lease.Event.EventId.ToString("D"));
        command.Parameters.AddWithValue("$claimId", lease.ClaimId.ToString("D"));
        command.Parameters.AddWithValue("$owner", lease.OwnerId);
        command.Parameters.AddWithValue("$fence", lease.FencingToken);
    }

    private static void ValidateBegin(SyncExecutionBeginRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateLeaseForRun(request.Lease, request.SyncRunId);
        if (request.ProfileId.IsEmpty || request.PlanId.IsEmpty ||
            string.IsNullOrWhiteSpace(request.PlanDigest.Sha256Hex))
        {
            throw new ArgumentException("The execution identifiers are required.", nameof(request));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(request.ExpectedRunRevision);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.ExpectedProfileRevision);
        ValidateSha256(request.ExpectedProfilePolicySha256, nameof(request));
        ValidateSha256(request.ApprovalSha256, nameof(request));
        SyncPersistenceUtilities.ValidateUtc(request.TransitionedAtUtc, nameof(request));
        if (request.Lease.Event.SequenceNumber != request.ExpectedRunRevision)
        {
            throw new ArgumentException("The apply event sequence must equal the approved run revision.", nameof(request));
        }
    }

    private static void ValidateTransition(SyncExecutionTransitionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateLeaseForRun(request.Lease, request.SyncRunId);
        ArgumentOutOfRangeException.ThrowIfNegative(request.ExpectedRunRevision);
        if (!Enum.IsDefined(request.ExpectedPhase) || !Enum.IsDefined(request.NextPhase) ||
            !Enum.IsDefined(request.StatusCode))
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        if (request.NextPhase == SyncRunPhase.Completed)
        {
            throw new ArgumentException(
                "Completion must atomically commit the verified baseline.",
                nameof(request));
        }

        SyncPersistenceUtilities.ValidateUtc(request.TransitionedAtUtc, nameof(request));
        if (request.SafeErrorSummary is not null)
        {
            SyncPersistenceUtilities.ValidateText(request.SafeErrorSummary, nameof(request), 2_048);
        }
    }

    private static void ValidateCommit(SyncExecutionBaselineCommitRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateLeaseForRun(request.Lease, request.SyncRunId);
        if (request.ProfileId.IsEmpty)
        {
            throw new ArgumentException("A sync profile ID is required.", nameof(request));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(request.ExpectedRunRevision);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.ExpectedProfileRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(request.ExpectedBaselineGeneration);
        ArgumentOutOfRangeException.ThrowIfNegative(request.ExpectedBaselineRevision);
        if (request.NewBaselineGeneration != checked(request.ExpectedBaselineGeneration + 1))
        {
            throw new ArgumentException("A committed baseline must advance exactly one generation.", nameof(request));
        }

        ValidateSha256(request.ExpectedProfilePolicySha256, nameof(request));
        ArgumentNullException.ThrowIfNull(request.Items);
        SyncPersistenceUtilities.ValidateUtc(request.CommittedAtUtc, nameof(request));
        _ = SyncPersistenceUtilities.ComputeBaselineDigest(request.Items);
    }

    private static void ValidateLeaseForRun(OutboxDeliveryLease lease, SyncRunId runId)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(lease.Event);
        if (runId.IsEmpty || lease.Event.EventId == Guid.Empty || lease.ClaimId == Guid.Empty ||
            lease.FencingToken <= 0 || lease.Event.DeliveryRevision != lease.FencingToken ||
            !StringComparer.Ordinal.Equals(lease.Event.EventKind, SyncOutboxEventKinds.ApplyRequested) ||
            !StringComparer.Ordinal.Equals(lease.Event.AggregateId, $"sync-run:{runId}"))
        {
            throw new ArgumentException("The apply outbox lease identity is invalid.", nameof(lease));
        }

        SyncPersistenceUtilities.ValidateText(
            lease.OwnerId,
            nameof(lease),
            SyncPersistenceUtilities.MaximumOwnerLength);
    }

    private static void ValidateSha256(string value, string parameterName)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A SHA-256 value must contain 64 hexadecimal characters.", parameterName);
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
}
