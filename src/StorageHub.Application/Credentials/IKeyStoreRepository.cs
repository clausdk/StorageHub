using StorageHub.Domain.Identifiers;

namespace StorageHub.Application.Credentials;

public enum KeyStoreWriteStatus
{
    Succeeded = 1,
    NotFound = 2,
    VersionConflict = 3,
    NameConflict = 4,

    /// <summary>The entry is still bound by at least one connection profile and was not removed.</summary>
    StillReferenced = 5
}

public sealed record KeyStoreWriteResult(
    KeyStoreWriteStatus Status,
    KeyStoreEntry? Entry = null,
    int? ActualVersion = null,
    IReadOnlyList<string>? ReferencedBy = null);

public sealed record KeyStoreSearch(
    string? Text = null,
    KeyMaterialKind? Kind = null,
    string? Tag = null,
    int Limit = 200)
{
    public int ValidatedLimit => Limit is >= 1 and <= 1_000
        ? Limit
        : throw new ArgumentOutOfRangeException(nameof(Limit), "The search limit must be between 1 and 1,000.");
}

/// <summary>
/// One entry plus the profiles that currently bind it. The count is what makes deletion safe to
/// refuse and lets the UI explain what a rotation is about to affect.
/// </summary>
public sealed record KeyStoreEntryUsage(KeyStoreEntry Entry, IReadOnlyList<string> ReferencedByProfileNames);

public interface IKeyStoreRepository
{
    ValueTask<KeyStoreWriteResult> CreateAsync(
        KeyStoreEntry entry,
        CancellationToken cancellationToken = default);

    ValueTask<KeyStoreEntry?> GetAsync(
        KeyStoreEntryId id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the entry that owns a vault reference, or null when the reference was enrolled
    /// directly against a profile rather than imported into the store. This is what lets bindings
    /// be derived from a saved profile instead of being asserted separately by the caller.
    /// </summary>
    ValueTask<KeyStoreEntry?> FindByMaterialReferenceAsync(
        string materialReference,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<KeyStoreEntryUsage>> SearchAsync(
        KeyStoreSearch search,
        CancellationToken cancellationToken = default);

    ValueTask<KeyStoreWriteResult> UpdateAsync(
        KeyStoreEntry entry,
        int expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes an entry only when nothing binds it. A refusal names the consuming profiles rather
    /// than failing blankly, because the caller's next step is to go and unbind them.
    /// </summary>
    ValueTask<KeyStoreWriteResult> DeleteAsync(
        KeyStoreEntryId id,
        int expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>Records that a profile slot binds an entry, inside the caller's own write.</summary>
    ValueTask BindAsync(
        ConnectionProfileId profileId,
        string credentialSlot,
        KeyStoreEntryId entryId,
        CancellationToken cancellationToken = default);

    ValueTask UnbindAsync(
        ConnectionProfileId profileId,
        string credentialSlot,
        CancellationToken cancellationToken = default);
}
