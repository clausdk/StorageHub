using Microsoft.Data.Sqlite;

namespace StorageHub.Persistence;

public interface IDatabaseMigration
{
    int Version { get; }
    string Name { get; }

    ValueTask ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken = default);
}
