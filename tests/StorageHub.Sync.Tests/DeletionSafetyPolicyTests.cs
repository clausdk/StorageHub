namespace StorageHub.Sync.Tests;

public sealed class DeletionSafetyPolicyTests
{
    [Fact]
    public void Defaults_allow_limits_but_block_when_either_limit_is_exceeded()
    {
        var complete = SnapshotCompleteness.Complete(totalItemCount: 1_000);
        var policy = DeletionSafetyPolicy.Default;

        var atLimits = policy.Evaluate(
            plannedDeletionCount: 100,
            baselineItemCount: 1_000,
            complete,
            complete);
        var overCount = policy.Evaluate(
            plannedDeletionCount: 101,
            baselineItemCount: 2_000,
            complete,
            complete);
        var overPercentage = policy.Evaluate(
            plannedDeletionCount: 11,
            baselineItemCount: 100,
            complete,
            complete);

        Assert.True(atLimits.Allowed);
        Assert.Equal(DeletionBlockReason.None, atLimits.Reason);
        Assert.False(overCount.Allowed);
        Assert.Equal(DeletionBlockReason.CountLimitExceeded, overCount.Reason);
        Assert.False(overPercentage.Allowed);
        Assert.Equal(DeletionBlockReason.PercentageLimitExceeded, overPercentage.Reason);
    }

    [Theory]
    [InlineData(false, true, true, true, true, false, DeletionBlockReason.EndpointUnavailable)]
    [InlineData(true, false, true, true, true, false, DeletionBlockReason.RootIdentityUnverified)]
    [InlineData(true, true, false, true, true, false, DeletionBlockReason.EnumerationIncomplete)]
    [InlineData(true, true, true, false, true, false, DeletionBlockReason.PaginationIncomplete)]
    [InlineData(true, true, true, true, false, false, DeletionBlockReason.PermissionErrors)]
    [InlineData(true, true, true, true, true, true, DeletionBlockReason.UnexpectedEmptyRoot)]
    public void Any_incomplete_snapshot_blocks_deletion(
        bool endpointAvailable,
        bool rootIdentityVerified,
        bool enumerationCompleted,
        bool paginationCompleted,
        bool permissionsIntact,
        bool unexpectedlyEmpty,
        DeletionBlockReason expected)
    {
        var unsafeSnapshot = new SnapshotCompleteness(
            endpointAvailable,
            rootIdentityVerified,
            enumerationCompleted,
            paginationCompleted,
            permissionsIntact,
            unexpectedlyEmpty,
            totalItemCount: 100);
        var complete = SnapshotCompleteness.Complete(100);

        var decision = DeletionSafetyPolicy.Default.Evaluate(
            plannedDeletionCount: 1,
            baselineItemCount: 100,
            unsafeSnapshot,
            complete);

        Assert.False(decision.Allowed);
        Assert.Equal(expected, decision.Reason);
    }

    [Fact]
    public void Deletion_against_empty_baseline_is_never_inferred()
    {
        var complete = SnapshotCompleteness.Complete(0);

        var decision = DeletionSafetyPolicy.Default.Evaluate(
            plannedDeletionCount: 1,
            baselineItemCount: 0,
            complete,
            complete);

        Assert.False(decision.Allowed);
        Assert.Equal(DeletionBlockReason.MissingBaseline, decision.Reason);
    }

    [Fact]
    public void Complete_but_empty_scan_is_still_suspicious_against_nonempty_baseline()
    {
        var empty = SnapshotCompleteness.Complete(totalItemCount: 0);

        var decision = DeletionSafetyPolicy.Default.Evaluate(
            plannedDeletionCount: 1,
            baselineItemCount: 100,
            empty,
            empty);

        Assert.False(decision.Allowed);
        Assert.Equal(DeletionBlockReason.UnexpectedEmptyRoot, decision.Reason);
    }

    [Fact]
    public void Zero_deletions_are_safe_even_when_snapshot_is_non_destructive()
    {
        var unavailable = new SnapshotCompleteness(
            endpointAvailable: false,
            rootIdentityVerified: false,
            enumerationCompleted: false,
            paginationCompleted: false,
            permissionsIntact: false,
            unexpectedlyEmpty: true,
            totalItemCount: 0);

        var decision = DeletionSafetyPolicy.Default.Evaluate(
            plannedDeletionCount: 0,
            baselineItemCount: 0,
            unavailable,
            unavailable);

        Assert.True(decision.Allowed);
    }
}
