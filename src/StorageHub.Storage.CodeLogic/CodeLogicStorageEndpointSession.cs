using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Models;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Capabilities;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Abstractions;
using StorageHub.Storage.Models;
using ClStorageFeature = CL.Storage.Models.StorageFeature;
using ClStorageMetadataUpdateMode = CL.Storage.Models.StorageMetadataUpdateMode;
using ClStorageSignedUrlMethod = CL.Storage.Models.StorageSignedUrlMethod;
using ClStorageTagUpdateMode = CL.Storage.Models.StorageTagUpdateMode;
using ContractStorageMetadataUpdateMode = StorageHub.Storage.Models.StorageMetadataUpdateMode;
using ContractStoragePage = StorageHub.Storage.Models.StoragePage;
using ContractStorageSignedUrl = StorageHub.Storage.Models.StorageSignedUrl;
using ContractStorageSignedUrlMethod = StorageHub.Storage.Models.StorageSignedUrlMethod;
using ContractStorageTagUpdateMode = StorageHub.Storage.Models.StorageTagUpdateMode;
using DomainStorageFeature = StorageHub.Domain.Capabilities.StorageFeature;

namespace StorageHub.Storage.CodeLogic;

/// <summary>Adapts one CL.Storage connection to StorageHub's root-safe session contract.</summary>
public sealed class CodeLogicStorageEndpointSession :
    IStorageEndpointSession,
    IStorageAdvancedEndpointSession,
    IStoragePortableChecksumSession
{
    private static readonly TimeSpan SignedUrlExpiryTolerance = TimeSpan.FromMinutes(1);
    private readonly IStorageService _storage;
    private int _disposed;

    public CodeLogicStorageEndpointSession(
        IStorageService storage,
        ConnectionProfileId profileId,
        string rootIdentity)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        if (profileId.IsEmpty)
        {
            throw new ArgumentException("A non-empty profile ID is required.", nameof(profileId));
        }

        if (string.IsNullOrWhiteSpace(rootIdentity))
        {
            throw new ArgumentException("A root identity is required.", nameof(rootIdentity));
        }

        ProfileId = profileId;
        RootIdentity = rootIdentity;
        Capabilities = CodeLogicStorageMapper.MapCapabilities(storage.Provider, storage.Capabilities);
    }

    public ConnectionProfileId ProfileId { get; }

    public string RootIdentity { get; }

    public EffectiveStorageCapabilities Capabilities { get; }

    public async ValueTask<StorageResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        try
        {
            if (_storage is IStorageBackend backend)
            {
                var backendResult = await backend.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
                return backendResult.IsSuccess
                    ? StorageResult.Success()
                    : StorageResult.Fail(CodeLogicStorageMapper.MapFailure(
                        backendResult.Error,
                        "storage.health.failed",
                        "The endpoint health check failed."));
            }

            var result = await _storage.GetInfoAsync(string.Empty, cancellationToken).ConfigureAwait(false);
            return result.IsSuccess
                ? StorageResult.Success()
                : StorageResult.Fail(CodeLogicStorageMapper.MapFailure(
                    result.Error,
                    "storage.health.failed",
                    "The endpoint health check failed."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return StorageResult.Fail(CodeLogicStorageMapper.Unexpected("health check"));
        }
    }

    public async ValueTask<StorageResult<StorageEntry>> GetEntryAsync(
        StorageAddress address,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var validation = Validate(address);
        if (validation is not null)
        {
            return StorageResult<StorageEntry>.Fail(validation);
        }

        try
        {
            var result = await _storage
                .GetInfoAsync(address.CanonicalRelativePath, cancellationToken)
                .ConfigureAwait(false);
            return result.IsSuccess
                ? CodeLogicStorageMapper.MapEntry(result.Value!, ProfileId, RootIdentity)
                : StorageResult<StorageEntry>.Fail(CodeLogicStorageMapper.MapFailure(
                    result.Error,
                    "storage.info.failed",
                    "The item could not be inspected."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return StorageResult<StorageEntry>.Fail(CodeLogicStorageMapper.Unexpected("item information"));
        }
    }

    public async ValueTask<StorageResult<ContractStoragePage>> ListAsync(
        StorageAddress address,
        StorageListRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var validation = Validate(address);
        if (validation is not null)
        {
            return StorageResult<ContractStoragePage>.Fail(validation);
        }

        request ??= new StorageListRequest();
        var requestValidation = request.Validate();
        if (requestValidation.IsFailure)
        {
            return StorageResult<ContractStoragePage>.Fail(requestValidation.Error);
        }

        if (_storage.Capabilities.Limits.MaxPageSize is { } maxPageSize &&
            request.PageSize > maxPageSize)
        {
            return StorageResult<ContractStoragePage>.Fail(new StorageFailure(
                "storage.list.provider_page_size_exceeded",
                StorageFailureKind.Validation,
                $"This provider accepts at most {maxPageSize} items per page."));
        }

        if (request.IncludeVersions)
        {
            return StorageResult<ContractStoragePage>.Fail(CodeLogicStorageMapper.Unsupported(
                "storage.versions.unsupported",
                "StorageHub's list contract does not yet expose CL.Storage's exact-object version pages."));
        }

        try
        {
            var result = await _storage.ListAsync(
                address.CanonicalRelativePath,
                new StorageListOptions
                {
                    Recursive = request.Recursive,
                    PageSize = request.PageSize,
                    ContinuationToken = request.ContinuationToken
                },
                cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                return StorageResult<ContractStoragePage>.Fail(CodeLogicStorageMapper.MapFailure(
                    result.Error,
                    "storage.list.failed",
                    "The endpoint could not be listed."));
            }

            var entries = new List<StorageEntry>(result.Value!.Items.Count);
            foreach (var item in result.Value.Items)
            {
                if (_storage.Provider == StorageProvider.Local &&
                    CodeLogicLocalStaging.IsReserved(item.Path))
                {
                    continue;
                }

                var entry = CodeLogicStorageMapper.MapEntry(item, ProfileId, RootIdentity);
                if (entry.IsFailure)
                {
                    return StorageResult<ContractStoragePage>.Fail(entry.Error);
                }

                entries.Add(entry.Value);
            }

            return StorageResult<ContractStoragePage>.Success(
                new ContractStoragePage(entries, result.Value.ContinuationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return StorageResult<ContractStoragePage>.Fail(CodeLogicStorageMapper.Unexpected("list"));
        }
    }

    public async ValueTask<StorageResult<Stream>> OpenReadAsync(
        StorageReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        var validation = Validate(request.Address);
        if (validation is not null)
        {
            return StorageResult<Stream>.Fail(validation);
        }

        var requestValidation = request.Validate();
        if (requestValidation.IsFailure)
        {
            return StorageResult<Stream>.Fail(requestValidation.Error);
        }

        if (request.ExpectedEntityTag is not null)
        {
            return StorageResult<Stream>.Fail(CodeLogicStorageMapper.Unsupported(
                "storage.read.etag_condition_unsupported",
                "CL.Storage cannot apply an entity-tag condition to a download."));
        }

        if (request.ExpectedVersionId is not null &&
            Capabilities[DomainStorageFeature.ObjectVersioning].Level != FeatureSupportLevel.Native)
        {
            return StorageResult<Stream>.Fail(CodeLogicStorageMapper.Unsupported(
                "storage.read.version_unsupported",
                "The provider does not advertise version-addressed reads."));
        }

        try
        {
            var result = await _storage.DownloadAsync(
                request.Address.CanonicalRelativePath,
                new StorageDownloadOptions
                {
                    Offset = request.Offset,
                    Length = request.Length,
                    VersionId = request.ExpectedVersionId
                },
                cancellationToken).ConfigureAwait(false);
            return result.IsSuccess
                ? StorageResult<Stream>.Success(result.Value!)
                : StorageResult<Stream>.Fail(CodeLogicStorageMapper.MapFailure(
                    result.Error,
                    "storage.read.failed",
                    "The item could not be opened for reading."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return StorageResult<Stream>.Fail(CodeLogicStorageMapper.Unexpected("read"));
        }
    }

    public ValueTask<StorageResult<IStorageWriteHandle>> OpenWriteAsync(
        StorageWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var validation = Validate(request.Destination);
        if (validation is not null)
        {
            return ValueTask.FromResult(StorageResult<IStorageWriteHandle>.Fail(validation));
        }

        var requestValidation = request.Validate();
        if (requestValidation.IsFailure)
        {
            return ValueTask.FromResult(StorageResult<IStorageWriteHandle>.Fail(requestValidation.Error));
        }

        var limitValidation = ValidateWriteLimits(request);
        if (limitValidation is not null)
        {
            return ValueTask.FromResult(StorageResult<IStorageWriteHandle>.Fail(limitValidation));
        }

        var hasUpdateCondition = request.ExpectedDestinationVersionId is not null ||
            request.ExpectedDestinationEntityTag is not null;
        if (hasUpdateCondition && request.Mode != StorageWriteMode.Overwrite)
        {
            return ValueTask.FromResult(StorageResult<IStorageWriteHandle>.Fail(new StorageFailure(
                "storage.write.condition_mode_invalid",
                StorageFailureKind.Validation,
                "Destination identity conditions are valid only for overwrite writes.")));
        }

        if (hasUpdateCondition &&
            Capabilities[DomainStorageFeature.ConditionalUpdate].Level != FeatureSupportLevel.Native)
        {
            return ValueTask.FromResult(StorageResult<IStorageWriteHandle>.Fail(CodeLogicStorageMapper.Unsupported(
                "storage.conditional_update.unsupported",
                "The provider cannot atomically enforce the requested destination identity.")));
        }

        if (request.Mode == StorageWriteMode.CreateNew &&
            Capabilities[DomainStorageFeature.ConditionalCreate].Level != FeatureSupportLevel.Native)
        {
            return ValueTask.FromResult(StorageResult<IStorageWriteHandle>.Fail(CodeLogicStorageMapper.Unsupported(
                "storage.create_new.atomicity_unsupported",
                "This provider cannot guarantee that create-new is atomic, so the write was not started.")));
        }

        if (request.Mode == StorageWriteMode.Resume)
        {
            return ValueTask.FromResult(StorageResult<IStorageWriteHandle>.Fail(CodeLogicStorageMapper.Unsupported(
                "storage.resume_upload.unsupported",
                "StorageHub's streaming handle does not yet expose CL.Storage resume tokens.")));
        }

        try
        {
            IStorageWriteHandle handle = new CodeLogicStreamingWriteHandle(_storage, request, ProfileId, RootIdentity);
            return ValueTask.FromResult(StorageResult<IStorageWriteHandle>.Success(handle));
        }
        catch (Exception)
        {
            return ValueTask.FromResult(StorageResult<IStorageWriteHandle>.Fail(
                CodeLogicStorageMapper.Unexpected("write initialization")));
        }
    }

    public async ValueTask<StorageResult<StorageEntry>> CreateDirectoryAsync(
        StorageAddress address,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var validation = Validate(address);
        if (validation is not null)
        {
            return StorageResult<StorageEntry>.Fail(validation);
        }

        if (!Capabilities.Supports(DomainStorageFeature.CreateDirectory))
        {
            return StorageResult<StorageEntry>.Fail(CodeLogicStorageMapper.Unsupported(
                "storage.directory.unsupported",
                "This endpoint does not support directories."));
        }

        try
        {
            var result = await _storage
                .CreateDirectoryAsync(address.CanonicalRelativePath, cancellationToken)
                .ConfigureAwait(false);
            if (result.IsFailure)
            {
                return StorageResult<StorageEntry>.Fail(CodeLogicStorageMapper.MapFailure(
                    result.Error,
                    "storage.directory.create_failed",
                    "The directory could not be created."));
            }

            return await GetEntryAsync(address, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return StorageResult<StorageEntry>.Fail(CodeLogicStorageMapper.Unexpected("create-directory"));
        }
    }

    public async ValueTask<StorageResult> DeleteAsync(
        StorageDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        var validation = Validate(request.Address);
        if (validation is not null)
        {
            return StorageResult.Fail(validation);
        }

        var requestValidation = request.Validate();
        if (requestValidation.IsFailure)
        {
            return requestValidation;
        }

        var hasDeleteCondition = request.ExpectedVersionId is not null || request.ExpectedEntityTag is not null;
        if (hasDeleteCondition &&
            Capabilities[DomainStorageFeature.ConditionalDelete].Level != FeatureSupportLevel.Native)
        {
            return StorageResult.Fail(CodeLogicStorageMapper.Unsupported(
                "storage.conditional_delete.unsupported",
                "The provider cannot atomically enforce the requested deletion identity."));
        }

        try
        {
            var result = await _storage.DeleteAsync(
                request.Address.CanonicalRelativePath,
                new StorageDeleteOptions
                {
                    Recursive = request.Recursive,
                    IgnoreMissing = request.IgnoreMissing,
                    Condition = hasDeleteCondition
                        ? new StorageMutationCondition
                        {
                            ExpectedETag = request.ExpectedEntityTag,
                            ExpectedVersionId = request.ExpectedVersionId
                        }
                        : null
                },
                cancellationToken).ConfigureAwait(false);
            return result.IsSuccess
                ? StorageResult.Success()
                : StorageResult.Fail(CodeLogicStorageMapper.MapFailure(
                    result.Error,
                    "storage.delete.failed",
                    "The item could not be deleted."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return StorageResult.Fail(CodeLogicStorageMapper.Unexpected("delete"));
        }
    }

    public async ValueTask<StorageResult<PortableChecksumResult>> ComputePortableChecksumAsync(
        PortableChecksumRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        var addressValidation = Validate(request.ExpectedEntry.Address);
        if (addressValidation is not null)
        {
            return StorageResult<PortableChecksumResult>.Fail(addressValidation);
        }

        var requestValidation = request.Validate();
        if (requestValidation.IsFailure)
        {
            return StorageResult<PortableChecksumResult>.Fail(requestValidation.Error);
        }

        if (request.Algorithm != PortableChecksumAlgorithm.Sha256)
        {
            return StorageResult<PortableChecksumResult>.Fail(CodeLogicStorageMapper.Unsupported(
                "storage.checksum.algorithm_unsupported",
                "The adapter only exposes portable SHA-256 evidence."));
        }

        try
        {
            var before = await GetEntryAsync(request.ExpectedEntry.Address, cancellationToken)
                .ConfigureAwait(false);
            if (before.IsFailure)
            {
                return StorageResult<PortableChecksumResult>.Fail(before.Error);
            }

            if (!MatchesChecksumObservation(request.ExpectedEntry, before.Value))
            {
                return ChecksumChanged();
            }

            var expectedLength = request.ExpectedEntry.Size!.Value;
            var checksum = await _storage.ComputeChecksumAsync(
                request.ExpectedEntry.Address.CanonicalRelativePath,
                StorageChecksumAlgorithm.Sha256,
                new StorageDownloadOptions
                {
                    // A positive range bounds the CL.Storage helper to the exact observed file.
                    // Empty files omit Length because CL.Storage correctly rejects a zero range.
                    Length = expectedLength == 0 ? null : expectedLength,
                    VersionId = request.ExpectedEntry.Address.VersionId,
                    MaxBufferedBytes = Math.Min(65_536, Math.Max(1, request.MaximumBytes)),
                },
                progress: null,
                cancellationToken).ConfigureAwait(false);
            if (checksum.IsFailure)
            {
                return StorageResult<PortableChecksumResult>.Fail(CodeLogicStorageMapper.MapFailure(
                    checksum.Error,
                    "storage.checksum.failed",
                    "The portable SHA-256 checksum could not be computed."));
            }

            var after = await GetEntryAsync(request.ExpectedEntry.Address, cancellationToken)
                .ConfigureAwait(false);
            if (after.IsFailure)
            {
                return StorageResult<PortableChecksumResult>.Fail(after.Error);
            }

            var value = checksum.Value!;
            if (!MatchesChecksumObservation(request.ExpectedEntry, after.Value) ||
                !MatchesChecksumObservation(before.Value, after.Value))
            {
                return ChecksumChanged();
            }

            if (value.Algorithm != StorageChecksumAlgorithm.Sha256 ||
                value.BytesProcessed != expectedLength)
            {
                return StorageResult<PortableChecksumResult>.Fail(new StorageFailure(
                    "storage.checksum.invalid_result",
                    StorageFailureKind.Integrity,
                    "CL.Storage returned checksum evidence for an unexpected algorithm or byte count."));
            }

            PortableContentDigest digest;
            try
            {
                digest = new PortableContentDigest(PortableChecksumAlgorithm.Sha256, value.HexValue);
            }
            catch (ArgumentException)
            {
                return StorageResult<PortableChecksumResult>.Fail(new StorageFailure(
                    "storage.checksum.invalid_result",
                    StorageFailureKind.Integrity,
                    "CL.Storage returned a malformed SHA-256 digest."));
            }

            return StorageResult<PortableChecksumResult>.Success(
                new PortableChecksumResult(digest, value.BytesProcessed));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return StorageResult<PortableChecksumResult>.Fail(
                CodeLogicStorageMapper.Unexpected("portable checksum"));
        }
    }

    public async ValueTask<StorageResult<StorageObjectVersionPage>> ListObjectVersionsAsync(
        StorageAddress address,
        StorageVersionListRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var addressValidation = ValidateExactObject(address);
        if (addressValidation is not null)
        {
            return StorageResult<StorageObjectVersionPage>.Fail(addressValidation);
        }

        request ??= new StorageVersionListRequest();
        var requestValidation = request.Validate();
        if (requestValidation.IsFailure)
        {
            return StorageResult<StorageObjectVersionPage>.Fail(requestValidation.Error);
        }

        if (Capabilities[DomainStorageFeature.ObjectVersioning].Level != FeatureSupportLevel.Native)
        {
            return StorageResult<StorageObjectVersionPage>.Fail(CodeLogicStorageMapper.Unsupported(
                "storage.versions.unsupported",
                "The provider does not advertise native object versioning."));
        }

        if (_storage is not IStorageVersionService versionService)
        {
            return StorageResult<StorageObjectVersionPage>.Fail(CodeLogicStorageMapper.Unsupported(
                "storage.versions.interface_unavailable",
                "The CL.Storage connection does not expose its object-version service."));
        }

        if (Capabilities.MaxPageSize is { } maxPageSize && request.PageSize > maxPageSize)
        {
            return StorageResult<StorageObjectVersionPage>.Fail(new StorageFailure(
                "storage.versions.provider_page_size_exceeded",
                StorageFailureKind.Validation,
                $"This provider accepts at most {maxPageSize} versions per page."));
        }

        try
        {
            var result = await versionService.ListVersionsAsync(
                address.CanonicalRelativePath,
                new StorageVersionListOptions
                {
                    PageSize = request.PageSize,
                    ContinuationToken = request.ContinuationToken,
                    IncludeDeleteMarkers = request.IncludeDeleteMarkers
                },
                cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                return StorageResult<StorageObjectVersionPage>.Fail(CodeLogicStorageMapper.MapFailure(
                    result.Error,
                    "storage.versions.list_failed",
                    "The object versions could not be listed."));
            }

            var providerPage = result.Value;
            if (providerPage?.Versions is null || providerPage.Versions.Count > request.PageSize)
            {
                return StorageResult<StorageObjectVersionPage>.Fail(AdvancedIntegrity(
                    "storage.versions.invalid_page",
                    "The provider returned an invalid or oversized object-version page."));
            }

            var versions = new List<StorageObjectVersion>(providerPage.Versions.Count);
            foreach (var providerVersion in providerPage.Versions)
            {
                if (providerVersion is null ||
                    !string.Equals(
                        providerVersion.Path,
                        address.CanonicalRelativePath,
                        StringComparison.Ordinal) ||
                    !request.IncludeDeleteMarkers && providerVersion.IsDeleteMarker)
                {
                    return StorageResult<StorageObjectVersionPage>.Fail(AdvancedIntegrity(
                        "storage.versions.invalid_entry",
                        "The provider returned an object version outside the requested exact-object page."));
                }

                var versionAddress = StorageAddress.Create(
                    ProfileId,
                    RootIdentity,
                    providerVersion.Path,
                    versionId: providerVersion.VersionId,
                    entityTag: providerVersion.ETag);
                if (versionAddress.IsFailure)
                {
                    return StorageResult<StorageObjectVersionPage>.Fail(AdvancedIntegrity(
                        "storage.versions.invalid_identity",
                        "The provider returned an invalid object-version identity."));
                }

                var version = StorageObjectVersion.Create(
                    versionAddress.Value,
                    providerVersion.Size,
                    providerVersion.LastModified,
                    providerVersion.IsLatest,
                    providerVersion.IsDeleteMarker);
                if (version.IsFailure)
                {
                    return StorageResult<StorageObjectVersionPage>.Fail(AdvancedIntegrity(
                        "storage.versions.invalid_entry",
                        "The provider returned an invalid object-version entry."));
                }

                versions.Add(version.Value);
            }

            var page = StorageObjectVersionPage.Create(versions, providerPage.ContinuationToken);
            return page.IsSuccess
                ? page
                : StorageResult<StorageObjectVersionPage>.Fail(AdvancedIntegrity(
                    "storage.versions.invalid_page",
                    "The provider returned an invalid object-version page."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return StorageResult<StorageObjectVersionPage>.Fail(
                CodeLogicStorageMapper.Unexpected("object-version list"));
        }
    }

    public async ValueTask<StorageResult> DeleteObjectVersionAsync(
        StorageDeleteVersionRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        var addressValidation = ValidateExactObject(request.Address);
        if (addressValidation is not null)
        {
            return StorageResult.Fail(addressValidation);
        }

        var requestValidation = request.Validate();
        if (requestValidation.IsFailure)
        {
            return requestValidation;
        }

        if (Capabilities[DomainStorageFeature.ObjectVersioning].Level != FeatureSupportLevel.Native)
        {
            return StorageResult.Fail(CodeLogicStorageMapper.Unsupported(
                "storage.versions.unsupported",
                "The provider does not advertise native object versioning."));
        }

        if (_storage is not IStorageVersionService versionService)
        {
            return StorageResult.Fail(CodeLogicStorageMapper.Unsupported(
                "storage.versions.interface_unavailable",
                "The CL.Storage connection does not expose its object-version service."));
        }

        try
        {
            var result = await versionService.DeleteVersionAsync(
                request.Address.CanonicalRelativePath,
                request.Address.VersionId!,
                cancellationToken).ConfigureAwait(false);
            return result.IsSuccess
                ? StorageResult.Success()
                : StorageResult.Fail(CodeLogicStorageMapper.MapFailure(
                    result.Error,
                    "storage.versions.delete_failed",
                    "The exact object version could not be deleted."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return StorageResult.Fail(CodeLogicStorageMapper.Unexpected("object-version delete"));
        }
    }

    public async ValueTask<StorageResult<StorageMetadata>> GetMetadataAsync(
        StorageAddress address,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var addressValidation = ValidateExactObject(address);
        if (addressValidation is not null)
        {
            return StorageResult<StorageMetadata>.Fail(addressValidation);
        }

        if (address.VersionId is not null)
        {
            return StorageResult<StorageMetadata>.Fail(CodeLogicStorageMapper.Unsupported(
                "storage.metadata.version_read_unsupported",
                "CL.Storage metadata reads cannot target an exact object version."));
        }

        if (Capabilities[DomainStorageFeature.Metadata].Level != FeatureSupportLevel.Native)
        {
            return StorageResult<StorageMetadata>.Fail(CodeLogicStorageMapper.Unsupported(
                "storage.metadata.unsupported",
                "The provider does not advertise native metadata reads."));
        }

        if (_storage is not IStorageMetadataService metadataService)
        {
            return StorageResult<StorageMetadata>.Fail(CodeLogicStorageMapper.Unsupported(
                "storage.metadata.interface_unavailable",
                "The CL.Storage connection does not expose its metadata service."));
        }

        try
        {
            var result = await metadataService
                .GetMetadataAsync(address.CanonicalRelativePath, cancellationToken)
                .ConfigureAwait(false);
            if (result.IsFailure)
            {
                return StorageResult<StorageMetadata>.Fail(CodeLogicStorageMapper.MapFailure(
                    result.Error,
                    "storage.metadata.read_failed",
                    "The object metadata could not be read."));
            }

            if (result.Value is null)
            {
                return StorageResult<StorageMetadata>.Fail(AdvancedIntegrity(
                    "storage.metadata.invalid_response",
                    "The provider returned an invalid metadata snapshot."));
            }

            var metadata = StorageMetadata.Create(result.Value);
            return metadata.IsSuccess
                ? metadata
                : StorageResult<StorageMetadata>.Fail(AdvancedIntegrity(
                    "storage.metadata.invalid_response",
                    "The provider returned an invalid metadata snapshot."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return StorageResult<StorageMetadata>.Fail(CodeLogicStorageMapper.Unexpected("metadata read"));
        }
    }

    public async ValueTask<StorageResult<StorageEntry>> SetMetadataAsync(
        StorageSetMetadataRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        var addressValidation = ValidateExactObject(request.Address);
        if (addressValidation is not null)
        {
            return StorageResult<StorageEntry>.Fail(addressValidation);
        }

        var requestValidation = request.Validate();
        if (requestValidation.IsFailure)
        {
            return StorageResult<StorageEntry>.Fail(requestValidation.Error);
        }

        var identityValidation = ValidateMetadataIdentity(request);
        if (identityValidation is not null)
        {
            return StorageResult<StorageEntry>.Fail(identityValidation);
        }

        if (Capabilities[DomainStorageFeature.MetadataWrite].Level != FeatureSupportLevel.Native)
        {
            return StorageResult<StorageEntry>.Fail(CodeLogicStorageMapper.Unsupported(
                "storage.metadata_write.unsupported",
                "The provider does not advertise native metadata updates."));
        }

        if (_storage is not IStorageMetadataService metadataService)
        {
            return StorageResult<StorageEntry>.Fail(CodeLogicStorageMapper.Unsupported(
                "storage.metadata.interface_unavailable",
                "The CL.Storage connection does not expose its metadata service."));
        }

        var expectedVersionId = request.ExpectedVersionId ?? request.Address.VersionId;
        var expectedEntityTag = request.ExpectedEntityTag ?? request.Address.EntityTag;
        if ((expectedVersionId is not null || expectedEntityTag is not null) &&
            Capabilities[DomainStorageFeature.ConditionalUpdate].Level != FeatureSupportLevel.Native)
        {
            return StorageResult<StorageEntry>.Fail(CodeLogicStorageMapper.Unsupported(
                "storage.metadata.condition_unsupported",
                "The provider cannot atomically enforce the requested metadata identity condition."));
        }

        if (expectedVersionId is not null &&
            Capabilities[DomainStorageFeature.ObjectVersioning].Level != FeatureSupportLevel.Native)
        {
            return StorageResult<StorageEntry>.Fail(CodeLogicStorageMapper.Unsupported(
                "storage.metadata.version_condition_unsupported",
                "The provider does not advertise version-aware metadata updates."));
        }

        if (Capabilities.MaxMetadataBytes is { } maxMetadataBytes &&
            GetEncodedSize(request.Metadata.Values) > maxMetadataBytes)
        {
            return StorageResult<StorageEntry>.Fail(new StorageFailure(
                "storage.metadata.provider_limit_exceeded",
                StorageFailureKind.Validation,
                $"This provider accepts at most {maxMetadataBytes} bytes of object metadata."));
        }

        try
        {
            var result = await metadataService.SetMetadataAsync(
                request.Address.CanonicalRelativePath,
                request.Metadata.Values,
                new StorageMetadataUpdateOptions
                {
                    Mode = request.Mode == ContractStorageMetadataUpdateMode.Merge
                        ? ClStorageMetadataUpdateMode.Merge
                        : ClStorageMetadataUpdateMode.Replace,
                    ExpectedVersionId = expectedVersionId,
                    ExpectedETag = expectedEntityTag
                },
                cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                return StorageResult<StorageEntry>.Fail(CodeLogicStorageMapper.MapFailure(
                    result.Error,
                    "storage.metadata.write_failed",
                    "The object metadata could not be updated."));
            }

            return MapUpdatedEntry(
                result.Value,
                request.Address.CanonicalRelativePath,
                "storage.metadata.invalid_response",
                "The provider returned an invalid metadata-update result.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return StorageResult<StorageEntry>.Fail(CodeLogicStorageMapper.Unexpected("metadata update"));
        }
    }

    public async ValueTask<StorageResult<StorageTags>> GetTagsAsync(
        StorageAddress address,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var addressValidation = ValidateExactObject(address);
        if (addressValidation is not null)
        {
            return StorageResult<StorageTags>.Fail(addressValidation);
        }

        if (address.VersionId is not null)
        {
            return StorageResult<StorageTags>.Fail(CodeLogicStorageMapper.Unsupported(
                "storage.tags.version_read_unsupported",
                "CL.Storage tag reads cannot target an exact object version."));
        }

        if (Capabilities[DomainStorageFeature.Tags].Level != FeatureSupportLevel.Native)
        {
            return StorageResult<StorageTags>.Fail(CodeLogicStorageMapper.Unsupported(
                "storage.tags.unsupported",
                "The provider does not advertise native object tags."));
        }

        if (_storage is not IStorageTagService tagService)
        {
            return StorageResult<StorageTags>.Fail(CodeLogicStorageMapper.Unsupported(
                "storage.tags.interface_unavailable",
                "The CL.Storage connection does not expose its tag service."));
        }

        try
        {
            var result = await tagService
                .GetTagsAsync(address.CanonicalRelativePath, cancellationToken)
                .ConfigureAwait(false);
            if (result.IsFailure)
            {
                return StorageResult<StorageTags>.Fail(CodeLogicStorageMapper.MapFailure(
                    result.Error,
                    "storage.tags.read_failed",
                    "The object tags could not be read."));
            }

            if (result.Value is null)
            {
                return StorageResult<StorageTags>.Fail(AdvancedIntegrity(
                    "storage.tags.invalid_response",
                    "The provider returned an invalid tag snapshot."));
            }

            var tags = StorageTags.Create(result.Value);
            return tags.IsSuccess
                ? tags
                : StorageResult<StorageTags>.Fail(AdvancedIntegrity(
                    "storage.tags.invalid_response",
                    "The provider returned an invalid tag snapshot."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return StorageResult<StorageTags>.Fail(CodeLogicStorageMapper.Unexpected("tag read"));
        }
    }

    public async ValueTask<StorageResult<StorageEntry>> SetTagsAsync(
        StorageSetTagsRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        var addressValidation = ValidateExactObject(request.Address);
        if (addressValidation is not null)
        {
            return StorageResult<StorageEntry>.Fail(addressValidation);
        }

        var requestValidation = request.Validate();
        if (requestValidation.IsFailure)
        {
            return StorageResult<StorageEntry>.Fail(requestValidation.Error);
        }

        if (request.Address.VersionId is not null || request.Address.EntityTag is not null)
        {
            return StorageResult<StorageEntry>.Fail(CodeLogicStorageMapper.Unsupported(
                "storage.tags.identity_update_unsupported",
                "CL.Storage tag updates cannot enforce object version or entity-tag conditions."));
        }

        if (Capabilities[DomainStorageFeature.Tags].Level != FeatureSupportLevel.Native)
        {
            return StorageResult<StorageEntry>.Fail(CodeLogicStorageMapper.Unsupported(
                "storage.tags.unsupported",
                "The provider does not advertise native object tags."));
        }

        if (_storage is not IStorageTagService tagService)
        {
            return StorageResult<StorageEntry>.Fail(CodeLogicStorageMapper.Unsupported(
                "storage.tags.interface_unavailable",
                "The CL.Storage connection does not expose its tag service."));
        }

        if (Capabilities.MaxTags is { } maxTags && request.Tags.Count > maxTags)
        {
            return StorageResult<StorageEntry>.Fail(new StorageFailure(
                "storage.tags.provider_limit_exceeded",
                StorageFailureKind.Validation,
                $"This provider accepts at most {maxTags} object tags."));
        }

        try
        {
            var result = await tagService.SetTagsAsync(
                request.Address.CanonicalRelativePath,
                request.Tags.Values,
                new StorageTagUpdateOptions
                {
                    Mode = request.Mode == ContractStorageTagUpdateMode.Merge
                        ? ClStorageTagUpdateMode.Merge
                        : ClStorageTagUpdateMode.Replace
                },
                cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                return StorageResult<StorageEntry>.Fail(CodeLogicStorageMapper.MapFailure(
                    result.Error,
                    "storage.tags.write_failed",
                    "The object tags could not be updated."));
            }

            return MapUpdatedEntry(
                result.Value,
                request.Address.CanonicalRelativePath,
                "storage.tags.invalid_response",
                "The provider returned an invalid tag-update result.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return StorageResult<StorageEntry>.Fail(CodeLogicStorageMapper.Unexpected("tag update"));
        }
    }

    public async ValueTask<StorageResult<ContractStorageSignedUrl>> CreateSignedUrlAsync(
        StorageSignedUrlRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        var addressValidation = ValidateExactObject(request.Address);
        if (addressValidation is not null)
        {
            return StorageResult<ContractStorageSignedUrl>.Fail(addressValidation);
        }

        var requestValidation = request.Validate();
        if (requestValidation.IsFailure)
        {
            return StorageResult<ContractStorageSignedUrl>.Fail(requestValidation.Error);
        }

        var requiredFeature = request.Method == ContractStorageSignedUrlMethod.Read
            ? DomainStorageFeature.SignedReadUrls
            : DomainStorageFeature.SignedWriteUrls;
        if (Capabilities[requiredFeature].Level != FeatureSupportLevel.Native)
        {
            return StorageResult<ContractStorageSignedUrl>.Fail(CodeLogicStorageMapper.Unsupported(
                request.Method == ContractStorageSignedUrlMethod.Read
                    ? "storage.signed_url.read_unsupported"
                    : "storage.signed_url.write_unsupported",
                "The provider does not advertise the requested signed URL operation."));
        }

        if (_storage is not IStorageSignedUrlService signedUrlService)
        {
            return StorageResult<ContractStorageSignedUrl>.Fail(CodeLogicStorageMapper.Unsupported(
                "storage.signed_url.interface_unavailable",
                "The CL.Storage connection does not expose its signed URL service."));
        }

        var providerMethod = request.Method == ContractStorageSignedUrlMethod.Read
            ? ClStorageSignedUrlMethod.Read
            : ClStorageSignedUrlMethod.Write;
        try
        {
            var requestedAtUtc = DateTimeOffset.UtcNow;
            var result = await signedUrlService.CreateSignedUrlAsync(
                request.Address.CanonicalRelativePath,
                new StorageSignedUrlOptions
                {
                    Method = providerMethod,
                    ExpiresIn = request.ExpiresIn,
                    ContentType = request.ContentType,
                    VersionId = request.Address.VersionId
                },
                cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                return StorageResult<ContractStorageSignedUrl>.Fail(CodeLogicStorageMapper.MapFailure(
                    result.Error,
                    "storage.signed_url.failed",
                    "The temporary signed URL could not be created."));
            }

            var providerUrl = result.Value;
            var completedAtUtc = DateTimeOffset.UtcNow;
            if (providerUrl is null ||
                providerUrl.Url is null ||
                providerUrl.Method != providerMethod ||
                providerUrl.ExpiresAt <= completedAtUtc ||
                providerUrl.ExpiresAt > requestedAtUtc + request.ExpiresIn + SignedUrlExpiryTolerance)
            {
                return StorageResult<ContractStorageSignedUrl>.Fail(AdvancedIntegrity(
                    "storage.signed_url.invalid_response",
                    "The provider returned an invalid signed URL response."));
            }

            var signedUrl = ContractStorageSignedUrl.Create(
                providerUrl.Url,
                request.Method,
                providerUrl.ExpiresAt);
            return signedUrl.IsSuccess
                ? signedUrl
                : StorageResult<ContractStorageSignedUrl>.Fail(AdvancedIntegrity(
                    "storage.signed_url.invalid_response",
                    "The provider returned an invalid signed URL response."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return StorageResult<ContractStorageSignedUrl>.Fail(
                CodeLogicStorageMapper.Unexpected("signed URL"));
        }
    }

    public ValueTask<StorageResult<StorageEntry>> CopyAsync(
        StorageCopyRequest request,
        CancellationToken cancellationToken = default) =>
        TransferWithinSessionAsync(request, move: false, cancellationToken);

    public ValueTask<StorageResult<StorageEntry>> MoveAsync(
        StorageMoveRequest request,
        CancellationToken cancellationToken = default) =>
        TransferWithinSessionAsync(request, move: true, cancellationToken);

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }

    private async ValueTask<StorageResult<StorageEntry>> TransferWithinSessionAsync(
        object request,
        bool move,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var (source, destination, overwrite, sourceVersion, destinationVersion) = request switch
        {
            StorageCopyRequest copy => (
                copy.Source,
                copy.Destination,
                copy.Overwrite,
                copy.ExpectedSourceVersionId,
                copy.ExpectedDestinationVersionId),
            StorageMoveRequest relocation => (
                relocation.Source,
                relocation.Destination,
                relocation.Overwrite,
                relocation.ExpectedSourceVersionId,
                relocation.ExpectedDestinationVersionId),
            _ => throw new ArgumentException("A copy or move request is required.", nameof(request))
        };

        var sourceValidation = Validate(source);
        if (sourceValidation is not null)
        {
            return StorageResult<StorageEntry>.Fail(sourceValidation);
        }

        var destinationValidation = Validate(destination);
        if (destinationValidation is not null)
        {
            return StorageResult<StorageEntry>.Fail(destinationValidation);
        }

        if (sourceVersion is not null || destinationVersion is not null)
        {
            return StorageResult<StorageEntry>.Fail(ConditionalVersionsUnsupported());
        }

        try
        {
            var options = new StorageTransferOptions { Overwrite = overwrite, CreateParents = true };
            var result = move
                ? await _storage.MoveAsync(
                    source.CanonicalRelativePath,
                    destination.CanonicalRelativePath,
                    options,
                    cancellationToken).ConfigureAwait(false)
                : await _storage.CopyAsync(
                    source.CanonicalRelativePath,
                    destination.CanonicalRelativePath,
                    options,
                    cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                return StorageResult<StorageEntry>.Fail(CodeLogicStorageMapper.MapFailure(
                    result.Error,
                    move ? "storage.move.failed" : "storage.copy.failed",
                    move ? "The item could not be moved." : "The item could not be copied."));
            }

            return await GetEntryAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return StorageResult<StorageEntry>.Fail(CodeLogicStorageMapper.Unexpected(move ? "move" : "copy"));
        }
    }

    private StorageFailure? Validate(StorageAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var validation = this.ValidateAddress(address);
        if (validation.IsFailure)
        {
            return validation.Error;
        }

        return _storage.Provider == StorageProvider.Local &&
            CodeLogicLocalStaging.IsReserved(address.CanonicalRelativePath)
                ? new StorageFailure(
                    "storage.path.reserved",
                    StorageFailureKind.Validation,
                    "The selected path is reserved for StorageHub's internal local-provider state.")
                : null;
    }

    private static bool MatchesChecksumObservation(StorageEntry expected, StorageEntry current)
    {
        var expectedEntityTag = expected.Address.EntityTag ?? expected.ETag;
        var currentEntityTag = current.Address.EntityTag ?? current.ETag;
        var identityMatches = expected.Address.VersionId is not null
            ? StringComparer.Ordinal.Equals(expected.Address.VersionId, current.Address.VersionId)
            : expectedEntityTag is not null
                ? StringComparer.Ordinal.Equals(expectedEntityTag, currentEntityTag)
                : expected.LastModifiedUtc is null || expected.LastModifiedUtc == current.LastModifiedUtc;
        return expected.Kind == StorageEntryKind.File &&
               current.Kind == StorageEntryKind.File &&
               expected.Address.ProfileId == current.Address.ProfileId &&
               StringComparer.Ordinal.Equals(expected.Address.RootIdentity, current.Address.RootIdentity) &&
               StringComparer.Ordinal.Equals(
                   expected.Address.CanonicalRelativePath,
                   current.Address.CanonicalRelativePath) &&
               expected.Size == current.Size &&
               identityMatches;
    }

    private static StorageResult<PortableChecksumResult> ChecksumChanged() =>
        StorageResult<PortableChecksumResult>.Fail(new StorageFailure(
            "storage.checksum.source_changed",
            StorageFailureKind.Conflict,
            "The item changed while its portable SHA-256 checksum was being computed."));

    private StorageFailure? ValidateExactObject(StorageAddress address)
    {
        var validation = Validate(address);
        if (validation is not null)
        {
            return validation;
        }

        return address.IsRoot
            ? new StorageFailure(
                "storage.advanced.object_required",
                StorageFailureKind.Validation,
                "This operation requires an exact object path rather than the endpoint root.")
            : null;
    }

    private static StorageFailure? ValidateMetadataIdentity(StorageSetMetadataRequest request)
    {
        if (request.Address.VersionId is not null &&
            request.ExpectedVersionId is not null &&
            !string.Equals(request.Address.VersionId, request.ExpectedVersionId, StringComparison.Ordinal))
        {
            return new StorageFailure(
                "storage.metadata.version_condition_mismatch",
                StorageFailureKind.Validation,
                "The address version and expected metadata version do not match.");
        }

        return request.Address.EntityTag is not null &&
            request.ExpectedEntityTag is not null &&
            !string.Equals(request.Address.EntityTag, request.ExpectedEntityTag, StringComparison.Ordinal)
                ? new StorageFailure(
                    "storage.metadata.etag_condition_mismatch",
                    StorageFailureKind.Validation,
                    "The address entity tag and expected metadata entity tag do not match.")
                : null;
    }

    private StorageResult<StorageEntry> MapUpdatedEntry(
        StorageItem? item,
        string expectedPath,
        string failureCode,
        string failureMessage)
    {
        if (item is null || !string.Equals(item.Path, expectedPath, StringComparison.Ordinal))
        {
            return StorageResult<StorageEntry>.Fail(AdvancedIntegrity(failureCode, failureMessage));
        }

        var mapped = CodeLogicStorageMapper.MapEntry(item, ProfileId, RootIdentity);
        return mapped.IsSuccess
            ? mapped
            : StorageResult<StorageEntry>.Fail(AdvancedIntegrity(failureCode, failureMessage));
    }

    private static long GetEncodedSize(IReadOnlyDictionary<string, string> values)
    {
        long byteCount = 0;
        foreach (var (name, value) in values)
        {
            byteCount += Encoding.UTF8.GetByteCount(name);
            byteCount += Encoding.UTF8.GetByteCount(value);
        }

        return byteCount;
    }

    private static StorageFailure AdvancedIntegrity(string code, string message) => new(
        code,
        StorageFailureKind.Integrity,
        message);

    private StorageFailure? ValidateWriteLimits(StorageWriteRequest request)
    {
        var limits = _storage.Capabilities.Limits;
        if (request.ExpectedLength is { } expectedLength)
        {
            if (limits.MaxObjectBytes is { } maxObjectBytes && expectedLength > maxObjectBytes)
            {
                return new StorageFailure(
                    "storage.write.object_too_large",
                    StorageFailureKind.Validation,
                    $"This provider accepts objects no larger than {maxObjectBytes} bytes.");
            }

            if (limits.MaxSingleUploadBytes is { } maxSingleUploadBytes &&
                expectedLength > maxSingleUploadBytes &&
                !_storage.Capabilities.SupportsAny(
                    ClStorageFeature.MultipartUpload | ClStorageFeature.ResumableUpload))
            {
                return new StorageFailure(
                    "storage.write.single_upload_too_large",
                    StorageFailureKind.Validation,
                    $"This provider accepts at most {maxSingleUploadBytes} bytes in one upload.");
            }
        }

        if (limits.MaxMetadataBytes is { } maxMetadataBytes)
        {
            long metadataBytes = 0;
            foreach (var (name, value) in request.Metadata)
            {
                metadataBytes += Encoding.UTF8.GetByteCount(name);
                metadataBytes += Encoding.UTF8.GetByteCount(value);
                if (metadataBytes > maxMetadataBytes)
                {
                    return new StorageFailure(
                        "storage.write.metadata_too_large",
                        StorageFailureKind.Validation,
                        $"This provider accepts at most {maxMetadataBytes} bytes of object metadata.");
                }
            }
        }

        return null;
    }

    private static StorageFailure ConditionalVersionsUnsupported() => CodeLogicStorageMapper.Unsupported(
        "storage.conditional_version.unsupported",
        "CL.Storage transfer options do not expose source or destination identity conditions.");

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(
        Volatile.Read(ref _disposed) != 0,
        this);
}
