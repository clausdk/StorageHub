namespace StorageHub.Persistence;

public sealed record SqliteDatabaseOptions
{
    public SqliteDatabaseOptions(
        string databasePath,
        int busyTimeoutMilliseconds = 5_000,
        bool pooling = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!Path.IsPathFullyQualified(databasePath))
        {
            throw new ArgumentException("The database path must be absolute.", nameof(databasePath));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(busyTimeoutMilliseconds);

        DatabasePath = Path.GetFullPath(databasePath);
        BusyTimeoutMilliseconds = busyTimeoutMilliseconds;
        Pooling = pooling;
    }

    public string DatabasePath { get; }
    public int BusyTimeoutMilliseconds { get; }
    public bool Pooling { get; }
}
