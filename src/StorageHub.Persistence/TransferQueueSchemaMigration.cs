using Microsoft.Data.Sqlite;

namespace StorageHub.Persistence;

/// <summary>Adds immutable intent data, optimistic revisions, and retry/lease metadata.</summary>
public sealed class TransferQueueSchemaMigration : IDatabaseMigration
{
    public const int SchemaVersion = 3;

    public int Version => SchemaVersion;

    public string Name => "durable-transfer-queue";

    public async ValueTask ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SchemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string SchemaSql = """
        ALTER TABLE transfer_jobs
            ADD COLUMN intent_json TEXT NOT NULL DEFAULT '{}'
            CHECK (json_valid(intent_json) AND json_type(intent_json) = 'object');
        ALTER TABLE transfer_jobs
            ADD COLUMN state_revision INTEGER NOT NULL DEFAULT 0 CHECK (state_revision >= 0);
        ALTER TABLE transfer_jobs
            ADD COLUMN attempt_number INTEGER NOT NULL DEFAULT 0 CHECK (attempt_number >= 0);
        ALTER TABLE transfer_jobs ADD COLUMN status_code TEXT NULL;
        ALTER TABLE transfer_jobs ADD COLUMN retry_not_before_utc TEXT NULL;
        ALTER TABLE transfer_jobs ADD COLUMN claim_acquired_utc TEXT NULL;

        -- Schema versions 1-2 did not contain immutable root identities or verification policy.
        -- Preserve their indexed intent for inspection, but fail closed by forcing every
        -- non-terminal legacy job into reconciliation instead of making it executable.
        UPDATE transfer_jobs
        SET
            intent_json = json_object(
                'version', 1,
                'operation', CASE lower(operation_kind)
                    WHEN 'move' THEN 'Move'
                    ELSE 'Copy'
                END,
                'source', json_object(
                    'profileId', source_profile_id,
                    'rootIdentity', 'legacy-unverified:' || source_profile_id,
                    'canonicalRelativePath', source_path,
                    'nativeItemId', NULL,
                    'versionId', NULL),
                'destination', json_object(
                    'profileId', destination_profile_id,
                    'rootIdentity', 'legacy-unverified:' || destination_profile_id,
                    'canonicalRelativePath', destination_path,
                    'nativeItemId', NULL,
                    'versionId', NULL),
                'expectedLength', expected_size,
                'verificationPolicy', 'Size',
                'createdAtUtc', created_utc,
                'expectedDestinationVersionId', NULL),
            attempt_number = COALESCE(
                (SELECT MAX(attempt_number)
                 FROM transfer_attempts
                 WHERE transfer_attempts.transfer_job_id = transfer_jobs.transfer_job_id),
                0),
            state = CASE lower(state)
                WHEN 'completed' THEN 'Completed'
                WHEN 'cancelled' THEN 'Cancelled'
                ELSE 'NeedsReconciliation'
            END,
            status_code = CASE lower(state)
                WHEN 'completed' THEN NULL
                WHEN 'cancelled' THEN NULL
                ELSE 'StateUncertain'
            END,
            state_revision = CASE lower(state)
                WHEN 'completed' THEN 0
                WHEN 'cancelled' THEN 0
                ELSE 1
            END,
            claimed_by = NULL,
            claim_expires_utc = NULL,
            retry_not_before_utc = NULL,
            last_error_code = CASE lower(state)
                WHEN 'completed' THEN last_error_code
                WHEN 'cancelled' THEN last_error_code
                ELSE 'transfer.migration.reconciliation_required'
            END,
            last_error_summary = CASE lower(state)
                WHEN 'completed' THEN last_error_summary
                WHEN 'cancelled' THEN last_error_summary
                ELSE 'This legacy transfer requires reconciliation because its original root identity was not recorded.'
            END;

        CREATE INDEX ix_transfer_jobs_claimable
            ON transfer_jobs(state, retry_not_before_utc, priority DESC, created_utc)
            WHERE claimed_by IS NULL;
        CREATE INDEX ix_transfer_jobs_expired_claim
            ON transfer_jobs(claim_expires_utc)
            WHERE claimed_by IS NOT NULL;
        """;
}
