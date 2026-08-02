using Microsoft.Data.Sqlite;

namespace StorageHub.Persistence;

/// <summary>Adds durable scheduler CAS, lease, fencing, queue, and outcome state.</summary>
public sealed class SchedulerSchemaMigration : IDatabaseMigration
{
    public const int SchemaVersion = 2;

    public int Version => SchemaVersion;

    public string Name => "durable-sync-scheduler";

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
        ALTER TABLE sync_schedules
            ADD COLUMN misfire_grace_seconds INTEGER NOT NULL DEFAULT 86400
            CHECK (misfire_grace_seconds > 0);
        ALTER TABLE sync_schedules
            ADD COLUMN queue_one_while_running INTEGER NOT NULL DEFAULT 1
            CHECK (queue_one_while_running IN (0, 1));
        ALTER TABLE sync_schedules ADD COLUMN queued_due_utc TEXT NULL;
        ALTER TABLE sync_schedules
            ADD COLUMN revision INTEGER NOT NULL DEFAULT 0 CHECK (revision >= 0);
        ALTER TABLE sync_schedules
            ADD COLUMN fencing_counter INTEGER NOT NULL DEFAULT 0 CHECK (fencing_counter >= 0);
        ALTER TABLE sync_schedules ADD COLUMN active_lease_id TEXT NULL;
        ALTER TABLE sync_schedules ADD COLUMN active_lease_acquired_utc TEXT NULL;
        ALTER TABLE sync_schedules ADD COLUMN active_lease_expires_utc TEXT NULL;
        ALTER TABLE sync_schedules
            ADD COLUMN active_lease_fencing_token INTEGER NULL
            CHECK (active_lease_fencing_token IS NULL OR active_lease_fencing_token > 0);
        ALTER TABLE sync_schedules ADD COLUMN last_run_started_utc TEXT NULL;
        ALTER TABLE sync_schedules ADD COLUMN last_run_completed_utc TEXT NULL;
        ALTER TABLE sync_schedules ADD COLUMN last_run_outcome TEXT NULL;
        ALTER TABLE sync_schedules ADD COLUMN last_error_code TEXT NULL;
        ALTER TABLE sync_schedules ADD COLUMN last_error_message TEXT NULL;

        CREATE UNIQUE INDEX ux_sync_schedules_active_profile_lease
            ON sync_schedules(sync_profile_id)
            WHERE active_lease_id IS NOT NULL;
        CREATE INDEX ix_sync_schedules_queued_due
            ON sync_schedules(enabled, queued_due_utc);
        """;
}
