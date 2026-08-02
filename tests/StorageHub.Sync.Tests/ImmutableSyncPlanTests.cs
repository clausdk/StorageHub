using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;

namespace StorageHub.Sync.Tests;

public sealed class ImmutableSyncPlanTests
{
    private static readonly OperationPlanId PlanId = new(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
    private static readonly SyncProfileId ProfileId = new(
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

    [Fact]
    public void Plan_defensively_copies_and_orders_operations_by_sequence()
    {
        var operations = new List<SyncPlanOperation>
        {
            SyncPlanOperation.Copy(1, Address("right/b.txt"), Address("left/b.txt"), 20),
            SyncPlanOperation.Copy(0, Address("left/a.txt"), Address("right/a.txt"), 10),
        };

        var plan = ImmutableSyncPlan.Create(
            PlanId,
            ProfileId,
            baselineGeneration: 42,
            operations,
            createdAtUtc: DateTimeOffset.UnixEpoch);
        operations.Clear();

        Assert.Equal(2, plan.Operations.Length);
        Assert.Equal(0, plan.Operations[0].Sequence);
        Assert.Equal(1, plan.Operations[1].Sequence);
        Assert.Matches("^[0-9a-f]{64}$", plan.Digest.Sha256Hex);
    }

    [Fact]
    public void Digest_is_deterministic_for_same_semantic_plan()
    {
        var first = ImmutableSyncPlan.Create(
            PlanId,
            ProfileId,
            42,
            [
                SyncPlanOperation.Copy(1, Address("b"), Address("dest/b"), 20),
                SyncPlanOperation.Copy(0, Address("a"), Address("dest/a"), 10),
            ],
            DateTimeOffset.UnixEpoch);
        var second = ImmutableSyncPlan.Create(
            PlanId,
            ProfileId,
            42,
            [
                SyncPlanOperation.Copy(0, Address("a"), Address("dest/a"), 10),
                SyncPlanOperation.Copy(1, Address("b"), Address("dest/b"), 20),
            ],
            DateTimeOffset.UnixEpoch);

        Assert.Equal(first.Digest, second.Digest);
    }

    [Fact]
    public void Digest_changes_with_baseline_address_identity_or_operation()
    {
        var operation = SyncPlanOperation.Copy(
            0,
            Address("a", versionId: "v1"),
            Address("dest/a"),
            10);
        var original = ImmutableSyncPlan.Create(
            PlanId,
            ProfileId,
            42,
            [operation],
            DateTimeOffset.UnixEpoch);
        var differentBaseline = ImmutableSyncPlan.Create(
            PlanId,
            ProfileId,
            43,
            [operation],
            DateTimeOffset.UnixEpoch);
        var differentVersion = ImmutableSyncPlan.Create(
            PlanId,
            ProfileId,
            42,
            [SyncPlanOperation.Copy(
                0,
                Address("a", versionId: "v2"),
                Address("dest/a"),
                10)],
            DateTimeOffset.UnixEpoch);
        var differentEntityTag = ImmutableSyncPlan.Create(
            PlanId,
            ProfileId,
            42,
            [SyncPlanOperation.Copy(
                0,
                Address("a", versionId: "v1", entityTag: "etag-v2"),
                Address("dest/a"),
                10)],
            DateTimeOffset.UnixEpoch);
        var deleteInstead = ImmutableSyncPlan.Create(
            PlanId,
            ProfileId,
            42,
            [SyncPlanOperation.Delete(0, Address("a", versionId: "v1"))],
            DateTimeOffset.UnixEpoch);

        Assert.NotEqual(original.Digest, differentBaseline.Digest);
        Assert.NotEqual(original.Digest, differentVersion.Digest);
        Assert.NotEqual(original.Digest, differentEntityTag.Digest);
        Assert.NotEqual(original.Digest, deleteInstead.Digest);
    }

    [Fact]
    public void Plan_rejects_duplicate_or_non_contiguous_sequences()
    {
        Assert.Throws<ArgumentException>(() => ImmutableSyncPlan.Create(
            PlanId,
            ProfileId,
            1,
            [
                SyncPlanOperation.Delete(0, Address("a")),
                SyncPlanOperation.Delete(0, Address("b")),
            ],
            DateTimeOffset.UnixEpoch));

        Assert.Throws<ArgumentException>(() => ImmutableSyncPlan.Create(
            PlanId,
            ProfileId,
            1,
            [SyncPlanOperation.Delete(1, Address("a"))],
            DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Digest_can_be_rehydrated_from_persisted_hex()
    {
        var plan = ImmutableSyncPlan.Create(
            PlanId,
            ProfileId,
            1,
            [SyncPlanOperation.Delete(0, Address("a"))],
            DateTimeOffset.UnixEpoch);

        Assert.True(SyncPlanDigest.TryParse(plan.Digest.Sha256Hex.ToUpperInvariant(), out var parsed));
        Assert.Equal(plan.Digest, parsed);
        Assert.False(SyncPlanDigest.TryParse("not-a-digest", out _));
        Assert.Throws<FormatException>(() => SyncPlanDigest.Parse("not-a-digest"));
    }

    private static StorageAddress Address(
        string path,
        string? versionId = null,
        string? entityTag = null)
    {
        var result = StorageAddress.Create(
            new ConnectionProfileId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            rootIdentity: "root-v1",
            path,
            versionId: versionId,
            entityTag: entityTag);
        Assert.True(result.IsSuccess);
        return result.Value;
    }
}
