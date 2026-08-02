using System.Globalization;
using Microsoft.Data.Sqlite;

namespace StorageHub.Persistence;

internal static class SqliteConnectionConfiguration
{
    internal static string BuildConnectionString(SqliteDatabaseOptions options, SqliteOpenMode mode) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = mode,
            Pooling = options.Pooling,
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(options.BusyTimeoutMilliseconds / 1_000d))
        }.ToString();

    internal static async Task ApplyPerConnectionSettingsAsync(
        SqliteConnection connection,
        SqliteDatabaseOptions options,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            PRAGMA foreign_keys = ON;
            PRAGMA synchronous = FULL;
            PRAGMA busy_timeout = {options.BusyTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)};
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
