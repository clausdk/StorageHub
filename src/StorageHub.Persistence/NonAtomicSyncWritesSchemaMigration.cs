using Microsoft.Data.Sqlite;

namespace StorageHub.Persistence;

/// <summary>Adds the explicit, fail-closed compatibility policy for legacy FTP/SFTP writes.</summary>
public sealed class NonAtomicSyncWritesSchemaMigration : IDatabaseMigration
{
    public const int SchemaVersion = 11;

    public int Version => SchemaVersion;

    public string Name => "non-atomic-sync-destination-writes";

    public async ValueTask ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE sync_profiles
                ADD COLUMN allow_non_atomic_destination_writes INTEGER NOT NULL DEFAULT 0
                CHECK (allow_non_atomic_destination_writes IN (0, 1));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
