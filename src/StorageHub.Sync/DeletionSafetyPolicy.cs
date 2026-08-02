namespace StorageHub.Sync;

public sealed record SnapshotCompleteness
{
    public SnapshotCompleteness(
        bool endpointAvailable,
        bool rootIdentityVerified,
        bool enumerationCompleted,
        bool paginationCompleted,
        bool permissionsIntact,
        bool unexpectedlyEmpty,
        long totalItemCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalItemCount);

        EndpointAvailable = endpointAvailable;
        RootIdentityVerified = rootIdentityVerified;
        EnumerationCompleted = enumerationCompleted;
        PaginationCompleted = paginationCompleted;
        PermissionsIntact = permissionsIntact;
        UnexpectedlyEmpty = unexpectedlyEmpty;
        TotalItemCount = totalItemCount;
    }

    public bool EndpointAvailable { get; }

    public bool RootIdentityVerified { get; }

    public bool EnumerationCompleted { get; }

    public bool PaginationCompleted { get; }

    public bool PermissionsIntact { get; }

    public bool UnexpectedlyEmpty { get; }

    public long TotalItemCount { get; }

    public bool IsComplete =>
        EndpointAvailable &&
        RootIdentityVerified &&
        EnumerationCompleted &&
        PaginationCompleted &&
        PermissionsIntact &&
        !UnexpectedlyEmpty;

    public static SnapshotCompleteness Complete(long totalItemCount) =>
        new(
            endpointAvailable: true,
            rootIdentityVerified: true,
            enumerationCompleted: true,
            paginationCompleted: true,
            permissionsIntact: true,
            unexpectedlyEmpty: false,
            totalItemCount);
}

public enum DeletionBlockReason
{
    None = 0,
    EndpointUnavailable = 1,
    RootIdentityUnverified = 2,
    EnumerationIncomplete = 3,
    PaginationIncomplete = 4,
    PermissionErrors = 5,
    UnexpectedEmptyRoot = 6,
    MissingBaseline = 7,
    CountLimitExceeded = 8,
    PercentageLimitExceeded = 9,
}

public readonly record struct DeletionSafetyDecision(
    bool Allowed,
    DeletionBlockReason Reason,
    decimal PlannedDeletionPercentage);

public sealed class DeletionSafetyPolicy
{
    public const int DefaultMaximumDeletionCount = 100;

    public const decimal DefaultMaximumDeletionPercentage = 10m;

    public static DeletionSafetyPolicy Default { get; } =
        new(DefaultMaximumDeletionCount, DefaultMaximumDeletionPercentage);

    public DeletionSafetyPolicy(int maximumDeletionCount, decimal maximumDeletionPercentage)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDeletionCount);

        if (maximumDeletionPercentage is <= 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDeletionPercentage));
        }

        MaximumDeletionCount = maximumDeletionCount;
        MaximumDeletionPercentage = maximumDeletionPercentage;
    }

    public int MaximumDeletionCount { get; }

    public decimal MaximumDeletionPercentage { get; }

    public DeletionSafetyDecision Evaluate(
        long plannedDeletionCount,
        long baselineItemCount,
        SnapshotCompleteness leftSnapshot,
        SnapshotCompleteness rightSnapshot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(plannedDeletionCount);
        ArgumentOutOfRangeException.ThrowIfNegative(baselineItemCount);
        ArgumentNullException.ThrowIfNull(leftSnapshot);
        ArgumentNullException.ThrowIfNull(rightSnapshot);

        if (plannedDeletionCount == 0)
        {
            return new DeletionSafetyDecision(true, DeletionBlockReason.None, 0);
        }

        var incompleteReason = GetIncompleteReason(leftSnapshot);
        if (incompleteReason == DeletionBlockReason.None)
        {
            incompleteReason = GetIncompleteReason(rightSnapshot);
        }

        if (incompleteReason != DeletionBlockReason.None)
        {
            return new DeletionSafetyDecision(false, incompleteReason, 0);
        }

        if (baselineItemCount == 0)
        {
            return new DeletionSafetyDecision(false, DeletionBlockReason.MissingBaseline, 100);
        }

        if (leftSnapshot.TotalItemCount == 0 || rightSnapshot.TotalItemCount == 0)
        {
            return new DeletionSafetyDecision(
                false,
                DeletionBlockReason.UnexpectedEmptyRoot,
                100);
        }

        var percentage = plannedDeletionCount * 100m / baselineItemCount;

        if (plannedDeletionCount > MaximumDeletionCount)
        {
            return new DeletionSafetyDecision(
                false,
                DeletionBlockReason.CountLimitExceeded,
                percentage);
        }

        if (percentage > MaximumDeletionPercentage)
        {
            return new DeletionSafetyDecision(
                false,
                DeletionBlockReason.PercentageLimitExceeded,
                percentage);
        }

        return new DeletionSafetyDecision(true, DeletionBlockReason.None, percentage);
    }

    private static DeletionBlockReason GetIncompleteReason(SnapshotCompleteness snapshot)
    {
        if (!snapshot.EndpointAvailable)
        {
            return DeletionBlockReason.EndpointUnavailable;
        }

        if (!snapshot.RootIdentityVerified)
        {
            return DeletionBlockReason.RootIdentityUnverified;
        }

        if (!snapshot.EnumerationCompleted)
        {
            return DeletionBlockReason.EnumerationIncomplete;
        }

        if (!snapshot.PaginationCompleted)
        {
            return DeletionBlockReason.PaginationIncomplete;
        }

        if (!snapshot.PermissionsIntact)
        {
            return DeletionBlockReason.PermissionErrors;
        }

        return snapshot.UnexpectedlyEmpty
            ? DeletionBlockReason.UnexpectedEmptyRoot
            : DeletionBlockReason.None;
    }
}
