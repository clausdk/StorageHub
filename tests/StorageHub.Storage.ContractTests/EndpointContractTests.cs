using StorageHub.Contracts.Results;
using StorageHub.Domain.Capabilities;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Abstractions;
using StorageHub.Storage.Models;

namespace StorageHub.Storage.ContractTests;

public sealed class EndpointContractTests
{
    private static readonly ConnectionProfileId ProfileId =
        new(Guid.Parse("f15fe397-77cc-43fc-9794-b412e3e3b760"));

    [Fact]
    public void Address_validation_rejects_cross_profile_and_stale_root_addresses()
    {
        var session = new StubSession(ProfileId, "current-root");
        var otherProfileAddress = StorageAddress.Create(
            ConnectionProfileId.New(), "current-root", "file.bin").Value;
        var staleRootAddress = StorageAddress.Create(
            ProfileId, "previous-root", "file.bin").Value;

        var profileResult = session.ValidateAddress(otherProfileAddress);
        var rootResult = session.ValidateAddress(staleRootAddress);

        Assert.True(profileResult.IsFailure);
        Assert.Equal("storage.address.profile_mismatch", profileResult.Error!.Code);
        Assert.True(rootResult.IsFailure);
        Assert.Equal("storage.address.root_mismatch", rootResult.Error!.Code);
    }

    [Fact]
    public void Page_takes_an_immutable_snapshot_and_keeps_opaque_continuation_tokens()
    {
        var address = StorageAddress.Create(ProfileId, "root", "one.txt").Value;
        var entries = new List<StorageEntry>
        {
            StorageEntry.Create(address, StorageEntryKind.File, size: 1).Value
        };
        var page = new StoragePage(entries, "provider/token+/=");
        entries.Clear();

        Assert.Single(page.Entries);
        Assert.Equal("provider/token+/=", page.ContinuationToken);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    public void Read_request_rejects_invalid_ranges(long offset, long length)
    {
        var address = StorageAddress.Create(ProfileId, "root", "one.txt").Value;
        var request = new StorageReadRequest(address, offset, length);

        Assert.True(request.Validate().IsFailure);
    }

    [Fact]
    public void Resume_write_requires_a_checkpoint_and_snapshots_metadata()
    {
        var address = StorageAddress.Create(ProfileId, "root", "one.txt").Value;
        var metadata = new Dictionary<string, string> { ["owner"] = "alice" };
        var invalid = new StorageWriteRequest(
            address,
            StorageWriteMode.Resume,
            expectedLength: 10,
            requestedOffset: 5,
            metadata: metadata);
        var valid = new StorageWriteRequest(
            address,
            StorageWriteMode.Resume,
            expectedLength: 10,
            requestedOffset: 5,
            resumeToken: "opaque/checkpoint",
            metadata: metadata);
        metadata["owner"] = "mallory";

        Assert.True(invalid.Validate().IsFailure);
        Assert.True(valid.Validate().IsSuccess);
        Assert.Equal("alice", valid.Metadata["owner"]);
    }

    [Fact]
    public void Mutation_identity_tokens_are_validated_without_being_path_normalized()
    {
        var address = StorageAddress.Create(ProfileId, "root", "one.txt").Value;
        var read = new StorageReadRequest(address, ExpectedEntityTag: "etag/../opaque");
        var write = new StorageWriteRequest(
            address,
            StorageWriteMode.Overwrite,
            expectedDestinationEntityTag: "etag/../opaque");
        var delete = new StorageDeleteRequest(
            address,
            ExpectedEntityTag: "etag/../opaque");

        Assert.True(read.Validate().IsSuccess);
        Assert.True(write.Validate().IsSuccess);
        Assert.True(delete.Validate().IsSuccess);
        Assert.True(new StorageReadRequest(address, ExpectedEntityTag: " ").Validate().IsFailure);
        Assert.True(new StorageWriteRequest(
            address,
            StorageWriteMode.Overwrite,
            expectedDestinationEntityTag: "bad\0etag").Validate().IsFailure);
        Assert.True(new StorageDeleteRequest(address, ExpectedEntityTag: string.Empty).Validate().IsFailure);
    }

    private sealed class StubSession : IStorageEndpointSession
    {
        public StubSession(ConnectionProfileId profileId, string rootIdentity)
        {
            ProfileId = profileId;
            RootIdentity = rootIdentity;
        }

        public ConnectionProfileId ProfileId { get; }

        public string RootIdentity { get; }

        public EffectiveStorageCapabilities Capabilities { get; } = EffectiveStorageCapabilities.None;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<StorageResult> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<StorageResult<StorageEntry>> GetEntryAsync(
            StorageAddress address,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<StorageResult<StoragePage>> ListAsync(
            StorageAddress address,
            StorageListRequest? request = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<StorageResult<Stream>> OpenReadAsync(
            StorageReadRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<StorageResult<IStorageWriteHandle>> OpenWriteAsync(
            StorageWriteRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<StorageResult<StorageEntry>> CreateDirectoryAsync(
            StorageAddress address,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<StorageResult> DeleteAsync(
            StorageDeleteRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<StorageResult<StorageEntry>> CopyAsync(
            StorageCopyRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<StorageResult<StorageEntry>> MoveAsync(
            StorageMoveRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
