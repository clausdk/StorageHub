namespace StorageHub.Application;

public interface IApplicationRuntimeCoordinator
{
    ApplicationOperationalState State { get; }

    string? HealthMessage { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task<ApplicationRuntimeHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(State switch
        {
            ApplicationOperationalState.Ready => ApplicationRuntimeHealth.Healthy(
                HealthMessage ?? "The application runtime is ready."),
            ApplicationOperationalState.RecoveryOnly or
            ApplicationOperationalState.Created or
            ApplicationOperationalState.Initializing or
            ApplicationOperationalState.Starting or
            ApplicationOperationalState.Stopping => ApplicationRuntimeHealth.Degraded(
                HealthMessage ?? $"The application runtime is {State}."),
            _ => ApplicationRuntimeHealth.Unhealthy(
                HealthMessage ?? $"The application runtime is {State}.")
        });
}

public enum ApplicationRuntimeHealthLevel
{
    Healthy,
    Degraded,
    Unhealthy
}

public sealed record ApplicationRuntimeHealth(
    ApplicationRuntimeHealthLevel Level,
    string Message,
    IReadOnlyDictionary<string, string>? Components = null)
{
    public static ApplicationRuntimeHealth Healthy(
        string message,
        IReadOnlyDictionary<string, string>? components = null) =>
        new(ApplicationRuntimeHealthLevel.Healthy, message, components);

    public static ApplicationRuntimeHealth Degraded(
        string message,
        IReadOnlyDictionary<string, string>? components = null) =>
        new(ApplicationRuntimeHealthLevel.Degraded, message, components);

    public static ApplicationRuntimeHealth Unhealthy(
        string message,
        IReadOnlyDictionary<string, string>? components = null) =>
        new(ApplicationRuntimeHealthLevel.Unhealthy, message, components);
}
