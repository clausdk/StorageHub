using Microsoft.Data.Sqlite;

namespace StorageHub.Persistence;

/// <summary>
/// Adds revisioned sync baselines, immutable plans, optimistic conflicts, an immutable
/// audit sequence, and leased/fenced reliable outbox delivery.
/// </summary>
public sealed class SyncDurabilitySchemaMigration : IDatabaseMigration
{
    public const int SchemaVersion = 5;

    public int Version => SchemaVersion;

    public string Name => "durable-sync-state-audit-outbox";

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
            ADD COLUMN baseline_generation INTEGER NOT NULL DEFAULT 0 CHECK (baseline_generation >= 0);
        ALTER TABLE sync_profiles
            ADD COLUMN baseline_revision INTEGER NOT NULL DEFAULT 0 CHECK (baseline_revision >= 0);
        ALTER TABLE sync_profiles ADD COLUMN baseline_sha256 TEXT NULL;
        ALTER TABLE sync_profiles ADD COLUMN baseline_updated_utc TEXT NULL;

        ALTER TABLE sync_item_state
            ADD COLUMN baseline_exists INTEGER NOT NULL DEFAULT 0 CHECK (baseline_exists IN (0, 1));
        ALTER TABLE sync_item_state
            ADD COLUMN baseline_length INTEGER NOT NULL DEFAULT 0 CHECK (baseline_length >= 0);
        ALTER TABLE sync_item_state ADD COLUMN baseline_digest_algorithm TEXT NULL;
        ALTER TABLE sync_item_state ADD COLUMN baseline_digest_value TEXT NULL;
        ALTER TABLE sync_item_state ADD COLUMN baseline_left_version_id TEXT NULL;
        ALTER TABLE sync_item_state ADD COLUMN baseline_right_version_id TEXT NULL;
        ALTER TABLE sync_item_state
            ADD COLUMN record_revision INTEGER NOT NULL DEFAULT 1 CHECK (record_revision > 0);
        ALTER TABLE sync_item_state
            ADD COLUMN baseline_updated_utc TEXT NOT NULL
            DEFAULT '1970-01-01T00:00:00.0000000+00:00';

        -- Preserve legacy observations conservatively. Rows with any recorded identity,
        -- size, or hash represented an observed item; legacy hashes did not retain an
        -- algorithm and are therefore labelled rather than guessed.
        UPDATE sync_item_state
        SET baseline_exists = CASE
                WHEN left_identity IS NOT NULL OR right_identity IS NOT NULL OR
                     left_size IS NOT NULL OR right_size IS NOT NULL OR
                     left_hash IS NOT NULL OR right_hash IS NOT NULL THEN 1
                ELSE 0
            END,
            baseline_length = COALESCE(left_size, right_size, 0),
            baseline_digest_algorithm = CASE
                WHEN COALESCE(left_hash, right_hash) IS NULL THEN NULL ELSE 'LEGACY'
            END,
            baseline_digest_value = COALESCE(left_hash, right_hash),
            baseline_left_version_id = left_identity,
            baseline_right_version_id = right_identity;
        UPDATE sync_profiles
        SET baseline_generation = COALESCE(
                (SELECT MAX(baseline_generation) FROM sync_item_state
                 WHERE sync_item_state.sync_profile_id = sync_profiles.sync_profile_id),
                0),
            baseline_revision = CASE
                WHEN EXISTS (SELECT 1 FROM sync_item_state
                             WHERE sync_item_state.sync_profile_id = sync_profiles.sync_profile_id)
                THEN 1 ELSE 0
            END,
            baseline_updated_utc = CASE
                WHEN EXISTS (SELECT 1 FROM sync_item_state
                             WHERE sync_item_state.sync_profile_id = sync_profiles.sync_profile_id)
                THEN '1970-01-01T00:00:00.0000000+00:00' ELSE NULL
            END;
        UPDATE sync_item_state
        SET baseline_generation = (
                SELECT baseline_generation FROM sync_profiles
                WHERE sync_profiles.sync_profile_id = sync_item_state.sync_profile_id
            ),
            record_revision = 1;

        CREATE TABLE sync_plans
        (
            plan_id TEXT NOT NULL PRIMARY KEY,
            sync_profile_id TEXT NOT NULL
                REFERENCES sync_profiles(sync_profile_id) ON DELETE RESTRICT,
            baseline_generation INTEGER NOT NULL CHECK (baseline_generation >= 0),
            plan_digest TEXT NOT NULL CHECK (length(plan_digest) = 64),
            operation_count INTEGER NOT NULL CHECK (operation_count BETWEEN 0 AND 1000000),
            created_utc TEXT NOT NULL
        );

        CREATE TABLE sync_plan_operations
        (
            plan_id TEXT NOT NULL REFERENCES sync_plans(plan_id) ON DELETE RESTRICT,
            operation_order INTEGER NOT NULL CHECK (operation_order >= 0),
            operation_kind TEXT NOT NULL CHECK (operation_kind IN ('Copy', 'Delete', 'CreateDirectory')),
            source_profile_id TEXT NOT NULL,
            source_root_identity TEXT NOT NULL CHECK (length(source_root_identity) BETWEEN 1 AND 8192),
            source_relative_path TEXT NOT NULL CHECK (length(source_relative_path) <= 32768),
            source_native_item_id TEXT NULL CHECK (source_native_item_id IS NULL OR length(source_native_item_id) <= 8192),
            source_version_id TEXT NULL CHECK (source_version_id IS NULL OR length(source_version_id) <= 8192),
            destination_profile_id TEXT NULL,
            destination_root_identity TEXT NULL CHECK (destination_root_identity IS NULL OR length(destination_root_identity) BETWEEN 1 AND 8192),
            destination_relative_path TEXT NULL CHECK (destination_relative_path IS NULL OR length(destination_relative_path) <= 32768),
            destination_native_item_id TEXT NULL CHECK (destination_native_item_id IS NULL OR length(destination_native_item_id) <= 8192),
            destination_version_id TEXT NULL CHECK (destination_version_id IS NULL OR length(destination_version_id) <= 8192),
            expected_length INTEGER NULL CHECK (expected_length IS NULL OR expected_length >= 0),
            PRIMARY KEY (plan_id, operation_order),
            CHECK (
                (operation_kind = 'Copy' AND
                 destination_profile_id IS NOT NULL AND destination_root_identity IS NOT NULL AND
                 destination_relative_path IS NOT NULL) OR
                (operation_kind <> 'Copy' AND
                 destination_profile_id IS NULL AND destination_root_identity IS NULL AND
                 destination_relative_path IS NULL AND destination_native_item_id IS NULL AND
                 destination_version_id IS NULL)
            )
        );

        CREATE INDEX ix_sync_plans_profile_created
            ON sync_plans(sync_profile_id, created_utc DESC);

        CREATE TRIGGER sync_plans_immutable_update
        BEFORE UPDATE ON sync_plans
        BEGIN
            SELECT RAISE(ABORT, 'sync plans are immutable');
        END;
        CREATE TRIGGER sync_plans_immutable_delete
        BEFORE DELETE ON sync_plans
        BEGIN
            SELECT RAISE(ABORT, 'sync plans are immutable');
        END;
        CREATE TRIGGER sync_plan_operations_immutable_update
        BEFORE UPDATE ON sync_plan_operations
        BEGIN
            SELECT RAISE(ABORT, 'sync plan operations are immutable');
        END;
        CREATE TRIGGER sync_plan_operations_immutable_delete
        BEFORE DELETE ON sync_plan_operations
        BEGIN
            SELECT RAISE(ABORT, 'sync plan operations are immutable');
        END;

        ALTER TABLE conflict_records
            ADD COLUMN safe_details_json TEXT NOT NULL DEFAULT '{}'
            CHECK (json_valid(safe_details_json) AND json_type(safe_details_json) = 'object');
        ALTER TABLE conflict_records ADD COLUMN safe_resolution_json TEXT NULL;
        ALTER TABLE conflict_records
            ADD COLUMN record_revision INTEGER NOT NULL DEFAULT 1 CHECK (record_revision > 0);
        ALTER TABLE conflict_records
            ADD COLUMN updated_utc TEXT NOT NULL
            DEFAULT '1970-01-01T00:00:00.0000000+00:00';
        UPDATE conflict_records
        SET safe_resolution_json = CASE
                WHEN resolution IS NULL THEN NULL
                ELSE json_object('legacyResolution', resolution)
            END,
            updated_utc = COALESCE(resolved_utc, detected_utc);

        ALTER TABLE audit_events ADD COLUMN sequence_number INTEGER NULL;
        ALTER TABLE audit_events ADD COLUMN correlation_id TEXT NULL;
        ALTER TABLE audit_events ADD COLUMN idempotency_key TEXT NULL;
        UPDATE audit_events
        SET sequence_number = (
                SELECT COUNT(*) FROM audit_events AS earlier
                WHERE earlier.rowid <= audit_events.rowid
            ),
            idempotency_key = 'legacy:' || audit_event_id;
        CREATE UNIQUE INDEX ux_audit_events_sequence ON audit_events(sequence_number);
        CREATE UNIQUE INDEX ux_audit_events_idempotency ON audit_events(idempotency_key);

        CREATE TRIGGER audit_events_validate_insert
        BEFORE INSERT ON audit_events
        WHEN NEW.sequence_number IS NULL OR NEW.sequence_number <= 0 OR
             NEW.idempotency_key IS NULL OR length(NEW.idempotency_key) = 0 OR
             length(CAST(NEW.safe_payload_json AS BLOB)) > 65536
        BEGIN
            SELECT RAISE(ABORT, 'invalid audit event');
        END;
        CREATE TRIGGER audit_events_immutable_update
        BEFORE UPDATE ON audit_events
        BEGIN
            SELECT RAISE(ABORT, 'audit events are immutable');
        END;
        CREATE TRIGGER audit_events_immutable_delete
        BEFORE DELETE ON audit_events
        BEGIN
            SELECT RAISE(ABORT, 'audit events are immutable');
        END;

        ALTER TABLE outbox_events
            ADD COLUMN delivery_revision INTEGER NOT NULL DEFAULT 0 CHECK (delivery_revision >= 0);
        ALTER TABLE outbox_events
            ADD COLUMN attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0);
        ALTER TABLE outbox_events ADD COLUMN next_attempt_utc TEXT NULL;
        ALTER TABLE outbox_events ADD COLUMN claim_id TEXT NULL;
        ALTER TABLE outbox_events ADD COLUMN claimed_by TEXT NULL;
        ALTER TABLE outbox_events ADD COLUMN claim_acquired_utc TEXT NULL;
        ALTER TABLE outbox_events ADD COLUMN claim_expires_utc TEXT NULL;
        ALTER TABLE outbox_events ADD COLUMN dead_lettered_utc TEXT NULL;
        ALTER TABLE outbox_events ADD COLUMN last_error_code TEXT NULL;
        ALTER TABLE outbox_events ADD COLUMN last_error_summary TEXT NULL;

        CREATE INDEX ix_outbox_delivery_due
            ON outbox_events(next_attempt_utc, created_utc, outbox_event_id)
            WHERE dispatched_utc IS NULL AND dead_lettered_utc IS NULL;
        CREATE INDEX ix_outbox_expired_claim
            ON outbox_events(claim_expires_utc)
            WHERE claim_id IS NOT NULL;
        CREATE UNIQUE INDEX ux_outbox_claim_id
            ON outbox_events(claim_id) WHERE claim_id IS NOT NULL;

        CREATE TRIGGER outbox_events_validate_insert
        BEFORE INSERT ON outbox_events
        WHEN length(CAST(NEW.safe_payload_json AS BLOB)) > 65536
        BEGIN
            SELECT RAISE(ABORT, 'invalid outbox event');
        END;

        CREATE TRIGGER sync_item_state_validate_insert
        BEFORE INSERT ON sync_item_state
        WHEN length(NEW.relative_path) > 32768 OR
             length(COALESCE(NEW.baseline_digest_algorithm, '')) > 64 OR
             length(COALESCE(NEW.baseline_digest_value, '')) > 8192 OR
             length(COALESCE(NEW.baseline_left_version_id, '')) > 8192 OR
             length(COALESCE(NEW.baseline_right_version_id, '')) > 8192
        BEGIN
            SELECT RAISE(ABORT, 'invalid sync baseline item');
        END;
        """;
}
