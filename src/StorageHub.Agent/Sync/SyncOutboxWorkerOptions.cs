namespace StorageHub.Agent.Sync;

public sealed record SyncOutboxWorkerOptions
{
    public int MaximumConcurrency { get; init; } = 1;
    public int MinimumConcurrency { get; init; } = 1;
    public bool AdaptiveConcurrency { get; init; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan LeaseRenewalInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan DefaultRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
    public int MaximumAttempts { get; init; } = 5;

    /// <summary>
    /// Upper bound for a terminal outbox write. Such a write deliberately ignores the host
    /// cancellation token so an event never stops in an ambiguous state, so it needs its own
    /// deadline; without one a wedged store would hang the worker and block shutdown.
    /// </summary>
    public TimeSpan StoreWriteTimeout { get; init; } = TimeSpan.FromSeconds(10);

    internal void Validate()
    {
        if (MaximumConcurrency is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumConcurrency),
                "Sync outbox concurrency must be between 1 and 8.");
        }

        if (MinimumConcurrency < 1 || MinimumConcurrency > MaximumConcurrency)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumConcurrency));
        }

        ValidatePositive(PollInterval, nameof(PollInterval));
        ValidatePositive(LeaseDuration, nameof(LeaseDuration));
        ValidatePositive(LeaseRenewalInterval, nameof(LeaseRenewalInterval));
        ValidatePositive(DefaultRetryDelay, nameof(DefaultRetryDelay));
        ValidatePositive(StoreWriteTimeout, nameof(StoreWriteTimeout));
        if (LeaseDuration > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(LeaseDuration), "An outbox lease cannot exceed one hour.");
        }

        if (LeaseRenewalInterval >= LeaseDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(LeaseRenewalInterval),
                "Lease renewal must occur before the outbox claim expires.");
        }

        if (MaximumAttempts is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumAttempts));
        }
    }

    private static void ValidatePositive(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, "The interval must be positive.");
        }
    }
}
