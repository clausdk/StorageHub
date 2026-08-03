using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace StorageHub.Persistence;

public sealed class StorageHubDatabaseInitializer
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> InitializationGates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly SqliteDatabaseOptions _options;
    private readonly SqliteMigrator _migrator;

    public StorageHubDatabaseInitializer(
        SqliteDatabaseOptions options,
        IEnumerable<IDatabaseMigration>? migrations = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _migrator = new SqliteMigrator(
            migrations ??
            [
                new InitialSchemaMigration(),
                new SchedulerSchemaMigration(),
                new TransferQueueSchemaMigration(),
                new SchedulerCompletionSchemaMigration(),
                new SyncDurabilitySchemaMigration(),
                new SyncOrchestrationSchemaMigration(),
                new SyncExecutionSchemaMigration(),
                new PortableChecksumEvidenceSchemaMigration(),
                new SymmetricSyncSchemaMigration()
            ],
            timeProvider);
    }

    public async Task<DatabaseInitializationResult> InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        var gate = InitializationGates.GetOrAdd(_options.DatabasePath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await InitializeCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<DatabaseInitializationResult> InitializeCoreAsync(CancellationToken cancellationToken)
    {
        var currentVersion = 0;
        var phase = InitializationPhase.Opening;
        try
        {
            var directory = Path.GetDirectoryName(_options.DatabasePath)!;
            Directory.CreateDirectory(directory);

            await using var connection = new SqliteConnection(
                SqliteConnectionConfiguration.BuildConnectionString(_options, SqliteOpenMode.ReadWriteCreate));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await SqliteConnectionConfiguration
                .ApplyPerConnectionSettingsAsync(connection, _options, cancellationToken)
                .ConfigureAwait(false);

            phase = InitializationPhase.Configuring;
            await using (var journal = connection.CreateCommand())
            {
                journal.CommandText = "PRAGMA journal_mode = WAL;";
                var mode = Convert.ToString(
                    await journal.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
                if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
                {
                    return DatabaseInitializationResult.RecoveryRequired(
                        DatabaseRecoveryReason.DatabaseUnreadable,
                        currentVersion,
                        "The database could not enable write-ahead logging.");
                }
            }

            phase = InitializationPhase.IntegrityChecking;
            if (!await IsIntegrityValidAsync(connection, cancellationToken).ConfigureAwait(false))
            {
                return DatabaseInitializationResult.RecoveryRequired(
                    DatabaseRecoveryReason.IntegrityCheckFailed,
                    currentVersion,
                    "The database integrity check failed; recovery is required.");
            }

            phase = InitializationPhase.Migrating;
            currentVersion = await _migrator.MigrateAsync(connection, cancellationToken).ConfigureAwait(false);

            phase = InitializationPhase.IntegrityChecking;
            if (!await IsIntegrityValidAsync(connection, cancellationToken).ConfigureAwait(false))
            {
                return DatabaseInitializationResult.RecoveryRequired(
                    DatabaseRecoveryReason.IntegrityCheckFailed,
                    currentVersion,
                    "The database integrity check failed after migration; recovery is required.");
            }

            return DatabaseInitializationResult.Ready(currentVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (NewerDatabaseSchemaException error)
        {
            return DatabaseInitializationResult.RecoveryRequired(
                DatabaseRecoveryReason.NewerSchema,
                error.DatabaseVersion,
                "The database was created by a newer StorageHub version and was left unchanged.");
        }
        catch (Exception) when (phase == InitializationPhase.Migrating)
        {
            return DatabaseInitializationResult.RecoveryRequired(
                DatabaseRecoveryReason.MigrationFailed,
                currentVersion,
                "A database migration failed; the database was not replaced.");
        }
        catch (Exception)
        {
            return DatabaseInitializationResult.RecoveryRequired(
                phase == InitializationPhase.IntegrityChecking
                    ? DatabaseRecoveryReason.IntegrityCheckFailed
                    : DatabaseRecoveryReason.DatabaseUnreadable,
                currentVersion,
                "The database could not be opened safely and was not replaced.");
        }
    }

    private static async Task<bool> IsIntegrityValidAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var foundRow = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            foundRow = true;
            if (!string.Equals(reader.GetString(0), "ok", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return foundRow;
    }

    private enum InitializationPhase
    {
        Opening,
        Configuring,
        IntegrityChecking,
        Migrating
    }
}
