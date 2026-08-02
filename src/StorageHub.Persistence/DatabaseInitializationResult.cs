namespace StorageHub.Persistence;

public enum DatabaseRecoveryReason
{
    None,
    DatabaseUnreadable,
    IntegrityCheckFailed,
    MigrationFailed,
    NewerSchema
}

public sealed class DatabaseRecoveryRequiredException(DatabaseRecoveryReason reason) : InvalidOperationException(
    "The StorageHub database requires recovery before this operation can continue.")
{
    public DatabaseRecoveryReason Reason { get; } = reason;
}

public sealed record DatabaseInitializationResult
{
    private DatabaseInitializationResult(
        bool isReady,
        int schemaVersion,
        DatabaseRecoveryReason recoveryReason,
        string message)
    {
        IsReady = isReady;
        SchemaVersion = schemaVersion;
        RecoveryReason = recoveryReason;
        Message = message;
    }

    public bool IsReady { get; }
    public int SchemaVersion { get; }
    public DatabaseRecoveryReason RecoveryReason { get; }
    public string Message { get; }

    public static DatabaseInitializationResult Ready(int schemaVersion) =>
        new(true, schemaVersion, DatabaseRecoveryReason.None, "The database is ready.");

    public static DatabaseInitializationResult RecoveryRequired(
        DatabaseRecoveryReason reason,
        int schemaVersion,
        string message) =>
        new(false, schemaVersion, reason, message);
}
