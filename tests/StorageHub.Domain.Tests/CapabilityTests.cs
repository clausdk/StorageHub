using StorageHub.Domain.Capabilities;

namespace StorageHub.Domain.Tests;

public sealed class CapabilityTests
{
    private static readonly char[] ExpectedInvalidCharacters = ['*', ':'];

    [Fact]
    public void Missing_features_are_explicitly_unsupported()
    {
        var capabilities = new EffectiveStorageCapabilities(
            new Dictionary<StorageFeature, FeatureSupport>
            {
                [StorageFeature.ReadStream] = FeatureSupport.Native(),
                [StorageFeature.WriteStream] = FeatureSupport.Emulated("staged locally")
            },
            StorageCaseSensitivity.Sensitive);

        Assert.True(capabilities.Supports(StorageFeature.ReadStream));
        Assert.True(capabilities.Supports(StorageFeature.WriteStream));
        Assert.False(capabilities.Supports(StorageFeature.Delete));
        Assert.Equal(FeatureSupportLevel.Unsupported, capabilities[StorageFeature.Delete].Level);
    }

    [Fact]
    public void Capabilities_take_immutable_snapshots_of_feature_and_path_rules()
    {
        var features = new Dictionary<StorageFeature, FeatureSupport>
        {
            [StorageFeature.ReadStream] = FeatureSupport.Native()
        };
        var invalidCharacters = new List<char> { ':', '*' };
        var capabilities = new EffectiveStorageCapabilities(
            features,
            StorageCaseSensitivity.Insensitive,
            maxObjectSizeBytes: 1024,
            maxPathLength: 260,
            nativePathSeparator: "\\",
            invalidPathCharacters: invalidCharacters,
            maxPageSize: 500,
            maxSingleUploadBytes: 900,
            maxMetadataBytes: 2_048,
            maxTags: 10,
            maxBatchItems: 100,
            preferredUploadPartBytes: 5_242_880);

        features[StorageFeature.ReadStream] = FeatureSupport.Unsupported("changed");
        invalidCharacters.Add('?');

        Assert.Equal(FeatureSupportLevel.Native, capabilities[StorageFeature.ReadStream].Level);
        Assert.Equal(ExpectedInvalidCharacters, capabilities.InvalidPathCharacters.Order());
        Assert.Equal(1024, capabilities.MaxObjectSizeBytes);
        Assert.Equal(260, capabilities.MaxPathLength);
        Assert.Equal(500, capabilities.MaxPageSize);
        Assert.Equal(900, capabilities.MaxSingleUploadBytes);
        Assert.Equal(2_048, capabilities.MaxMetadataBytes);
        Assert.Equal(10, capabilities.MaxTags);
        Assert.Equal(100, capabilities.MaxBatchItems);
        Assert.Equal(5_242_880, capabilities.PreferredUploadPartBytes);
    }

    [Fact]
    public void Non_positive_provider_limits_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EffectiveStorageCapabilities([], maxPageSize: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EffectiveStorageCapabilities([], maxSingleUploadBytes: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EffectiveStorageCapabilities([], maxMetadataBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EffectiveStorageCapabilities([], maxTags: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EffectiveStorageCapabilities([], maxBatchItems: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EffectiveStorageCapabilities([], preferredUploadPartBytes: -1));
    }
}
