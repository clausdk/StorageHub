using Microsoft.Data.Sqlite;

namespace StorageHub.Persistence;

/// <summary>
/// Persists typed portable SHA-256 evidence and the digest schema that binds it into approvals.
/// Existing plans default to digest schema v2 and therefore retain their original digest.
/// </summary>
public sealed class PortableChecksumEvidenceSchemaMigration : IDatabaseMigration
{
    public const int SchemaVersion = 8;

    public int Version => SchemaVersion;

    public string Name => "portable-sha256-sync-evidence";

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
        ALTER TABLE sync_plans
            ADD COLUMN digest_schema_version INTEGER NOT NULL DEFAULT 2
            CHECK (digest_schema_version IN (2, 3));

        ALTER TABLE sync_plan_operations ADD COLUMN source_digest_algorithm TEXT NULL;
        ALTER TABLE sync_plan_operations ADD COLUMN source_digest_value TEXT NULL;
        ALTER TABLE sync_plan_operations ADD COLUMN destination_digest_algorithm TEXT NULL;
        ALTER TABLE sync_plan_operations ADD COLUMN destination_digest_value TEXT NULL;

        CREATE TRIGGER sync_plan_operations_digest_validate_insert
        BEFORE INSERT ON sync_plan_operations
        WHEN (NEW.source_digest_algorithm IS NULL) <> (NEW.source_digest_value IS NULL) OR
             (NEW.destination_digest_algorithm IS NULL) <> (NEW.destination_digest_value IS NULL) OR
             (NEW.source_digest_algorithm IS NOT NULL AND
                 (NEW.operation_kind <> 'Copy' OR NEW.source_digest_algorithm <> 'SHA256' OR
                  length(NEW.source_digest_value) <> 64 OR
                  NEW.source_digest_value GLOB '*[^0-9a-f]*')) OR
             (NEW.destination_digest_algorithm IS NOT NULL AND
                 (NEW.operation_kind <> 'Copy' OR NEW.destination_digest_algorithm <> 'SHA256' OR
                  length(NEW.destination_digest_value) <> 64 OR
                  NEW.destination_digest_value GLOB '*[^0-9a-f]*'))
        BEGIN
            SELECT RAISE(ABORT, 'invalid portable sync digest');
        END;

        CREATE TRIGGER sync_plan_operations_digest_validate_update
        BEFORE UPDATE OF operation_kind, source_digest_algorithm, source_digest_value,
                         destination_digest_algorithm, destination_digest_value
        ON sync_plan_operations
        WHEN (NEW.source_digest_algorithm IS NULL) <> (NEW.source_digest_value IS NULL) OR
             (NEW.destination_digest_algorithm IS NULL) <> (NEW.destination_digest_value IS NULL) OR
             (NEW.source_digest_algorithm IS NOT NULL AND
                 (NEW.operation_kind <> 'Copy' OR NEW.source_digest_algorithm <> 'SHA256' OR
                  length(NEW.source_digest_value) <> 64 OR
                  NEW.source_digest_value GLOB '*[^0-9a-f]*')) OR
             (NEW.destination_digest_algorithm IS NOT NULL AND
                 (NEW.operation_kind <> 'Copy' OR NEW.destination_digest_algorithm <> 'SHA256' OR
                  length(NEW.destination_digest_value) <> 64 OR
                  NEW.destination_digest_value GLOB '*[^0-9a-f]*'))
        BEGIN
            SELECT RAISE(ABORT, 'invalid portable sync digest');
        END;
        """;
}
