using System.Globalization;
using System.Runtime.ExceptionServices;
using Microsoft.Data.Sqlite;

namespace StorageHub.Persistence;

public sealed class SqliteMigrator
{
    private readonly IDatabaseMigration[] _migrations;
    private readonly TimeProvider _timeProvider;

    public SqliteMigrator(IEnumerable<IDatabaseMigration> migrations, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        _migrations = migrations.OrderBy(migration => migration.Version).ToArray();
        _timeProvider = timeProvider ?? TimeProvider.System;

        for (var index = 0; index < _migrations.Length; index++)
        {
            var expectedVersion = index + 1;
            var migration = _migrations[index];
            if (migration.Version != expectedVersion)
            {
                throw new ArgumentException(
                    $"Database migrations must be contiguous from version 1; expected {expectedVersion}.",
                    nameof(migrations));
            }

            if (string.IsNullOrWhiteSpace(migration.Name))
            {
                throw new ArgumentException("Database migration names cannot be blank.", nameof(migrations));
            }
        }
    }

    public int LatestVersion => _migrations.Length == 0 ? 0 : _migrations[^1].Version;

    public async Task<int> MigrateAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();
        // This idempotent bootstrap intentionally survives a failed first migration.
        // All version-sensitive observations and writes happen under the lock below.
        await EnsureMigrationTableAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            // Acquire SQLite's cross-process writer lock before observing either
            // schema version source. A waiter must then re-read committed state.
            var currentVersion = await ReadUserVersionAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            if (currentVersion > LatestVersion)
            {
                throw new NewerDatabaseSchemaException(currentVersion, LatestVersion);
            }

            if (await LegacyPreviewDatabaseCompatibility.TryArchiveAsync(
                    connection,
                    transaction,
                    currentVersion,
                    cancellationToken).ConfigureAwait(false))
            {
                currentVersion = 0;
            }

            await ValidateMigrationJournalAsync(connection, transaction, currentVersion, cancellationToken)
                .ConfigureAwait(false);

            foreach (var migration in _migrations.Where(migration => migration.Version > currentVersion))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await migration.ApplyAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

                await using (var journal = connection.CreateCommand())
                {
                    journal.Transaction = transaction;
                    journal.CommandText = """
                        INSERT INTO schema_migrations(version, name, applied_utc)
                        VALUES ($version, $name, $appliedUtc);
                        """;
                    journal.Parameters.AddWithValue("$version", migration.Version);
                    journal.Parameters.AddWithValue("$name", migration.Name);
                    journal.Parameters.AddWithValue(
                        "$appliedUtc",
                        _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
                    await journal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await using (var version = connection.CreateCommand())
                {
                    version.Transaction = transaction;
                    version.CommandText = $"PRAGMA user_version = {migration.Version.ToString(CultureInfo.InvariantCulture)};";
                    await version.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                currentVersion = migration.Version;
            }

            cancellationToken.ThrowIfCancellationRequested();
            // Once every statement has succeeded, make commit outcome independent of caller cancellation.
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return currentVersion;
        }
        catch (Exception migrationError)
        {
            Exception? rollbackError = null;
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                rollbackError = error;
            }

            if (rollbackError is not null)
            {
                throw new DatabaseMigrationException(
                    "The database migration failed and its transaction could not be rolled back.",
                    new AggregateException(migrationError, rollbackError));
            }

            ExceptionDispatchInfo.Capture(migrationError).Throw();
            throw;
        }
    }

    private static async Task EnsureMigrationTableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations
            (
                version INTEGER NOT NULL PRIMARY KEY CHECK (version > 0),
                name TEXT NOT NULL,
                applied_utc TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadUserVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private async Task ValidateMigrationJournalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int currentVersion,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version, name FROM schema_migrations ORDER BY version;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var expectedVersion = 1;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var recordedVersion = reader.GetInt32(0);
            var recordedName = reader.GetString(1);
            if (recordedVersion != expectedVersion || recordedVersion > currentVersion)
            {
                throw new DatabaseMigrationException(
                    "The migration journal is not contiguous with the schema version.");
            }

            if (recordedVersion > _migrations.Length ||
                !string.Equals(_migrations[recordedVersion - 1].Name, recordedName, StringComparison.Ordinal))
            {
                throw new DatabaseMigrationException(
                    $"Migration journal entry {recordedVersion} does not match the registered migration.");
            }

            expectedVersion++;
        }

        if (expectedVersion - 1 != currentVersion)
        {
            throw new DatabaseMigrationException(
                "The schema version and migration journal disagree.");
        }
    }
}

public sealed class DatabaseMigrationException : Exception
{
    public DatabaseMigrationException(string message)
        : base(message)
    {
    }

    public DatabaseMigrationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class NewerDatabaseSchemaException(int databaseVersion, int supportedVersion) : Exception(
    $"Database schema version {databaseVersion} is newer than supported version {supportedVersion}.")
{
    public int DatabaseVersion { get; } = databaseVersion;
    public int SupportedVersion { get; } = supportedVersion;
}
