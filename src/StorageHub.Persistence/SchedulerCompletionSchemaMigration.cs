using Microsoft.Data.Sqlite;

namespace StorageHub.Persistence;

/// <summary>Adds an immutable journal for idempotent, fenced scheduler completion writes.</summary>
public sealed class SchedulerCompletionSchemaMigration : IDatabaseMigration
{
    public const int SchemaVersion = 4;

    public int Version => SchemaVersion;

    public string Name => "idempotent-scheduler-completions";

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
        CREATE TABLE sync_schedule_completions
        (
            lease_id TEXT NOT NULL PRIMARY KEY,
            sync_schedule_id TEXT NOT NULL
                REFERENCES sync_schedules(sync_schedule_id) ON DELETE CASCADE,
            sync_profile_id TEXT NOT NULL
                REFERENCES sync_profiles(sync_profile_id) ON DELETE CASCADE,
            fencing_token INTEGER NOT NULL CHECK (fencing_token > 0),
            scheduled_for_utc TEXT NOT NULL,
            started_utc TEXT NOT NULL,
            completed_utc TEXT NOT NULL,
            outcome TEXT NOT NULL CHECK (outcome IN ('completed', 'failed', 'cancelled')),
            error_code TEXT NULL,
            error_message TEXT NULL,
            UNIQUE (sync_schedule_id, fencing_token)
        );

        CREATE INDEX ix_sync_schedule_completions_profile_completed
            ON sync_schedule_completions(sync_profile_id, completed_utc DESC);
        """;
}
