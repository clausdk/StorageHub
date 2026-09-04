using Microsoft.Data.Sqlite;

namespace StorageHub.Persistence;

/// <summary>Allows durable queue intents to address agent-owned, root-validated local endpoints
/// that are not user-visible saved connection profiles.</summary>
public sealed class LocalTransferEndpointsSchemaMigration : IDatabaseMigration
{
    public const int SchemaVersion = 12;
    public int Version => SchemaVersion;
    public string Name => "local-transfer-endpoints";

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
        CREATE TABLE transfer_jobs_v12
        (
            transfer_job_id TEXT NOT NULL PRIMARY KEY,
            source_profile_id TEXT NOT NULL,
            destination_profile_id TEXT NOT NULL,
            source_path TEXT NOT NULL,
            destination_path TEXT NOT NULL,
            operation_kind TEXT NOT NULL,
            state TEXT NOT NULL,
            priority INTEGER NOT NULL DEFAULT 0,
            expected_size INTEGER NULL CHECK (expected_size IS NULL OR expected_size >= 0),
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL,
            owner_epoch INTEGER NOT NULL DEFAULT 0 CHECK (owner_epoch >= 0),
            claimed_by TEXT NULL,
            claim_expires_utc TEXT NULL,
            last_error_code TEXT NULL,
            last_error_summary TEXT NULL,
            intent_json TEXT NOT NULL DEFAULT '{}'
                CHECK (json_valid(intent_json) AND json_type(intent_json) = 'object'),
            state_revision INTEGER NOT NULL DEFAULT 0 CHECK (state_revision >= 0),
            attempt_number INTEGER NOT NULL DEFAULT 0 CHECK (attempt_number >= 0),
            status_code TEXT NULL,
            retry_not_before_utc TEXT NULL,
            claim_acquired_utc TEXT NULL
        );

        INSERT INTO transfer_jobs_v12
        SELECT transfer_job_id, source_profile_id, destination_profile_id,
               source_path, destination_path, operation_kind, state, priority,
               expected_size, created_utc, updated_utc, owner_epoch, claimed_by,
               claim_expires_utc, last_error_code, last_error_summary, intent_json,
               state_revision, attempt_number, status_code, retry_not_before_utc,
               claim_acquired_utc
        FROM transfer_jobs;

        CREATE TABLE transfer_attempts_v12
        (
            transfer_attempt_id TEXT NOT NULL PRIMARY KEY,
            transfer_job_id TEXT NOT NULL REFERENCES transfer_jobs_v12(transfer_job_id) ON DELETE CASCADE,
            attempt_number INTEGER NOT NULL CHECK (attempt_number > 0),
            started_utc TEXT NOT NULL,
            completed_utc TEXT NULL,
            outcome TEXT NULL,
            error_code TEXT NULL,
            safe_error_summary TEXT NULL,
            UNIQUE (transfer_job_id, attempt_number)
        );
        INSERT INTO transfer_attempts_v12 SELECT * FROM transfer_attempts;

        CREATE TABLE transfer_checkpoints_v12
        (
            transfer_job_id TEXT NOT NULL PRIMARY KEY REFERENCES transfer_jobs_v12(transfer_job_id) ON DELETE CASCADE,
            checkpoint_version INTEGER NOT NULL CHECK (checkpoint_version > 0),
            source_identity TEXT NULL,
            destination_identity TEXT NULL,
            expected_size INTEGER NULL CHECK (expected_size IS NULL OR expected_size >= 0),
            verified_offset INTEGER NOT NULL DEFAULT 0 CHECK (verified_offset >= 0),
            temporary_name TEXT NULL,
            multipart_upload_id TEXT NULL,
            completed_parts_json TEXT NOT NULL DEFAULT '[]' CHECK (json_valid(completed_parts_json)),
            non_secret_state_json TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(non_secret_state_json)),
            updated_utc TEXT NOT NULL
        );
        INSERT INTO transfer_checkpoints_v12 SELECT * FROM transfer_checkpoints;

        DROP TABLE transfer_attempts;
        DROP TABLE transfer_checkpoints;
        DROP TABLE transfer_jobs;
        ALTER TABLE transfer_jobs_v12 RENAME TO transfer_jobs;
        ALTER TABLE transfer_attempts_v12 RENAME TO transfer_attempts;
        ALTER TABLE transfer_checkpoints_v12 RENAME TO transfer_checkpoints;

        CREATE INDEX ix_transfer_jobs_state_priority
            ON transfer_jobs(state, priority DESC, created_utc);
        CREATE INDEX ix_transfer_jobs_claimable
            ON transfer_jobs(state, retry_not_before_utc, priority DESC, created_utc)
            WHERE claimed_by IS NULL;
        CREATE INDEX ix_transfer_jobs_expired_claim
            ON transfer_jobs(claim_expires_utc)
            WHERE claimed_by IS NOT NULL;
        CREATE INDEX ix_transfer_attempts_job
            ON transfer_attempts(transfer_job_id, attempt_number);
        """;
}
