using Microsoft.Data.Sqlite;
using Xunit;

namespace StorageHub.Persistence.Tests;

public sealed class DatabaseFoundationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"storagehub-db-{Guid.NewGuid():N}");

    [Fact]
    public async Task Initialize_creates_versioned_schema_with_durable_settings()
    {
        var options = Options();
        var result = await new StorageHubDatabaseInitializer(options).InitializeAsync();

        Assert.True(result.IsReady, result.Message);
        Assert.Equal(PortableChecksumEvidenceSchemaMigration.SchemaVersion, result.SchemaVersion);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await ApplyConnectionPragmasAsync(connection);

        Assert.Equal("wal", await ScalarTextAsync(connection, "PRAGMA journal_mode;"));
        Assert.Equal(1L, await ScalarInt64Async(connection, "PRAGMA foreign_keys;"));
        Assert.Equal(2L, await ScalarInt64Async(connection, "PRAGMA synchronous;"));
        Assert.Equal(PortableChecksumEvidenceSchemaMigration.SchemaVersion, await ScalarInt64Async(connection, "PRAGMA user_version;"));

        var requiredTables = new[]
        {
            "schema_migrations", "connection_profiles", "credential_references", "profile_credentials",
            "trust_records", "transfer_jobs", "transfer_attempts", "transfer_checkpoints", "sync_profiles",
            "sync_schedules", "sync_schedule_completions", "sync_runs", "sync_operations",
            "sync_item_state", "sync_plans", "sync_plan_operations", "conflict_records",
            "audit_events", "outbox_events"
        };
        foreach (var table in requiredTables)
        {
            Assert.Equal(1L, await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = $name;",
                ("$name", table)));
        }
    }

    [Fact]
    public async Task Initialize_is_idempotent_and_records_each_migration_once()
    {
        var options = Options();
        var initializer = new StorageHubDatabaseInitializer(options);

        Assert.True((await initializer.InitializeAsync()).IsReady);
        Assert.True((await initializer.InitializeAsync()).IsReady);

        await using var connection = await OpenConfiguredAsync(options.DatabasePath);
        Assert.Equal(8L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM schema_migrations;"));
    }

    [Fact]
    public async Task Concurrent_initializers_leave_one_complete_migration_journal()
    {
        var options = Options();
        var initializers = Enumerable.Range(0, 8)
            .Select(_ => new StorageHubDatabaseInitializer(options))
            .ToArray();

        var results = await Task.WhenAll(initializers.Select(initializer => initializer.InitializeAsync()));

        Assert.All(results, result =>
        {
            Assert.True(result.IsReady, result.Message);
            Assert.Equal(PortableChecksumEvidenceSchemaMigration.SchemaVersion, result.SchemaVersion);
        });
        await using var connection = await OpenConfiguredAsync(options.DatabasePath);
        Assert.Equal(8L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal(8L, await ScalarInt64Async(connection, "PRAGMA user_version;"));
    }

    [Fact]
    public async Task Concurrent_connection_migrators_wait_then_reread_version_inside_writer_lock()
    {
        Directory.CreateDirectory(_directory);
        var databasePath = Options().DatabasePath;
        await using var firstConnection = await OpenCreateConfiguredAsync(databasePath);
        await using var secondConnection = await OpenCreateConfiguredAsync(databasePath);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applyCount = 0;
        var migration = new TestMigration(
            1,
            "serialized",
            async (database, transaction, cancellationToken) =>
            {
                Interlocked.Increment(ref applyCount);
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                await using var command = database.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "CREATE TABLE serialized_marker(value INTEGER NOT NULL);";
                await command.ExecuteNonQueryAsync(cancellationToken);
            });
        var firstMigrator = new SqliteMigrator([migration]);
        var secondMigrator = new SqliteMigrator([migration]);

        var firstTask = firstMigrator.MigrateAsync(firstConnection);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondTask = Task.Run(() => secondMigrator.MigrateAsync(secondConnection));
        await Task.Delay(100);
        Assert.False(secondTask.IsCompleted);

        release.TrySetResult();
        var versions = await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([1, 1], versions);
        Assert.Equal(1, Volatile.Read(ref applyCount));
        Assert.Equal(1L, await ScalarInt64Async(
            firstConnection,
            "SELECT COUNT(*) FROM schema_migrations WHERE version = 1 AND name = 'serialized';"));
    }

    [Fact]
    public async Task Version_one_database_upgrades_to_latest_schema_without_replacing_data()
    {
        var options = Options();
        var versionOne = await new StorageHubDatabaseInitializer(
            options,
            [new InitialSchemaMigration()]).InitializeAsync();
        Assert.True(versionOne.IsReady);
        Assert.Equal(InitialSchemaMigration.SchemaVersion, versionOne.SchemaVersion);
        var writer = new SingleWriterSqliteDatabase(options);
        await using (var lease = await writer.AcquireWriterAsync())
        {
            await using var command = lease.Connection.CreateCommand();
            command.CommandText = """
                INSERT INTO application_settings(setting_key, non_secret_value_json, updated_utc)
                VALUES ('migration-marker', '{"preserved":true}', $now);
                """;
            command.Parameters.AddWithValue(
                "$now",
                DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            _ = await command.ExecuteNonQueryAsync();
        }

        var upgraded = await new StorageHubDatabaseInitializer(options).InitializeAsync();

        Assert.True(upgraded.IsReady, upgraded.Message);
        Assert.Equal(PortableChecksumEvidenceSchemaMigration.SchemaVersion, upgraded.SchemaVersion);
        await using var connection = await OpenConfiguredAsync(options.DatabasePath);
        Assert.Equal(1L, await ScalarInt64Async(
            connection,
            "SELECT COUNT(*) FROM application_settings WHERE setting_key = 'migration-marker';"));
        Assert.Equal(1L, await ScalarInt64Async(
            connection,
            "SELECT COUNT(*) FROM pragma_table_info('sync_schedules') WHERE name = 'active_lease_fencing_token';"));
        Assert.Equal(1L, await ScalarInt64Async(
            connection,
            "SELECT COUNT(*) FROM pragma_table_info('transfer_jobs') WHERE name = 'state_revision';"));
        Assert.Equal(1L, await ScalarInt64Async(
            connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'sync_schedule_completions';"));
    }

    [Fact]
    public async Task Corrupt_database_returns_recovery_result_without_replacing_file()
    {
        Directory.CreateDirectory(_directory);
        var options = Options();
        var corruptBytes = "not-a-sqlite-database"u8.ToArray();
        await File.WriteAllBytesAsync(options.DatabasePath, corruptBytes);

        var result = await new StorageHubDatabaseInitializer(options).InitializeAsync();

        Assert.False(result.IsReady);
        Assert.Equal(DatabaseRecoveryReason.DatabaseUnreadable, result.RecoveryReason);
        Assert.Equal(corruptBytes, await File.ReadAllBytesAsync(options.DatabasePath));
    }

    [Fact]
    public async Task Credential_table_can_store_only_opaque_metadata_not_secret_values()
    {
        var options = Options();
        Assert.True((await new StorageHubDatabaseInitializer(options).InitializeAsync()).IsReady);
        await using var connection = await OpenConfiguredAsync(options.DatabasePath);

        var columns = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(credential_references);";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(1));
            }
        }

        Assert.Contains("credential_id", columns);
        Assert.DoesNotContain(columns, column =>
            column.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            column.Contains("secret_value", StringComparison.OrdinalIgnoreCase) ||
            column.Contains("private_key", StringComparison.OrdinalIgnoreCase) ||
            column.Contains("access_token", StringComparison.OrdinalIgnoreCase) ||
            column.Contains("refresh_token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Writer_lease_serializes_mutating_connections()
    {
        var options = Options();
        Assert.True((await new StorageHubDatabaseInitializer(options).InitializeAsync()).IsReady);
        var database = new SingleWriterSqliteDatabase(options);

        await using var first = await database.AcquireWriterAsync();
        var secondTask = database.AcquireWriterAsync().AsTask();
        await Task.Delay(50);
        Assert.False(secondTask.IsCompleted);

        await first.DisposeAsync();
        await using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1L, await ScalarInt64Async(second.Connection, "PRAGMA foreign_keys;"));
        Assert.Equal(2L, await ScalarInt64Async(second.Connection, "PRAGMA synchronous;"));
    }

    [Fact]
    public async Task Read_connections_are_configured_and_reject_mutations()
    {
        var options = Options();
        Assert.True((await new StorageHubDatabaseInitializer(options).InitializeAsync()).IsReady);
        var database = new SingleWriterSqliteDatabase(options);

        await using var connection = await database.OpenReadConnectionAsync();
        Assert.Equal(1L, await ScalarInt64Async(connection, "PRAGMA foreign_keys;"));
        Assert.Equal(2L, await ScalarInt64Async(connection, "PRAGMA synchronous;"));

        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO application_settings(setting_key, non_secret_value_json, updated_utc) VALUES ('x', '{}', 'now');";
        var error = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(8, error.SqliteErrorCode);
    }

    [Fact]
    public void Migrator_rejects_noncontiguous_registered_versions()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new SqliteMigrator([
                new TestMigration(1, "one"),
                new TestMigration(3, "three")
            ]));

        Assert.Contains("contiguous", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Migrator_rejects_a_gap_in_the_persisted_journal()
    {
        await using var connection = await OpenMemoryDatabaseAsync();
        await ExecuteAsync(connection, """
            CREATE TABLE schema_migrations
            (
                version INTEGER NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                applied_utc TEXT NOT NULL
            );
            INSERT INTO schema_migrations(version, name, applied_utc) VALUES (2, 'two', 'now');
            PRAGMA user_version = 2;
            """);
        var migrator = new SqliteMigrator([
            new TestMigration(1, "one"),
            new TestMigration(2, "two")
        ]);

        var error = await Assert.ThrowsAsync<DatabaseMigrationException>(
            () => migrator.MigrateAsync(connection));

        Assert.Contains("contiguous", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Migrator_rejects_a_journal_name_that_does_not_match_registered_code()
    {
        await using var connection = await OpenMemoryDatabaseAsync();
        await ExecuteAsync(connection, """
            CREATE TABLE schema_migrations
            (
                version INTEGER NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                applied_utc TEXT NOT NULL
            );
            INSERT INTO schema_migrations(version, name, applied_utc) VALUES (1, 'substituted', 'now');
            PRAGMA user_version = 1;
            """);
        var migrator = new SqliteMigrator([new TestMigration(1, "one")]);

        var error = await Assert.ThrowsAsync<DatabaseMigrationException>(
            () => migrator.MigrateAsync(connection));

        Assert.Contains("does not match", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Failed_migration_rolls_back_schema_journal_and_user_version()
    {
        await using var connection = await OpenMemoryDatabaseAsync();
        var original = new InvalidOperationException("migration exploded");
        var migrator = new SqliteMigrator([
            new TestMigration(
                1,
                "failing",
                async (database, transaction, cancellationToken) =>
                {
                    await using var command = database.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "CREATE TABLE must_be_rolled_back(value TEXT);";
                    await command.ExecuteNonQueryAsync(cancellationToken);
                    throw original;
                })
        ]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => migrator.MigrateAsync(connection));

        Assert.Same(original, error);
        Assert.Equal(0L, await ScalarInt64Async(connection, "PRAGMA user_version;"));
        Assert.Equal(0L, await ScalarInt64Async(
            connection,
            "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal(0L, await ScalarInt64Async(
            connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'must_be_rolled_back';"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private SqliteDatabaseOptions Options() => new(Path.Combine(_directory, "storagehub.db"), pooling: false);

    private static async Task<SqliteConnection> OpenConfiguredAsync(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await ApplyConnectionPragmasAsync(connection);
        return connection;
    }

    private static async Task<SqliteConnection> OpenCreateConfiguredAsync(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        await connection.OpenAsync();
        await ApplyConnectionPragmasAsync(connection);
        await ExecuteAsync(connection, "PRAGMA busy_timeout = 5000;");
        return connection;
    }

    private static async Task ApplyConnectionPragmasAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA synchronous = FULL;";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarInt64Async(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarTextAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private static async Task<SqliteConnection> OpenMemoryDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class TestMigration(
        int version,
        string name,
        Func<SqliteConnection, SqliteTransaction, CancellationToken, ValueTask>? apply = null)
        : IDatabaseMigration
    {
        public int Version { get; } = version;
        public string Name { get; } = name;

        public ValueTask ApplyAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            CancellationToken cancellationToken = default) =>
            apply?.Invoke(connection, transaction, cancellationToken) ?? ValueTask.CompletedTask;
    }
}
