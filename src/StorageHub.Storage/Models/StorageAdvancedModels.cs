using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Storage;

namespace StorageHub.Storage.Models;

public sealed record StorageVersionListRequest(
    int PageSize = 500,
    string? ContinuationToken = null,
    bool IncludeDeleteMarkers = true)
{
    public const int MaximumPageSize = 10_000;
    public const int MaximumContinuationTokenLength = 8_192;

    public StorageResult Validate()
    {
        if (PageSize is < 1 or > MaximumPageSize)
        {
            return Invalid("storage.version_list.invalid_page_size",
                $"Page size must be between 1 and {MaximumPageSize}.");
        }

        if (!StorageAdvancedValidation.IsOptionalOpaqueToken(
                ContinuationToken,
                MaximumContinuationTokenLength))
        {
            return Invalid("storage.version_list.invalid_token",
                "The continuation token is empty, too long, or contains control characters.");
        }

        return StorageResult.Success();
    }

    private static StorageResult Invalid(string code, string message) => StorageResult.Fail(
        new StorageFailure(code, StorageFailureKind.Validation, message));
}

public sealed record StorageDeleteVersionRequest(StorageAddress Address)
{
    public StorageResult Validate()
    {
        ArgumentNullException.ThrowIfNull(Address);
        return string.IsNullOrWhiteSpace(Address.VersionId)
            ? StorageResult.Fail(new StorageFailure(
                "storage.version_delete.version_required",
                StorageFailureKind.Validation,
                "An exact provider version ID is required."))
            : StorageResult.Success();
    }
}

public sealed record StorageObjectVersion
{
    private StorageObjectVersion(
        StorageAddress address,
        long? size,
        DateTimeOffset? lastModifiedUtc,
        bool isLatest,
        bool isDeleteMarker)
    {
        Address = address;
        Size = size;
        LastModifiedUtc = lastModifiedUtc?.ToUniversalTime();
        IsLatest = isLatest;
        IsDeleteMarker = isDeleteMarker;
    }

    public StorageAddress Address { get; }
    public long? Size { get; }
    public DateTimeOffset? LastModifiedUtc { get; }
    public bool IsLatest { get; }
    public bool IsDeleteMarker { get; }

    public static StorageResult<StorageObjectVersion> Create(
        StorageAddress address,
        long? size,
        DateTimeOffset? lastModifiedUtc,
        bool isLatest,
        bool isDeleteMarker)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (string.IsNullOrWhiteSpace(address.VersionId))
        {
            return Invalid("A provider version ID is required.");
        }

        if (size < 0)
        {
            return Invalid("A version size cannot be negative.");
        }

        return StorageResult<StorageObjectVersion>.Success(new StorageObjectVersion(
            address,
            size,
            lastModifiedUtc,
            isLatest,
            isDeleteMarker));
    }

    private static StorageResult<StorageObjectVersion> Invalid(string message) =>
        StorageResult<StorageObjectVersion>.Fail(new StorageFailure(
            "storage.version.invalid",
            StorageFailureKind.Validation,
            message));
}

public sealed class StorageObjectVersionPage
{
    private StorageObjectVersionPage(
        IReadOnlyList<StorageObjectVersion> versions,
        string? continuationToken)
    {
        Versions = versions;
        ContinuationToken = continuationToken;
    }

    public IReadOnlyList<StorageObjectVersion> Versions { get; }
    public string? ContinuationToken { get; }

    public static StorageResult<StorageObjectVersionPage> Create(
        IEnumerable<StorageObjectVersion> versions,
        string? continuationToken = null)
    {
        ArgumentNullException.ThrowIfNull(versions);
        if (!StorageAdvancedValidation.IsOptionalOpaqueToken(
                continuationToken,
                StorageVersionListRequest.MaximumContinuationTokenLength))
        {
            return Invalid("The provider returned an invalid continuation token.");
        }

        var snapshot = versions.ToArray();
        if (snapshot.Length > StorageVersionListRequest.MaximumPageSize || snapshot.Any(item => item is null))
        {
            return Invalid("The provider returned an invalid or oversized version page.");
        }

        return StorageResult<StorageObjectVersionPage>.Success(new StorageObjectVersionPage(
            Array.AsReadOnly(snapshot),
            continuationToken));
    }

    private static StorageResult<StorageObjectVersionPage> Invalid(string message) =>
        StorageResult<StorageObjectVersionPage>.Fail(new StorageFailure(
            "storage.version_page.invalid",
            StorageFailureKind.Integrity,
            message));
}

public enum StorageMetadataUpdateMode
{
    Merge,
    Replace
}

public sealed class StorageMetadata
{
    public const int MaximumEntries = 1_024;
    public const int MaximumNameLength = 256;
    public const int MaximumCombinedBytes = 64 * 1_024;
    private readonly ReadOnlyDictionary<string, string> _values;

    private StorageMetadata(Dictionary<string, string> values)
    {
        _values = new ReadOnlyDictionary<string, string>(values);
    }

    public IReadOnlyDictionary<string, string> Values => _values;
    public int Count => _values.Count;

    public static StorageResult<StorageMetadata> Create(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > MaximumEntries)
        {
            return Invalid($"Metadata cannot contain more than {MaximumEntries} entries.");
        }

        var snapshot = new Dictionary<string, string>(values.Count, StringComparer.Ordinal);
        long combinedBytes = 0;
        foreach (var (name, value) in values)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > MaximumNameLength ||
                name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
            {
                return Invalid(
                    "Metadata names must use portable ASCII letters, digits, '.', '_' or '-' and be at most 256 characters.");
            }

            if (value is null || value.Any(char.IsControl))
            {
                return Invalid("Metadata values cannot be null or contain control characters.");
            }

            combinedBytes += Encoding.UTF8.GetByteCount(name);
            combinedBytes += Encoding.UTF8.GetByteCount(value);
            if (combinedBytes > MaximumCombinedBytes)
            {
                return Invalid($"Combined metadata exceeds the {MaximumCombinedBytes}-byte portable limit.");
            }

            snapshot.Add(name, value);
        }

        return StorageResult<StorageMetadata>.Success(new StorageMetadata(snapshot));
    }

    private static StorageResult<StorageMetadata> Invalid(string message) =>
        StorageResult<StorageMetadata>.Fail(new StorageFailure(
            "storage.metadata.invalid",
            StorageFailureKind.Validation,
            message));
}

public sealed record StorageSetMetadataRequest(
    StorageAddress Address,
    StorageMetadata Metadata,
    StorageMetadataUpdateMode Mode = StorageMetadataUpdateMode.Replace,
    string? ExpectedVersionId = null,
    string? ExpectedEntityTag = null)
{
    public StorageResult Validate()
    {
        ArgumentNullException.ThrowIfNull(Address);
        ArgumentNullException.ThrowIfNull(Metadata);
        if (!Enum.IsDefined(Mode))
        {
            return Invalid("The metadata update mode is invalid.");
        }

        if (!StorageAdvancedValidation.IsOptionalOpaqueToken(ExpectedVersionId) ||
            !StorageAdvancedValidation.IsOptionalOpaqueToken(ExpectedEntityTag))
        {
            return Invalid("Expected identity tokens are empty, too long, or contain control characters.");
        }

        return StorageResult.Success();
    }

    private static StorageResult Invalid(string message) => StorageResult.Fail(new StorageFailure(
        "storage.metadata_update.invalid",
        StorageFailureKind.Validation,
        message));
}

public enum StorageTagUpdateMode
{
    Merge,
    Replace
}

public sealed class StorageTags
{
    public const int MaximumEntries = 10;
    public const int MaximumNameLength = 128;
    public const int MaximumValueLength = 256;
    private readonly ReadOnlyDictionary<string, string> _values;

    private StorageTags(Dictionary<string, string> values)
    {
        _values = new ReadOnlyDictionary<string, string>(values);
    }

    public IReadOnlyDictionary<string, string> Values => _values;
    public int Count => _values.Count;

    public static StorageResult<StorageTags> Create(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > MaximumEntries)
        {
            return Invalid($"Tags cannot contain more than {MaximumEntries} entries.");
        }

        var snapshot = new Dictionary<string, string>(values.Count, StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MaximumNameLength ||
                name.Any(character => !IsPortableTagCharacter(character)))
            {
                return Invalid("Tag names must contain 1-128 portable ASCII tag characters.");
            }

            if (value is null || value.Length > MaximumValueLength ||
                value.Any(character => !IsPortableTagCharacter(character)))
            {
                return Invalid("Tag values must contain at most 256 portable ASCII tag characters.");
            }

            snapshot.Add(name, value);
        }

        return StorageResult<StorageTags>.Success(new StorageTags(snapshot));
    }

    private static bool IsPortableTagCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is ' ' or '+' or '-' or '.' or '/' or ':' or '=' or '_';

    private static StorageResult<StorageTags> Invalid(string message) =>
        StorageResult<StorageTags>.Fail(new StorageFailure(
            "storage.tags.invalid",
            StorageFailureKind.Validation,
            message));
}

public sealed record StorageSetTagsRequest(
    StorageAddress Address,
    StorageTags Tags,
    StorageTagUpdateMode Mode = StorageTagUpdateMode.Replace)
{
    public StorageResult Validate()
    {
        ArgumentNullException.ThrowIfNull(Address);
        ArgumentNullException.ThrowIfNull(Tags);
        return Enum.IsDefined(Mode)
            ? StorageResult.Success()
            : StorageResult.Fail(new StorageFailure(
                "storage.tags_update.invalid",
                StorageFailureKind.Validation,
                "The tag update mode is invalid."));
    }
}

public enum StorageSignedUrlMethod
{
    Read,
    Write
}

public sealed class StorageSignedUrlRequest
{
    public static readonly TimeSpan MinimumLifetime = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromDays(7);

    public StorageSignedUrlRequest(
        StorageAddress address,
        StorageSignedUrlMethod method,
        TimeSpan? expiresIn = null,
        string? contentType = null)
    {
        Address = address ?? throw new ArgumentNullException(nameof(address));
        Method = method;
        ExpiresIn = expiresIn ?? TimeSpan.FromMinutes(15);
        ContentType = contentType;
    }

    public StorageAddress Address { get; }
    public StorageSignedUrlMethod Method { get; }
    public TimeSpan ExpiresIn { get; }
    public string? ContentType { get; }

    public StorageResult Validate()
    {
        if (!Enum.IsDefined(Method))
        {
            return Invalid("The signed URL method is invalid.");
        }

        if (ExpiresIn < MinimumLifetime || ExpiresIn > MaximumLifetime)
        {
            return Invalid("Signed URL lifetime must be between one second and seven days.");
        }

        if (Method == StorageSignedUrlMethod.Write &&
            (Address.VersionId is not null || Address.EntityTag is not null))
        {
            return Invalid(
                "A signed write URL cannot discard an object version or entity-tag condition.");
        }

        if (ContentType is not null &&
            (string.IsNullOrWhiteSpace(ContentType) || ContentType.Length > 1_024 || ContentType.Any(char.IsControl)))
        {
            return Invalid("Content type is empty, too long, or contains control characters.");
        }

        return StorageResult.Success();
    }

    private static StorageResult Invalid(string message) => StorageResult.Fail(new StorageFailure(
        "storage.signed_url.invalid_request",
        StorageFailureKind.Validation,
        message));
}

/// <summary>
/// Contains a temporary provider credential embedded in a URL. This value is secret-bearing and
/// must never be written to logs, telemetry, exception messages, or diagnostic bundles.
/// </summary>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class StorageSignedUrl
{
    public const int MaximumUrlLength = 16_384;

    private StorageSignedUrl(Uri url, StorageSignedUrlMethod method, DateTimeOffset expiresAtUtc)
    {
        Url = url;
        Method = method;
        ExpiresAtUtc = expiresAtUtc.ToUniversalTime();
    }

    /// <summary>The secret-bearing signed URL. Callers must not include it in diagnostics.</summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public Uri Url { get; }
    public StorageSignedUrlMethod Method { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
    public bool IsSecretBearing { get; } = true;

    public static StorageResult<StorageSignedUrl> Create(
        Uri url,
        StorageSignedUrlMethod method,
        DateTimeOffset expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(url);
        var serialized = url.OriginalString;
        if (!url.IsAbsoluteUri || serialized.Length > MaximumUrlLength || serialized.Any(char.IsControl) ||
            url.Scheme is not ("http" or "https") || !Enum.IsDefined(method) || expiresAtUtc == default)
        {
            return StorageResult<StorageSignedUrl>.Fail(new StorageFailure(
                "storage.signed_url.invalid_response",
                StorageFailureKind.Integrity,
                "The provider returned an invalid signed URL."));
        }

        return StorageResult<StorageSignedUrl>.Success(new StorageSignedUrl(url, method, expiresAtUtc));
    }

    public override string ToString() =>
        $"StorageSignedUrl {{ Method = {Method}, ExpiresAtUtc = {ExpiresAtUtc:O}, Url = [REDACTED] }}";

    private string DebuggerDisplay => ToString();
}

internal static class StorageAdvancedValidation
{
    internal const int MaximumIdentityTokenLength = 8_192;

    internal static bool IsOptionalOpaqueToken(
        string? value,
        int maximumLength = MaximumIdentityTokenLength) =>
        value is null ||
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Any(char.IsControl);
}
