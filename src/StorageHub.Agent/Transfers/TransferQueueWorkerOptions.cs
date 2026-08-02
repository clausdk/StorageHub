using StorageHub.Transfers;

namespace StorageHub.Agent.Transfers;

public sealed record TransferQueueWorkerOptions
{
    public int MaximumConcurrency { get; init; } = 2;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan LeaseRenewalInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromMinutes(5);
    public int MaximumAttempts { get; init; } = 3;
    public int BufferSize { get; init; } = BoundedStreamCopier.DefaultBufferSize;

    internal void Validate()
    {
        if (MaximumConcurrency is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumConcurrency),
                "Transfer concurrency must be between 1 and 32.");
        }

        ValidatePositive(PollInterval, nameof(PollInterval));
        ValidatePositive(LeaseDuration, nameof(LeaseDuration));
        ValidatePositive(LeaseRenewalInterval, nameof(LeaseRenewalInterval));
        ValidatePositive(CheckpointInterval, nameof(CheckpointInterval));
        ValidatePositive(InitialRetryDelay, nameof(InitialRetryDelay));
        ValidatePositive(MaximumRetryDelay, nameof(MaximumRetryDelay));
        if (LeaseDuration > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(LeaseDuration), "A transfer lease cannot exceed 24 hours.");
        }

        if (LeaseRenewalInterval >= LeaseDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(LeaseRenewalInterval),
                "Lease renewal must occur before the current lease expires.");
        }

        if (MaximumRetryDelay < InitialRetryDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumRetryDelay),
                "The maximum retry delay cannot be shorter than the initial delay.");
        }

        if (MaximumAttempts is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumAttempts), "Maximum attempts must be between 1 and 100.");
        }

        if (BufferSize is < 1 or > BoundedStreamCopier.MaximumBufferSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(BufferSize),
                $"The transfer buffer must be between 1 and {BoundedStreamCopier.MaximumBufferSize} bytes.");
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
