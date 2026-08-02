using CodeLogic.Framework.Application;
using CodeLogic.Framework.Libraries;

namespace StorageHub.Application;

public sealed class StorageHubApplication(IApplicationRuntimeCoordinator coordinator) : IApplication
{
    private readonly IApplicationRuntimeCoordinator _coordinator = coordinator;

    public ApplicationManifest Manifest { get; } = new()
    {
        Id = "storagehub",
        Name = "StorageHub",
        Version = "0.1.0",
        Description = "Secure file management, transfer, and synchronization",
        Author = "StorageHub Contributors"
    };

    public Task OnConfigureAsync(ApplicationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Configuration.Register<StorageHubApplicationConfig>();
        context.Localization.Register<StorageHubStrings>();
        context.Logger.Info("StorageHub registered non-secret configuration and localization models");
        return Task.CompletedTask;
    }

    public async Task OnInitializeAsync(ApplicationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Logger.Info("StorageHub application services are initializing");
        await _coordinator.InitializeAsync().ConfigureAwait(false);
    }

    public async Task OnStartAsync(ApplicationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        await _coordinator.StartAsync().ConfigureAwait(false);
        context.Logger.Info("StorageHub application services started");
    }

    public Task OnStopAsync() => _coordinator.StopAsync();

    public async Task<HealthStatus> HealthCheckAsync()
    {
        var runtime = await _coordinator.CheckHealthAsync().ConfigureAwait(false);
        return runtime.Level switch
        {
            ApplicationRuntimeHealthLevel.Healthy => HealthStatus.Healthy(runtime.Message),
            ApplicationRuntimeHealthLevel.Degraded => HealthStatus.Degraded(runtime.Message),
            _ => HealthStatus.Unhealthy(runtime.Message)
        };
    }
}
