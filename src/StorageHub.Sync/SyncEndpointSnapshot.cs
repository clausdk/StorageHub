using System.Collections.ObjectModel;
using StorageHub.Domain.Capabilities;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Abstractions;

namespace StorageHub.Sync;

/// <summary>
/// A complete, root-scoped enumeration captured by the planning phase. Keys are canonical paths
/// relative to the verified scan root; provider paths never leak into plan comparison logic.
/// </summary>
public sealed class SyncEndpointSnapshot
{
    public SyncEndpointSnapshot(
        ConnectionProfileId profileId,
        string rootIdentity,
        IEnumerable<KeyValuePair<string, StorageEntry>> entries,
        SnapshotCompleteness completeness,
        StorageCaseSensitivity caseSensitivity = StorageCaseSensitivity.Sensitive,
        IEnumerable<KeyValuePair<string, PortableContentDigest>>? portableDigests = null)
    {
        if (profileId.IsEmpty)
        {
            throw new ArgumentException("A connection profile ID is required.", nameof(profileId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(rootIdentity);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(completeness);
        if (!Enum.IsDefined(caseSensitivity))
        {
            throw new ArgumentOutOfRangeException(nameof(caseSensitivity));
        }

        var comparer = caseSensitivity == StorageCaseSensitivity.Sensitive
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;
        var snapshot = new SortedDictionary<string, StorageEntry>(comparer);
        foreach (var (relativePath, entry) in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ValidateRelativePath(relativePath, nameof(entries));
            if (entry.Address.ProfileId != profileId ||
                !StringComparer.Ordinal.Equals(entry.Address.RootIdentity, rootIdentity))
            {
                throw new ArgumentException(
                    "Every snapshot entry must belong to the snapshot profile and root identity.",
                    nameof(entries));
            }

            if (!snapshot.TryAdd(relativePath, entry))
            {
                throw new ArgumentException(
                    $"The snapshot contains a path collision at '{relativePath}'.",
                    nameof(entries));
            }
        }

        if (completeness.TotalItemCount != snapshot.Count)
        {
            throw new ArgumentException(
                "Snapshot completeness must report the exact number of captured entries.",
                nameof(completeness));
        }

        var digestSnapshot = new SortedDictionary<string, PortableContentDigest>(comparer);
        foreach (var (relativePath, digest) in portableDigests ?? [])
        {
            ArgumentNullException.ThrowIfNull(digest);
            ValidateRelativePath(relativePath, nameof(portableDigests));
            if (!snapshot.TryGetValue(relativePath, out var entry) ||
                entry.Kind != StorageEntryKind.File)
            {
                throw new ArgumentException(
                    "Portable digests must identify a file captured by the same snapshot.",
                    nameof(portableDigests));
            }

            if (!digestSnapshot.TryAdd(relativePath, digest))
            {
                throw new ArgumentException(
                    $"The portable digest set contains a path collision at '{relativePath}'.",
                    nameof(portableDigests));
            }
        }

        ProfileId = profileId;
        RootIdentity = rootIdentity;
        Entries = new ReadOnlyDictionary<string, StorageEntry>(snapshot);
        PortableDigests = new ReadOnlyDictionary<string, PortableContentDigest>(digestSnapshot);
        Completeness = completeness;
        CaseSensitivity = caseSensitivity;
    }

    public ConnectionProfileId ProfileId { get; }

    public string RootIdentity { get; }

    public IReadOnlyDictionary<string, StorageEntry> Entries { get; }

    /// <summary>Explicit, portable content evidence keyed by canonical snapshot-relative path.</summary>
    public IReadOnlyDictionary<string, PortableContentDigest> PortableDigests { get; }

    public SnapshotCompleteness Completeness { get; }

    public StorageCaseSensitivity CaseSensitivity { get; }

    internal static void ValidateRelativePath(string relativePath, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath, parameterName);
        var validation = StorageAddress.Create(
            new ConnectionProfileId(new Guid("6B813463-AE20-4F5C-9621-A2158EFDB271")),
            "sync-snapshot-validation",
            relativePath);
        if (validation.IsFailure ||
            !StringComparer.Ordinal.Equals(validation.Value.CanonicalRelativePath, relativePath))
        {
            throw new ArgumentException(
                "Snapshot keys must be canonical non-root relative paths.",
                parameterName);
        }
    }
}
