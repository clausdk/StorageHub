using Microsoft.Data.Sqlite;

namespace StorageHub.Persistence;

/// <summary>
/// Recognizes the unrelated schema used by the earliest engineering preview, which also
/// claimed SQLite user-version 1 without writing the current migration journal. The old
/// tables are archived in place before the authoritative migrations run; no legacy row is
/// interpreted as a current-domain record.
/// </summary>
internal static class LegacyPreviewDatabaseCompatibility
{
    private const string ArchivePrefix = "legacy_preview_v1_";

    private static readonly string[] LegacyTables =
    [
        "audit_events",
        "favorites",
        "jobs",
        "plugin_state",
        "profiles",
        "run_history",
        "schedules",
        "sync_manifests",
        "tombstones",
        "transfer_items",
        "transfer_queues",
        "trusted_hosts",
        "vault_secrets",
        "workspaces"
    ];

    private static readonly IReadOnlyDictionary<string, string[]> RequiredSignatures =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["audit_events"] = ["id", "occurred_utc", "category", "action", "subject_id", "detail_json"],
            ["plugin_state"] = ["plugin_id", "enabled", "state_json", "updated_utc"],
            ["profiles"] =
            [
                "id", "provider_id", "name", "settings_json", "secret_refs_json", "tags_json", "color",
                "last_used_utc"
            ],
            ["trusted_hosts"] = ["profile_id", "host", "algorithm", "fingerprint", "trusted_utc"]
        };

    public static async Task<bool> TryArchiveAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int currentVersion,
        CancellationToken cancellationToken)
    {
        if (currentVersion != 1 ||
            await CountJournalRowsAsync(connection, transaction, cancellationToken).ConfigureAwait(false) != 0 ||
            await TableExistsAsync(connection, transaction, "connection_profiles", cancellationToken)
                .ConfigureAwait(false))
        {
            return false;
        }

        foreach (var table in LegacyTables)
        {
            if (!await TableExistsAsync(connection, transaction, table, cancellationToken).ConfigureAwait(false) ||
                await TableExistsAsync(connection, transaction, ArchivePrefix + table, cancellationToken)
                    .ConfigureAwait(false))
            {
                return false;
            }
        }

        foreach (var (table, expectedColumns) in RequiredSignatures)
        {
            var actualColumns = await ReadColumnsAsync(connection, transaction, table, cancellationToken)
                .ConfigureAwait(false);
            if (!actualColumns.SetEquals(expectedColumns))
            {
                return false;
            }
        }

        foreach (var table in LegacyTables)
        {
            await using var rename = connection.CreateCommand();
            rename.Transaction = transaction;
            rename.CommandText = $"ALTER TABLE \"{table}\" RENAME TO \"{ArchivePrefix}{table}\";";
            await rename.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var resetVersion = connection.CreateCommand();
        resetVersion.Transaction = transaction;
        resetVersion.CommandText = "PRAGMA user_version = 0;";
        await resetVersion.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task<long> CountJournalRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations;";
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L) == 1;
    }

    private static async Task<HashSet<string>> ReadColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var columns = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }
}
