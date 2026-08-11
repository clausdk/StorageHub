using System.Text.Json.Serialization;

namespace StorageHub.Contracts.Ipc;

/// <summary>The independently versioned, read-only storage browsing contract.</summary>
public static class StorageIpcContract
{
    public const int LegacyVersion = 1;
    public const int CurrentVersion = 2;

    public static bool IsSupported(int version) => version is LegacyVersion or CurrentVersion;

    public static bool SupportsStableItemIdentities(int version) => version == CurrentVersion;
}

/// <summary>
/// Limits keep every read-only response comfortably below the normal IPC frame ceiling,
/// including JSON escaping of provider-controlled text.
/// </summary>
public static class StorageIpcLimits
{
    public const int MaximumConnectionResults = 100;
    public const int MaximumSearchTextLength = 256;
    public const int MaximumStoragePageSize = 250;
    public const int MaximumStableIdentityPageSize = 40;
    public const int MaximumRelativePathLength = 4_096;
    public const int MaximumItemNameLength = 512;
    public const int MaximumContentTypeLength = 128;
    public const int MaximumContinuationTokenLength = 8_192;
    public const int MaximumOpaqueIdentityLength = 8_192;
    public const int MaximumFailureCodeLength = 128;
    public const int MaximumFailureMessageLength = 256;

    public static int GetMaximumStoragePageSize(int contractVersion) =>
        StorageIpcContract.SupportsStableItemIdentities(contractVersion)
            ? MaximumStableIdentityPageSize
            : MaximumStoragePageSize;
}

public static class StorageIpcMessageTypes
{
    public const string ConnectionListRequest = "connection.list.request";
    public const string ConnectionListResponse = "connection.list.response";
    public const string ConnectionTestRequest = "connection.test.request";
    public const string ConnectionTestResponse = "connection.test.response";
    public const string StorageListRequest = "storage.list.request";
    public const string StorageListResponse = "storage.list.response";
}

[JsonConverter(typeof(JsonStringEnumConverter<StorageConnectionProvider>))]
public enum StorageConnectionProvider
{
    Local = 1,
    S3 = 2,
    Ftp = 3,
    Ftps = 4,
    Sftp = 5,
    Ssh = 6
}

[JsonConverter(typeof(JsonStringEnumConverter<StorageIpcFailureCategory>))]
public enum StorageIpcFailureCategory
{
    Validation,
    NotFound,
    Conflict,
    Unsupported,
    Unauthorized,
    Unavailable,
    Timeout,
    Cancelled,
    Integrity,
    Security,
    Provider,
    Unexpected
}

[JsonConverter(typeof(JsonStringEnumConverter<StorageItemKind>))]
public enum StorageItemKind
{
    File,
    Directory,
    Prefix,
    SymbolicLink,
    Other
}

public sealed record StorageIpcFailure(
    string Code,
    StorageIpcFailureCategory Category,
    string Message,
    bool IsTransient);

public sealed record ConnectionListRequest(
    int ContractVersion = StorageIpcContract.CurrentVersion,
    string? SearchText = null,
    StorageConnectionProvider? Provider = null,
    bool IncludeDisabled = false,
    int Limit = 50)
{
    public bool HasValidBounds =>
        ContractVersion > 0 &&
        Limit is >= 1 and <= StorageIpcLimits.MaximumConnectionResults &&
        (SearchText is null ||
            SearchText.Length <= StorageIpcLimits.MaximumSearchTextLength &&
            !SearchText.Any(char.IsControl)) &&
        (Provider is null || Enum.IsDefined(Provider.Value));
}

public sealed record ConnectionSummary(
    Guid ConnectionId,
    string DisplayName,
    StorageConnectionProvider Provider,
    string? FolderPath,
    string[] Tags,
    bool IsFavorite,
    bool IsEnabled,
    string? IconKey,
    string? AccentColor,
    long Version,
    ConnectionProfileType Type = ConnectionProfileType.Storage);

public sealed record ConnectionListResponse(
    int ContractVersion,
    ConnectionSummary[] Connections,
    StorageIpcFailure? Failure = null);

public sealed record ConnectionTestRequest(
    int ContractVersion,
    Guid ConnectionId)
{
    public bool HasValidBounds => ContractVersion > 0 && ConnectionId != Guid.Empty;
}

public sealed record ConnectionTestResponse(
    int ContractVersion,
    Guid ConnectionId,
    bool Succeeded,
    long ElapsedMilliseconds,
    StorageIpcFailure? Failure = null);

public sealed record StorageListPageRequest(
    int ContractVersion,
    Guid ConnectionId,
    string RelativePath,
    int PageSize = StorageIpcLimits.MaximumStableIdentityPageSize,
    string? ContinuationToken = null,
    bool IncludeVersions = false,
    bool Recursive = false)
{
    public bool HasValidBounds =>
        ContractVersion > 0 &&
        ConnectionId != Guid.Empty &&
        RelativePath is not null &&
        RelativePath.Length <= StorageIpcLimits.MaximumRelativePathLength &&
        !RelativePath.Any(char.IsControl) &&
        PageSize >= 1 && PageSize <= StorageIpcLimits.GetMaximumStoragePageSize(ContractVersion) &&
        (ContinuationToken is null ||
            ContinuationToken.Length <= StorageIpcLimits.MaximumContinuationTokenLength &&
            !ContinuationToken.Any(char.IsControl));
}

public sealed record StorageListItem(
    string Name,
    string RelativePath,
    StorageItemKind Kind,
    long? Size,
    DateTimeOffset? LastModifiedUtc,
    string? ContentType,
    bool IsContainer,
    string? NativeItemId = null,
    string? VersionId = null,
    string? EntityTag = null)
{
    public bool HasValidIdentityBounds =>
        IsBoundedIdentity(NativeItemId) &&
        IsBoundedIdentity(VersionId) &&
        IsBoundedIdentity(EntityTag);

    private static bool IsBoundedIdentity(string? value) => value is null ||
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= StorageIpcLimits.MaximumOpaqueIdentityLength &&
        !value.Any(char.IsControl);
}

public sealed record StorageListPageResponse(
    int ContractVersion,
    Guid ConnectionId,
    string RelativePath,
    StorageListItem[] Entries,
    string? ContinuationToken,
    StorageIpcFailure? Failure = null,
    string? RootIdentity = null)
{
    public bool HasValidRootIdentity =>
        !string.IsNullOrWhiteSpace(RootIdentity) &&
        RootIdentity.Length <= StorageIpcLimits.MaximumOpaqueIdentityLength &&
        !RootIdentity.Any(char.IsControl);
}
