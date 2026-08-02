using System.Text;

namespace StorageHub.Contracts.Ipc;

/// <summary>The independently versioned, read-only exact-object inspection contract.</summary>
public static class ObjectInspectorIpcContract
{
    public const int CurrentVersion = 1;

    public static bool IsSupported(int version) => version == CurrentVersion;
}

/// <summary>
/// Bounds for provider-controlled inspector data. The version page ceiling accounts for worst-case
/// JSON escaping of opaque provider identities and keeps responses below the normal IPC frame limit.
/// </summary>
public static class ObjectInspectorIpcLimits
{
    public const int MaximumVersionPageSize = 25;
    public const int MaximumRelativePathLength = StorageIpcLimits.MaximumRelativePathLength;
    public const int MaximumOpaqueIdentityLength = StorageIpcLimits.MaximumOpaqueIdentityLength;
    public const int MaximumContinuationTokenLength = StorageIpcLimits.MaximumContinuationTokenLength;
    public const int MaximumMetadataEntries = 1_024;
    public const int MaximumMetadataNameLength = 256;
    public const int MaximumMetadataCombinedBytes = 64 * 1_024;
    public const int MaximumTagEntries = 10;
    public const int MaximumTagNameLength = 128;
    public const int MaximumTagValueLength = 256;
}

public static class ObjectInspectorIpcMessageTypes
{
    public const string VersionListRequest = "object-inspector.versions.list.request";
    public const string VersionListResponse = "object-inspector.versions.list.response";
    public const string MetadataGetRequest = "object-inspector.metadata.get.request";
    public const string MetadataGetResponse = "object-inspector.metadata.get.response";
    public const string TagsGetRequest = "object-inspector.tags.get.request";
    public const string TagsGetResponse = "object-inspector.tags.get.response";
}

/// <summary>An exact object identity captured from a root-scoped browsing session.</summary>
public sealed record ObjectInspectorAddress(
    Guid ConnectionId,
    string RootIdentity,
    string RelativePath,
    string? NativeItemId = null,
    string? VersionId = null,
    string? EntityTag = null)
{
    public bool HasValidBounds =>
        ConnectionId != Guid.Empty &&
        IsRequiredOpaque(RootIdentity) &&
        !string.IsNullOrWhiteSpace(RelativePath) &&
        RelativePath.Length <= ObjectInspectorIpcLimits.MaximumRelativePathLength &&
        !RelativePath.Any(char.IsControl) &&
        IsOptionalOpaque(NativeItemId) &&
        IsOptionalOpaque(VersionId) &&
        IsOptionalOpaque(EntityTag);

    public static bool IsRequiredOpaque(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= ObjectInspectorIpcLimits.MaximumOpaqueIdentityLength &&
        !value.Any(char.IsControl);

    public static bool IsOptionalOpaque(string? value) => value is null || IsRequiredOpaque(value);
}

public sealed record ObjectVersionListRequest(
    int ContractVersion,
    ObjectInspectorAddress Address,
    int PageSize = ObjectInspectorIpcLimits.MaximumVersionPageSize,
    string? ContinuationToken = null,
    bool IncludeDeleteMarkers = true)
{
    public bool HasValidBounds =>
        ObjectInspectorIpcContract.IsSupported(ContractVersion) &&
        Address?.HasValidBounds == true &&
        Address.VersionId is null &&
        PageSize is >= 1 and <= ObjectInspectorIpcLimits.MaximumVersionPageSize &&
        IsOptionalToken(ContinuationToken);

    public static bool IsOptionalToken(string? value) => value is null ||
        value.Length is > 0 and <= ObjectInspectorIpcLimits.MaximumContinuationTokenLength &&
        !value.Any(char.IsControl);
}

public sealed record ObjectVersionSummary(
    string VersionId,
    string? EntityTag,
    long? Size,
    DateTimeOffset? LastModifiedUtc,
    bool IsLatest,
    bool IsDeleteMarker)
{
    public bool HasValidBounds =>
        ObjectInspectorAddress.IsRequiredOpaque(VersionId) &&
        ObjectInspectorAddress.IsOptionalOpaque(EntityTag) &&
        Size is null or >= 0;
}

public sealed record ObjectVersionListResponse(
    int ContractVersion,
    ObjectInspectorAddress Address,
    ObjectVersionSummary[] Versions,
    string? ContinuationToken,
    StorageIpcFailure? Failure = null);

public sealed record ObjectMetadataGetRequest(
    int ContractVersion,
    ObjectInspectorAddress Address)
{
    public bool HasValidBounds =>
        ObjectInspectorIpcContract.IsSupported(ContractVersion) &&
        Address?.HasValidBounds == true;
}

public sealed record ObjectMetadataEntry(string Name, string Value)
{
    public bool HasValidBounds =>
        IsPortableMetadataName(Name) &&
        Value is not null &&
        !Value.Any(char.IsControl);

    internal static bool IsPortableMetadataName(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= ObjectInspectorIpcLimits.MaximumMetadataNameLength &&
        value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}

public sealed record ObjectMetadataGetResponse(
    int ContractVersion,
    ObjectInspectorAddress Address,
    ObjectMetadataEntry[] Metadata,
    StorageIpcFailure? Failure = null)
{
    public bool HasValidMetadataBounds =>
        Metadata is not null &&
        Metadata.Length <= ObjectInspectorIpcLimits.MaximumMetadataEntries &&
        Metadata.All(static entry => entry?.HasValidBounds == true) &&
        Metadata.Select(static entry => entry.Name).Distinct(StringComparer.Ordinal).Count() == Metadata.Length &&
        GetCombinedUtf8Bytes(Metadata) <= ObjectInspectorIpcLimits.MaximumMetadataCombinedBytes;

    private static long GetCombinedUtf8Bytes(IEnumerable<ObjectMetadataEntry> entries)
    {
        long bytes = 0;
        foreach (var entry in entries)
        {
            bytes += Encoding.UTF8.GetByteCount(entry.Name);
            bytes += Encoding.UTF8.GetByteCount(entry.Value);
        }

        return bytes;
    }
}

public sealed record ObjectTagsGetRequest(
    int ContractVersion,
    ObjectInspectorAddress Address)
{
    public bool HasValidBounds =>
        ObjectInspectorIpcContract.IsSupported(ContractVersion) &&
        Address?.HasValidBounds == true;
}

public sealed record ObjectTagEntry(string Name, string Value)
{
    public bool HasValidBounds =>
        !string.IsNullOrEmpty(Name) &&
        Name.Length <= ObjectInspectorIpcLimits.MaximumTagNameLength &&
        Name.All(IsPortableTagCharacter) &&
        Value is not null &&
        Value.Length <= ObjectInspectorIpcLimits.MaximumTagValueLength &&
        Value.All(IsPortableTagCharacter);

    private static bool IsPortableTagCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is ' ' or '+' or '-' or '.' or '/' or ':' or '=' or '_';
}

public sealed record ObjectTagsGetResponse(
    int ContractVersion,
    ObjectInspectorAddress Address,
    ObjectTagEntry[] Tags,
    StorageIpcFailure? Failure = null)
{
    public bool HasValidTagBounds =>
        Tags is not null &&
        Tags.Length <= ObjectInspectorIpcLimits.MaximumTagEntries &&
        Tags.All(static entry => entry?.HasValidBounds == true) &&
        Tags.Select(static entry => entry.Name).Distinct(StringComparer.Ordinal).Count() == Tags.Length;
}
