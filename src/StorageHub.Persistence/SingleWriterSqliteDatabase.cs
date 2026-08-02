using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace StorageHub.Persistence;

public sealed class SingleWriterSqliteDatabase
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriterGates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly SqliteDatabaseOptions _options;
    private readonly SemaphoreSlim _writerGate;

    public SingleWriterSqliteDatabase(SqliteDatabaseOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _writerGate = WriterGates.GetOrAdd(options.DatabasePath, static _ => new SemaphoreSlim(1, 1));
    }

    public async ValueTask<SqliteConnection> OpenReadConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(
            SqliteConnectionConfiguration.BuildConnectionString(_options, SqliteOpenMode.ReadOnly));
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await SqliteConnectionConfiguration
                .ApplyPerConnectionSettingsAsync(connection, _options, cancellationToken)
                .ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<SqliteWriteLease> AcquireWriterAsync(
        CancellationToken cancellationToken = default)
    {
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection? connection = null;
        try
        {
            connection = new SqliteConnection(
                SqliteConnectionConfiguration.BuildConnectionString(_options, SqliteOpenMode.ReadWrite));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await SqliteConnectionConfiguration
                .ApplyPerConnectionSettingsAsync(connection, _options, cancellationToken)
                .ConfigureAwait(false);
            var lease = new SqliteWriteLease(connection, _writerGate);
            connection = null;
            return lease;
        }
        catch
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            _writerGate.Release();
            throw;
        }
    }
}

public sealed class SqliteWriteLease : IAsyncDisposable
{
    private SqliteConnection? _connection;
    private SemaphoreSlim? _writerGate;

    internal SqliteWriteLease(SqliteConnection connection, SemaphoreSlim writerGate)
    {
        _connection = connection;
        _writerGate = writerGate;
    }

    public SqliteConnection Connection =>
        _connection ?? throw new ObjectDisposedException(nameof(SqliteWriteLease));

    public async ValueTask DisposeAsync()
    {
        var connection = Interlocked.Exchange(ref _connection, null);
        var writerGate = Interlocked.Exchange(ref _writerGate, null);
        if (connection is null)
        {
            return;
        }

        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            writerGate!.Release();
        }
    }
}
