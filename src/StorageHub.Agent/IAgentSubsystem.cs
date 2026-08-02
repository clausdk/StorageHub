namespace StorageHub.Agent;

public interface IAgentSubsystem
{
    string Name { get; }

    bool CanRunInRecoveryMode { get; }

    Task<SubsystemInitializationResult> InitializeAsync(CancellationToken cancellationToken);

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    Task<SubsystemHealth> CheckHealthAsync(CancellationToken cancellationToken);
}
