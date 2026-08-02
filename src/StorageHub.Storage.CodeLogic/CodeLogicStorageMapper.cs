using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Capabilities;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using ClStorageCapabilities = CL.Storage.Models.StorageCapabilities;
using ClStorageFeature = CL.Storage.Models.StorageFeature;
using DomainStorageFeature = StorageHub.Domain.Capabilities.StorageFeature;

namespace StorageHub.Storage.CodeLogic;

internal static class CodeLogicStorageMapper
{
    public static EffectiveStorageCapabilities MapCapabilities(
        StorageProvider provider,
        ClStorageCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        var features = new Dictionary<DomainStorageFeature, FeatureSupport>
        {
            [DomainStorageFeature.List] = FeatureSupport.Native(),
            [DomainStorageFeature.PaginatedList] = capabilities.Supports(ClStorageFeature.ServerPagination)
                ? FeatureSupport.Native("The provider supplies opaque continuation tokens.")
                : FeatureSupport.Emulated("CL.Storage pages an in-memory provider result."),
            [DomainStorageFeature.RecursiveList] = FeatureSupport.Emulated("CL.Storage normalizes recursive listing."),
            [DomainStorageFeature.ReadStream] = FeatureSupport.Native(),
            [DomainStorageFeature.WriteStream] = FeatureSupport.Native(),
            [DomainStorageFeature.ConditionalCreate] = capabilities.Supports(ClStorageFeature.ConditionalCreate)
                ? FeatureSupport.Native("CL.Storage applies the provider's atomic create condition.")
                : FeatureSupport.Unsupported("The provider does not advertise atomic conditional create."),
            [DomainStorageFeature.ConditionalUpdate] = capabilities.Supports(ClStorageFeature.ConditionalUpdate)
                ? FeatureSupport.Native("CL.Storage forwards provider ETag and version preconditions atomically.")
                : FeatureSupport.Unsupported("The provider does not advertise atomic conditional update."),
            [DomainStorageFeature.ConditionalDelete] = capabilities.Supports(ClStorageFeature.ConditionalDelete)
                ? FeatureSupport.Native("CL.Storage forwards provider ETag and version preconditions atomically.")
                : FeatureSupport.Unsupported("The provider does not advertise atomic conditional delete."),
            [DomainStorageFeature.AtomicReplace] = capabilities.Supports(ClStorageFeature.AtomicReplace)
                ? FeatureSupport.Native("The provider publishes replacement content without exposing a partial object.")
                : FeatureSupport.Unsupported("The provider does not advertise atomic replacement."),
            [DomainStorageFeature.ResumeUpload] = capabilities.Supports(ClStorageFeature.ResumableUpload)
                ? FeatureSupport.Unsupported("The provider supports resumable upload internally, but the adapter does not yet expose a resume-token bridge.")
                : FeatureSupport.Unsupported("The provider does not advertise resumable upload."),
            [DomainStorageFeature.ResumeDownload] = capabilities.Supports(ClStorageFeature.RangeReads)
                ? FeatureSupport.Native()
                : FeatureSupport.Unsupported("The provider does not advertise range reads."),
            [DomainStorageFeature.MultipartUpload] = capabilities.Supports(ClStorageFeature.MultipartUpload)
                ? FeatureSupport.Native("CL.Storage selects and manages multipart upload transparently.")
                : FeatureSupport.Unsupported("The provider does not advertise multipart upload."),
            [DomainStorageFeature.CreateDirectory] = capabilities.SupportsAny(
                ClStorageFeature.PhysicalDirectories | ClStorageFeature.VirtualDirectories)
                ? FeatureSupport.Native()
                : FeatureSupport.Unsupported("The provider does not advertise directories."),
            [DomainStorageFeature.Delete] = FeatureSupport.Native(),
            [DomainStorageFeature.FileCopy] = MapCopyCapability(
                capabilities,
                ClStorageFeature.FileCopy,
                "file"),
            [DomainStorageFeature.DirectoryCopy] = MapCopyCapability(
                capabilities,
                ClStorageFeature.DirectoryCopy,
                "directory tree"),
            [DomainStorageFeature.FileMove] = MapMoveCapability(
                capabilities,
                ClStorageFeature.FileMove,
                "file"),
            [DomainStorageFeature.DirectoryMove] = MapMoveCapability(
                capabilities,
                ClStorageFeature.DirectoryMove,
                "directory tree"),
            [DomainStorageFeature.Rename] = MapAggregateMoveCapability(
                capabilities,
                "The legacy rename capability requires both file and directory move support; inspect the granular move capabilities instead."),
            [DomainStorageFeature.Move] = MapAggregateMoveCapability(
                capabilities,
                "The legacy move capability requires both file and directory move support; inspect FileMove and DirectoryMove instead."),
            [DomainStorageFeature.Copy] = MapAggregateCopyCapability(capabilities),
            [DomainStorageFeature.ServerSideCopy] = capabilities.Supports(ClStorageFeature.ServerSideCopy)
                ? FeatureSupport.Native()
                : FeatureSupport.Unsupported("The provider does not advertise native copy."),
            [DomainStorageFeature.AtomicRename] = capabilities.Supports(ClStorageFeature.AtomicMove)
                ? FeatureSupport.Native()
                : FeatureSupport.Unsupported("The provider does not advertise atomic move."),
            [DomainStorageFeature.SymbolicLinks] = capabilities.Supports(ClStorageFeature.Links)
                ? FeatureSupport.Native()
                : FeatureSupport.Unsupported("The provider does not advertise link support."),
            [DomainStorageFeature.Checksums] = FeatureSupport.Emulated(
                "StorageHub streams content through CL.Storage's explicit SHA-256 helper."),
            [DomainStorageFeature.ObjectVersioning] = capabilities.Supports(ClStorageFeature.Versioning)
                ? FeatureSupport.Native("StorageHub preserves provider version IDs and can read an exact version.")
                : FeatureSupport.Unsupported("The provider does not advertise object versioning."),
            [DomainStorageFeature.Metadata] = capabilities.Supports(ClStorageFeature.MetadataRead)
                ? FeatureSupport.Native()
                : FeatureSupport.Unsupported("The provider does not advertise metadata."),
            [DomainStorageFeature.MetadataWrite] = capabilities.Supports(ClStorageFeature.MetadataWrite)
                ? FeatureSupport.Native()
                : FeatureSupport.Unsupported("The provider does not advertise metadata updates."),
            [DomainStorageFeature.Tags] = capabilities.Supports(ClStorageFeature.Tags)
                ? FeatureSupport.Native()
                : FeatureSupport.Unsupported("The provider does not advertise tags."),
            [DomainStorageFeature.SignedReadUrls] = capabilities.Supports(ClStorageFeature.SignedReadUrls)
                ? FeatureSupport.Native()
                : FeatureSupport.Unsupported("The provider does not advertise temporary signed read URLs."),
            [DomainStorageFeature.SignedWriteUrls] = capabilities.Supports(ClStorageFeature.SignedWriteUrls)
                ? FeatureSupport.Native()
                : FeatureSupport.Unsupported("The provider does not advertise temporary signed write URLs."),
            [DomainStorageFeature.AccessControlLists] = MapUnexposedCapability(
                capabilities,
                ClStorageFeature.AccessControlLists,
                "access-control lists",
                "an access-control-list operation contract"),
            [DomainStorageFeature.Leases] = MapUnexposedCapability(
                capabilities,
                ClStorageFeature.Leases,
                "object leases",
                "a lease operation contract"),
            [DomainStorageFeature.Append] = MapUnexposedCapability(
                capabilities,
                ClStorageFeature.Append,
                "append writes",
                "an append-write operation contract"),
            [DomainStorageFeature.ContentType] = capabilities.Supports(ClStorageFeature.MetadataRead)
                ? FeatureSupport.Native()
                : FeatureSupport.Emulated("Content type is inferred when the provider cannot persist it."),
            [DomainStorageFeature.TemporaryFiles] = provider == StorageProvider.Local
                ? FeatureSupport.Native("StorageHub uses a reserved same-volume staging namespace.")
                : FeatureSupport.Unsupported("The adapter does not expose provider-side temporary files."),
            [DomainStorageFeature.ChangeNotifications] = capabilities.Supports(ClStorageFeature.ChangeNotifications)
                ? FeatureSupport.Native()
                : FeatureSupport.Unsupported("The provider does not advertise change notifications."),
            [DomainStorageFeature.RemoteHashing] = FeatureSupport.Unsupported(
                "CL.Storage checksum helpers stream content through the client."),
            [DomainStorageFeature.DirectRemoteToRemoteTransfer] = FeatureSupport.Unsupported(
                "Cross-session transfer remains owned by StorageHub's transfer engine.")
        };

        return new EffectiveStorageCapabilities(
            features,
            GetCaseSensitivity(provider),
            maxObjectSizeBytes: capabilities.Limits.MaxObjectBytes,
            nativePathSeparator: provider == StorageProvider.Local && OperatingSystem.IsWindows() ? "\\" : "/",
            invalidPathCharacters: provider == StorageProvider.Local && OperatingSystem.IsWindows()
                ? Path.GetInvalidFileNameChars()
                : [],
            maxPageSize: capabilities.Limits.MaxPageSize,
            maxSingleUploadBytes: capabilities.Limits.MaxSingleUploadBytes,
            maxMetadataBytes: capabilities.Limits.MaxMetadataBytes,
            maxTags: capabilities.Limits.MaxTags,
            maxBatchItems: capabilities.Limits.MaxBatchItems,
            preferredUploadPartBytes: capabilities.Limits.PreferredUploadPartBytes);
    }

    public static StorageResult<StorageEntry> MapEntry(
        StorageItem item,
        ConnectionProfileId profileId,
        string rootIdentity)
    {
        ArgumentNullException.ThrowIfNull(item);
        var address = StorageAddress.Create(
            profileId,
            rootIdentity,
            item.Path,
            versionId: item.VersionId,
            entityTag: item.ETag);
        if (address.IsFailure)
        {
            return StorageResult<StorageEntry>.Fail(address.Error);
        }

        var kind = item.ItemType switch
        {
            StorageItemType.File => StorageEntryKind.File,
            StorageItemType.Directory => StorageEntryKind.Directory,
            StorageItemType.Link => StorageEntryKind.SymbolicLink,
            _ => StorageEntryKind.Other
        };

        return StorageEntry.Create(
            address.Value,
            kind,
            item.Size,
            item.LastModified,
            item.ContentType,
            item.ETag,
            metadata: item.Metadata);
    }

    public static StorageFailure MapFailure(Error? error, string fallbackCode, string fallbackMessage)
    {
        if (error is null)
        {
            return new StorageFailure(fallbackCode, StorageFailureKind.Unexpected, fallbackMessage);
        }

        var (kind, transient) = error.Code switch
        {
            StorageErrors.InvalidPathCode => (StorageFailureKind.Validation, false),
            StorageErrors.InvalidContentCode => (StorageFailureKind.Validation, false),
            StorageErrors.NotFoundCode => (StorageFailureKind.NotFound, false),
            StorageErrors.UnauthorizedCode => (StorageFailureKind.Unauthorized, false),
            StorageErrors.TimeoutCode => (StorageFailureKind.Timeout, true),
            StorageErrors.ConflictCode => (StorageFailureKind.Conflict, false),
            StorageErrors.UnavailableCode => (StorageFailureKind.Unavailable, true),
            StorageErrors.UnsupportedCode => (StorageFailureKind.Unsupported, false),
            StorageErrors.TooLargeCode => (StorageFailureKind.Validation, false),
            StorageErrors.PartialFailureCode => (StorageFailureKind.Provider, false),
            StorageErrors.ProviderErrorCode => (StorageFailureKind.Provider, false),
            _ => (StorageFailureKind.Provider, false)
        };

        return new StorageFailure(
            error.Code,
            kind,
            fallbackMessage,
            transient,
            error.Code);
    }

    public static StorageFailure Unexpected(string operation) => new(
        "storage.codelogic.unexpected",
        StorageFailureKind.Unexpected,
        $"The CL.Storage {operation} operation failed unexpectedly.");

    public static StorageFailure Unsupported(string code, string message) => new(
        code,
        StorageFailureKind.Unsupported,
        message);

    private static FeatureSupport MapCopyCapability(
        ClStorageCapabilities capabilities,
        ClStorageFeature exactFeature,
        string itemKind)
    {
        if (!capabilities.Supports(exactFeature))
        {
            return FeatureSupport.Unsupported($"The provider does not advertise {itemKind} copy.");
        }

        return capabilities.Supports(ClStorageFeature.ServerSideCopy)
            ? FeatureSupport.Native($"CL.Storage copies the {itemKind} within the provider.")
            : capabilities.Supports(ClStorageFeature.RelayedCopy)
                ? FeatureSupport.Emulated($"CL.Storage copies the {itemKind} through a bounded stream relay.")
                : FeatureSupport.Emulated($"CL.Storage exposes {itemKind} copy without a native server-side guarantee.");
    }

    private static FeatureSupport MapMoveCapability(
        ClStorageCapabilities capabilities,
        ClStorageFeature exactFeature,
        string itemKind)
    {
        if (!capabilities.Supports(exactFeature))
        {
            return FeatureSupport.Unsupported($"The provider does not advertise {itemKind} move.");
        }

        return capabilities.Supports(ClStorageFeature.ServerSideMove)
            ? FeatureSupport.Native($"CL.Storage moves the {itemKind} within the provider.")
            : FeatureSupport.Emulated($"CL.Storage exposes {itemKind} move without a native server-side guarantee.");
    }

    private static FeatureSupport MapAggregateCopyCapability(ClStorageCapabilities capabilities)
    {
        const ClStorageFeature required = ClStorageFeature.FileCopy | ClStorageFeature.DirectoryCopy;
        if (!capabilities.Supports(required))
        {
            return FeatureSupport.Unsupported(
                "The legacy copy capability requires both file and directory copy support; inspect FileCopy and DirectoryCopy instead.");
        }

        return capabilities.Supports(ClStorageFeature.ServerSideCopy)
            ? FeatureSupport.Native("Both file and directory copy operations are available within the provider.")
            : FeatureSupport.Emulated("Both file and directory copy operations are available without a native server-side guarantee.");
    }

    private static FeatureSupport MapAggregateMoveCapability(
        ClStorageCapabilities capabilities,
        string unsupportedReason)
    {
        const ClStorageFeature required = ClStorageFeature.FileMove | ClStorageFeature.DirectoryMove;
        if (!capabilities.Supports(required))
        {
            return FeatureSupport.Unsupported(unsupportedReason);
        }

        return capabilities.Supports(ClStorageFeature.ServerSideMove)
            ? FeatureSupport.Native("Both file and directory move operations are available within the provider.")
            : FeatureSupport.Emulated("Both file and directory move operations are available without a native server-side guarantee.");
    }

    private static FeatureSupport MapUnexposedCapability(
        ClStorageCapabilities capabilities,
        ClStorageFeature providerFeature,
        string providerFeatureName,
        string missingContract)
    {
        return capabilities.Supports(providerFeature)
            ? FeatureSupport.Unsupported(
                $"CL.Storage advertises {providerFeatureName}, but StorageHub does not expose {missingContract} yet.")
            : FeatureSupport.Unsupported($"The provider does not advertise {providerFeatureName}.");
    }

    private static StorageCaseSensitivity GetCaseSensitivity(StorageProvider provider) => provider switch
    {
        StorageProvider.Local when OperatingSystem.IsWindows() => StorageCaseSensitivity.Insensitive,
        StorageProvider.Local => StorageCaseSensitivity.Sensitive,
        StorageProvider.S3 or StorageProvider.Sftp or StorageProvider.AzureBlob or
            StorageProvider.GoogleCloudStorage or StorageProvider.OpenStackSwift => StorageCaseSensitivity.Sensitive,
        _ => StorageCaseSensitivity.ProviderDefined
    };
}
