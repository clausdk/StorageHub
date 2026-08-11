using StorageHub.Domain.Capabilities;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Abstractions;

namespace StorageHub.Sync.Tests;

public sealed class SyncPlanBuilderTests
{
    [Fact]
    public void Copy_new_and_compare_only_never_plan_updates()
    {
        var fixture = Fixture.Create(
            left: [File("shared.txt", 11, "AA")],
            right: [File("shared.txt", 10, "BB")]);

        var copyNew = SyncPlanBuilder.Build(fixture.Request(
            SyncDirection.LeftToRight,
            SyncDeletionMode.Disabled,
            SyncBehavior.CopyNewFilesAToB));
        var compare = SyncPlanBuilder.Build(fixture.Request(
            SyncDirection.LeftToRight,
            SyncDeletionMode.Disabled,
            SyncBehavior.CompareOnly));

        Assert.Empty(copyNew.Value.Plan.Operations);
        Assert.Empty(compare.Value.Plan.Operations);
        Assert.Single(compare.Value.Conflicts);
    }

    [Fact]
    public void Filters_remove_paths_from_operations_and_baseline_scope()
    {
        var fixture = Fixture.Create(
            left: [File("include.txt", 1, "AA"), File("private/secret.txt", 2, "BB")],
            right: []);
        var filter = new SyncPathFilterPolicy(["**/*.txt", "*.txt"], ["private/**"]);

        var result = SyncPlanBuilder.Build(fixture.Request(
            SyncDirection.LeftToRight,
            SyncDeletionMode.Disabled,
            SyncBehavior.UpdateAToB,
            filter));

        Assert.Equal(
            "include.txt",
            Assert.Single(result.Value.Plan.Operations).SourceOrTarget.CanonicalRelativePath);
    }

    [Fact]
    public void Left_to_right_update_copies_source_changes_without_deleting_destination_only_items()
    {
        var fixture = Fixture.Create(
            left: [File("source.txt", 10, "AA")],
            right: [File("old.txt", 5, "BB")]);

        var result = SyncPlanBuilder.Build(fixture.Request(
            SyncDirection.LeftToRight,
            SyncDeletionMode.Disabled));

        Assert.True(result.IsSuccess);
        var operation = Assert.Single(result.Value.Plan.Operations);
        Assert.Equal(SyncPlanOperationKind.Copy, operation.Kind);
        Assert.False(operation.DestinationExisted);
        Assert.Equal("source.txt", operation.SourceOrTarget.CanonicalRelativePath);
        Assert.Equal("source.txt", operation.Destination!.CanonicalRelativePath);
        Assert.Empty(result.Value.Conflicts);
    }

    [Fact]
    public void Mirror_deletes_destination_only_items_but_executor_evidence_marks_empty_source_suspicious()
    {
        var fixture = Fixture.Create(
            left: [],
            right: [File("old.txt", 5, "BB")],
            baseline: new Dictionary<string, SyncBaselineObservation>
            {
                ["old.txt"] = SyncBaselineObservation.Present(5, Digest("BB"), null, "right-v1")
            });

        var result = SyncPlanBuilder.Build(fixture.Request(
            SyncDirection.LeftToRight,
            SyncDeletionMode.Mirror));

        Assert.True(result.IsSuccess);
        Assert.Equal(SyncPlanOperationKind.Delete, Assert.Single(result.Value.Plan.Operations).Kind);
        Assert.True(result.Value.Snapshots.Left.UnexpectedlyEmpty);
        Assert.False(result.Value.Snapshots.Left.IsComplete);
    }

    [Fact]
    public void Two_way_concurrent_modification_is_preserved_as_a_conflict()
    {
        var baseline = new Dictionary<string, SyncBaselineObservation>
        {
            ["shared.txt"] = SyncBaselineObservation.Present(10, Digest("00"), "left-v1", "right-v1")
        };
        var fixture = Fixture.Create(
            left: [File("shared.txt", 11, "AA", "left-v2")],
            right: [File("shared.txt", 12, "BB", "right-v2")],
            baseline: baseline);

        var result = SyncPlanBuilder.Build(fixture.Request(
            SyncDirection.TwoWay,
            SyncDeletionMode.Propagate));

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Plan.Operations);
        var conflict = Assert.Single(result.Value.Conflicts);
        Assert.Equal("shared.txt", conflict.RelativePath);
        Assert.Equal(SyncChangeKind.ConflictBothModified, conflict.Kind);
    }

    [Fact]
    public void Keep_both_creates_deterministic_location_copies_and_converges_original()
    {
        var baseline = new Dictionary<string, SyncBaselineObservation>
        {
            ["shared.txt"] = SyncBaselineObservation.Present(10, Digest("00"), "left-v1", "right-v1")
        };
        var fixture = Fixture.Create(
            left: [File("shared.txt", 11, "AA", "left-v2")],
            right: [File("shared.txt", 12, "BB", "right-v2")],
            baseline: baseline);

        var result = SyncPlanBuilder.Build(fixture.Request(
            SyncDirection.TwoWay,
            SyncDeletionMode.Disabled,
            SyncBehavior.TwoWaySync,
            conflictPolicy: SyncConflictPolicy.KeepBoth));

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Conflicts);
        Assert.Equal(5, result.Value.Plan.Operations.Length);
        Assert.Contains(result.Value.Plan.Operations,
            operation => operation.Destination?.CanonicalRelativePath ==
                "shared.storagehub-conflict-location-a.txt");
        Assert.Contains(result.Value.Plan.Operations,
            operation => operation.Destination?.CanonicalRelativePath ==
                "shared.storagehub-conflict-location-b.txt");
    }

    [Fact]
    public void Two_way_single_side_modification_copies_to_the_unchanged_side()
    {
        var baseline = new Dictionary<string, SyncBaselineObservation>
        {
            ["shared.txt"] = SyncBaselineObservation.Present(10, Digest("00"), "left-v1", "right-v1")
        };
        var fixture = Fixture.Create(
            left: [File("shared.txt", 11, "AA", "left-v2")],
            right: [File("shared.txt", 10, "00", "right-v1")],
            baseline: baseline);

        var result = SyncPlanBuilder.Build(fixture.Request(
            SyncDirection.TwoWay,
            SyncDeletionMode.Propagate));

        Assert.True(result.IsSuccess);
        var operation = Assert.Single(result.Value.Plan.Operations);
        Assert.Equal(fixture.LeftProfileId, operation.SourceOrTarget.ProfileId);
        Assert.Equal(fixture.RightProfileId, operation.Destination!.ProfileId);
        Assert.Empty(result.Value.Conflicts);
    }

    [Fact]
    public void Overwrite_plan_binds_the_exact_destination_version_captured_by_the_scan()
    {
        var fixture = Fixture.Create(
            left: [File("shared.txt", 11, "AA", "left-v2")],
            right: [File("shared.txt", 10, "00", "right-v1")]);

        var result = SyncPlanBuilder.Build(fixture.Request(
            SyncDirection.LeftToRight,
            SyncDeletionMode.Disabled));

        Assert.True(result.IsSuccess);
        var operation = Assert.Single(result.Value.Plan.Operations);
        Assert.Equal("left-v2", operation.SourceOrTarget.VersionId);
        Assert.Equal("right-v1", operation.Destination!.VersionId);
        Assert.True(operation.DestinationExisted);
    }

    [Fact]
    public void Overwrite_plan_binds_the_exact_destination_entity_tag_captured_by_the_scan()
    {
        var fixture = Fixture.Create(
            left: [File("shared.txt", 11, "AA", "left-v2", "left-etag")],
            right: [File("shared.txt", 10, "00", null, "right-etag")]);

        var result = SyncPlanBuilder.Build(fixture.Request(
            SyncDirection.LeftToRight,
            SyncDeletionMode.Disabled));

        Assert.True(result.IsSuccess);
        var operation = Assert.Single(result.Value.Plan.Operations);
        Assert.Equal("left-etag", operation.SourceOrTarget.EntityTag);
        Assert.Equal("right-etag", operation.Destination!.EntityTag);
    }

    [Fact]
    public void Matching_provider_entity_tags_do_not_prove_cross_endpoint_content_equality()
    {
        var fixture = Fixture.Create(
            left: [File("shared.txt", 10, null, "left-v2", "same-looking-etag")],
            right: [File("shared.txt", 10, null, "right-v1", "same-looking-etag")]);

        var result = SyncPlanBuilder.Build(fixture.Request(
            SyncDirection.LeftToRight,
            SyncDeletionMode.Disabled));

        Assert.True(result.IsSuccess);
        Assert.Equal(SyncPlanOperationKind.Copy, Assert.Single(result.Value.Plan.Operations).Kind);
    }

    [Fact]
    public void Portable_sha256_proves_equality_while_opaque_checksums_remain_ignored()
    {
        var sha256 = new string('a', 64);
        var fixture = Fixture.Create(
            left: [File("shared.txt", 10, "opaque-left", portableSha256: sha256)],
            right: [File("shared.txt", 10, "opaque-right", portableSha256: sha256)]);

        var result = SyncPlanBuilder.Build(fixture.Request(
            SyncDirection.LeftToRight,
            SyncDeletionMode.Disabled));

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Plan.Operations);
    }

    [Fact]
    public void Copy_operation_and_plan_digest_bind_both_snapshot_sha256_values()
    {
        var sourceSha256 = new string('a', 64);
        var destinationSha256 = new string('b', 64);
        var fixture = Fixture.Create(
            left: [File("shared.txt", 10, "same-opaque", portableSha256: sourceSha256)],
            right: [File("shared.txt", 10, "same-opaque", portableSha256: destinationSha256)]);

        var result = SyncPlanBuilder.Build(fixture.Request(
            SyncDirection.LeftToRight,
            SyncDeletionMode.Disabled));

        Assert.True(result.IsSuccess);
        var operation = Assert.Single(result.Value.Plan.Operations);
        Assert.Equal(sourceSha256, operation.SourceDigest!.Value);
        Assert.Equal(destinationSha256, operation.DestinationDigest!.Value);
        Assert.Equal(ImmutableSyncPlan.CurrentDigestSchemaVersion, result.Value.Plan.DigestSchemaVersion);
    }

    [Fact]
    public void Directory_creation_is_ordered_before_child_copy_and_deletion_is_deepest_first()
    {
        var fixture = Fixture.Create(
            left:
            [
                Directory("new"),
                File("new/file.txt", 2, "AA")
            ],
            right:
            [
                Directory("obsolete"),
                File("obsolete/file.txt", 2, "BB")
            ],
            baseline: new Dictionary<string, SyncBaselineObservation>
            {
                ["obsolete"] = SyncBaselineObservation.Present(0, null, null, null),
                ["obsolete/file.txt"] = SyncBaselineObservation.Present(2, Digest("BB"), null, null)
            });

        var result = SyncPlanBuilder.Build(fixture.Request(
            SyncDirection.LeftToRight,
            SyncDeletionMode.Mirror));

        Assert.True(result.IsSuccess);
        Assert.Collection(
            result.Value.Plan.Operations,
            operation => Assert.Equal(SyncPlanOperationKind.CreateDirectory, operation.Kind),
            operation => Assert.Equal(SyncPlanOperationKind.Copy, operation.Kind),
            operation =>
            {
                Assert.Equal(SyncPlanOperationKind.Delete, operation.Kind);
                Assert.EndsWith("obsolete/file.txt", operation.SourceOrTarget.CanonicalRelativePath, StringComparison.Ordinal);
            },
            operation =>
            {
                Assert.Equal(SyncPlanOperationKind.Delete, operation.Kind);
                Assert.EndsWith("obsolete", operation.SourceOrTarget.CanonicalRelativePath, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void Replacing_a_destination_directory_deletes_its_subtree_before_copying_the_file()
    {
        var fixture = Fixture.Create(
            left: [File("node", 3, "AA")],
            right:
            [
                Directory("node"),
                File("node/child.txt", 2, "BB")
            ]);

        var result = SyncPlanBuilder.Build(fixture.Request(
            SyncDirection.LeftToRight,
            SyncDeletionMode.Mirror));

        Assert.True(result.IsSuccess);
        Assert.Collection(
            result.Value.Plan.Operations,
            operation =>
            {
                Assert.Equal(SyncPlanOperationKind.Delete, operation.Kind);
                Assert.Equal("node/child.txt", operation.SourceOrTarget.CanonicalRelativePath);
            },
            operation =>
            {
                Assert.Equal(SyncPlanOperationKind.Delete, operation.Kind);
                Assert.Equal("node", operation.SourceOrTarget.CanonicalRelativePath);
            },
            operation => Assert.Equal(SyncPlanOperationKind.Copy, operation.Kind));
    }

    [Fact]
    public void Planning_rejects_paths_that_collide_under_the_other_endpoints_case_rules()
    {
        var fixture = Fixture.Create(
            left:
            [
                File("File.txt", 1, "AA"),
                File("file.txt", 1, "BB")
            ],
            right: [],
            leftCaseSensitivity: StorageCaseSensitivity.Sensitive,
            rightCaseSensitivity: StorageCaseSensitivity.Insensitive);

        var result = SyncPlanBuilder.Build(fixture.Request(
            SyncDirection.LeftToRight,
            SyncDeletionMode.Disabled));

        Assert.True(result.IsFailure);
        Assert.Equal("sync.plan.path_collision", result.Error.Code);
    }

    private static SnapshotSeed File(
        string path,
        long length,
        string? checksum,
        string? versionId = null,
        string? entityTag = null,
        string? portableSha256 = null) =>
        new(path, StorageEntryKind.File, length, checksum, versionId, entityTag, portableSha256);

    private static SnapshotSeed Directory(string path) =>
        new(path, StorageEntryKind.Directory, null, null, null, null, null);

    private static ContentDigest Digest(string value) => new("SHA256", value);

    private sealed record SnapshotSeed(
        string Path,
        StorageEntryKind Kind,
        long? Length,
        string? Checksum,
        string? VersionId,
        string? EntityTag,
        string? PortableSha256);

    private sealed record Fixture(
        ConnectionProfileId LeftProfileId,
        ConnectionProfileId RightProfileId,
        StorageAddress LeftRoot,
        StorageAddress RightRoot,
        SyncEndpointSnapshot Left,
        SyncEndpointSnapshot Right,
        IReadOnlyDictionary<string, SyncBaselineObservation> Baseline)
    {
        public static Fixture Create(
            IReadOnlyList<SnapshotSeed> left,
            IReadOnlyList<SnapshotSeed> right,
            IReadOnlyDictionary<string, SyncBaselineObservation>? baseline = null,
            StorageCaseSensitivity leftCaseSensitivity = StorageCaseSensitivity.Sensitive,
            StorageCaseSensitivity rightCaseSensitivity = StorageCaseSensitivity.Sensitive)
        {
            var leftProfileId = ConnectionProfileId.New();
            var rightProfileId = ConnectionProfileId.New();
            var leftRoot = SyncTestEntries.Address(leftProfileId, "left-root", string.Empty);
            var rightRoot = SyncTestEntries.Address(rightProfileId, "right-root", string.Empty);
            return new Fixture(
                leftProfileId,
                rightProfileId,
                leftRoot,
                rightRoot,
                Snapshot(leftProfileId, "left-root", left, leftCaseSensitivity),
                Snapshot(rightProfileId, "right-root", right, rightCaseSensitivity),
                baseline ?? new Dictionary<string, SyncBaselineObservation>());
        }

        public SyncPlanBuildRequest Request(
            SyncDirection direction,
            SyncDeletionMode deletionMode,
            SyncBehavior? behavior = null,
            SyncPathFilterPolicy? filterPolicy = null,
            SyncConflictPolicy conflictPolicy = SyncConflictPolicy.Block) => new(
            OperationPlanId.New(),
            SyncProfileId.New(),
            baselineGeneration: 1,
            LeftRoot,
            RightRoot,
            Left,
            Right,
            Baseline,
            direction,
            deletionMode,
            DateTimeOffset.UnixEpoch,
            behavior,
            filterPolicy,
            conflictPolicy);

        private static SyncEndpointSnapshot Snapshot(
            ConnectionProfileId profileId,
            string rootIdentity,
            IReadOnlyList<SnapshotSeed> seeds,
            StorageCaseSensitivity caseSensitivity)
        {
            var entries = seeds.Select(seed =>
            {
                var address = SyncTestEntries.Address(
                    profileId,
                    rootIdentity,
                    seed.Path,
                    seed.VersionId,
                    seed.EntityTag);
                var entry = StorageEntry.Create(
                    address,
                    seed.Kind,
                    seed.Length,
                    checksum: seed.Checksum).Value;
                return new KeyValuePair<string, StorageEntry>(seed.Path, entry);
            });
            var portableDigests = seeds
                .Where(static seed => seed.PortableSha256 is not null)
                .Select(static seed => new KeyValuePair<string, PortableContentDigest>(
                    seed.Path,
                    new PortableContentDigest(
                        PortableChecksumAlgorithm.Sha256,
                        seed.PortableSha256!)));
            return new SyncEndpointSnapshot(
                profileId,
                rootIdentity,
                entries,
                SnapshotCompleteness.Complete(seeds.Count),
                caseSensitivity,
                portableDigests);
        }
    }
}
