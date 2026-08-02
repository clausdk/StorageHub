using System.Collections.ObjectModel;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Capabilities;
using StorageHub.Domain.Storage;
using StorageHub.Sync.Persistence;

namespace StorageHub.Sync;

/// <summary>
/// Builds a last-known-good baseline only from two fresh complete scans plus evidence that each
/// synchronized file pair is equal. Cross-provider ETags are retained as side-local versions but
/// are never treated as portable content digests.
/// </summary>
public static class VerifiedSyncBaselineBuilder
{
    public static StorageResult<IReadOnlyDictionary<string, SyncBaselineObservation>> Build(
        SyncProfile profile,
        ImmutableSyncPlan executedPlan,
        SyncBaselineSnapshot previousBaseline,
        SyncEndpointSnapshot left,
        SyncEndpointSnapshot right)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(executedPlan);
        ArgumentNullException.ThrowIfNull(previousBaseline);
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (!left.Completeness.IsComplete || !right.Completeness.IsComplete)
        {
            return Fail("sync.baseline.scan_incomplete", "A baseline requires two complete fresh endpoint scans.");
        }

        if (executedPlan.ProfileId != profile.ProfileId ||
            previousBaseline.ProfileId != profile.ProfileId ||
            executedPlan.BaselineGeneration != previousBaseline.Generation ||
            left.ProfileId != profile.LeftConnectionProfileId ||
            right.ProfileId != profile.RightConnectionProfileId)
        {
            return Fail("sync.baseline.binding_mismatch", "The fresh scans do not match the executed profile and plan.");
        }

        var comparer = left.CaseSensitivity == StorageCaseSensitivity.Sensitive &&
            right.CaseSensitivity == StorageCaseSensitivity.Sensitive
                ? StringComparer.Ordinal
                : StringComparer.OrdinalIgnoreCase;
        if (!TryIndex(left.Entries, comparer, out var leftEntries) ||
            !TryIndex(right.Entries, comparer, out var rightEntries) ||
            !TryIndex(previousBaseline.Items, comparer, out var previous))
        {
            return Fail("sync.baseline.path_collision", "The verified baseline contains a cross-endpoint path collision.");
        }

        var authoritative = profile.Direction == SyncDirection.RightToLeft ? rightEntries : leftEntries;
        var counterpart = profile.Direction == SyncDirection.RightToLeft ? leftEntries : rightEntries;
        if (profile.Direction == SyncDirection.TwoWay)
        {
            authoritative = leftEntries;
            counterpart = rightEntries;
        }

        if ((profile.Direction == SyncDirection.TwoWay || profile.DeletionMode == SyncDeletionMode.Mirror) &&
            (authoritative.Count != counterpart.Count ||
             authoritative.Keys.Any(path => !counterpart.ContainsKey(path))))
        {
            return Fail(
                "sync.baseline.path_set_mismatch",
                "The endpoints do not have the exact path set required by this sync mode.");
        }

        var copiedDestinations = executedPlan.Operations
            .Where(static operation => operation.Kind == SyncPlanOperationKind.Copy)
            .Select(static operation => operation.Destination!)
            .ToArray();
        var observations = new Dictionary<string, SyncBaselineObservation>(comparer);
        foreach (var (path, sourceEntry) in authoritative)
        {
            if (!IsSupported(sourceEntry) || !counterpart.TryGetValue(path, out var destinationEntry) ||
                !IsSupported(destinationEntry) || !KindsAreCompatible(sourceEntry, destinationEntry))
            {
                return Fail(
                    "sync.baseline.item_mismatch",
                    "A synchronized path is absent or has incompatible item kinds after execution.");
            }

            var leftEntry = profile.Direction == SyncDirection.RightToLeft ? destinationEntry : sourceEntry;
            var rightEntry = profile.Direction == SyncDirection.RightToLeft ? sourceEntry : destinationEntry;
            if (profile.Direction == SyncDirection.TwoWay)
            {
                leftEntry = sourceEntry;
                rightEntry = destinationEntry;
            }

            if (leftEntry.IsContainer)
            {
                observations.Add(path, SyncBaselineObservation.Present(
                    0,
                    null,
                    Version(leftEntry),
                    Version(rightEntry)));
                continue;
            }

            if (leftEntry.Size is null || rightEntry.Size is null || leftEntry.Size != rightEntry.Size)
            {
                return Fail(
                    "sync.baseline.file_size_mismatch",
                    "A synchronized file pair does not have the same known length after execution.");
            }

            var copiedAndVerified = copiedDestinations.Any(destination =>
                Matches(destination, leftEntry.Address) || Matches(destination, rightEntry.Address));
            _ = left.PortableDigests.TryGetValue(path, out var leftDigest);
            _ = right.PortableDigests.TryGetValue(path, out var rightDigest);
            if (leftDigest is not null && rightDigest is not null && leftDigest != rightDigest)
            {
                return Fail(
                    "sync.baseline.portable_hash_mismatch",
                    "A synchronized file pair has different portable SHA-256 content evidence.");
            }

            var portableEquality = leftDigest is not null && leftDigest == rightDigest;
            _ = previous.TryGetValue(path, out var oldObservation);
            var unchangedFromKnownBaseline = oldObservation is { Exists: true } &&
                oldObservation.Length == leftEntry.Size.Value &&
                SideVersionMatches(oldObservation.LeftVersionId, Version(leftEntry)) &&
                SideVersionMatches(oldObservation.RightVersionId, Version(rightEntry));
            if (!copiedAndVerified && !unchangedFromKnownBaseline && !portableEquality)
            {
                return Fail(
                    "sync.baseline.content_unproven",
                    "File equality could not be proven by successful copy verification or unchanged side-local versions.");
            }

            observations.Add(path, SyncBaselineObservation.Present(
                leftEntry.Size.Value,
                portableEquality
                    ? new ContentDigest(leftDigest!.AlgorithmName, leftDigest.Value)
                    : null,
                Version(leftEntry),
                Version(rightEntry)));
        }

        return StorageResult<IReadOnlyDictionary<string, SyncBaselineObservation>>.Success(
            new ReadOnlyDictionary<string, SyncBaselineObservation>(observations));
    }

    private static bool TryIndex<T>(
        IReadOnlyDictionary<string, T> source,
        StringComparer comparer,
        out Dictionary<string, T> result)
    {
        result = new Dictionary<string, T>(comparer);
        foreach (var (path, value) in source)
        {
            if (!result.TryAdd(path, value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Matches(StorageAddress expected, StorageAddress actual) =>
        expected.ProfileId == actual.ProfileId &&
        StringComparer.Ordinal.Equals(expected.RootIdentity, actual.RootIdentity) &&
        StringComparer.Ordinal.Equals(expected.CanonicalRelativePath, actual.CanonicalRelativePath);

    private static string? Version(StorageEntry entry) =>
        entry.Address.VersionId ?? entry.Address.EntityTag ?? entry.ETag;

    private static bool SideVersionMatches(string? baseline, string? current) =>
        baseline is not null && current is not null && StringComparer.Ordinal.Equals(baseline, current);

    private static bool IsSupported(StorageEntry entry) => entry.Kind is
        StorageEntryKind.File or StorageEntryKind.Directory or StorageEntryKind.Prefix;

    private static bool KindsAreCompatible(StorageEntry left, StorageEntry right) =>
        left.IsContainer == right.IsContainer &&
        (left.IsContainer || left.Kind == StorageEntryKind.File && right.Kind == StorageEntryKind.File);

    private static StorageResult<IReadOnlyDictionary<string, SyncBaselineObservation>> Fail(
        string code,
        string message) => StorageResult<IReadOnlyDictionary<string, SyncBaselineObservation>>.Fail(
        new StorageFailure(code, StorageFailureKind.Integrity, message));
}
