using StorageHub.Contracts.Ipc;

namespace StorageHub.Agent.IntegrationTests;

public sealed class StorageIpcContractTests
{
    [Fact]
    public void StorageListRequestEnforcesVersionedFrameSafeBounds()
    {
        var connectionId = Guid.NewGuid();
        var valid = new StorageListPageRequest(
            StorageIpcContract.CurrentVersion,
            connectionId,
            "archive/2026",
            StorageIpcLimits.MaximumStableIdentityPageSize);
        var oversizedPage = valid with { PageSize = StorageIpcLimits.MaximumStableIdentityPageSize + 1 };
        var oversizedPath = valid with
        {
            RelativePath = new string('x', StorageIpcLimits.MaximumRelativePathLength + 1)
        };

        Assert.True(valid.HasValidBounds);
        Assert.False(oversizedPage.HasValidBounds);
        Assert.False(oversizedPath.HasValidBounds);
        Assert.False(valid with { ContractVersion = 0 } is { HasValidBounds: true });
        Assert.True(StorageIpcContract.IsSupported(StorageIpcContract.LegacyVersion));
        Assert.True(StorageIpcContract.IsSupported(StorageIpcContract.CurrentVersion));
        Assert.False(StorageIpcContract.SupportsStableItemIdentities(StorageIpcContract.LegacyVersion));
        Assert.True(StorageIpcContract.SupportsStableItemIdentities(StorageIpcContract.CurrentVersion));
    }

    [Fact]
    public void StorageListV2IdentityFieldsAreStrictlyBounded()
    {
        var item = new StorageListItem(
            "file.txt",
            "folder/file.txt",
            StorageItemKind.File,
            1,
            LastModifiedUtc: null,
            ContentType: null,
            IsContainer: false,
            NativeItemId: "native-1",
            VersionId: "version-1",
            EntityTag: "etag-1");
        var page = new StorageListPageResponse(
            StorageIpcContract.CurrentVersion,
            Guid.NewGuid(),
            "folder",
            [item],
            ContinuationToken: null,
            RootIdentity: "root-1");

        Assert.True(item.HasValidIdentityBounds);
        Assert.True(page.HasValidRootIdentity);
        Assert.False(item with { EntityTag = "bad\rvalue" } is { HasValidIdentityBounds: true });
        Assert.False(page with { RootIdentity = new string('x', StorageIpcLimits.MaximumOpaqueIdentityLength + 1) }
            is { HasValidRootIdentity: true });
    }

    [Fact]
    public void ReadOnlyResponseContractsHaveNoSecretBearingMembers()
    {
        var responseTypes = new[]
        {
            typeof(ConnectionListResponse),
            typeof(ConnectionSummary),
            typeof(ConnectionTestResponse),
            typeof(StorageListPageResponse),
            typeof(StorageListItem),
            typeof(StorageIpcFailure)
        };
        var forbiddenTerms = new[] { "secret", "password", "privatekey", "credentialreference" };

        foreach (var property in responseTypes.SelectMany(static type => type.GetProperties()))
        {
            Assert.DoesNotContain(
                forbiddenTerms,
                term => property.Name.Contains(term, StringComparison.OrdinalIgnoreCase));
        }
    }
}
