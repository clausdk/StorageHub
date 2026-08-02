using System.Collections.ObjectModel;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Storage;

namespace StorageHub.Storage.Models;

public sealed record StorageListRequest(
    bool Recursive = false,
    int PageSize = 500,
    string? ContinuationToken = null,
    bool IncludeVersions = false)
{
    public const int MaximumPageSize = 10_000;

    public StorageResult Validate()
    {
        if (PageSize is < 1 or > MaximumPageSize)
        {
            return Invalid(
                "storage.list.invalid_page_size",
                $"Page size must be between 1 and {MaximumPageSize}.");
        }

        if (ContinuationToken?.Any(char.IsControl) == true)
        {
            return Invalid("storage.list.invalid_token", "A continuation token cannot contain control characters.");
        }

        return StorageResult.Success();
    }

    private static StorageResult Invalid(string code, string message) => StorageResult.Fail(
        new StorageFailure(code, StorageFailureKind.Validation, message));
}

public sealed record StorageReadRequest(
    StorageAddress Address,
    long Offset = 0,
    long? Length = null,
    string? ExpectedVersionId = null,
    string? ExpectedEntityTag = null)
{
    public StorageResult Validate()
    {
        ArgumentNullException.ThrowIfNull(Address);
        if (Offset < 0)
        {
            return Invalid("The read offset cannot be negative.");
        }

        if (Length <= 0)
        {
            return Invalid("A requested read length must be greater than zero.");
        }

        if (Length.HasValue && Offset > long.MaxValue - Length.Value)
        {
            return Invalid("The requested read range exceeds the supported integer range.");
        }

        if (!StorageIdentityToken.IsValid(ExpectedVersionId) ||
            !StorageIdentityToken.IsValid(ExpectedEntityTag))
        {
            return Invalid("Expected identity tokens cannot be empty or contain control characters.");
        }

        return StorageResult.Success();
    }

    private static StorageResult Invalid(string message) => StorageResult.Fail(new StorageFailure(
        "storage.read.invalid_range",
        StorageFailureKind.Validation,
        message));
}

public enum StorageWriteMode
{
    CreateNew,
    Overwrite,
    Resume
}

public sealed class StorageWriteRequest
{
    public StorageWriteRequest(
        StorageAddress destination,
        StorageWriteMode mode,
        long? expectedLength = null,
        long requestedOffset = 0,
        string? resumeToken = null,
        string? contentType = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        string? expectedDestinationVersionId = null,
        string? expectedDestinationEntityTag = null)
    {
        Destination = destination ?? throw new ArgumentNullException(nameof(destination));
        Mode = mode;
        ExpectedLength = expectedLength;
        RequestedOffset = requestedOffset;
        ResumeToken = resumeToken;
        ContentType = contentType;
        ExpectedDestinationVersionId = expectedDestinationVersionId;
        ExpectedDestinationEntityTag = expectedDestinationEntityTag;
        Metadata = new ReadOnlyDictionary<string, string>(
            metadata is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(metadata, StringComparer.Ordinal));
    }

    public StorageAddress Destination { get; }
    public StorageWriteMode Mode { get; }
    public long? ExpectedLength { get; }
    public long RequestedOffset { get; }
    public string? ResumeToken { get; }
    public string? ContentType { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }
    public string? ExpectedDestinationVersionId { get; }
    public string? ExpectedDestinationEntityTag { get; }

    public StorageResult Validate()
    {
        if (ExpectedLength < 0 || RequestedOffset < 0)
        {
            return Invalid("Expected length and requested offset cannot be negative.");
        }

        if (ExpectedLength.HasValue && RequestedOffset > ExpectedLength.Value)
        {
            return Invalid("The requested offset cannot exceed the expected content length.");
        }

        if (Mode == StorageWriteMode.Resume && string.IsNullOrWhiteSpace(ResumeToken))
        {
            return Invalid("Resume mode requires an opaque resume token.");
        }

        if (Mode != StorageWriteMode.Resume && (RequestedOffset != 0 || ResumeToken is not null))
        {
            return Invalid("Only resume mode can specify an offset or resume token.");
        }

        if (ResumeToken?.Any(char.IsControl) == true || Metadata.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Any(char.IsControl) || pair.Value.Any(char.IsControl)))
        {
            return Invalid("Resume tokens and metadata cannot contain control characters, and metadata names cannot be blank.");
        }

        if (!StorageIdentityToken.IsValid(ExpectedDestinationVersionId) ||
            !StorageIdentityToken.IsValid(ExpectedDestinationEntityTag))
        {
            return Invalid("Expected destination identity tokens cannot be empty or contain control characters.");
        }

        return StorageResult.Success();
    }

    private static StorageResult Invalid(string message) => StorageResult.Fail(new StorageFailure(
        "storage.write.invalid_request",
        StorageFailureKind.Validation,
        message));
}

public sealed record StorageDeleteRequest(
    StorageAddress Address,
    bool Recursive = false,
    bool IgnoreMissing = false,
    string? ExpectedVersionId = null,
    string? ExpectedEntityTag = null)
{
    public StorageResult Validate()
    {
        ArgumentNullException.ThrowIfNull(Address);
        return StorageIdentityToken.IsValid(ExpectedVersionId) &&
               StorageIdentityToken.IsValid(ExpectedEntityTag)
            ? StorageResult.Success()
            : StorageResult.Fail(new StorageFailure(
                "storage.delete.invalid_request",
                StorageFailureKind.Validation,
                "Expected identity tokens cannot be empty or contain control characters."));
    }
}

public sealed record StorageCopyRequest(
    StorageAddress Source,
    StorageAddress Destination,
    bool Overwrite = false,
    string? ExpectedSourceVersionId = null,
    string? ExpectedDestinationVersionId = null);

public sealed record StorageMoveRequest(
    StorageAddress Source,
    StorageAddress Destination,
    bool Overwrite = false,
    string? ExpectedSourceVersionId = null,
    string? ExpectedDestinationVersionId = null);

internal static class StorageIdentityToken
{
    private const int MaximumLength = 8_192;

    internal static bool IsValid(string? value) =>
        value is null ||
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaximumLength &&
        !value.Any(char.IsControl);
}
