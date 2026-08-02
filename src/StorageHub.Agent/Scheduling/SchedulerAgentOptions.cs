namespace StorageHub.Agent.Scheduling;

public sealed record SchedulerAgentOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(15);

    public int MaximumConcurrency { get; init; } = 2;

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(30);

    public TimeSpan LeaseRenewalInterval { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan StoreWriteTimeout { get; init; } = TimeSpan.FromSeconds(10);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PollInterval, TimeSpan.Zero);

        if (MaximumConcurrency is < 1 or > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumConcurrency));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(LeaseDuration, TimeSpan.Zero);

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            LeaseRenewalInterval,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            LeaseRenewalInterval,
            LeaseDuration);

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(StoreWriteTimeout, TimeSpan.Zero);
    }
}
