using Microsoft.Data.Sqlite;

namespace StorageHub.Persistence;

public sealed class InitialSchemaMigration : IDatabaseMigration
{
    public const int SchemaVersion = 1;

    public int Version => SchemaVersion;
    public string Name => "initial-storagehub-schema";

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
        CREATE TABLE provider_definitions
        (
            provider_id TEXT NOT NULL PRIMARY KEY,
            display_name TEXT NOT NULL,
            implementation_version TEXT NOT NULL,
            capabilities_json TEXT NOT NULL CHECK (json_valid(capabilities_json)),
            enabled INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1))
        );

        CREATE TABLE connection_folders
        (
            folder_id TEXT NOT NULL PRIMARY KEY,
            parent_folder_id TEXT NULL REFERENCES connection_folders(folder_id) ON DELETE RESTRICT,
            name TEXT NOT NULL,
            sort_order INTEGER NOT NULL DEFAULT 0,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL
        );

        CREATE TABLE credential_references
        (
            credential_id TEXT NOT NULL PRIMARY KEY,
            credential_kind TEXT NOT NULL,
            display_name TEXT NOT NULL,
            vault_version INTEGER NOT NULL CHECK (vault_version > 0),
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL,
            last_used_utc TEXT NULL
        );

        CREATE TABLE connection_profiles
        (
            profile_id TEXT NOT NULL PRIMARY KEY,
            provider TEXT NOT NULL CHECK (provider IN ('local', 's3', 'ftp', 'ftps', 'sftp')),
            display_name TEXT NOT NULL COLLATE NOCASE,
            folder_path TEXT NULL COLLATE NOCASE,
            tags_json TEXT NOT NULL CHECK (json_valid(tags_json) AND json_type(tags_json) = 'array'),
            metadata_json TEXT NOT NULL CHECK (json_valid(metadata_json)),
            endpoint_json TEXT NOT NULL CHECK (json_valid(endpoint_json)),
            authentication_json TEXT NOT NULL CHECK (json_valid(authentication_json)),
            operational_options_json TEXT NOT NULL CHECK (json_valid(operational_options_json)),
            is_favorite INTEGER NOT NULL CHECK (is_favorite IN (0, 1)),
            is_enabled INTEGER NOT NULL CHECK (is_enabled IN (0, 1)),
            version INTEGER NOT NULL CHECK (version > 0),
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL,
            deleted_utc TEXT NULL
        );

        CREATE TABLE profile_credentials
        (
            profile_id TEXT NOT NULL REFERENCES connection_profiles(profile_id) ON DELETE CASCADE,
            credential_slot TEXT NOT NULL,
            credential_id TEXT NOT NULL REFERENCES credential_references(credential_id) ON DELETE RESTRICT,
            PRIMARY KEY (profile_id, credential_slot)
        );

        CREATE TABLE trust_records
        (
            trust_id TEXT NOT NULL PRIMARY KEY,
            profile_id TEXT NULL REFERENCES connection_profiles(profile_id) ON DELETE CASCADE,
            artifact_kind TEXT NOT NULL CHECK (artifact_kind IN ('tls-certificate', 'ssh-host-key')),
            canonical_host TEXT NOT NULL,
            port INTEGER NOT NULL CHECK (port BETWEEN 1 AND 65535),
            algorithm TEXT NOT NULL,
            sha256_fingerprint TEXT NOT NULL,
            decision TEXT NOT NULL CHECK (decision IN ('trusted', 'rejected', 'revoked')),
            decision_source TEXT NOT NULL,
            first_seen_utc TEXT NOT NULL,
            last_seen_utc TEXT NOT NULL,
            expires_utc TEXT NULL,
            previous_fingerprint TEXT NULL,
            record_version INTEGER NOT NULL DEFAULT 1 CHECK (record_version > 0),
            UNIQUE (artifact_kind, canonical_host, port, algorithm, sha256_fingerprint)
        );

        CREATE TABLE favorite_locations
        (
            favorite_id TEXT NOT NULL PRIMARY KEY,
            profile_id TEXT NOT NULL REFERENCES connection_profiles(profile_id) ON DELETE CASCADE,
            storage_path TEXT NOT NULL,
            display_name TEXT NOT NULL,
            sort_order INTEGER NOT NULL DEFAULT 0,
            UNIQUE (profile_id, storage_path)
        );

        CREATE TABLE recent_locations
        (
            profile_id TEXT NOT NULL REFERENCES connection_profiles(profile_id) ON DELETE CASCADE,
            storage_path TEXT NOT NULL,
            last_opened_utc TEXT NOT NULL,
            PRIMARY KEY (profile_id, storage_path)
        );

        CREATE TABLE transfer_jobs
        (
            transfer_job_id TEXT NOT NULL PRIMARY KEY,
            source_profile_id TEXT NOT NULL REFERENCES connection_profiles(profile_id) ON DELETE RESTRICT,
            destination_profile_id TEXT NOT NULL REFERENCES connection_profiles(profile_id) ON DELETE RESTRICT,
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
            last_error_summary TEXT NULL
        );

        CREATE TABLE transfer_attempts
        (
            transfer_attempt_id TEXT NOT NULL PRIMARY KEY,
            transfer_job_id TEXT NOT NULL REFERENCES transfer_jobs(transfer_job_id) ON DELETE CASCADE,
            attempt_number INTEGER NOT NULL CHECK (attempt_number > 0),
            started_utc TEXT NOT NULL,
            completed_utc TEXT NULL,
            outcome TEXT NULL,
            error_code TEXT NULL,
            safe_error_summary TEXT NULL,
            UNIQUE (transfer_job_id, attempt_number)
        );

        CREATE TABLE transfer_checkpoints
        (
            transfer_job_id TEXT NOT NULL PRIMARY KEY REFERENCES transfer_jobs(transfer_job_id) ON DELETE CASCADE,
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

        CREATE TABLE sync_profiles
        (
            sync_profile_id TEXT NOT NULL PRIMARY KEY,
            display_name TEXT NOT NULL,
            left_profile_id TEXT NOT NULL REFERENCES connection_profiles(profile_id) ON DELETE RESTRICT,
            right_profile_id TEXT NOT NULL REFERENCES connection_profiles(profile_id) ON DELETE RESTRICT,
            left_root TEXT NOT NULL,
            right_root TEXT NOT NULL,
            direction TEXT NOT NULL,
            deletion_policy TEXT NOT NULL DEFAULT 'disabled',
            conflict_policy TEXT NOT NULL DEFAULT 'block',
            policy_hash TEXT NOT NULL,
            enabled INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL
        );

        CREATE TABLE sync_schedules
        (
            sync_schedule_id TEXT NOT NULL PRIMARY KEY,
            sync_profile_id TEXT NOT NULL REFERENCES sync_profiles(sync_profile_id) ON DELETE CASCADE,
            cron_expression TEXT NOT NULL,
            time_zone_id TEXT NOT NULL,
            time_zone_rule_version TEXT NULL,
            enabled INTEGER NOT NULL DEFAULT 0 CHECK (enabled IN (0, 1)),
            next_due_utc TEXT NULL,
            last_due_utc TEXT NULL,
            misfire_policy TEXT NOT NULL DEFAULT 'coalesce-one'
        );

        CREATE TABLE sync_runs
        (
            sync_run_id TEXT NOT NULL PRIMARY KEY,
            sync_profile_id TEXT NOT NULL REFERENCES sync_profiles(sync_profile_id) ON DELETE RESTRICT,
            generation INTEGER NOT NULL CHECK (generation > 0),
            trigger_kind TEXT NOT NULL,
            state TEXT NOT NULL,
            plan_digest TEXT NULL,
            started_utc TEXT NOT NULL,
            completed_utc TEXT NULL,
            safe_error_summary TEXT NULL,
            UNIQUE (sync_profile_id, generation)
        );

        CREATE TABLE sync_operations
        (
            sync_operation_id TEXT NOT NULL PRIMARY KEY,
            sync_run_id TEXT NOT NULL REFERENCES sync_runs(sync_run_id) ON DELETE CASCADE,
            operation_order INTEGER NOT NULL CHECK (operation_order >= 0),
            operation_kind TEXT NOT NULL,
            relative_path TEXT NOT NULL,
            expected_source_identity TEXT NULL,
            expected_destination_identity TEXT NULL,
            state TEXT NOT NULL,
            UNIQUE (sync_run_id, operation_order)
        );

        CREATE TABLE sync_item_state
        (
            sync_profile_id TEXT NOT NULL REFERENCES sync_profiles(sync_profile_id) ON DELETE CASCADE,
            relative_path TEXT NOT NULL,
            baseline_generation INTEGER NOT NULL CHECK (baseline_generation > 0),
            left_identity TEXT NULL,
            right_identity TEXT NULL,
            left_modified_utc TEXT NULL,
            right_modified_utc TEXT NULL,
            left_size INTEGER NULL,
            right_size INTEGER NULL,
            left_hash TEXT NULL,
            right_hash TEXT NULL,
            tombstone_side TEXT NULL,
            PRIMARY KEY (sync_profile_id, relative_path)
        );

        CREATE TABLE conflict_records
        (
            conflict_id TEXT NOT NULL PRIMARY KEY,
            sync_run_id TEXT NOT NULL REFERENCES sync_runs(sync_run_id) ON DELETE CASCADE,
            relative_path TEXT NOT NULL,
            conflict_kind TEXT NOT NULL,
            state TEXT NOT NULL DEFAULT 'unresolved',
            detected_utc TEXT NOT NULL,
            resolved_utc TEXT NULL,
            resolution TEXT NULL
        );

        CREATE TABLE notification_records
        (
            notification_id TEXT NOT NULL PRIMARY KEY,
            severity TEXT NOT NULL,
            title TEXT NOT NULL,
            safe_message TEXT NOT NULL,
            created_utc TEXT NOT NULL,
            read_utc TEXT NULL
        );

        CREATE TABLE audit_events
        (
            audit_event_id TEXT NOT NULL PRIMARY KEY,
            event_kind TEXT NOT NULL,
            actor_id TEXT NULL,
            subject_type TEXT NULL,
            subject_id TEXT NULL,
            safe_payload_json TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(safe_payload_json)),
            occurred_utc TEXT NOT NULL
        );

        CREATE TABLE application_settings
        (
            setting_key TEXT NOT NULL PRIMARY KEY,
            non_secret_value_json TEXT NOT NULL CHECK (json_valid(non_secret_value_json)),
            updated_utc TEXT NOT NULL
        );

        CREATE TABLE plugin_state
        (
            plugin_id TEXT NOT NULL PRIMARY KEY,
            enabled INTEGER NOT NULL DEFAULT 0 CHECK (enabled IN (0, 1)),
            installed_version TEXT NULL,
            non_secret_state_json TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(non_secret_state_json)),
            updated_utc TEXT NOT NULL
        );

        CREATE TABLE outbox_events
        (
            outbox_event_id TEXT NOT NULL PRIMARY KEY,
            event_kind TEXT NOT NULL,
            aggregate_id TEXT NOT NULL,
            safe_payload_json TEXT NOT NULL CHECK (json_valid(safe_payload_json)),
            sequence_number INTEGER NOT NULL CHECK (sequence_number >= 0),
            created_utc TEXT NOT NULL,
            dispatched_utc TEXT NULL
        );

        CREATE UNIQUE INDEX ux_connection_profiles_active_name
            ON connection_profiles(display_name COLLATE NOCASE)
            WHERE deleted_utc IS NULL;
        CREATE INDEX ix_connection_profiles_folder ON connection_profiles(folder_path, display_name);
        CREATE INDEX ix_connection_profiles_browse
            ON connection_profiles(deleted_utc, is_enabled, is_favorite DESC, display_name);
        CREATE INDEX ix_profile_credentials_credential ON profile_credentials(credential_id);
        CREATE INDEX ix_trust_records_endpoint ON trust_records(artifact_kind, canonical_host, port);
        CREATE INDEX ix_transfer_jobs_state_priority ON transfer_jobs(state, priority DESC, created_utc);
        CREATE INDEX ix_transfer_attempts_job ON transfer_attempts(transfer_job_id, attempt_number);
        CREATE INDEX ix_sync_schedules_due ON sync_schedules(enabled, next_due_utc);
        CREATE INDEX ix_sync_runs_profile_started ON sync_runs(sync_profile_id, started_utc DESC);
        CREATE INDEX ix_sync_operations_run_state ON sync_operations(sync_run_id, state, operation_order);
        CREATE INDEX ix_conflicts_run_state ON conflict_records(sync_run_id, state);
        CREATE INDEX ix_audit_events_occurred ON audit_events(occurred_utc DESC);
        CREATE UNIQUE INDEX ux_outbox_sequence ON outbox_events(aggregate_id, sequence_number);
        CREATE INDEX ix_outbox_pending ON outbox_events(dispatched_utc, created_utc);
        """;
}
