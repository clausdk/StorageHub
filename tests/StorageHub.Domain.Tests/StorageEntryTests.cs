using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;

namespace StorageHub.Domain.Tests;

public sealed class StorageEntryTests
{
    private static StorageAddress FileAddress => StorageAddress.Create(
        new ConnectionProfileId(Guid.Parse("53cb9d4c-55dd-4df5-8d73-2f73313136a5")),
        "root",
        "reports/report.csv",
        versionId: "generation-7").Value;

    [Fact]
    public void Create_takes_an_immutable_metadata_snapshot()
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["owner"] = "alice"
        };

        var result = StorageEntry.Create(
            FileAddress,
            StorageEntryKind.File,
            size: 42,
            metadata: metadata);
        metadata["owner"] = "mallory";

        Assert.True(result.IsSuccess);
        Assert.Equal("report.csv", result.Value.Name);
        Assert.Equal("alice", result.Value.Metadata["owner"]);
        Assert.Equal("generation-7", result.Value.Address.VersionId);
    }

    [Fact]
    public void Create_rejects_negative_file_sizes_and_sized_directories()
    {
        var negativeFile = StorageEntry.Create(FileAddress, StorageEntryKind.File, size: -1);
        var sizedDirectory = StorageEntry.Create(FileAddress, StorageEntryKind.Directory, size: 1);

        Assert.True(negativeFile.IsFailure);
        Assert.True(sizedDirectory.IsFailure);
        Assert.Equal("storage.entry.invalid_size", negativeFile.Error.Code);
        Assert.Equal("storage.entry.invalid_size", sizedDirectory.Error.Code);
    }
}
