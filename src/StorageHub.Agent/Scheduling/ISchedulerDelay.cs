namespace StorageHub.Agent.Scheduling;

/// <summary>Delay seam used for periodic polling and deterministic lease-renewal tests.</summary>
public interface ISchedulerDelay
{
    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class TimeProviderSchedulerDelay(TimeProvider timeProvider) : ISchedulerDelay
{
    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        new(Task.Delay(delay, timeProvider, cancellationToken));
}
