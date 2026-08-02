using Microsoft.Data.Sqlite;
using StorageHub.Persistence;

namespace StorageHub.Agent.Windows;

internal sealed class DatabaseAgentSubsystem(SqliteDatabaseOptions options) : IAgentSubsystem
{
    private readonly SqliteDatabaseOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private DatabaseInitializationResult? _initialization;
    private bool _running;

    public string Name => "StorageHub database";

    public bool CanRunInRecoveryMode => true;

    public SingleWriterSqliteDatabase? Database { get; private set; }

    public async Task<SubsystemInitializationResult> InitializeAsync(CancellationToken cancellationToken)
    {
        _initialization = await new StorageHubDatabaseInitializer(_options)
            .InitializeAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!_initialization.IsReady)
        {
            return SubsystemInitializationResult.RecoveryOnly(_initialization.Message);
        }

        Database = new SingleWriterSqliteDatabase(_options);
        return SubsystemInitializationResult.Ready();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _running = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _running = false;
        return Task.CompletedTask;
    }

    public async Task<SubsystemHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        if (_initialization is null)
        {
            return SubsystemHealth.Unhealthy("The database has not been initialized.");
        }

        if (!_initialization.IsReady)
        {
            return SubsystemHealth.Degraded(_initialization.Message);
        }

        if (!_running || Database is null)
        {
            return SubsystemHealth.Degraded("The database subsystem is stopped.");
        }

        try
        {
            await using var connection = await Database
                .OpenReadConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return SubsystemHealth.Healthy($"SQLite schema {_initialization.SchemaVersion} is ready.");
        }
        catch (SqliteException)
        {
            return SubsystemHealth.Unhealthy("The StorageHub database health query failed.");
        }
    }
}
