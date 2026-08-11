using System.Security.Cryptography;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Capabilities;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Abstractions;
using StorageHub.Storage.Models;
using ClStorageFeature = CL.Storage.Models.StorageFeature;
using DomainStorageFeature = StorageHub.Domain.Capabilities.StorageFeature;

namespace StorageHub.Storage.CodeLogic.Tests;

public sealed class CodeLogicStorageEndpointSessionTests
{
    private const string RootIdentity = "root-revision-7";

    [Fact]
    public async Task PrefersBackendHealthProbeAndMapsItsFailure()
    {
        var service = new FakeStorageBackend
        {
            HealthHandler = _ => Task.FromResult(Result.Failure(StorageErrors.Timeout("probe timed out"))),
            GetInfoHandler = (_, _) => throw new InvalidOperationException("Fallback probe must not run.")
        };
        await using var session = CreateSession(service);

        var result = await session.CheckHealthAsync();

        Assert.True(result.IsFailure);
        Assert.Equal(StorageFailureKind.Timeout, result.Error.Kind);
        Assert.True(result.Error.IsTransient);
        Assert.Equal(1, service.HealthCallCount);
        Assert.Equal(0, service.GetInfoCallCount);
    }

    [Fact]
    public async Task FallsBackToRootInfoHealthProbeForPlainStorageService()
    {
        var service = new FakeStorageService
        {
            GetInfoHandler = (path, _) => Task.FromResult(Result<StorageItem>.Success(
                Item(path, StorageItemType.Directory)))
        };
        await using var session = CreateSession(service);

        var result = await session.CheckHealthAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(1, service.GetInfoCallCount);
        Assert.Equal(string.Empty, service.LastPath);
    }

    [Fact]
    public async Task MapsProviderItemToRootScopedStorageEntry()
    {
        var profileId = ConnectionProfileId.New();
        var modified = new DateTimeOffset(2026, 8, 2, 10, 15, 0, TimeSpan.FromHours(2));
        var service = new FakeStorageService
        {
            GetInfoHandler = (path, _) => Task.FromResult(Result<StorageItem>.Success(new StorageItem
            {
                Path = path,
                Name = "report.txt",
                ItemType = StorageItemType.File,
                Size = 42,
                LastModified = modified,
                ContentType = "text/plain",
                ETag = "etag-42",
                VersionId = "generation-7",
                Metadata = new Dictionary<string, string> { ["owner"] = "StorageHub" }
            }))
        };
        await using var session = CreateSession(service, profileId);
        var address = Address(profileId, RootIdentity, "documents/report.txt");

        var result = await session.GetEntryAsync(address);

        Assert.True(result.IsSuccess);
        Assert.Equal(address.ProfileId, result.Value.Address.ProfileId);
        Assert.Equal(address.RootIdentity, result.Value.Address.RootIdentity);
        Assert.Equal(address.CanonicalRelativePath, result.Value.Address.CanonicalRelativePath);
        Assert.Equal("generation-7", result.Value.Address.VersionId);
        Assert.Equal("etag-42", result.Value.Address.EntityTag);
        Assert.Equal(StorageEntryKind.File, result.Value.Kind);
        Assert.Equal(42, result.Value.Size);
        Assert.Equal(modified.ToUniversalTime(), result.Value.LastModifiedUtc);
        Assert.Equal("text/plain", result.Value.ContentType);
        Assert.Equal("etag-42", result.Value.ETag);
        Assert.Equal("StorageHub", result.Value.Metadata["owner"]);
        Assert.Equal("documents/report.txt", service.LastPath);
    }

    [Fact]
    public async Task RejectsAddressFromAnotherProfileBeforeCallingProvider()
    {
        var service = new FakeStorageService();
        await using var session = CreateSession(service);
        var foreignAddress = Address(ConnectionProfileId.New(), RootIdentity, "secret.txt");

        var result = await session.GetEntryAsync(foreignAddress);

        Assert.True(result.IsFailure);
        Assert.Equal("storage.address.profile_mismatch", result.Error.Code);
        Assert.Equal(StorageFailureKind.Validation, result.Error.Kind);
        Assert.Equal(0, service.GetInfoCallCount);
    }

    [Fact]
    public async Task RejectsAddressFromPriorRootRevisionBeforeCallingProvider()
    {
        var profileId = ConnectionProfileId.New();
        var service = new FakeStorageService();
        await using var session = CreateSession(service, profileId);
        var staleAddress = Address(profileId, "root-revision-6", "secret.txt");

        var result = await session.GetEntryAsync(staleAddress);

        Assert.True(result.IsFailure);
        Assert.Equal("storage.address.root_mismatch", result.Error.Code);
        Assert.Equal(StorageFailureKind.Conflict, result.Error.Kind);
        Assert.Equal(0, service.GetInfoCallCount);
    }

    [Theory]
    [InlineData("invalid", StorageFailureKind.Validation, false)]
    [InlineData("invalid-content", StorageFailureKind.Validation, false)]
    [InlineData("not-found", StorageFailureKind.NotFound, false)]
    [InlineData("unauthorized", StorageFailureKind.Unauthorized, false)]
    [InlineData("timeout", StorageFailureKind.Timeout, true)]
    [InlineData("conflict", StorageFailureKind.Conflict, false)]
    [InlineData("unavailable", StorageFailureKind.Unavailable, true)]
    [InlineData("unsupported", StorageFailureKind.Unsupported, false)]
    [InlineData("too-large", StorageFailureKind.Validation, false)]
    [InlineData("partial", StorageFailureKind.Provider, false)]
    [InlineData("provider", StorageFailureKind.Provider, false)]
    public async Task MapsCodeLogicErrorsToStableStorageHubFailures(
        string errorName,
        StorageFailureKind expectedKind,
        bool expectedTransient)
    {
        var error = errorName switch
        {
            "invalid" => StorageErrors.InvalidPath("invalid path"),
            "invalid-content" => StorageErrors.InvalidContent("invalid content"),
            "not-found" => StorageErrors.NotFound("not found"),
            "unauthorized" => StorageErrors.Unauthorized("denied"),
            "timeout" => StorageErrors.Timeout("timed out"),
            "conflict" => StorageErrors.Conflict("conflict"),
            "unavailable" => StorageErrors.Unavailable("offline"),
            "unsupported" => StorageErrors.Unsupported("unsupported"),
            "too-large" => StorageErrors.TooLarge("too large"),
            "partial" => StorageErrors.PartialFailure("partially failed"),
            "provider" => StorageErrors.ProviderError("provider failed"),
            _ => throw new ArgumentOutOfRangeException(nameof(errorName))
        };
        var service = new FakeStorageService
        {
            GetInfoHandler = (_, _) => Task.FromResult(Result<StorageItem>.Failure(error))
        };
        await using var session = CreateSession(service);

        var result = await session.GetEntryAsync(Address(session.ProfileId, RootIdentity, "item"));

        Assert.True(result.IsFailure);
        Assert.Equal(error.Code, result.Error.Code);
        Assert.Equal(error.Code, result.Error.ProviderCode);
        Assert.Equal(expectedKind, result.Error.Kind);
        Assert.Equal(expectedTransient, result.Error.IsTransient);
        Assert.Equal("The item could not be inspected.", result.Error.Message);
        Assert.NotEqual(error.Message, result.Error.Message);
    }

    [Fact]
    public async Task MapsListingAndForwardsOpaquePagingOptions()
    {
        var service = new FakeStorageService
        {
            ListHandler = (_, _, _) => Task.FromResult(Result<CL.Storage.Models.StoragePage>.Success(
                new CL.Storage.Models.StoragePage(
                [
                    Item("folder/file.bin", StorageItemType.File, 512),
                    Item("folder/nested", StorageItemType.Directory)
                ],
                "opaque-next")))
        };
        await using var session = CreateSession(service);
        var request = new StorageListRequest(
            Recursive: true,
            PageSize: 73,
            ContinuationToken: "opaque-current");

        var result = await session.ListAsync(
            Address(session.ProfileId, RootIdentity, "folder"),
            request);

        Assert.True(result.IsSuccess);
        Assert.Collection(
            result.Value.Entries,
            entry =>
            {
                Assert.Equal("folder/file.bin", entry.Address.CanonicalRelativePath);
                Assert.Equal(StorageEntryKind.File, entry.Kind);
                Assert.Equal(512, entry.Size);
            },
            entry =>
            {
                Assert.Equal("folder/nested", entry.Address.CanonicalRelativePath);
                Assert.Equal(StorageEntryKind.Directory, entry.Kind);
                Assert.Null(entry.Size);
            });
        Assert.Equal("opaque-next", result.Value.ContinuationToken);
        Assert.NotNull(service.LastListOptions);
        Assert.True(service.LastListOptions.Recursive);
        Assert.Equal(73, service.LastListOptions.PageSize);
        Assert.Equal("opaque-current", service.LastListOptions.ContinuationToken);
    }

    [Fact]
    public async Task RejectsPageSizeAboveProviderLimitBeforeListing()
    {
        var service = new FakeStorageService
        {
            Capabilities = new StorageCapabilities(
                ClStorageFeature.ServerPagination,
                new StorageLimits { MaxPageSize = 10 })
        };
        await using var session = CreateSession(service);

        var result = await session.ListAsync(
            Address(session.ProfileId, RootIdentity, string.Empty),
            new StorageListRequest(PageSize: 11));

        Assert.True(result.IsFailure);
        Assert.Equal("storage.list.provider_page_size_exceeded", result.Error.Code);
        Assert.Equal(0, service.ListCallCount);
    }

    [Fact]
    public async Task RejectsVersionListingWithoutCallingProvider()
    {
        var service = new FakeStorageService();
        await using var session = CreateSession(service);

        var result = await session.ListAsync(
            Address(session.ProfileId, RootIdentity, string.Empty),
            new StorageListRequest(IncludeVersions: true));

        Assert.True(result.IsFailure);
        Assert.Equal("storage.versions.unsupported", result.Error.Code);
        Assert.Equal(StorageFailureKind.Unsupported, result.Error.Kind);
        Assert.Equal(0, service.ListCallCount);
    }

    [Fact]
    public async Task RejectsConditionalVersionWriteWhenCapabilityIsAbsent()
    {
        var service = new FakeStorageService();
        await using var session = CreateSession(service);
        var request = new StorageWriteRequest(
            Address(session.ProfileId, RootIdentity, "versioned.bin"),
            StorageWriteMode.Overwrite,
            expectedDestinationVersionId: "generation-3");

        var result = await session.OpenWriteAsync(request);

        Assert.True(result.IsFailure);
        Assert.Equal("storage.conditional_update.unsupported", result.Error.Code);
        Assert.Equal(0, service.UploadCallCount);
    }

    [Fact]
    public async Task RejectsKnownObjectSizeAboveProviderLimitBeforeUpload()
    {
        var service = new FakeStorageService
        {
            Capabilities = new StorageCapabilities(
                ClStorageFeature.None,
                new StorageLimits { MaxObjectBytes = 10 })
        };
        await using var session = CreateSession(service);

        var result = await session.OpenWriteAsync(new StorageWriteRequest(
            Address(session.ProfileId, RootIdentity, "large.bin"),
            StorageWriteMode.Overwrite,
            expectedLength: 11));

        Assert.True(result.IsFailure);
        Assert.Equal("storage.write.object_too_large", result.Error.Code);
        Assert.Equal(0, service.UploadCallCount);
    }

    [Fact]
    public async Task RejectsMetadataAboveProviderLimitBeforeUpload()
    {
        var service = new FakeStorageService
        {
            Capabilities = new StorageCapabilities(
                ClStorageFeature.MetadataWrite,
                new StorageLimits { MaxMetadataBytes = 3 })
        };
        await using var session = CreateSession(service);

        var result = await session.OpenWriteAsync(new StorageWriteRequest(
            Address(session.ProfileId, RootIdentity, "metadata.bin"),
            StorageWriteMode.Overwrite,
            metadata: new Dictionary<string, string> { ["ab"] = "cd" }));

        Assert.True(result.IsFailure);
        Assert.Equal("storage.write.metadata_too_large", result.Error.Code);
        Assert.Equal(0, service.UploadCallCount);
    }

    [Fact]
    public async Task RejectsKnownSingleUploadLimitWhenProviderHasNoMultipartOrResumePath()
    {
        var service = new FakeStorageService
        {
            Capabilities = new StorageCapabilities(
                ClStorageFeature.None,
                new StorageLimits { MaxSingleUploadBytes = 10 })
        };
        await using var session = CreateSession(service);

        var result = await session.OpenWriteAsync(new StorageWriteRequest(
            Address(session.ProfileId, RootIdentity, "large.bin"),
            StorageWriteMode.Overwrite,
            expectedLength: 11));

        Assert.True(result.IsFailure);
        Assert.Equal("storage.write.single_upload_too_large", result.Error.Code);
        Assert.Equal(0, service.UploadCallCount);
    }

    [Fact]
    public async Task ForwardsVersionAndEntityTagConditionToUploadWhenCapabilityIsPresent()
    {
        var service = new FakeStorageService
        {
            Capabilities = new StorageCapabilities(ClStorageFeature.ConditionalUpdate),
            UploadHandler = async (path, source, _, cancellationToken) =>
            {
                await source.CopyToAsync(Stream.Null, cancellationToken);
                return Result<StorageItem>.Success(Item(
                    path,
                    StorageItemType.File,
                    1,
                    versionId: "generation-8",
                    entityTag: "etag-8"));
            }
        };
        await using var session = CreateSession(service);
        var request = new StorageWriteRequest(
            Address(session.ProfileId, RootIdentity, "versioned.bin"),
            StorageWriteMode.Overwrite,
            expectedLength: 1,
            expectedDestinationVersionId: "generation-7",
            expectedDestinationEntityTag: "etag-7");

        var open = await session.OpenWriteAsync(request);

        Assert.True(open.IsSuccess);
        await using var handle = open.Value;
        Assert.NotNull(service.LastUploadOptions?.Condition);
        Assert.Equal("generation-7", service.LastUploadOptions.Condition.ExpectedVersionId);
        Assert.Equal("etag-7", service.LastUploadOptions.Condition.ExpectedETag);
        await handle.Content.WriteAsync(new byte[] { 1 });
        var commit = await handle.CommitAsync();
        Assert.True(commit.IsSuccess);
        Assert.Equal("generation-8", commit.Value.Address.VersionId);
        Assert.Equal("etag-8", commit.Value.Address.EntityTag);
    }

    [Fact]
    public async Task ForwardsAtomicCreateToRemoteProviderWhenCapabilityIsPresent()
    {
        var service = new FakeStorageService
        {
            Capabilities = new StorageCapabilities(ClStorageFeature.ConditionalCreate),
            UploadHandler = async (path, source, _, cancellationToken) =>
            {
                await source.CopyToAsync(Stream.Null, cancellationToken);
                return Result<StorageItem>.Success(Item(path, StorageItemType.File, 1));
            }
        };
        await using var session = CreateSession(service);

        var open = await session.OpenWriteAsync(new StorageWriteRequest(
            Address(session.ProfileId, RootIdentity, "new-object.bin"),
            StorageWriteMode.CreateNew,
            expectedLength: 1));

        Assert.True(open.IsSuccess);
        await using var handle = open.Value;
        Assert.False(service.LastUploadOptions?.Overwrite);
        Assert.Null(service.LastUploadOptions?.Condition);
        await handle.Content.WriteAsync(new byte[] { 1 });
        Assert.True((await handle.CommitAsync()).IsSuccess);
    }

    [Fact]
    public async Task ForwardsExactVersionReadWhenVersioningCapabilityIsPresent()
    {
        var service = new FakeStorageService
        {
            Capabilities = new StorageCapabilities(ClStorageFeature.Versioning),
            DownloadHandler = (_, _, _) => Task.FromResult(Result<Stream>.Success(
                new MemoryStream(new byte[] { 1 }, writable: false)))
        };
        await using var session = CreateSession(service);

        var result = await session.OpenReadAsync(new StorageReadRequest(
            Address(session.ProfileId, RootIdentity, "versioned.bin"),
            ExpectedVersionId: "generation-7"));

        Assert.True(result.IsSuccess);
        await using var content = result.Value;
        Assert.Equal("generation-7", service.LastDownloadOptions?.VersionId);
    }

    [Fact]
    public async Task PortableChecksumStreamsExplicitSha256WithExactByteBound()
    {
        var content = Enumerable.Range(0, 200_000).Select(static index => (byte)(index % 251)).ToArray();
        var modified = new DateTimeOffset(2026, 8, 2, 8, 0, 0, TimeSpan.Zero);
        var service = new FakeStorageService
        {
            GetInfoHandler = (path, _) => Task.FromResult(Result<StorageItem>.Success(new StorageItem
            {
                Path = path,
                Name = "portable.bin",
                ItemType = StorageItemType.File,
                Size = content.LongLength,
                LastModified = modified,
            })),
            DownloadHandler = (_, _, _) => Task.FromResult(Result<Stream>.Success(
                new MemoryStream(content, writable: false))),
        };
        await using var session = CreateSession(service);
        var address = Address(session.ProfileId, RootIdentity, "portable.bin");
        var entry = await session.GetEntryAsync(address);
        Assert.True(entry.IsSuccess);

        var result = await ((IStoragePortableChecksumSession)session).ComputePortableChecksumAsync(
            new PortableChecksumRequest(entry.Value, maximumBytes: content.LongLength));

        Assert.True(result.IsSuccess);
        Assert.Equal(content.LongLength, result.Value.BytesProcessed);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(content)),
            result.Value.Digest.Value);
        Assert.Equal(content.LongLength, service.LastDownloadOptions?.Length);
        Assert.InRange(service.LastDownloadOptions!.MaxBufferedBytes!.Value, 1, 65_536);
        Assert.Equal(3, service.GetInfoCallCount);
    }

    [Fact]
    public async Task PortableChecksumRejectsUnexpectedByteCount()
    {
        var service = new FakeStorageService
        {
            GetInfoHandler = (path, _) => Task.FromResult(Result<StorageItem>.Success(
                Item(path, StorageItemType.File, size: 4))),
            DownloadHandler = (_, _, _) => Task.FromResult(Result<Stream>.Success(
                new MemoryStream(new byte[] { 1, 2, 3 }, writable: false))),
        };
        await using var session = CreateSession(service);
        var entry = await session.GetEntryAsync(Address(session.ProfileId, RootIdentity, "short.bin"));

        var result = await ((IStoragePortableChecksumSession)session).ComputePortableChecksumAsync(
            new PortableChecksumRequest(entry.Value, maximumBytes: 4));

        Assert.True(result.IsFailure);
        Assert.Equal("storage.checksum.invalid_result", result.Error.Code);
        Assert.Equal(StorageFailureKind.Integrity, result.Error.Kind);
    }

    [Fact]
    public async Task PortableChecksumUsesStrongEntityTagInsteadOfProviderTimestampPrecision()
    {
        var content = new byte[] { 1, 2, 3, 4 };
        var first = true;
        var service = new FakeStorageService
        {
            GetInfoHandler = (path, _) =>
            {
                var modified = first
                    ? new DateTimeOffset(2026, 8, 10, 8, 0, 0, 123, TimeSpan.Zero)
                    : new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero);
                first = false;
                return Task.FromResult(Result<StorageItem>.Success(new StorageItem
                {
                    Path = path,
                    Name = "etag.bin",
                    ItemType = StorageItemType.File,
                    Size = content.LongLength,
                    LastModified = modified,
                    ETag = "stable-etag"
                }));
            },
            DownloadHandler = (_, _, _) => Task.FromResult(Result<Stream>.Success(
                new MemoryStream(content, writable: false)))
        };
        await using var session = CreateSession(service);
        var entry = await session.GetEntryAsync(Address(session.ProfileId, RootIdentity, "etag.bin"));

        var result = await ((IStoragePortableChecksumSession)session).ComputePortableChecksumAsync(
            new PortableChecksumRequest(entry.Value, content.LongLength));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
    }

    [Fact]
    public async Task RejectsExactVersionReadWhenVersioningCapabilityIsAbsent()
    {
        var service = new FakeStorageService();
        await using var session = CreateSession(service);

        var result = await session.OpenReadAsync(new StorageReadRequest(
            Address(session.ProfileId, RootIdentity, "versioned.bin"),
            ExpectedVersionId: "generation-7"));

        Assert.True(result.IsFailure);
        Assert.Equal("storage.read.version_unsupported", result.Error.Code);
        Assert.Null(service.LastDownloadOptions);
    }

    [Fact]
    public async Task RejectsEntityTagReadBecauseCodeLogicHasNoReadCondition()
    {
        var service = new FakeStorageService
        {
            Capabilities = new StorageCapabilities(ClStorageFeature.Versioning)
        };
        await using var session = CreateSession(service);

        var result = await session.OpenReadAsync(new StorageReadRequest(
            Address(session.ProfileId, RootIdentity, "versioned.bin"),
            ExpectedEntityTag: "etag-7"));

        Assert.True(result.IsFailure);
        Assert.Equal("storage.read.etag_condition_unsupported", result.Error.Code);
        Assert.Null(service.LastDownloadOptions);
    }

    [Fact]
    public async Task ForwardsVersionAndEntityTagConditionToDeleteWhenCapabilityIsPresent()
    {
        var service = new FakeStorageService
        {
            Capabilities = new StorageCapabilities(ClStorageFeature.ConditionalDelete)
        };
        await using var session = CreateSession(service);

        var result = await session.DeleteAsync(new StorageDeleteRequest(
            Address(session.ProfileId, RootIdentity, "versioned.bin"),
            ExpectedVersionId: "generation-7",
            ExpectedEntityTag: "etag-7"));

        Assert.True(result.IsSuccess);
        Assert.NotNull(service.LastDeleteOptions?.Condition);
        Assert.Equal("generation-7", service.LastDeleteOptions.Condition.ExpectedVersionId);
        Assert.Equal("etag-7", service.LastDeleteOptions.Condition.ExpectedETag);
    }

    [Fact]
    public async Task RejectsConditionalDeleteWhenCapabilityIsAbsent()
    {
        var service = new FakeStorageService();
        await using var session = CreateSession(service);

        var result = await session.DeleteAsync(new StorageDeleteRequest(
            Address(session.ProfileId, RootIdentity, "versioned.bin"),
            ExpectedVersionId: "generation-7"));

        Assert.True(result.IsFailure);
        Assert.Equal("storage.conditional_delete.unsupported", result.Error.Code);
        Assert.Equal(0, service.DeleteCallCount);
    }

    [Fact]
    public async Task RejectsNonAtomicRemoteCreateNewBeforeStartingUpload()
    {
        var service = new FakeStorageService { Provider = StorageProvider.S3 };
        await using var session = CreateSession(service);

        var result = await session.OpenWriteAsync(new StorageWriteRequest(
            Address(session.ProfileId, RootIdentity, "new-object.bin"),
            StorageWriteMode.CreateNew));

        Assert.True(result.IsFailure);
        Assert.Equal("storage.create_new.atomicity_unsupported", result.Error.Code);
        Assert.Equal(StorageFailureKind.Unsupported, result.Error.Kind);
        Assert.Equal(0, service.UploadCallCount);
    }

    [Fact]
    public async Task CapabilityMappingIsVisibleThroughPublicSessionSurface()
    {
        var service = new FakeStorageService
        {
            Provider = StorageProvider.S3,
            Capabilities = new StorageCapabilities(
                Directories: false,
                NativeCopy: true,
                NativeMove: false,
                RangeReads: true,
                Metadata: true,
                ServerPagination: true)
        };
        await using var session = CreateSession(service);

        Assert.Equal(StorageCaseSensitivity.Sensitive, session.Capabilities.CaseSensitivity);
        Assert.Equal("/", session.Capabilities.NativePathSeparator);
        Assert.Equal(FeatureSupportLevel.Native, session.Capabilities[DomainStorageFeature.PaginatedList].Level);
        Assert.Equal(FeatureSupportLevel.Native, session.Capabilities[DomainStorageFeature.ResumeDownload].Level);
        Assert.Equal(FeatureSupportLevel.Native, session.Capabilities[DomainStorageFeature.FileCopy].Level);
        Assert.Equal(FeatureSupportLevel.Unsupported, session.Capabilities[DomainStorageFeature.DirectoryCopy].Level);
        Assert.Equal(FeatureSupportLevel.Unsupported, session.Capabilities[DomainStorageFeature.Copy].Level);
        Assert.Equal(FeatureSupportLevel.Native, session.Capabilities[DomainStorageFeature.ServerSideCopy].Level);
        Assert.Equal(FeatureSupportLevel.Native, session.Capabilities[DomainStorageFeature.Metadata].Level);
        Assert.Equal(FeatureSupportLevel.Emulated, session.Capabilities[DomainStorageFeature.RecursiveList].Level);
        Assert.Equal(FeatureSupportLevel.Unsupported, session.Capabilities[DomainStorageFeature.Move].Level);
        Assert.Equal(FeatureSupportLevel.Unsupported, session.Capabilities[DomainStorageFeature.FileMove].Level);
        Assert.Equal(FeatureSupportLevel.Unsupported, session.Capabilities[DomainStorageFeature.DirectoryMove].Level);
        Assert.Equal(FeatureSupportLevel.Unsupported, session.Capabilities[DomainStorageFeature.CreateDirectory].Level);
        Assert.Equal(FeatureSupportLevel.Unsupported, session.Capabilities[DomainStorageFeature.ResumeUpload].Level);
        Assert.Equal(FeatureSupportLevel.Unsupported, session.Capabilities[DomainStorageFeature.ObjectVersioning].Level);
        Assert.Equal(FeatureSupportLevel.Unsupported, session.Capabilities[DomainStorageFeature.DirectRemoteToRemoteTransfer].Level);
        Assert.Equal(FeatureSupportLevel.Unsupported, session.Capabilities[DomainStorageFeature.ConditionalCreate].Level);
    }

    [Fact]
    public async Task MapsFileAndDirectoryTransferCapabilitiesIndependently()
    {
        var service = new FakeStorageService
        {
            Provider = StorageProvider.Ftp,
            Capabilities = new StorageCapabilities(
                ClStorageFeature.FileCopy |
                ClStorageFeature.DirectoryMove |
                ClStorageFeature.RelayedCopy)
        };
        await using var session = CreateSession(service);

        Assert.Equal(FeatureSupportLevel.Emulated, session.Capabilities[DomainStorageFeature.FileCopy].Level);
        Assert.Equal(FeatureSupportLevel.Unsupported, session.Capabilities[DomainStorageFeature.DirectoryCopy].Level);
        Assert.Equal(FeatureSupportLevel.Unsupported, session.Capabilities[DomainStorageFeature.FileMove].Level);
        Assert.Equal(FeatureSupportLevel.Emulated, session.Capabilities[DomainStorageFeature.DirectoryMove].Level);
        Assert.Equal(FeatureSupportLevel.Unsupported, session.Capabilities[DomainStorageFeature.Copy].Level);
        Assert.Equal(FeatureSupportLevel.Unsupported, session.Capabilities[DomainStorageFeature.Move].Level);
        Assert.Equal(FeatureSupportLevel.Unsupported, session.Capabilities[DomainStorageFeature.Rename].Level);
    }

    [Fact]
    public async Task LegacyTransferAggregatesRequireBothExactItemKinds()
    {
        var service = new FakeStorageService
        {
            Provider = StorageProvider.S3,
            Capabilities = new StorageCapabilities(
                ClStorageFeature.FileCopy |
                ClStorageFeature.DirectoryCopy |
                ClStorageFeature.FileMove |
                ClStorageFeature.DirectoryMove |
                ClStorageFeature.ServerSideCopy |
                ClStorageFeature.ServerSideMove)
        };
        await using var session = CreateSession(service);

        Assert.Equal(FeatureSupportLevel.Native, session.Capabilities[DomainStorageFeature.FileCopy].Level);
        Assert.Equal(FeatureSupportLevel.Native, session.Capabilities[DomainStorageFeature.DirectoryCopy].Level);
        Assert.Equal(FeatureSupportLevel.Native, session.Capabilities[DomainStorageFeature.FileMove].Level);
        Assert.Equal(FeatureSupportLevel.Native, session.Capabilities[DomainStorageFeature.DirectoryMove].Level);
        Assert.Equal(FeatureSupportLevel.Native, session.Capabilities[DomainStorageFeature.Copy].Level);
        Assert.Equal(FeatureSupportLevel.Native, session.Capabilities[DomainStorageFeature.Move].Level);
        Assert.Equal(FeatureSupportLevel.Native, session.Capabilities[DomainStorageFeature.Rename].Level);
    }

    [Fact]
    public async Task MapsGranularCodeLogicFeaturesAndObjectLimitWithoutEnumAmbiguity()
    {
        var service = new FakeStorageService
        {
            Provider = StorageProvider.S3,
            Capabilities = new StorageCapabilities(
                ClStorageFeature.VirtualDirectories |
                ClStorageFeature.Links |
                ClStorageFeature.AtomicMove |
                ClStorageFeature.AtomicReplace |
                ClStorageFeature.MetadataRead |
                ClStorageFeature.ConditionalCreate |
                ClStorageFeature.ConditionalUpdate |
                ClStorageFeature.ConditionalDelete |
                ClStorageFeature.Checksums |
                ClStorageFeature.Versioning |
                ClStorageFeature.Tags |
                ClStorageFeature.MultipartUpload |
                ClStorageFeature.ResumableUpload |
                ClStorageFeature.ChangeNotifications,
                new StorageLimits { MaxObjectBytes = 9_876_543 })
        };
        await using var session = CreateSession(service);

        Assert.Equal(9_876_543, session.Capabilities.MaxObjectSizeBytes);
        Assert.Equal(FeatureSupportLevel.Native, session.Capabilities[DomainStorageFeature.CreateDirectory].Level);
        Assert.Equal(FeatureSupportLevel.Native, session.Capabilities[DomainStorageFeature.AtomicRename].Level);
        Assert.Equal(FeatureSupportLevel.Native, session.Capabilities[DomainStorageFeature.AtomicReplace].Level);
        Assert.Equal(FeatureSupportLevel.Native, session.Capabilities[DomainStorageFeature.SymbolicLinks].Level);
        Assert.Equal(FeatureSupportLevel.Native, session.Capabilities[DomainStorageFeature.Metadata].Level);
        Assert.Equal(FeatureSupportLevel.Native, session.Capabilities[DomainStorageFeature.ConditionalCreate].Level);
        Assert.Equal(FeatureSupportLevel.Native, session.Capabilities[DomainStorageFeature.ConditionalUpdate].Level);
        Assert.Equal(FeatureSupportLevel.Native, session.Capabilities[DomainStorageFeature.ConditionalDelete].Level);
        Assert.Equal(FeatureSupportLevel.Emulated, session.Capabilities[DomainStorageFeature.Checksums].Level);
        Assert.Equal(FeatureSupportLevel.Native, session.Capabilities[DomainStorageFeature.ObjectVersioning].Level);
        Assert.Equal(FeatureSupportLevel.Native, session.Capabilities[DomainStorageFeature.Tags].Level);
        Assert.Equal(FeatureSupportLevel.Native, session.Capabilities[DomainStorageFeature.MultipartUpload].Level);
        Assert.Equal(FeatureSupportLevel.Native, session.Capabilities[DomainStorageFeature.ChangeNotifications].Level);
        Assert.Equal(FeatureSupportLevel.Unsupported, session.Capabilities[DomainStorageFeature.ResumeUpload].Level);
    }

    [Fact]
    public async Task StreamingWriteAppliesBackpressureAndCommitsMappedEntry()
    {
        var payload = new byte[(2 * 1024 * 1024) + 17];
        Random.Shared.NextBytes(payload);
        var uploadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowProviderRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[]? uploaded = null;
        var service = new FakeStorageService
        {
            UploadHandler = async (path, source, _, cancellationToken) =>
            {
                uploadStarted.TrySetResult();
                await allowProviderRead.Task.WaitAsync(cancellationToken);
                await using var buffer = new MemoryStream();
                await source.CopyToAsync(buffer, cancellationToken);
                uploaded = buffer.ToArray();
                return Result<StorageItem>.Success(Item(path, StorageItemType.File, uploaded.LongLength));
            }
        };
        await using var session = CreateSession(service);
        var destination = Address(session.ProfileId, RootIdentity, "uploads/large.bin");
        var openResult = await session.OpenWriteAsync(new StorageWriteRequest(
            destination,
            StorageWriteMode.Overwrite,
            expectedLength: payload.LongLength,
            contentType: "application/octet-stream",
            metadata: new Dictionary<string, string> { ["source"] = "test" }));
        Assert.True(openResult.IsSuccess);
        await using var handle = openResult.Value;
        await uploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var write = handle.Content.WriteAsync(payload).AsTask();
        await Task.Delay(100);
        Assert.False(write.IsCompleted, "The producer should pause after the bounded pipe reaches its threshold.");
        allowProviderRead.TrySetResult();
        await write.WaitAsync(TimeSpan.FromSeconds(5));
        var commit = await handle.CommitAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(commit.IsSuccess);
        Assert.Equal(destination, commit.Value.Address);
        Assert.Equal(payload.LongLength, commit.Value.Size);
        Assert.Equal(StorageWriteHandleState.Committed, handle.State);
        Assert.Equal(payload, uploaded);
        Assert.NotNull(service.LastUploadOptions);
        Assert.True(service.LastUploadOptions.Overwrite);
        Assert.True(service.LastUploadOptions.CreateParents);
        Assert.Equal("application/octet-stream", service.LastUploadOptions.ContentType);
        Assert.Equal("test", service.LastUploadOptions.Metadata["source"]);
    }

    [Fact]
    public async Task LengthMismatchFaultsWriteAndCancelsProviderUpload()
    {
        var uploadCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeStorageService
        {
            UploadHandler = async (_, source, _, cancellationToken) =>
            {
                try
                {
                    await source.CopyToAsync(Stream.Null, cancellationToken);
                    return Result<StorageItem>.Success(Item("unexpected", StorageItemType.File, 0));
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    uploadCancelled.TrySetResult();
                    throw new OperationCanceledException(cancellationToken);
                }
            }
        };
        await using var session = CreateSession(service);
        var openResult = await session.OpenWriteAsync(new StorageWriteRequest(
            Address(session.ProfileId, RootIdentity, "short.bin"),
            StorageWriteMode.Overwrite,
            expectedLength: 10));
        Assert.True(openResult.IsSuccess);
        var handle = openResult.Value;

        await handle.Content.WriteAsync(new byte[] { 1, 2, 3 });
        var commit = await handle.CommitAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(commit.IsFailure);
        Assert.Equal("storage.write.length_mismatch", commit.Error.Code);
        Assert.Equal(StorageFailureKind.Integrity, commit.Error.Kind);
        Assert.Equal(StorageWriteHandleState.Faulted, handle.State);
        await uploadCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await handle.DisposeAsync();
    }

    [Fact]
    public async Task Local_staging_conflict_is_cleaned_because_reserved_name_is_storagehub_owned()
    {
        var service = new FakeStorageService
        {
            Provider = StorageProvider.Local,
            Capabilities = LocalCapabilities(),
            UploadHandler = async (_, source, _, cancellationToken) =>
            {
                await source.CopyToAsync(Stream.Null, cancellationToken);
                return Result<StorageItem>.Failure(StorageErrors.Conflict("post-create I/O failure"));
            }
        };
        await using var session = CreateSession(service);
        var opened = await session.OpenWriteAsync(new StorageWriteRequest(
            Address(session.ProfileId, RootIdentity, "conflict.bin"),
            StorageWriteMode.CreateNew,
            expectedLength: 1));
        Assert.True(opened.IsSuccess);
        await using var handle = opened.Value;
        await handle.Content.WriteAsync(new byte[] { 1 });

        var committed = await handle.CommitAsync();

        Assert.True(committed.IsFailure);
        Assert.Equal(StorageFailureKind.Conflict, committed.Error.Kind);
        Assert.Equal(1, service.DeleteCallCount);
        Assert.StartsWith(".storagehub-internal/staging/", service.LastPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AbortCancelsProviderAndNeverCommits()
    {
        var uploadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uploadCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = CreateCancellationObservingService(uploadStarted, uploadCancelled);
        await using var session = CreateSession(service);
        var openResult = await session.OpenWriteAsync(new StorageWriteRequest(
            Address(session.ProfileId, RootIdentity, "abort.bin"),
            StorageWriteMode.CreateNew));
        Assert.True(openResult.IsSuccess);
        await using var handle = openResult.Value;
        await uploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var abort = await handle.AbortAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(abort.IsSuccess);
        Assert.Equal(StorageWriteHandleState.Aborted, handle.State);
        await uploadCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PreCancelledAbortLeavesCleanupOwnedByDispose()
    {
        var uploadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uploadCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = CreateCancellationObservingService(uploadStarted, uploadCancelled);
        await using var session = CreateSession(service);
        var openResult = await session.OpenWriteAsync(new StorageWriteRequest(
            Address(session.ProfileId, RootIdentity, "cancelled-abort.bin"),
            StorageWriteMode.CreateNew));
        Assert.True(openResult.IsSuccess);
        var handle = openResult.Value;
        await uploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handle.AbortAsync(cancellation.Token).AsTask());

        Assert.Equal(StorageWriteHandleState.Open, handle.State);
        await handle.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StorageWriteHandleState.Aborted, handle.State);
        await uploadCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DisposingUncommittedHandleCancelsProviderAndMarksItAborted()
    {
        var uploadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uploadCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = CreateCancellationObservingService(uploadStarted, uploadCancelled);
        await using var session = CreateSession(service);
        var openResult = await session.OpenWriteAsync(new StorageWriteRequest(
            Address(session.ProfileId, RootIdentity, "dispose.bin"),
            StorageWriteMode.Overwrite));
        Assert.True(openResult.IsSuccess);
        var handle = openResult.Value;
        await uploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await handle.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StorageWriteHandleState.Aborted, handle.State);
        await uploadCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EarlyProviderFailureFailsBlockedProducerAndReaderIsDisposed()
    {
        var providerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Stream? providerSource = null;
        var service = new FakeStorageService
        {
            UploadHandler = async (_, source, _, _) =>
            {
                providerSource = source;
                providerStarted.TrySetResult();
                await failProvider.Task;
                return Result<StorageItem>.Failure(StorageErrors.ProviderError("early failure"));
            }
        };
        await using var session = CreateSession(service);
        var openResult = await session.OpenWriteAsync(new StorageWriteRequest(
            Address(session.ProfileId, RootIdentity, "early-failure.bin"),
            StorageWriteMode.Overwrite));
        Assert.True(openResult.IsSuccess);
        var handle = openResult.Value;
        await providerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var write = handle.Content.WriteAsync(new byte[(2 * 1024 * 1024) + 1]).AsTask();
        Assert.False(write.IsCompleted, "The producer must be waiting on the bounded pipe before the provider fails.");

        failProvider.TrySetResult();

        await Assert.ThrowsAsync<IOException>(
            () => write.WaitAsync(TimeSpan.FromSeconds(5)));
        var commit = await handle.CommitAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(commit.IsFailure);
        Assert.Equal(StorageFailureKind.Provider, commit.Error.Kind);
        Assert.Equal(StorageWriteHandleState.Faulted, handle.State);
        Assert.NotNull(providerSource);
        Assert.ThrowsAny<InvalidOperationException>(() => providerSource.ReadByte());

        await handle.DisposeAsync();
        Assert.Equal(StorageWriteHandleState.Faulted, handle.State);
    }

    [Fact]
    public async Task AbortCannotTakeOwnershipFromActiveCommit()
    {
        var providerReachedEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowProviderResult = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeStorageService
        {
            UploadHandler = async (path, source, _, cancellationToken) =>
            {
                await source.CopyToAsync(Stream.Null, cancellationToken);
                providerReachedEnd.TrySetResult();
                await allowProviderResult.Task.WaitAsync(cancellationToken);
                return Result<StorageItem>.Success(Item(path, StorageItemType.File, 3));
            }
        };
        await using var session = CreateSession(service);
        var openResult = await session.OpenWriteAsync(new StorageWriteRequest(
            Address(session.ProfileId, RootIdentity, "commit-versus-abort.bin"),
            StorageWriteMode.Overwrite,
            expectedLength: 3));
        Assert.True(openResult.IsSuccess);
        await using var handle = openResult.Value;
        await handle.Content.WriteAsync(new byte[] { 1, 2, 3 });

        var commitTask = handle.CommitAsync().AsTask();
        await providerReachedEnd.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StorageWriteHandleState.Committing, handle.State);

        var abort = await handle.AbortAsync();

        Assert.True(abort.IsFailure);
        Assert.Equal("storage.write.invalid_state", abort.Error.Code);
        Assert.Equal(StorageWriteHandleState.Committing, handle.State);

        allowProviderResult.TrySetResult();
        var commit = await commitTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(commit.IsSuccess);
        Assert.Equal(StorageWriteHandleState.Committed, handle.State);
    }

    [Fact]
    public async Task DisposeJoinsActiveCommitWithoutOverwritingCommittedState()
    {
        var providerReachedEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowProviderResult = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeStorageService
        {
            UploadHandler = async (path, source, _, cancellationToken) =>
            {
                await source.CopyToAsync(Stream.Null, cancellationToken);
                providerReachedEnd.TrySetResult();
                await allowProviderResult.Task.WaitAsync(cancellationToken);
                return Result<StorageItem>.Success(Item(path, StorageItemType.File, 1));
            }
        };
        await using var session = CreateSession(service);
        var openResult = await session.OpenWriteAsync(new StorageWriteRequest(
            Address(session.ProfileId, RootIdentity, "commit-versus-dispose.bin"),
            StorageWriteMode.Overwrite,
            expectedLength: 1));
        Assert.True(openResult.IsSuccess);
        var handle = openResult.Value;
        await handle.Content.WriteAsync(new byte[] { 42 });

        var commitTask = handle.CommitAsync().AsTask();
        await providerReachedEnd.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposeTask = handle.DisposeAsync().AsTask();

        Assert.False(disposeTask.IsCompleted, "Disposal must join the terminal owner before releasing resources.");
        Assert.Equal(StorageWriteHandleState.Committing, handle.State);

        allowProviderResult.TrySetResult();
        var commit = await commitTask.WaitAsync(TimeSpan.FromSeconds(5));
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(commit.IsSuccess);
        Assert.Equal(StorageWriteHandleState.Committed, handle.State);
        await handle.DisposeAsync();
    }

    [Fact]
    public async Task DisposeJoinsActiveAbortBeforeReleasingResources()
    {
        var providerObservedCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowAbortToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeStorageService
        {
            Provider = StorageProvider.Local,
            Capabilities = LocalCapabilities(),
            UploadHandler = async (_, _, _, cancellationToken) =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return Result<StorageItem>.Success(Item("unexpected", StorageItemType.File, 0));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    providerObservedCancellation.TrySetResult();
                    await allowAbortToFinish.Task;
                    throw;
                }
            }
        };
        await using var session = CreateSession(service);
        var openResult = await session.OpenWriteAsync(new StorageWriteRequest(
            Address(session.ProfileId, RootIdentity, "abort-versus-dispose.bin"),
            StorageWriteMode.CreateNew));
        Assert.True(openResult.IsSuccess);
        var handle = openResult.Value;

        var abortTask = handle.AbortAsync().AsTask();
        await providerObservedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposeTask = handle.DisposeAsync().AsTask();

        Assert.False(disposeTask.IsCompleted, "Disposal must wait for the active abort owner.");
        Assert.Equal(StorageWriteHandleState.Aborting, handle.State);

        allowAbortToFinish.TrySetResult();
        var abort = await abortTask.WaitAsync(TimeSpan.FromSeconds(5));
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(abort.IsSuccess);
        Assert.Equal(StorageWriteHandleState.Aborted, handle.State);
    }

    [Fact]
    public async Task CallerCancellationAfterProviderSuccessCannotTurnCommitIntoAbort()
    {
        var providerReachedEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var providerResult = new TaskCompletionSource<Result<StorageItem>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeStorageService
        {
            UploadHandler = async (_, source, _, cancellationToken) =>
            {
                await source.CopyToAsync(Stream.Null, cancellationToken);
                providerReachedEnd.TrySetResult();
                return await providerResult.Task;
            }
        };
        await using var session = CreateSession(service);
        var destination = Address(session.ProfileId, RootIdentity, "cancel-after-success.bin");
        var openResult = await session.OpenWriteAsync(new StorageWriteRequest(
            destination,
            StorageWriteMode.Overwrite,
            expectedLength: 1));
        Assert.True(openResult.IsSuccess);
        await using var handle = openResult.Value;
        await handle.Content.WriteAsync(new byte[] { 7 });
        using var cancellation = new CancellationTokenSource();

        var commitTask = handle.CommitAsync(cancellation.Token).AsTask();
        await providerReachedEnd.Task.WaitAsync(TimeSpan.FromSeconds(5));
        providerResult.TrySetResult(Result<StorageItem>.Success(
            Item(destination.CanonicalRelativePath, StorageItemType.File, 1)));
        cancellation.Cancel();

        var commit = await commitTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(commit.IsSuccess);
        Assert.Equal(StorageWriteHandleState.Committed, handle.State);
    }

    [Fact]
    public async Task PreCancelledCommitDoesNotClaimTerminalOwnership()
    {
        var uploadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uploadCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = CreateCancellationObservingService(uploadStarted, uploadCancelled);
        await using var session = CreateSession(service);
        var openResult = await session.OpenWriteAsync(new StorageWriteRequest(
            Address(session.ProfileId, RootIdentity, "pre-cancelled-commit.bin"),
            StorageWriteMode.CreateNew));
        Assert.True(openResult.IsSuccess);
        var handle = openResult.Value;
        await uploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handle.CommitAsync(cancellation.Token).AsTask());

        Assert.Equal(StorageWriteHandleState.Open, handle.State);
        await handle.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StorageWriteHandleState.Aborted, handle.State);
        await uploadCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UnexpectedProviderCancellationFaultsCommitInsteadOfClaimingAbort()
    {
        var service = new FakeStorageService
        {
            UploadHandler = async (_, source, _, cancellationToken) =>
            {
                await source.CopyToAsync(Stream.Null, cancellationToken);
                throw new OperationCanceledException("Provider canceled without an abort request.");
            }
        };
        await using var session = CreateSession(service);
        var openResult = await session.OpenWriteAsync(new StorageWriteRequest(
            Address(session.ProfileId, RootIdentity, "provider-cancelled.bin"),
            StorageWriteMode.Overwrite));
        Assert.True(openResult.IsSuccess);
        var handle = openResult.Value;

        var commit = await handle.CommitAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(commit.IsFailure);
        Assert.Equal(StorageFailureKind.Integrity, commit.Error.Kind);
        Assert.Equal(StorageWriteHandleState.Faulted, handle.State);
        await handle.DisposeAsync();
        Assert.Equal(StorageWriteHandleState.Faulted, handle.State);
    }

    private static FakeStorageService CreateCancellationObservingService(
        TaskCompletionSource uploadStarted,
        TaskCompletionSource uploadCancelled) => new()
        {
            Provider = StorageProvider.Local,
            Capabilities = LocalCapabilities(),
            UploadHandler = async (_, _, _, cancellationToken) =>
            {
                uploadStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return Result<StorageItem>.Success(Item("unexpected", StorageItemType.File, 0));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    uploadCancelled.TrySetResult();
                    throw;
                }
            }
        };

    private static StorageCapabilities LocalCapabilities() => new(
        ClStorageFeature.PhysicalDirectories |
        ClStorageFeature.FileCopy |
        ClStorageFeature.FileMove |
        ClStorageFeature.ServerSideCopy |
        ClStorageFeature.ServerSideMove |
        ClStorageFeature.AtomicMove |
        ClStorageFeature.AtomicReplace |
        ClStorageFeature.ConditionalCreate |
        ClStorageFeature.RangeReads);

    private static CodeLogicStorageEndpointSession CreateSession(
        FakeStorageService service,
        ConnectionProfileId? profileId = null) =>
        new(service, profileId ?? ConnectionProfileId.New(), RootIdentity);

    private static StorageAddress Address(
        ConnectionProfileId profileId,
        string rootIdentity,
        string relativePath)
    {
        var result = StorageAddress.Create(profileId, rootIdentity, relativePath);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private static StorageItem Item(
        string path,
        StorageItemType itemType,
        long? size = null,
        string? versionId = null,
        string? entityTag = null) => new()
        {
            Path = path,
            Name = path.Length == 0 ? string.Empty : path[(path.LastIndexOf('/') + 1)..],
            ItemType = itemType,
            Size = size,
            VersionId = versionId,
            ETag = entityTag
        };
}
