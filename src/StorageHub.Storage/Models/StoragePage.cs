using System.Collections.ObjectModel;
using StorageHub.Domain.Storage;

namespace StorageHub.Storage.Models;

/// <summary>One immutable provider page and its opaque continuation token.</summary>
public sealed class StoragePage
{
    public StoragePage(IEnumerable<StorageEntry> entries, string? continuationToken = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Entries = new ReadOnlyCollection<StorageEntry>(entries.ToArray());
        ContinuationToken = continuationToken;
    }

    public IReadOnlyList<StorageEntry> Entries { get; }

    /// <summary>An opaque provider token. Callers must not parse or modify it.</summary>
    public string? ContinuationToken { get; }

    public bool IsLastPage => ContinuationToken is null;
}
