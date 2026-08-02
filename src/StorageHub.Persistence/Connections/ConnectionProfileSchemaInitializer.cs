using System.Diagnostics.CodeAnalysis;

namespace StorageHub.Persistence.Connections;

/// <summary>
/// Compatibility facade that ensures the authoritative versioned database migrations have
/// completed. Repositories never create isolated tables outside the migration journal.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The gate has repository lifetime; disposing it can race an in-flight lazy initialization.")]
public sealed class ConnectionProfileSchemaInitializer
{
    private readonly StorageHubDatabaseInitializer _databaseInitializer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _initialized;

    public ConnectionProfileSchemaInitializer(SqliteDatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _databaseInitializer = new StorageHubDatabaseInitializer(options);
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            var result = await _databaseInitializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
            if (!result.IsReady)
            {
                throw new InvalidOperationException(
                    $"The StorageHub database is not ready ({result.RecoveryReason}); repository access was refused.");
            }

            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
