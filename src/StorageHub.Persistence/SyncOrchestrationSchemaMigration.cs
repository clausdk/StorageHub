using Microsoft.Data.Sqlite;

namespace StorageHub.Persistence;

/// <summary>
/// Adds revisioned sync profiles, durable preview/approval state, scheduler-to-outbox fencing,
/// and entity-tag preservation for immutable plans.
/// </summary>
public sealed class SyncOrchestrationSchemaMigration : IDatabaseMigration
{
    public const int SchemaVersion = 6;

    public int Version => SchemaVersion;

    public string Name => "provider-neutral-sync-orchestration";

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
        ALTER TABLE sync_profiles
            ADD COLUMN profile_revision INTEGER NOT NULL DEFAULT 1 CHECK (profile_revision > 0);
        ALTER TABLE sync_profiles
            ADD COLUMN maximum_deletion_count INTEGER NOT NULL DEFAULT 100
            CHECK (maximum_deletion_count > 0);
        ALTER TABLE sync_profiles
            ADD COLUMN maximum_deletion_percentage TEXT NOT NULL DEFAULT '10';
        ALTER TABLE sync_profiles
            ADD COLUMN transfer_overwrite INTEGER NOT NULL DEFAULT 0
            CHECK (transfer_overwrite IN (0, 1));
        ALTER TABLE sync_profiles
            ADD COLUMN transfer_buffer_size INTEGER NOT NULL DEFAULT 65536
            CHECK (transfer_buffer_size BETWEEN 1 AND 1048576);

        ALTER TABLE sync_plan_operations ADD COLUMN source_entity_tag TEXT NULL
            CHECK (source_entity_tag IS NULL OR length(source_entity_tag) <= 8192);
        ALTER TABLE sync_plan_operations ADD COLUMN destination_entity_tag TEXT NULL
            CHECK (destination_entity_tag IS NULL OR length(destination_entity_tag) <= 8192);

        ALTER TABLE sync_runs
            ADD COLUMN run_revision INTEGER NOT NULL DEFAULT 0 CHECK (run_revision >= 0);
        ALTER TABLE sync_runs ADD COLUMN status_code TEXT NOT NULL DEFAULT 'None';
        ALTER TABLE sync_runs
            ADD COLUMN transitioned_utc TEXT NOT NULL
            DEFAULT '1970-01-01T00:00:00.0000000+00:00';
        ALTER TABLE sync_runs ADD COLUMN operation_plan_id TEXT NULL;
        ALTER TABLE sync_runs
            ADD COLUMN profile_revision INTEGER NOT NULL DEFAULT 1 CHECK (profile_revision > 0);
        ALTER TABLE sync_runs ADD COLUMN profile_policy_hash TEXT NULL;
        ALTER TABLE sync_runs ADD COLUMN execution_snapshot_json TEXT NULL;
        ALTER TABLE sync_runs ADD COLUMN approval_challenge TEXT NULL;
        ALTER TABLE sync_runs
            ADD COLUMN approved_execution INTEGER NOT NULL DEFAULT 0
            CHECK (approved_execution IN (0, 1));
        ALTER TABLE sync_runs ADD COLUMN approved_utc TEXT NULL;
        ALTER TABLE sync_runs ADD COLUMN dispatch_event_id TEXT NULL;
        ALTER TABLE sync_runs ADD COLUMN trigger_idempotency_key TEXT NULL;

        UPDATE sync_runs
        SET transitioned_utc = started_utc,
            profile_policy_hash = (
                SELECT policy_hash FROM sync_profiles
                WHERE sync_profiles.sync_profile_id = sync_runs.sync_profile_id
            );

        UPDATE conflict_records
        SET state = CASE lower(state)
            WHEN 'unresolved' THEN 'Unresolved'
            WHEN 'resolved' THEN 'Resolved'
            WHEN 'dismissed' THEN 'Dismissed'
            ELSE state
        END;

        CREATE UNIQUE INDEX ux_sync_runs_trigger_idempotency
            ON sync_runs(sync_profile_id, trigger_idempotency_key)
            WHERE trigger_idempotency_key IS NOT NULL;
        CREATE UNIQUE INDEX ux_sync_runs_operation_plan
            ON sync_runs(operation_plan_id) WHERE operation_plan_id IS NOT NULL;
        CREATE UNIQUE INDEX ux_sync_runs_dispatch_event
            ON sync_runs(dispatch_event_id) WHERE dispatch_event_id IS NOT NULL;

        CREATE TRIGGER sync_runs_orchestration_validate_insert
        BEFORE INSERT ON sync_runs
        WHEN NEW.operation_plan_id IS NOT NULL AND
             (NEW.profile_policy_hash IS NULL OR length(NEW.profile_policy_hash) <> 64 OR
              NEW.execution_snapshot_json IS NULL OR
              NOT json_valid(NEW.execution_snapshot_json) OR
              json_type(NEW.execution_snapshot_json) <> 'object' OR
              length(CAST(NEW.execution_snapshot_json AS BLOB)) > 65536 OR
              NEW.approval_challenge IS NULL OR length(NEW.approval_challenge) <> 64 OR
              NEW.trigger_idempotency_key IS NULL OR length(NEW.trigger_idempotency_key) = 0)
        BEGIN
            SELECT RAISE(ABORT, 'invalid sync preview');
        END;
        """;
}
