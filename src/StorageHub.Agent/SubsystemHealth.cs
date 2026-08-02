namespace StorageHub.Agent;

public enum SubsystemHealthLevel
{
    Healthy,
    Degraded,
    Unhealthy
}

public sealed record SubsystemHealth(SubsystemHealthLevel Level, string Message)
{
    public static SubsystemHealth Healthy(string message = "Healthy") => new(SubsystemHealthLevel.Healthy, message);

    public static SubsystemHealth Degraded(string message) => new(SubsystemHealthLevel.Degraded, message);

    public static SubsystemHealth Unhealthy(string message) => new(SubsystemHealthLevel.Unhealthy, message);
}
