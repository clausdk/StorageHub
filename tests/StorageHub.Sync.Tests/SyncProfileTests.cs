using StorageHub.Domain.Identifiers;
using StorageHub.Transfers;

namespace StorageHub.Sync.Tests;

public sealed class SyncProfileTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly ConnectionProfileId _left = ConnectionProfileId.New();
    private readonly ConnectionProfileId _right = ConnectionProfileId.New();

    [Fact]
    public void Policy_hash_changes_for_effectful_settings_but_not_display_or_enabled_state()
    {
        var baseline = Create();
        var renamed = Create(displayName: "Renamed", enabled: false);
        var differentRoot = Create(leftRoot: "archive");
        var differentSafety = Create(
            deletionSafety: new DeletionSafetyPolicy(3, 1m));
        var differentTransfer = Create(
            transferOptions: new TransferExecutionOptions(Overwrite: true));

        Assert.Equal(baseline.PolicySha256, renamed.PolicySha256);
        Assert.NotEqual(baseline.PolicySha256, differentRoot.PolicySha256);
        Assert.NotEqual(baseline.PolicySha256, differentSafety.PolicySha256);
        Assert.NotEqual(baseline.PolicySha256, differentTransfer.PolicySha256);
        Assert.Equal(64, baseline.PolicySha256.Length);
    }

    [Fact]
    public void Profile_rejects_ambiguous_or_unsafe_endpoint_mapping()
    {
        Assert.Throws<ArgumentException>(() => new SyncProfile(
            SyncProfileId.New(),
            "Invalid",
            _left,
            "../escape",
            _right,
            string.Empty,
            SyncDirection.LeftToRight,
            SyncDeletionMode.Disabled,
            SyncConflictPolicy.Block,
            DeletionSafetyPolicy.Default,
            new TransferExecutionOptions(),
            true,
            1,
            Now,
            Now));
        Assert.Throws<ArgumentException>(() => new SyncProfile(
            SyncProfileId.New(),
            "Invalid",
            _left,
            string.Empty,
            _left,
            string.Empty,
            SyncDirection.LeftToRight,
            SyncDeletionMode.Disabled,
            SyncConflictPolicy.Block,
            DeletionSafetyPolicy.Default,
            new TransferExecutionOptions(),
            true,
            1,
            Now,
            Now));
        Assert.Throws<ArgumentException>(() => Create(
            direction: SyncDirection.TwoWay,
            deletionMode: SyncDeletionMode.Mirror));
    }

    [Fact]
    public void Same_connection_allows_only_non_overlapping_location_roots()
    {
        var valid = new SyncProfile(
            SyncProfileId.New(), "Same connection", _left, "incoming", _left, "archive",
            SyncDirection.LeftToRight, SyncDeletionMode.Disabled, SyncConflictPolicy.Block,
            DeletionSafetyPolicy.Default, new TransferExecutionOptions(Overwrite: true), true, 1, Now, Now);

        Assert.Equal(valid.LocationAConnectionProfileId, valid.LocationBConnectionProfileId);
        Assert.Throws<ArgumentException>(() => new SyncProfile(
            SyncProfileId.New(), "Overlap", _left, "incoming", _left, "incoming/child",
            SyncDirection.LeftToRight, SyncDeletionMode.Disabled, SyncConflictPolicy.Block,
            DeletionSafetyPolicy.Default, new TransferExecutionOptions(), true, 1, Now, Now));
    }

    [Fact]
    public void Filters_conflicts_and_behavior_are_bound_into_policy_hash()
    {
        var baseline = Create();
        var filtered = new SyncProfile(
            SyncProfileId.New(), "Sync", _left, "documents", _right, "backup",
            SyncDirection.LeftToRight, SyncDeletionMode.Disabled, SyncConflictPolicy.KeepBoth,
            DeletionSafetyPolicy.Default, new TransferExecutionOptions(Overwrite: true), true, 1, Now, Now,
            new SyncPathFilterPolicy(["**/*.txt"], ["private/**"], includeHiddenFiles: false),
            SyncBehavior.UpdateAToB);

        Assert.NotEqual(baseline.PolicySha256, filtered.PolicySha256);
    }

    private SyncProfile Create(
        string displayName = "Sync",
        bool enabled = true,
        string leftRoot = "documents",
        SyncDirection direction = SyncDirection.LeftToRight,
        SyncDeletionMode deletionMode = SyncDeletionMode.Disabled,
        DeletionSafetyPolicy? deletionSafety = null,
        TransferExecutionOptions? transferOptions = null) => new(
        SyncProfileId.New(),
        displayName,
        _left,
        leftRoot,
        _right,
        "backup",
        direction,
        deletionMode,
        SyncConflictPolicy.Block,
        deletionSafety ?? DeletionSafetyPolicy.Default,
        transferOptions ?? new TransferExecutionOptions(),
        enabled,
        1,
        Now,
        Now);
}
