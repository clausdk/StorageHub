using Microsoft.Data.Sqlite;

namespace StorageHub.Persistence;

/// <summary>Adds v2 neutral sync policy fields while retaining legacy endpoint columns.</summary>
public sealed class SymmetricSyncSchemaMigration : IDatabaseMigration
{
    public const int SchemaVersion = 9;
    public int Version => SchemaVersion;
    public string Name => "symmetric-sync-profile-v2";

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
        ALTER TABLE sync_profiles ADD COLUMN behavior TEXT NOT NULL DEFAULT 'copy-new-a-to-b';
        ALTER TABLE sync_profiles ADD COLUMN include_globs_json TEXT NOT NULL DEFAULT '[]';
        ALTER TABLE sync_profiles ADD COLUMN exclude_globs_json TEXT NOT NULL
            DEFAULT '[".storagehub",".storagehub/**","**/.storagehub/**"]';
        ALTER TABLE sync_profiles ADD COLUMN include_hidden_files INTEGER NOT NULL DEFAULT 1
            CHECK (include_hidden_files IN (0, 1));
        ALTER TABLE sync_schedules ADD COLUMN execution_mode TEXT NOT NULL DEFAULT 'preview-only'
            CHECK (execution_mode IN ('preview-only', 'safe-automatic'));

        UPDATE sync_profiles
        SET behavior = CASE
            WHEN direction IN ('two-way', 'bidirectional') AND deletion_policy = 'propagate'
                THEN 'two-way-delete'
            WHEN direction IN ('two-way', 'bidirectional') THEN 'two-way'
            WHEN direction = 'right-to-left' AND deletion_policy = 'mirror' THEN 'mirror-b-to-a'
            WHEN direction = 'right-to-left' AND transfer_overwrite = 1 THEN 'update-b-to-a'
            WHEN direction = 'right-to-left' THEN 'copy-new-b-to-a'
            WHEN deletion_policy = 'mirror' THEN 'mirror-a-to-b'
            WHEN transfer_overwrite = 1 THEN 'update-a-to-b'
            ELSE 'copy-new-a-to-b'
        END;

        DELETE FROM sync_item_state;
        UPDATE sync_profiles
        SET baseline_generation = 0,
            baseline_revision = baseline_revision + 1,
            baseline_sha256 = NULL,
            baseline_updated_utc = NULL;
        """;
}
