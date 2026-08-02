using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;

namespace StorageHub.Transfers.Tests;

public sealed class TransferIntentAndCheckpointTests
{
    [Fact]
    public void Intent_is_immutable_and_rejects_same_source_and_destination()
    {
        var source = Address("source/file.bin", versionId: "source-v1");
        var destination = Address("destination/file.bin");
        var createdAt = DateTimeOffset.UnixEpoch;

        var intent = new TransferIntent(
            TransferJobId.New(),
            TransferOperationKind.Copy,
            source,
            destination,
            expectedLength: 20,
            TransferVerificationPolicy.StrongHashWhenAvailable,
            createdAt);

        Assert.Equal(source, intent.Source);
        Assert.Equal(destination, intent.Destination);
        Assert.Equal(20, intent.ExpectedLength);
        Assert.Equal(createdAt, intent.CreatedAtUtc);
        Assert.Throws<ArgumentException>(() => new TransferIntent(
            TransferJobId.New(),
            TransferOperationKind.Copy,
            source,
            source,
            expectedLength: 20,
            TransferVerificationPolicy.Size,
            createdAt));
    }

    [Fact]
    public void Intent_captures_destination_version_and_entity_tag_preconditions()
    {
        var source = Address("source/file.bin", versionId: "source-v1", entityTag: "source-etag");
        var destination = Address(
            "destination/file.bin",
            versionId: "destination-v1",
            entityTag: "destination-etag");

        var intent = new TransferIntent(
            TransferJobId.New(),
            TransferOperationKind.Copy,
            source,
            destination,
            expectedLength: 20,
            TransferVerificationPolicy.Size,
            DateTimeOffset.UnixEpoch);

        Assert.Equal("destination-v1", intent.ExpectedDestinationVersionId);
        Assert.Equal("destination-etag", intent.ExpectedDestinationEntityTag);
    }

    [Fact]
    public void Checkpoint_defensively_copies_completed_parts()
    {
        var parts = new List<CompletedTransferPart>
        {
            new(partNumber: 1, offset: 0, length: 5, providerTag: "etag-1"),
        };

        var checkpoint = TransferCheckpoint.Create(
            TransferJobId.New(),
            attempt: 1,
            verifiedBytes: 5,
            expectedLength: 10,
            source: Address("source/file.bin", versionId: "source-v1"),
            destinationTemporaryAddress: Address("destination/.file.storagehub-part"),
            resumeMode: TransferResumeMode.Multipart,
            sourceDigest: new TransferContentDigest("SHA-256", "digest-v1"),
            providerResumeId: "multipart-1",
            completedParts: parts,
            recordedAtUtc: DateTimeOffset.UnixEpoch);

        parts.Add(new CompletedTransferPart(2, 5, 5, "etag-2"));

        Assert.Single(checkpoint.CompletedParts);
        Assert.Equal(5, checkpoint.VerifiedBytes);
    }

    [Fact]
    public void Resume_requires_same_root_path_length_and_source_version()
    {
        var source = Address("source/file.bin", versionId: "source-v1");
        var checkpoint = TransferCheckpoint.Create(
            TransferJobId.New(),
            attempt: 1,
            verifiedBytes: 5,
            expectedLength: 10,
            source,
            Address("destination/.file.storagehub-part"),
            resumeMode: TransferResumeMode.Offset,
            sourceDigest: new TransferContentDigest("SHA-256", "digest-v1"),
            providerResumeId: null,
            completedParts: [],
            recordedAtUtc: DateTimeOffset.UnixEpoch);

        Assert.True(checkpoint.CanResumeFrom(
            source,
            currentLength: 10,
            currentDigest: new TransferContentDigest("SHA-256", "digest-v1")));
        Assert.False(checkpoint.CanResumeFrom(
            Address("source/file.bin", versionId: "source-v2"),
            currentLength: 10,
            currentDigest: new TransferContentDigest("SHA-256", "digest-v1")));
        Assert.False(checkpoint.CanResumeFrom(
            source,
            currentLength: 11,
            currentDigest: new TransferContentDigest("SHA-256", "digest-v1")));
    }

    [Fact]
    public void Resume_without_version_requires_matching_strong_digest()
    {
        var source = Address("source/file.bin");
        var checkpoint = TransferCheckpoint.Create(
            TransferJobId.New(),
            attempt: 1,
            verifiedBytes: 5,
            expectedLength: 10,
            source,
            Address("destination/.file.storagehub-part"),
            resumeMode: TransferResumeMode.Offset,
            sourceDigest: new TransferContentDigest("SHA-256", "digest-v1"),
            providerResumeId: null,
            completedParts: [],
            recordedAtUtc: DateTimeOffset.UnixEpoch);

        Assert.False(checkpoint.CanResumeFrom(source, 10, currentDigest: null));
        Assert.False(checkpoint.CanResumeFrom(
            source,
            10,
            new TransferContentDigest("SHA-256", "different")));
        Assert.True(checkpoint.CanResumeFrom(
            source,
            10,
            new TransferContentDigest("SHA-256", "digest-v1")));
    }

    [Fact]
    public void Resume_can_bind_to_an_unchanged_source_entity_tag()
    {
        var source = Address("source/file.bin", entityTag: "etag-v1");
        var checkpoint = TransferCheckpoint.Create(
            TransferJobId.New(),
            attempt: 1,
            verifiedBytes: 5,
            expectedLength: 10,
            source,
            Address("destination/.file.storagehub-part"),
            resumeMode: TransferResumeMode.Offset,
            sourceDigest: null,
            providerResumeId: null,
            completedParts: [],
            recordedAtUtc: DateTimeOffset.UnixEpoch);

        Assert.True(checkpoint.CanResumeFrom(source, 10, currentDigest: null));
        Assert.False(checkpoint.CanResumeFrom(
            Address("source/file.bin", entityTag: "etag-v2"),
            10,
            currentDigest: null));
    }

    [Fact]
    public void Checkpoint_rejects_offsets_beyond_expected_length_and_duplicate_parts()
    {
        var source = Address("source/file.bin", versionId: "v1");
        var destination = Address("destination/.file.storagehub-part");

        Assert.Throws<ArgumentOutOfRangeException>(() => TransferCheckpoint.Create(
            TransferJobId.New(),
            attempt: 1,
            verifiedBytes: 11,
            expectedLength: 10,
            source,
            destination,
            resumeMode: TransferResumeMode.Offset,
            sourceDigest: null,
            providerResumeId: null,
            completedParts: [],
            recordedAtUtc: DateTimeOffset.UnixEpoch));

        Assert.Throws<ArgumentException>(() => TransferCheckpoint.Create(
            TransferJobId.New(),
            attempt: 1,
            verifiedBytes: 10,
            expectedLength: 10,
            source,
            destination,
            resumeMode: TransferResumeMode.Multipart,
            sourceDigest: null,
            providerResumeId: null,
            completedParts:
            [
                new CompletedTransferPart(1, 0, 5, null),
                new CompletedTransferPart(1, 5, 5, null),
            ],
            recordedAtUtc: DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Checkpoint_never_claims_resume_when_provider_capability_was_absent()
    {
        var source = Address("source/file.bin", versionId: "v1");
        var checkpoint = TransferCheckpoint.Create(
            TransferJobId.New(),
            attempt: 1,
            verifiedBytes: 5,
            expectedLength: 10,
            source,
            Address("destination/.file.storagehub-part"),
            resumeMode: TransferResumeMode.None,
            sourceDigest: new TransferContentDigest("SHA-256", "digest-v1"),
            providerResumeId: null,
            completedParts: [],
            recordedAtUtc: DateTimeOffset.UnixEpoch);

        Assert.False(checkpoint.CanResumeFrom(
            source,
            currentLength: 10,
            currentDigest: new TransferContentDigest("SHA-256", "digest-v1")));
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
