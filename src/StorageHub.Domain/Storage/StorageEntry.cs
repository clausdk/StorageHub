using System.Collections.ObjectModel;
using StorageHub.Contracts.Results;

namespace StorageHub.Domain.Storage;

public enum StorageEntryKind
{
    File,
    Directory,
    Prefix,
    SymbolicLink,
    Other
}

/// <summary>An immutable provider-neutral snapshot of one storage item.</summary>
public sealed record StorageEntry
{
    private StorageEntry(
        StorageAddress address,
        StorageEntryKind kind,
        long? size,
        DateTimeOffset? lastModifiedUtc,
        string? contentType,
        string? eTag,
        string? checksum,
        IReadOnlyDictionary<string, string> metadata)
    {
        Address = address;
        Kind = kind;
        Size = size;
        LastModifiedUtc = lastModifiedUtc?.ToUniversalTime();
        ContentType = contentType;
        ETag = eTag;
        Checksum = checksum;
        Metadata = metadata;
    }

    public StorageAddress Address { get; }
    public string Name => Address.Name;
    public StorageEntryKind Kind { get; }
    public long? Size { get; }
    public DateTimeOffset? LastModifiedUtc { get; }
    public string? ContentType { get; }
    public string? ETag { get; }
    public string? Checksum { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }
    public bool IsContainer => Kind is StorageEntryKind.Directory or StorageEntryKind.Prefix;

    public static StorageResult<StorageEntry> Create(
        StorageAddress address,
        StorageEntryKind kind,
        long? size = null,
        DateTimeOffset? lastModifiedUtc = null,
        string? contentType = null,
        string? eTag = null,
        string? checksum = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (size < 0 || kind is StorageEntryKind.Directory or StorageEntryKind.Prefix && size.HasValue)
        {
            return StorageResult<StorageEntry>.Fail(new StorageFailure(
                "storage.entry.invalid_size",
                StorageFailureKind.Validation,
                "File sizes cannot be negative and container entries cannot have a byte size."));
        }

        var metadataSnapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        if (metadata is not null)
        {
            foreach (var (key, value) in metadata)
            {
                if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl) || value.Any(char.IsControl))
                {
                    return StorageResult<StorageEntry>.Fail(new StorageFailure(
                        "storage.entry.invalid_metadata",
                        StorageFailureKind.Validation,
                        "Metadata names must be non-empty and metadata cannot contain control characters."));
                }

                metadataSnapshot.Add(key, value);
            }
        }

        return StorageResult<StorageEntry>.Success(new StorageEntry(
            address,
            kind,
            size,
            lastModifiedUtc,
            contentType,
            eTag,
            checksum,
            new ReadOnlyDictionary<string, string>(metadataSnapshot)));
    }
}
