namespace StorageHub.Application;

public enum ApplicationOperationalState
{
    Created,
    Initializing,
    Starting,
    Ready,
    RecoveryOnly,
    Stopping,
    Stopped,
    Faulted
}
