using Microsoft.Data.Sqlite;

namespace StorageHub.Persistence;

/// <summary>
/// Binds a sync apply run to the exact reliable-outbox claim that owns execution and records the
/// conservative point after which provider state may have changed.
/// </summary>
public sealed class SyncExecutionSchemaMigration : IDatabaseMigration
{
    public const int SchemaVersion = 7;

    public int Version => SchemaVersion;

    public string Name => "fenced-sync-outbox-execution";

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
        ALTER TABLE sync_runs ADD COLUMN execution_claim_id TEXT NULL;
        ALTER TABLE sync_runs ADD COLUMN execution_owner_id TEXT NULL;
        ALTER TABLE sync_runs
            ADD COLUMN execution_fencing_token INTEGER NULL
            CHECK (execution_fencing_token IS NULL OR execution_fencing_token > 0);
        ALTER TABLE sync_runs ADD COLUMN execution_bound_utc TEXT NULL;
        ALTER TABLE sync_runs
            ADD COLUMN provider_mutation_may_have_started INTEGER NOT NULL DEFAULT 0
            CHECK (provider_mutation_may_have_started IN (0, 1));

        CREATE INDEX ix_sync_runs_execution_claim
            ON sync_runs(execution_claim_id) WHERE execution_claim_id IS NOT NULL;
        CREATE INDEX ix_outbox_kind_delivery_due
            ON outbox_events(event_kind, next_attempt_utc, created_utc, outbox_event_id)
            WHERE dispatched_utc IS NULL AND dead_lettered_utc IS NULL;

        CREATE TRIGGER sync_runs_execution_fence_validate_insert
        BEFORE INSERT ON sync_runs
        WHEN (NEW.execution_claim_id IS NULL) <> (NEW.execution_owner_id IS NULL) OR
             (NEW.execution_claim_id IS NULL) <> (NEW.execution_fencing_token IS NULL) OR
             (NEW.execution_claim_id IS NULL) <> (NEW.execution_bound_utc IS NULL) OR
             (NEW.provider_mutation_may_have_started = 1 AND NEW.execution_claim_id IS NULL)
        BEGIN
            SELECT RAISE(ABORT, 'invalid sync execution fence');
        END;

        CREATE TRIGGER sync_runs_execution_fence_validate_update
        BEFORE UPDATE OF execution_claim_id, execution_owner_id, execution_fencing_token,
                         execution_bound_utc, provider_mutation_may_have_started ON sync_runs
        WHEN (NEW.execution_claim_id IS NULL) <> (NEW.execution_owner_id IS NULL) OR
             (NEW.execution_claim_id IS NULL) <> (NEW.execution_fencing_token IS NULL) OR
             (NEW.execution_claim_id IS NULL) <> (NEW.execution_bound_utc IS NULL) OR
             (NEW.provider_mutation_may_have_started = 1 AND NEW.execution_claim_id IS NULL)
        BEGIN
            SELECT RAISE(ABORT, 'invalid sync execution fence');
        END;
        """;
}
