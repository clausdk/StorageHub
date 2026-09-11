namespace StorageHub.Persistence;

public sealed record SqliteDatabaseOptions
{
    public SqliteDatabaseOptions(
        string databasePath,
        int busyTimeoutMilliseconds = 5_000,
        bool pooling = true,
        int commandTimeoutSeconds = DefaultCommandTimeoutSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!Path.IsPathFullyQualified(databasePath))
        {
            throw new ArgumentException("The database path must be absolute.", nameof(databasePath));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(busyTimeoutMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(commandTimeoutSeconds);

        DatabasePath = Path.GetFullPath(databasePath);
        BusyTimeoutMilliseconds = busyTimeoutMilliseconds;
        Pooling = pooling;
        CommandTimeoutSeconds = commandTimeoutSeconds;
    }

    /// <summary>
    /// Default ADO.NET command timeout. Deliberately larger than the busy timeout: the busy
    /// timeout bounds how long SQLite waits for the write lock, while this bounds how long a
    /// statement may run once it holds that lock. Deriving one from the other made a slow
    /// statement indistinguishable from a contended one.
    /// </summary>
    public const int DefaultCommandTimeoutSeconds = 30;

    public string DatabasePath { get; }

    /// <summary>How long SQLite waits for a contended write lock, in milliseconds.</summary>
    public int BusyTimeoutMilliseconds { get; }

    public bool Pooling { get; }

    /// <summary>How long a single statement may run once it holds its lock, in seconds.</summary>
    public int CommandTimeoutSeconds { get; }
}
