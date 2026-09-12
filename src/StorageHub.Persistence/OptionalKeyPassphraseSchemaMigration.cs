using Microsoft.Data.Sqlite;

namespace StorageHub.Persistence;

/// <summary>
/// Allows a stored PKCS#12 certificate to carry no password.
///
/// Schema v13 made <c>passphrase_reference</c> mandatory, which assumed every certificate is
/// password protected. A PKCS#12 bundle may legitimately have none. SSH private keys still require
/// one, but that rule belongs in the domain model rather than the column, because only the kind
/// column distinguishes the two.
/// </summary>
public sealed class OptionalKeyPassphraseSchemaMigration : IDatabaseMigration
{
    public const int SchemaVersion = 14;
    public int Version => SchemaVersion;
    public string Name => "optional-key-passphrase";

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
        CREATE TABLE credential_references_v14
        (
            credential_id TEXT NOT NULL PRIMARY KEY,
            credential_kind TEXT NOT NULL
                CHECK (credential_kind IN ('pkcs12-certificate', 'ssh-private-key')),
            display_name TEXT NOT NULL COLLATE NOCASE,
            description TEXT NULL,
            tags_json TEXT NOT NULL DEFAULT '[]'
                CHECK (json_valid(tags_json) AND json_type(tags_json) = 'array'),
            material_reference TEXT NOT NULL,
            passphrase_reference TEXT NULL,
            summary_json TEXT NOT NULL
                CHECK (json_valid(summary_json) AND json_type(summary_json) = 'object'),
            version INTEGER NOT NULL CHECK (version > 0),
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL,
            last_used_utc TEXT NULL,
            -- An SSH private key StorageHub cannot decrypt is one it could never use.
            CHECK (credential_kind <> 'ssh-private-key' OR passphrase_reference IS NOT NULL)
        );

        INSERT INTO credential_references_v14
        SELECT credential_id, credential_kind, display_name, description, tags_json,
               material_reference, passphrase_reference, summary_json, version,
               created_utc, updated_utc, last_used_utc
        FROM credential_references;

        CREATE TABLE profile_credentials_v14
        (
            profile_id TEXT NOT NULL REFERENCES connection_profiles(profile_id) ON DELETE CASCADE,
            credential_slot TEXT NOT NULL,
            credential_id TEXT NOT NULL
                REFERENCES credential_references_v14(credential_id) ON DELETE RESTRICT,
            PRIMARY KEY (profile_id, credential_slot)
        );
        INSERT INTO profile_credentials_v14 SELECT * FROM profile_credentials;

        DROP TABLE profile_credentials;
        DROP TABLE credential_references;
        ALTER TABLE credential_references_v14 RENAME TO credential_references;
        ALTER TABLE profile_credentials_v14 RENAME TO profile_credentials;

        CREATE UNIQUE INDEX ux_credential_references_display_name
            ON credential_references(display_name COLLATE NOCASE);
        CREATE INDEX ix_credential_references_kind
            ON credential_references(credential_kind, display_name COLLATE NOCASE);
        CREATE INDEX ix_profile_credentials_credential
            ON profile_credentials(credential_id);
        """;
}
