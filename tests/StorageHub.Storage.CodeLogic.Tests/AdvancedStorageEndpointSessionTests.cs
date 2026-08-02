using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Capabilities;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Models;
using ClSignedUrl = CL.Storage.Models.StorageSignedUrl;
using ClSignedUrlMethod = CL.Storage.Models.StorageSignedUrlMethod;
using ClStorageFeature = CL.Storage.Models.StorageFeature;
using ClStorageMetadataUpdateMode = CL.Storage.Models.StorageMetadataUpdateMode;
using ClStorageTagUpdateMode = CL.Storage.Models.StorageTagUpdateMode;
using ContractSignedUrlMethod = StorageHub.Storage.Models.StorageSignedUrlMethod;
using ContractStorageMetadataUpdateMode = StorageHub.Storage.Models.StorageMetadataUpdateMode;
using ContractStorageTagUpdateMode = StorageHub.Storage.Models.StorageTagUpdateMode;
using DomainStorageFeature = StorageHub.Domain.Capabilities.StorageFeature;

namespace StorageHub.Storage.CodeLogic.Tests;

public sealed class AdvancedStorageEndpointSessionTests
{
    private const string RootIdentity = "advanced-root-revision";

    [Fact]
    public async Task ListsAndDeletesExactVersionsWithDeleteMarkersAndOpaquePaging()
    {
        var modified = new DateTimeOffset(2026, 8, 2, 10, 0, 0, TimeSpan.Zero);
        var service = new FakeAdvancedStorageService
        {
            Capabilities = Capabilities(ClStorageFeature.Versioning, new StorageLimits { MaxPageSize = 50 }),
            ListVersionsHandler = (path, _, _) => Task.FromResult(Result<StorageVersionPage>.Success(
                new StorageVersionPage(
                    [
                        new StorageVersion
                        {
                            Path = path,
                            VersionId = "version-2",
                            ETag = "etag-2",
                            Size = 42,
                            LastModified = modified,
                            IsLatest = true
                        },
                        new StorageVersion
                        {
                            Path = path,
                            VersionId = "delete-1",
                            IsDeleteMarker = true
                        }
                    ],
                    "opaque-next"))),
            DeleteVersionHandler = (_, _, _) => Task.FromResult(Result.Success())
        };
        await using var session = CreateSession(service);
        var address = Address(session.ProfileId, "archive/report.bin");

        var listed = await session.ListObjectVersionsAsync(
            address,
            new StorageVersionListRequest(25, "opaque-current", IncludeDeleteMarkers: true));

        Assert.True(listed.IsSuccess);
        Assert.Equal("archive/report.bin", service.LastAdvancedPath);
        Assert.Equal(25, service.LastVersionListOptions?.PageSize);
        Assert.Equal("opaque-current", service.LastVersionListOptions?.ContinuationToken);
        Assert.True(service.LastVersionListOptions?.IncludeDeleteMarkers);
        Assert.Equal("opaque-next", listed.Value.ContinuationToken);
        Assert.Equal(2, listed.Value.Versions.Count);
        Assert.Equal("version-2", listed.Value.Versions[0].Address.VersionId);
        Assert.Equal("etag-2", listed.Value.Versions[0].Address.EntityTag);
        Assert.Equal(modified, listed.Value.Versions[0].LastModifiedUtc);
        Assert.True(listed.Value.Versions[1].IsDeleteMarker);

        var deleted = await session.DeleteObjectVersionAsync(new StorageDeleteVersionRequest(
            Address(session.ProfileId, "archive/report.bin", versionId: "version-2")));

        Assert.True(deleted.IsSuccess);
        Assert.Equal(1, service.DeleteVersionCallCount);
        Assert.Equal("archive/report.bin", service.LastAdvancedPath);
        Assert.Equal("version-2", service.LastDeletedVersionId);
    }

    [Fact]
    public async Task RejectsVersionPageAboveProviderLimitBeforeProviderIo()
    {
        var service = new FakeAdvancedStorageService
        {
            Capabilities = Capabilities(ClStorageFeature.Versioning, new StorageLimits { MaxPageSize = 2 })
        };
        await using var session = CreateSession(service);

        var result = await session.ListObjectVersionsAsync(
            Address(session.ProfileId, "large-page.bin"),
            new StorageVersionListRequest(PageSize: 3));

        Assert.True(result.IsFailure);
        Assert.Equal(StorageFailureKind.Validation, result.Error.Kind);
        Assert.Equal(0, service.ListVersionsCallCount);
    }

    [Fact]
    public async Task VersionOperationsFailClosedWhenCapabilityOrOptionalInterfaceIsMissing()
    {
        var noCapability = new FakeAdvancedStorageService
        {
            Capabilities = Capabilities(ClStorageFeature.None)
        };
        await using var firstSession = CreateSession(noCapability);

        var capabilityResult = await firstSession.ListObjectVersionsAsync(
            Address(firstSession.ProfileId, "report.bin"));

        Assert.True(capabilityResult.IsFailure);
        Assert.Equal("storage.versions.unsupported", capabilityResult.Error.Code);
        Assert.Equal(0, noCapability.ListVersionsCallCount);

        var noInterface = new FakeStorageService
        {
            Capabilities = Capabilities(ClStorageFeature.Versioning)
        };
        await using var secondSession = CreateSession(noInterface);

        var interfaceResult = await secondSession.ListObjectVersionsAsync(
            Address(secondSession.ProfileId, "report.bin"));

        Assert.True(interfaceResult.IsFailure);
        Assert.Equal("storage.versions.interface_unavailable", interfaceResult.Error.Code);
    }

    [Fact]
    public async Task RejectsVersionEntriesForAnotherObjectAsProviderIntegrityFailure()
    {
        var service = new FakeAdvancedStorageService
        {
            Capabilities = Capabilities(ClStorageFeature.Versioning),
            ListVersionsHandler = (_, _, _) => Task.FromResult(Result<StorageVersionPage>.Success(
                new StorageVersionPage(
                    [new StorageVersion { Path = "other.bin", VersionId = "version-1" }],
                    null)))
        };
        await using var session = CreateSession(service);

        var result = await session.ListObjectVersionsAsync(Address(session.ProfileId, "requested.bin"));

        Assert.True(result.IsFailure);
        Assert.Equal(StorageFailureKind.Integrity, result.Error.Kind);
        Assert.Equal("storage.versions.invalid_entry", result.Error.Code);
    }

    [Fact]
    public async Task MetadataReadSnapshotsAndUpdateForwardsModeAndIdentityConditions()
    {
        var providerMetadata = new Dictionary<string, string> { ["owner"] = "StorageHub" };
        var service = new FakeAdvancedStorageService
        {
            Capabilities = Capabilities(
                ClStorageFeature.MetadataRead |
                ClStorageFeature.MetadataWrite |
                ClStorageFeature.ConditionalUpdate |
                ClStorageFeature.Versioning),
            GetMetadataHandler = (_, _) => Task.FromResult(
                Result<IReadOnlyDictionary<string, string>>.Success(providerMetadata)),
            SetMetadataHandler = (path, _, _, _) => Task.FromResult(
                Result<StorageItem>.Success(Item(path, versionId: "version-8", entityTag: "etag-8")))
        };
        await using var session = CreateSession(service);
        var path = "objects/data.bin";

        var read = await session.GetMetadataAsync(Address(session.ProfileId, path));
        providerMetadata["owner"] = "mutated-after-return";

        Assert.True(read.IsSuccess);
        Assert.Equal("StorageHub", read.Value.Values["owner"]);

        var metadata = StorageMetadata.Create(new Dictionary<string, string> { ["tier"] = "archive" });
        Assert.True(metadata.IsSuccess);
        var updated = await session.SetMetadataAsync(new StorageSetMetadataRequest(
            Address(session.ProfileId, path, versionId: "version-7", entityTag: "etag-7"),
            metadata.Value,
            ContractStorageMetadataUpdateMode.Merge));

        Assert.True(updated.IsSuccess);
        Assert.Equal("version-8", updated.Value.Address.VersionId);
        Assert.Equal("etag-8", updated.Value.Address.EntityTag);
        Assert.Equal(ClStorageMetadataUpdateMode.Merge, service.LastMetadataOptions?.Mode);
        Assert.Equal("version-7", service.LastMetadataOptions?.ExpectedVersionId);
        Assert.Equal("etag-7", service.LastMetadataOptions?.ExpectedETag);
        Assert.Equal("archive", service.LastMetadata?["tier"]);
    }

    [Fact]
    public async Task MetadataUpdateEnforcesProviderLimitAndMissingInterfaceBeforeProviderIo()
    {
        var metadata = StorageMetadata.Create(new Dictionary<string, string> { ["key"] = "value" });
        Assert.True(metadata.IsSuccess);
        var limitedService = new FakeAdvancedStorageService
        {
            Capabilities = Capabilities(
                ClStorageFeature.MetadataWrite,
                new StorageLimits { MaxMetadataBytes = 3 })
        };
        await using var limitedSession = CreateSession(limitedService);

        var limitResult = await limitedSession.SetMetadataAsync(new StorageSetMetadataRequest(
            Address(limitedSession.ProfileId, "limited.bin"),
            metadata.Value));

        Assert.True(limitResult.IsFailure);
        Assert.Equal("storage.metadata.provider_limit_exceeded", limitResult.Error.Code);
        Assert.Equal(0, limitedService.SetMetadataCallCount);

        var plainService = new FakeStorageService
        {
            Capabilities = Capabilities(ClStorageFeature.MetadataRead | ClStorageFeature.MetadataWrite)
        };
        await using var plainSession = CreateSession(plainService);

        var interfaceResult = await plainSession.GetMetadataAsync(Address(plainSession.ProfileId, "plain.bin"));

        Assert.True(interfaceResult.IsFailure);
        Assert.Equal("storage.metadata.interface_unavailable", interfaceResult.Error.Code);

        var noConditionService = new FakeAdvancedStorageService
        {
            Capabilities = Capabilities(
                ClStorageFeature.MetadataWrite | ClStorageFeature.Versioning)
        };
        await using var noConditionSession = CreateSession(noConditionService);

        var conditionResult = await noConditionSession.SetMetadataAsync(new StorageSetMetadataRequest(
            Address(noConditionSession.ProfileId, "conditional.bin", versionId: "version-1"),
            metadata.Value));

        Assert.True(conditionResult.IsFailure);
        Assert.Equal("storage.metadata.condition_unsupported", conditionResult.Error.Code);
        Assert.Equal(0, noConditionService.SetMetadataCallCount);
    }

    [Fact]
    public async Task TagsSnapshotForwardMergeModeAndHonorProviderCountLimit()
    {
        var providerTags = new Dictionary<string, string> { ["stage"] = "prod" };
        var service = new FakeAdvancedStorageService
        {
            Capabilities = Capabilities(ClStorageFeature.Tags, new StorageLimits { MaxTags = 1 }),
            GetTagsHandler = (_, _) => Task.FromResult(
                Result<IReadOnlyDictionary<string, string>>.Success(providerTags)),
            SetTagsHandler = (path, _, _, _) => Task.FromResult(Result<StorageItem>.Success(Item(path)))
        };
        await using var session = CreateSession(service);
        var address = Address(session.ProfileId, "tagged.bin");

        var read = await session.GetTagsAsync(address);
        providerTags["stage"] = "mutated-after-return";

        Assert.True(read.IsSuccess);
        Assert.Equal("prod", read.Value.Values["stage"]);

        var oneTag = StorageTags.Create(new Dictionary<string, string> { ["team"] = "core" });
        Assert.True(oneTag.IsSuccess);
        var updated = await session.SetTagsAsync(new StorageSetTagsRequest(
            address,
            oneTag.Value,
            ContractStorageTagUpdateMode.Merge));

        Assert.True(updated.IsSuccess);
        Assert.Equal(ClStorageTagUpdateMode.Merge, service.LastTagOptions?.Mode);
        Assert.Equal("core", service.LastTags?["team"]);

        var twoTags = StorageTags.Create(new Dictionary<string, string>
        {
            ["team"] = "core",
            ["stage"] = "prod"
        });
        Assert.True(twoTags.IsSuccess);
        var rejected = await session.SetTagsAsync(new StorageSetTagsRequest(address, twoTags.Value));

        Assert.True(rejected.IsFailure);
        Assert.Equal("storage.tags.provider_limit_exceeded", rejected.Error.Code);
        Assert.Equal(1, service.SetTagsCallCount);

        var identityBearing = await session.SetTagsAsync(new StorageSetTagsRequest(
            Address(session.ProfileId, "tagged.bin", entityTag: "etag-must-match"),
            oneTag.Value));

        Assert.True(identityBearing.IsFailure);
        Assert.Equal("storage.tags.identity_update_unsupported", identityBearing.Error.Code);
        Assert.Equal(1, service.SetTagsCallCount);
    }

    [Fact]
    public async Task SignedUrlForwardsVersionAndNeverLeaksSecretThroughContractDiagnostics()
    {
        const string secret = "top-secret-signature";
        var expires = DateTimeOffset.UtcNow.AddHours(1);
        var service = new FakeAdvancedStorageService
        {
            Capabilities = Capabilities(
                ClStorageFeature.SignedReadUrls | ClStorageFeature.SignedWriteUrls),
            CreateSignedUrlHandler = (_, options, _) => Task.FromResult(Result<ClSignedUrl>.Success(
                new ClSignedUrl(
                    new Uri($"https://storage.example/object?signature={secret}"),
                    options?.Method ?? ClSignedUrlMethod.Read,
                    expires)))
        };
        await using var session = CreateSession(service);

        var result = await session.CreateSignedUrlAsync(new StorageSignedUrlRequest(
            Address(session.ProfileId, "signed.bin", versionId: "version-3"),
            ContractSignedUrlMethod.Read,
            TimeSpan.FromHours(1)));

        Assert.True(result.IsSuccess);
        Assert.Equal(ClSignedUrlMethod.Read, service.LastSignedUrlOptions?.Method);
        Assert.Equal(TimeSpan.FromHours(1), service.LastSignedUrlOptions?.ExpiresIn);
        Assert.Equal("version-3", service.LastSignedUrlOptions?.VersionId);
        Assert.DoesNotContain(secret, result.Value.ToString(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result.Value.ToString(), StringComparison.Ordinal);

        var write = await session.CreateSignedUrlAsync(new StorageSignedUrlRequest(
            Address(session.ProfileId, "signed.bin"),
            ContractSignedUrlMethod.Write,
            TimeSpan.FromHours(1),
            "application/octet-stream"));

        Assert.True(write.IsSuccess);
        Assert.Equal(ClSignedUrlMethod.Write, service.LastSignedUrlOptions?.Method);
        Assert.Equal("application/octet-stream", service.LastSignedUrlOptions?.ContentType);
        Assert.Null(service.LastSignedUrlOptions?.VersionId);
    }

    [Fact]
    public async Task SignedUrlProviderFailureMessageIsSanitizedAndMissingInterfaceFailsClosed()
    {
        const string secretUrl = "https://storage.example/file?signature=must-never-leak";
        var failingService = new FakeAdvancedStorageService
        {
            Capabilities = Capabilities(ClStorageFeature.SignedWriteUrls),
            CreateSignedUrlHandler = (_, _, _) => Task.FromResult(Result<ClSignedUrl>.Failure(
                StorageErrors.ProviderError($"provider rejected {secretUrl}")))
        };
        await using var failingSession = CreateSession(failingService);

        var conditionalWrite = await failingSession.CreateSignedUrlAsync(new StorageSignedUrlRequest(
            Address(failingSession.ProfileId, "write.bin", entityTag: "etag-must-match"),
            ContractSignedUrlMethod.Write));

        Assert.True(conditionalWrite.IsFailure);
        Assert.Equal("storage.signed_url.invalid_request", conditionalWrite.Error.Code);
        Assert.Equal(0, failingService.CreateSignedUrlCallCount);

        var failed = await failingSession.CreateSignedUrlAsync(new StorageSignedUrlRequest(
            Address(failingSession.ProfileId, "write.bin"),
            ContractSignedUrlMethod.Write));

        Assert.True(failed.IsFailure);
        Assert.DoesNotContain(secretUrl, failed.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secretUrl, failed.Error.ProviderCode ?? string.Empty, StringComparison.Ordinal);

        var plainService = new FakeStorageService
        {
            Capabilities = Capabilities(ClStorageFeature.SignedReadUrls)
        };
        await using var plainSession = CreateSession(plainService);

        var unavailable = await plainSession.CreateSignedUrlAsync(new StorageSignedUrlRequest(
            Address(plainSession.ProfileId, "read.bin"),
            ContractSignedUrlMethod.Read));

        Assert.True(unavailable.IsFailure);
        Assert.Equal("storage.signed_url.interface_unavailable", unavailable.Error.Code);
    }

    [Fact]
    public async Task SignedUrlRejectsExpiredOrUnexpectedlyLongLivedProviderCredentials()
    {
        var expiredService = new FakeAdvancedStorageService
        {
            Capabilities = Capabilities(ClStorageFeature.SignedReadUrls),
            CreateSignedUrlHandler = (_, _, _) => Task.FromResult(Result<ClSignedUrl>.Success(
                new ClSignedUrl(
                    new Uri("https://storage.example/expired?signature=secret"),
                    ClSignedUrlMethod.Read,
                    DateTimeOffset.UtcNow.AddMinutes(-1))))
        };
        await using var expiredSession = CreateSession(expiredService);

        var expired = await expiredSession.CreateSignedUrlAsync(new StorageSignedUrlRequest(
            Address(expiredSession.ProfileId, "expired.bin"),
            ContractSignedUrlMethod.Read,
            TimeSpan.FromMinutes(5)));

        Assert.True(expired.IsFailure);
        Assert.Equal(StorageFailureKind.Integrity, expired.Error.Kind);
        Assert.Equal("storage.signed_url.invalid_response", expired.Error.Code);

        var longLivedService = new FakeAdvancedStorageService
        {
            Capabilities = Capabilities(ClStorageFeature.SignedReadUrls),
            CreateSignedUrlHandler = (_, _, _) => Task.FromResult(Result<ClSignedUrl>.Success(
                new ClSignedUrl(
                    new Uri("https://storage.example/long?signature=secret"),
                    ClSignedUrlMethod.Read,
                    DateTimeOffset.UtcNow.AddMinutes(7))))
        };
        await using var longLivedSession = CreateSession(longLivedService);

        var longLived = await longLivedSession.CreateSignedUrlAsync(new StorageSignedUrlRequest(
            Address(longLivedSession.ProfileId, "long.bin"),
            ContractSignedUrlMethod.Read,
            TimeSpan.FromMinutes(5)));

        Assert.True(longLived.IsFailure);
        Assert.Equal(StorageFailureKind.Integrity, longLived.Error.Kind);
        Assert.Equal("storage.signed_url.invalid_response", longLived.Error.Code);
    }

    [Fact]
    public async Task MapsExposedAdvancedFeaturesAndRejectsUnexposedOnes()
    {
        var service = new FakeStorageService
        {
            Capabilities = Capabilities(
                ClStorageFeature.MetadataRead |
                ClStorageFeature.MetadataWrite |
                ClStorageFeature.SignedReadUrls |
                ClStorageFeature.SignedWriteUrls |
                ClStorageFeature.Versioning |
                ClStorageFeature.Tags |
                ClStorageFeature.AccessControlLists |
                ClStorageFeature.Leases |
                ClStorageFeature.Append,
                new StorageLimits
                {
                    MaxPageSize = 101,
                    MaxObjectBytes = 102,
                    MaxSingleUploadBytes = 103,
                    MaxMetadataBytes = 104,
                    MaxTags = 5,
                    MaxBatchItems = 106,
                    PreferredUploadPartBytes = 107
                })
        };
        await using var session = CreateSession(service);

        Assert.Equal(FeatureSupportLevel.Native, session.Capabilities[DomainStorageFeature.Metadata].Level);
        Assert.Equal(FeatureSupportLevel.Native, session.Capabilities[DomainStorageFeature.MetadataWrite].Level);
        Assert.Equal(FeatureSupportLevel.Native, session.Capabilities[DomainStorageFeature.SignedReadUrls].Level);
        Assert.Equal(FeatureSupportLevel.Native, session.Capabilities[DomainStorageFeature.SignedWriteUrls].Level);
        Assert.Equal(FeatureSupportLevel.Native, session.Capabilities[DomainStorageFeature.ObjectVersioning].Level);
        Assert.Equal(FeatureSupportLevel.Native, session.Capabilities[DomainStorageFeature.Tags].Level);
        Assert.Equal(FeatureSupportLevel.Unsupported, session.Capabilities[DomainStorageFeature.AccessControlLists].Level);
        Assert.Equal(FeatureSupportLevel.Unsupported, session.Capabilities[DomainStorageFeature.Leases].Level);
        Assert.Equal(FeatureSupportLevel.Unsupported, session.Capabilities[DomainStorageFeature.Append].Level);
        Assert.Contains("does not expose", session.Capabilities[DomainStorageFeature.AccessControlLists].Detail);
        Assert.Contains("does not expose", session.Capabilities[DomainStorageFeature.Leases].Detail);
        Assert.Contains("does not expose", session.Capabilities[DomainStorageFeature.Append].Detail);
        Assert.Equal(101, session.Capabilities.MaxPageSize);
        Assert.Equal(102, session.Capabilities.MaxObjectSizeBytes);
        Assert.Equal(103, session.Capabilities.MaxSingleUploadBytes);
        Assert.Equal(104, session.Capabilities.MaxMetadataBytes);
        Assert.Equal(5, session.Capabilities.MaxTags);
        Assert.Equal(106, session.Capabilities.MaxBatchItems);
        Assert.Equal(107, session.Capabilities.PreferredUploadPartBytes);
    }

    private static StorageCapabilities Capabilities(
        ClStorageFeature features,
        StorageLimits? limits = null) => new(features, limits);

    private static CodeLogicStorageEndpointSession CreateSession(IStorageService service) =>
        new(service, ConnectionProfileId.New(), RootIdentity);

    private static StorageAddress Address(
        ConnectionProfileId profileId,
        string relativePath,
        string? versionId = null,
        string? entityTag = null)
    {
        var result = StorageAddress.Create(
            profileId,
            RootIdentity,
            relativePath,
            versionId: versionId,
            entityTag: entityTag);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private static StorageItem Item(
        string path,
        string? versionId = null,
        string? entityTag = null) => new()
        {
            Path = path,
            Name = path[(path.LastIndexOf('/') + 1)..],
            ItemType = StorageItemType.File,
            VersionId = versionId,
            ETag = entityTag
        };
}
