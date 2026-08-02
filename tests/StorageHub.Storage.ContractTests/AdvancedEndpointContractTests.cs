using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Models;

namespace StorageHub.Storage.ContractTests;

public sealed class AdvancedEndpointContractTests
{
    private static readonly ConnectionProfileId ProfileId =
        new(Guid.Parse("fc0ac89d-a94f-45bc-b512-a3c07f3c384d"));

    [Fact]
    public void Metadata_is_bounded_validated_and_snapshotted()
    {
        var source = new Dictionary<string, string> { ["owner"] = "alice" };

        var result = StorageMetadata.Create(source);
        source["owner"] = "mallory";

        Assert.True(result.IsSuccess);
        Assert.Equal("alice", result.Value.Values["owner"]);
        Assert.True(StorageMetadata.Create(new Dictionary<string, string> { ["bad name"] = "x" }).IsFailure);
        Assert.True(StorageMetadata.Create(new Dictionary<string, string> { ["owner"] = "bad\0value" }).IsFailure);
        Assert.True(StorageMetadata.Create(new Dictionary<string, string>
        {
            ["large"] = new string('x', StorageMetadata.MaximumCombinedBytes)
        }).IsFailure);
    }

    [Fact]
    public void Tags_enforce_portable_limits_and_take_an_immutable_snapshot()
    {
        var source = new Dictionary<string, string> { ["environment"] = "test" };

        var result = StorageTags.Create(source);
        source["environment"] = "production";

        Assert.True(result.IsSuccess);
        Assert.Equal("test", result.Value.Values["environment"]);
        Assert.True(StorageTags.Create(Enumerable.Range(0, StorageTags.MaximumEntries + 1)
            .ToDictionary(index => $"tag{index}", _ => "value")).IsFailure);
        Assert.True(StorageTags.Create(new Dictionary<string, string> { ["bad?"] = "value" }).IsFailure);
    }

    [Fact]
    public void Version_page_preserves_delete_markers_and_opaque_tokens_immutably()
    {
        var address = Address("item.bin", "generation-7", "etag-7");
        var version = StorageObjectVersion.Create(
            address,
            size: null,
            lastModifiedUtc: new DateTimeOffset(2026, 8, 2, 10, 0, 0, TimeSpan.FromHours(2)),
            isLatest: true,
            isDeleteMarker: true).Value;
        var source = new List<StorageObjectVersion> { version };

        var page = StorageObjectVersionPage.Create(source, "opaque/token+/=");
        source.Clear();

        Assert.True(page.IsSuccess);
        Assert.Single(page.Value.Versions);
        Assert.True(page.Value.Versions[0].IsDeleteMarker);
        Assert.Equal("generation-7", page.Value.Versions[0].Address.VersionId);
        Assert.Equal("opaque/token+/=", page.Value.ContinuationToken);
    }

    [Fact]
    public void Version_requests_validate_page_bounds_tokens_and_exact_deletion_identity()
    {
        Assert.True(new StorageVersionListRequest(PageSize: 1).Validate().IsSuccess);
        Assert.True(new StorageVersionListRequest(PageSize: 0).Validate().IsFailure);
        Assert.True(new StorageVersionListRequest(ContinuationToken: "bad\0token").Validate().IsFailure);
        Assert.True(new StorageDeleteVersionRequest(Address("item.bin", "generation-7")).Validate().IsSuccess);
        Assert.True(new StorageDeleteVersionRequest(Address("item.bin")).Validate().IsFailure);
    }

    [Fact]
    public void Metadata_update_conditions_remain_opaque_and_reject_invalid_tokens()
    {
        var metadata = StorageMetadata.Create(new Dictionary<string, string> { ["owner"] = "alice" }).Value;
        var valid = new StorageSetMetadataRequest(
            Address("item.bin"),
            metadata,
            StorageMetadataUpdateMode.Merge,
            ExpectedVersionId: "opaque/../generation",
            ExpectedEntityTag: "etag/../opaque");
        var invalid = valid with { ExpectedEntityTag = "bad\0etag" };

        Assert.True(valid.Validate().IsSuccess);
        Assert.True(invalid.Validate().IsFailure);
    }

    [Fact]
    public void Signed_url_requests_enforce_lifetime_and_version_rules()
    {
        Assert.True(new StorageSignedUrlRequest(
            Address("item.bin"),
            StorageSignedUrlMethod.Read,
            StorageSignedUrlRequest.MinimumLifetime).Validate().IsSuccess);
        Assert.True(new StorageSignedUrlRequest(
            Address("item.bin"),
            StorageSignedUrlMethod.Read,
            StorageSignedUrlRequest.MaximumLifetime).Validate().IsSuccess);
        Assert.True(new StorageSignedUrlRequest(
            Address("item.bin"),
            StorageSignedUrlMethod.Read,
            TimeSpan.Zero).Validate().IsFailure);
        Assert.True(new StorageSignedUrlRequest(
            Address("item.bin"),
            StorageSignedUrlMethod.Read,
            StorageSignedUrlRequest.MaximumLifetime + TimeSpan.FromTicks(1)).Validate().IsFailure);
        Assert.True(new StorageSignedUrlRequest(
            Address("item.bin", "generation-7"),
            StorageSignedUrlMethod.Write).Validate().IsFailure);
        Assert.True(new StorageSignedUrlRequest(
            Address("item.bin", entityTag: "etag-7"),
            StorageSignedUrlMethod.Write).Validate().IsFailure);
    }

    [Fact]
    public void Signed_url_value_is_marked_secret_bearing_and_redacts_string_output()
    {
        const string secret = "do-not-log-this-signature";
        var result = StorageSignedUrl.Create(
            new Uri($"https://objects.example.test/item?signature={secret}"),
            StorageSignedUrlMethod.Read,
            new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero));

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsSecretBearing);
        Assert.DoesNotContain(secret, result.Value.ToString(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result.Value.ToString(), StringComparison.Ordinal);
    }

    private static StorageAddress Address(
        string path,
        string? versionId = null,
        string? entityTag = null) => StorageAddress.Create(
            ProfileId,
            "root-revision-1",
            path,
            versionId: versionId,
            entityTag: entityTag).Value;
}
