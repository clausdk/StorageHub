namespace StorageHub.Sync.Tests;

public sealed class ThreeWayChangeClassifierTests
{
    private static readonly ContentDigest OriginalHash = new("SHA-256", "original");
    private static readonly ContentDigest LeftHash = new("SHA-256", "left");
    private static readonly ContentDigest RightHash = new("SHA-256", "right");

    [Fact]
    public void Stable_side_version_tokens_prove_both_sides_unchanged()
    {
        var baseline = SyncBaselineObservation.Present(
            length: 10,
            OriginalHash,
            leftVersionId: "left-v1",
            rightVersionId: "right-v1");
        var left = SyncItemObservation.Present(10, digest: null, versionId: "left-v1");
        var right = SyncItemObservation.Present(10, digest: null, versionId: "right-v1");

        var result = ThreeWayChangeClassifier.Classify(baseline, left, right);

        Assert.Equal(SyncChangeKind.Unchanged, result.Kind);
        Assert.Equal(SyncSideDelta.Unchanged, result.LeftDelta);
        Assert.Equal(SyncSideDelta.Unchanged, result.RightDelta);
    }

    [Fact]
    public void One_modified_side_is_distinguished_from_unchanged_peer()
    {
        var baseline = SyncBaselineObservation.Present(
            10,
            OriginalHash,
            leftVersionId: "left-v1",
            rightVersionId: "right-v1");
        var left = SyncItemObservation.Present(11, LeftHash, "left-v2");
        var right = SyncItemObservation.Present(10, OriginalHash, "right-v1");

        var result = ThreeWayChangeClassifier.Classify(baseline, left, right);

        Assert.Equal(SyncChangeKind.LeftModified, result.Kind);
        Assert.Equal(SyncSideDelta.Modified, result.LeftDelta);
        Assert.Equal(SyncSideDelta.Unchanged, result.RightDelta);
    }

    [Fact]
    public void Concurrent_different_modifications_are_a_conflict()
    {
        var baseline = SyncBaselineObservation.Present(10, OriginalHash, "l1", "r1");
        var left = SyncItemObservation.Present(12, LeftHash, "l2");
        var right = SyncItemObservation.Present(13, RightHash, "r2");

        var result = ThreeWayChangeClassifier.Classify(baseline, left, right);

        Assert.Equal(SyncChangeKind.ConflictBothModified, result.Kind);
        Assert.True(result.IsConflict);
    }

    [Fact]
    public void Delete_against_modify_is_a_conflict()
    {
        var baseline = SyncBaselineObservation.Present(10, OriginalHash, "l1", "r1");
        var left = SyncItemObservation.Missing;
        var right = SyncItemObservation.Present(12, RightHash, "r2");

        var result = ThreeWayChangeClassifier.Classify(baseline, left, right);

        Assert.Equal(SyncChangeKind.ConflictDeleteModify, result.Kind);
        Assert.True(result.IsConflict);
    }

    [Fact]
    public void Same_size_without_hash_or_stable_version_is_indeterminate()
    {
        var baseline = SyncBaselineObservation.Present(
            length: 10,
            digest: null,
            leftVersionId: null,
            rightVersionId: null);
        var left = SyncItemObservation.Present(10, digest: null, versionId: null);
        var right = SyncItemObservation.Present(10, digest: null, versionId: null);

        var result = ThreeWayChangeClassifier.Classify(baseline, left, right);

        Assert.Equal(SyncChangeKind.ConflictIndeterminate, result.Kind);
        Assert.True(result.IsConflict);
    }

    [Fact]
    public void Independently_created_identical_content_is_not_a_conflict()
    {
        var baseline = SyncBaselineObservation.Missing;
        var left = SyncItemObservation.Present(10, OriginalHash, "l1");
        var right = SyncItemObservation.Present(10, OriginalHash, "r1");

        var result = ThreeWayChangeClassifier.Classify(baseline, left, right);

        Assert.Equal(SyncChangeKind.BothCreatedIdentical, result.Kind);
        Assert.False(result.IsConflict);
    }
}
