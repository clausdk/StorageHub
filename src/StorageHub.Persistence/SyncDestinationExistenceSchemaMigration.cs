using Microsoft.Data.Sqlite;

namespace StorageHub.Persistence;

/// <summary>
/// Binds each copy operation to whether its destination existed in the approved snapshot.
/// This lets one sync plan safely mix atomic creates with conditional overwrites.
/// </summary>
public sealed class SyncDestinationExistenceSchemaMigration : IDatabaseMigration
{
    public const int SchemaVersion = 10;

    public int Version => SchemaVersion;

    public string Name => "sync-destination-existence-evidence";

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
            ADD COLUMN digest_schema_version_v4 INTEGER NOT NULL DEFAULT 2
            CHECK (digest_schema_version_v4 IN (2, 3, 4));
        UPDATE sync_plans SET digest_schema_version_v4 = digest_schema_version;
        ALTER TABLE sync_plans DROP COLUMN digest_schema_version;
        ALTER TABLE sync_plans RENAME COLUMN digest_schema_version_v4 TO digest_schema_version;

        ALTER TABLE sync_plan_operations
            ADD COLUMN destination_existed INTEGER NOT NULL DEFAULT 0
            CHECK (destination_existed IN (0, 1));
        """;
}
