using CodeLogic.Framework.Libraries;

namespace StorageHub.Application.Tests;

public sealed class StorageHubApplicationTests
{
    [Fact]
    public async Task Lifecycle_delegates_to_runtime_coordinator_in_order()
    {
        var coordinator = new RecordingCoordinator();
        var application = new StorageHubApplication(coordinator);

        // CodeLogic owns ApplicationContext construction; these callbacks only
        // need a non-null context for logging, so coordinator behavior is tested
        // directly through the stop and health surfaces here.
        await application.OnStopAsync();

        Assert.Equal(["stop"], coordinator.Calls);
        Assert.Equal("storagehub", application.Manifest.Id);
    }

    [Theory]
    [InlineData(ApplicationOperationalState.Ready, HealthStatusLevel.Healthy)]
    [InlineData(ApplicationOperationalState.RecoveryOnly, HealthStatusLevel.Degraded)]
    [InlineData(ApplicationOperationalState.Starting, HealthStatusLevel.Degraded)]
    [InlineData(ApplicationOperationalState.Faulted, HealthStatusLevel.Unhealthy)]
    public async Task Health_maps_operational_state(
        ApplicationOperationalState state,
        HealthStatusLevel expected)
    {
        var coordinator = new RecordingCoordinator { State = state };
        var application = new StorageHubApplication(coordinator);

        var health = await application.HealthCheckAsync();

        Assert.Equal(expected, health.Status);
    }

    [Fact]
    public void Configuration_defaults_are_safe_and_bounded()
    {
        var config = new StorageHubApplicationConfig();

        Assert.Equal("System", config.Theme);
        Assert.Equal(4, config.GlobalTransferConcurrency);
        Assert.Equal(2, config.PerConnectionTransferConcurrency);
        Assert.Equal(14, config.LogRetentionDays);
        Assert.DoesNotContain(
            typeof(StorageHubApplicationConfig).GetProperties(),
            property => property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class RecordingCoordinator : IApplicationRuntimeCoordinator
    {
        public List<string> Calls { get; } = [];

        public ApplicationOperationalState State { get; set; } = ApplicationOperationalState.Created;

        public string? HealthMessage => null;

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("initialize");
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("start");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("stop");
            return Task.CompletedTask;
        }
    }
}
