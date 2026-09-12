using System.Globalization;
using Microsoft.Data.Sqlite;

namespace StorageHub.Persistence;

/// <summary>
/// Turns the v1 <c>credential_references</c> placeholder into the real key and certificate store:
/// importable PKCS#12 bundles and SSH private keys that any number of connection profiles can bind.
/// The companion <c>profile_credentials</c> table already carries <c>ON DELETE RESTRICT</c> and is
/// finally used, so an entry that is still bound cannot be deleted out from under its consumers.
/// </summary>
public sealed class KeyStoreSchemaMigration : IDatabaseMigration
{
    public const int SchemaVersion = 13;
    public int Version => SchemaVersion;
    public string Name => "key-store";

    public async ValueTask ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        // No released build ever wrote these rows: the tables were declared in v1 and never used.
        // Rebuilding is therefore safe, but only if that assumption still holds - a populated table
        // means an unrecognized writer exists, and silently dropping its data would be wrong.
        await using (var guard = connection.CreateCommand())
        {
            guard.Transaction = transaction;
            guard.CommandText = "SELECT COUNT(*) FROM credential_references;";
            var existing = Convert.ToInt64(
                await guard.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            if (existing != 0)
            {
                throw new InvalidOperationException(
                    "credential_references already holds rows written by an unrecognized component; " +
                    "the key store migration will not discard them.");
            }
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SchemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string SchemaSql = """
        DROP TABLE profile_credentials;
        DROP TABLE credential_references;

        CREATE TABLE credential_references
        (
            credential_id TEXT NOT NULL PRIMARY KEY,
            credential_kind TEXT NOT NULL
                CHECK (credential_kind IN ('pkcs12-certificate', 'ssh-private-key')),
            display_name TEXT NOT NULL COLLATE NOCASE,
            description TEXT NULL,
            tags_json TEXT NOT NULL DEFAULT '[]'
                CHECK (json_valid(tags_json) AND json_type(tags_json) = 'array'),
            material_reference TEXT NOT NULL,
            passphrase_reference TEXT NOT NULL,
            summary_json TEXT NOT NULL
                CHECK (json_valid(summary_json) AND json_type(summary_json) = 'object'),
            version INTEGER NOT NULL CHECK (version > 0),
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL,
            last_used_utc TEXT NULL
        );

        CREATE UNIQUE INDEX ux_credential_references_display_name
            ON credential_references(display_name COLLATE NOCASE);
        CREATE INDEX ix_credential_references_kind
            ON credential_references(credential_kind, display_name COLLATE NOCASE);

        CREATE TABLE profile_credentials
        (
            profile_id TEXT NOT NULL REFERENCES connection_profiles(profile_id) ON DELETE CASCADE,
            credential_slot TEXT NOT NULL,
            credential_id TEXT NOT NULL REFERENCES credential_references(credential_id) ON DELETE RESTRICT,
            PRIMARY KEY (profile_id, credential_slot)
        );

        CREATE INDEX ix_profile_credentials_credential
            ON profile_credentials(credential_id);
        """;
}
