namespace StorageHub.Agent;

public sealed record SubsystemInitializationResult(bool IsReady, bool RequiresRecoveryMode, string? Message)
{
    public static SubsystemInitializationResult Ready() => new(true, false, null);

    public static SubsystemInitializationResult RecoveryOnly(string message) => new(false, true, message);
}
