using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;

namespace StorageHub.Domain.Tests;

public sealed class StorageAddressTests
{
    private static readonly ConnectionProfileId ProfileId =
        new(Guid.Parse("ff0c4c2f-f5a9-4a33-a606-553fa88d84f0"));

    [Fact]
    public void Create_canonicalizes_separators_dot_segments_and_unicode()
    {
        var result = StorageAddress.Create(
            ProfileId,
            "root-token",
            "folder\\sub//Cafe\u0301/./file.txt");

        Assert.True(result.IsSuccess);
        Assert.Equal("folder/sub/Caf\u00e9/file.txt", result.Value.CanonicalRelativePath);
        Assert.Equal("file.txt", result.Value.Name);
        Assert.False(result.Value.IsRoot);
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("folder/../secret")]
    [InlineData("folder/%2e%2e/secret")]
    [InlineData("folder/%252e%252e/secret")]
    [InlineData("folder/%2e%2e%2fsecret")]
    [InlineData("/absolute/path")]
    [InlineData("\\\\server\\share")]
    [InlineData("C:\\Windows")]
    [InlineData("safe/\0unsafe")]
    public void Create_rejects_paths_that_are_not_safe_root_relative_paths(string path)
    {
        var result = StorageAddress.Create(ProfileId, "root-token", path);

        Assert.True(result.IsFailure);
        Assert.Equal("storage.address.invalid_path", result.Error.Code);
    }

    [Fact]
    public void Opaque_native_version_and_entity_tag_ids_are_preserved_and_never_path_normalized()
    {
        const string nativeItemId = "opaque/../item%2Fid";
        const string versionId = "version//with/../segments";
        const string entityTag = "etag//with/../segments";

        var result = StorageAddress.Create(
            ProfileId,
            "root-token",
            "folder/file.bin",
            nativeItemId,
            versionId,
            entityTag);

        Assert.True(result.IsSuccess);
        Assert.Equal(nativeItemId, result.Value.NativeItemId);
        Assert.Equal(versionId, result.Value.VersionId);
        Assert.Equal(entityTag, result.Value.EntityTag);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("etag\0unsafe")]
    public void Create_rejects_invalid_entity_tags(string entityTag)
    {
        var result = StorageAddress.Create(
            ProfileId,
            "root-token",
            "file.bin",
            entityTag: entityTag);

        Assert.True(result.IsFailure);
        Assert.Equal("storage.address.invalid", result.Error.Code);
    }

    [Fact]
    public void Child_and_parent_operations_remain_inside_the_same_root_identity()
    {
        var root = StorageAddress.Create(ProfileId, "root-token", string.Empty).Value;
        var child = root.Append("one/two.txt");

        Assert.True(child.IsSuccess);
        Assert.Equal(ProfileId, child.Value.ProfileId);
        Assert.Equal("root-token", child.Value.RootIdentity);
        Assert.Equal("one/two.txt", child.Value.CanonicalRelativePath);
        Assert.Equal("one", child.Value.Parent.CanonicalRelativePath);
        Assert.True(child.Value.Parent.Parent.IsRoot);
        Assert.Null(child.Value.EntityTag);
    }
}
