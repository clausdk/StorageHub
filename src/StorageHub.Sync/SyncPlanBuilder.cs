using System.Collections.ObjectModel;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Capabilities;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Abstractions;

namespace StorageHub.Sync;

public enum SyncDirection
{
    LeftToRight = 0,
    RightToLeft = 1,
    TwoWay = 2,
}

public enum SyncDeletionMode
{
    Disabled = 0,
    Mirror = 1,
    Propagate = 2,
}

public sealed record SyncPlanningConflict(
    string RelativePath,
    SyncChangeKind Kind,
    string SafeReason);

public sealed record SyncPlanBuildRequest
{
    public SyncPlanBuildRequest(
        OperationPlanId planId,
        SyncProfileId profileId,
        long baselineGeneration,
        StorageAddress leftRoot,
        StorageAddress rightRoot,
        SyncEndpointSnapshot left,
        SyncEndpointSnapshot right,
        IReadOnlyDictionary<string, SyncBaselineObservation> baseline,
        SyncDirection direction,
        SyncDeletionMode deletionMode,
        DateTimeOffset createdAtUtc,
        SyncBehavior? behavior = null,
        SyncPathFilterPolicy? filterPolicy = null,
        SyncConflictPolicy conflictPolicy = SyncConflictPolicy.Block)
    {
        if (planId.IsEmpty)
        {
            throw new ArgumentException("A plan ID is required.", nameof(planId));
        }

        if (profileId.IsEmpty)
        {
            throw new ArgumentException("A sync profile ID is required.", nameof(profileId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(baselineGeneration);
        ArgumentNullException.ThrowIfNull(leftRoot);
        ArgumentNullException.ThrowIfNull(rightRoot);
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(baseline);
        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        if (!Enum.IsDefined(deletionMode))
        {
            throw new ArgumentOutOfRangeException(nameof(deletionMode));
        }

        if (createdAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Plan creation time must be UTC.", nameof(createdAtUtc));
        }

        PlanId = planId;
        ProfileId = profileId;
        BaselineGeneration = baselineGeneration;
        LeftRoot = leftRoot;
        RightRoot = rightRoot;
        Left = left;
        Right = right;
        Baseline = baseline;
        Direction = direction;
        DeletionMode = deletionMode;
        CreatedAtUtc = createdAtUtc;
        Behavior = behavior ?? InferBehavior(direction, deletionMode);
        FilterPolicy = filterPolicy ?? new SyncPathFilterPolicy([], [], includeHiddenFiles: true);
        ConflictPolicy = conflictPolicy;
    }

    public OperationPlanId PlanId { get; }

    public SyncProfileId ProfileId { get; }

    public long BaselineGeneration { get; }

    public StorageAddress LeftRoot { get; }

    public StorageAddress RightRoot { get; }

    public SyncEndpointSnapshot Left { get; }

    public SyncEndpointSnapshot Right { get; }

    public IReadOnlyDictionary<string, SyncBaselineObservation> Baseline { get; }

    public SyncDirection Direction { get; }

    public SyncDeletionMode DeletionMode { get; }

    public DateTimeOffset CreatedAtUtc { get; }
    public SyncBehavior Behavior { get; }
    public SyncPathFilterPolicy FilterPolicy { get; }
    public SyncConflictPolicy ConflictPolicy { get; }

    private static SyncBehavior InferBehavior(SyncDirection direction, SyncDeletionMode deletionMode) =>
        (direction, deletionMode) switch
        {
            (SyncDirection.LeftToRight, SyncDeletionMode.Mirror) => SyncBehavior.MirrorAToB,
            (SyncDirection.LeftToRight, _) => SyncBehavior.UpdateAToB,
            (SyncDirection.RightToLeft, SyncDeletionMode.Mirror) => SyncBehavior.MirrorBToA,
            (SyncDirection.RightToLeft, _) => SyncBehavior.UpdateBToA,
            (SyncDirection.TwoWay, SyncDeletionMode.Propagate) => SyncBehavior.TwoWayWithDeletionPropagation,
            _ => SyncBehavior.TwoWaySync,
        };
}

public sealed record SyncPlanBuildResult(
    ImmutableSyncPlan Plan,
    IReadOnlyList<SyncPlanningConflict> Conflicts,
    SyncExecutionSnapshots Snapshots);

/// <summary>
/// Compiles two complete endpoint snapshots and an optional last-known-good baseline into one
/// immutable preview. The builder never performs I/O and represents every ambiguity as a conflict.
/// </summary>
public static class SyncPlanBuilder
{
    public static StorageResult<SyncPlanBuildResult> Build(SyncPlanBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = ValidateRequest(request);
        if (validation is not null)
        {
            return StorageResult<SyncPlanBuildResult>.Fail(validation);
        }

        var comparer = SelectComparer(request.Left, request.Right);
        var caseSensitive = comparer == StringComparer.Ordinal;
        var filteredLeft = request.Left.Entries.Where(pair => request.FilterPolicy.Includes(pair.Key, caseSensitive));
        var filteredRight = request.Right.Entries.Where(pair => request.FilterPolicy.Includes(pair.Key, caseSensitive));
        if (!TryCreatePlanningIndex(filteredLeft, comparer, out var leftEntries) ||
            !TryCreatePlanningIndex(filteredRight, comparer, out var rightEntries))
        {
            return Invalid(
                "sync.plan.path_collision",
                "An endpoint contains paths that collide under the effective cross-endpoint case rules.");
        }

        var baseline = new Dictionary<string, SyncBaselineObservation>(comparer);
        try
        {
            foreach (var (path, observation) in request.Baseline)
            {
                SyncEndpointSnapshot.ValidateRelativePath(path, nameof(request));
                ArgumentNullException.ThrowIfNull(observation);
                if (!request.FilterPolicy.Includes(path, caseSensitive))
                {
                    continue;
                }
                if (!baseline.TryAdd(path, observation))
                {
                    return Invalid("sync.plan.baseline_collision", "The baseline contains a path collision.");
                }
            }
        }
        catch (ArgumentException)
        {
            return Invalid("sync.plan.invalid_baseline", "The baseline contains an invalid relative path.");
        }

        var operations = new List<PendingOperation>();
        var conflicts = new List<SyncPlanningConflict>();
        if (request.Behavior == SyncBehavior.CompareOnly)
        {
            AddComparisonConflicts(leftEntries, rightEntries, comparer, conflicts);
        }
        else if (request.Direction == SyncDirection.TwoWay)
        {
            BuildTwoWay(
                request,
                leftEntries,
                rightEntries,
                baseline,
                comparer,
                operations,
                conflicts);
        }
        else
        {
            BuildOneWay(
                request,
                leftEntries,
                rightEntries,
                comparer,
                operations,
                conflicts);
        }

        var orderedOperations = OrderOperations(operations)
            .Select((operation, sequence) => operation.Kind switch
            {
                SyncPlanOperationKind.Copy => SyncPlanOperation.Copy(
                    sequence,
                    operation.Source,
                    operation.Destination!,
                    operation.ExpectedLength,
                    operation.SourceDigest,
                    operation.DestinationDigest),
                SyncPlanOperationKind.Delete => SyncPlanOperation.Delete(sequence, operation.Source),
                SyncPlanOperationKind.CreateDirectory => SyncPlanOperation.CreateDirectory(sequence, operation.Source),
                _ => throw new InvalidOperationException("Unknown pending sync operation.")
            })
            .ToArray();
        var plan = ImmutableSyncPlan.Create(
            request.PlanId,
            request.ProfileId,
            request.BaselineGeneration,
            orderedOperations,
            request.CreatedAtUtc);
        var snapshots = CreateExecutionSnapshots(request);
        return StorageResult<SyncPlanBuildResult>.Success(new SyncPlanBuildResult(
            plan,
            new ReadOnlyCollection<SyncPlanningConflict>(conflicts
                .OrderBy(conflict => conflict.RelativePath, comparer)
                .ToArray()),
            snapshots));
    }

    private static StorageFailure? ValidateRequest(SyncPlanBuildRequest request)
    {
        if (!request.Left.Completeness.IsComplete || !request.Right.Completeness.IsComplete)
        {
            return new StorageFailure(
                "sync.plan.incomplete_snapshot",
                StorageFailureKind.Validation,
                "A sync plan requires complete endpoint snapshots.");
        }

        if (request.LeftRoot.ProfileId != request.Left.ProfileId ||
            request.RightRoot.ProfileId != request.Right.ProfileId ||
            !StringComparer.Ordinal.Equals(request.LeftRoot.RootIdentity, request.Left.RootIdentity) ||
            !StringComparer.Ordinal.Equals(request.RightRoot.RootIdentity, request.Right.RootIdentity))
        {
            return new StorageFailure(
                "sync.plan.root_mismatch",
                StorageFailureKind.Security,
                "A sync snapshot does not match its verified endpoint root.");
        }

        if (request.Direction == SyncDirection.TwoWay && request.DeletionMode == SyncDeletionMode.Mirror)
        {
            return new StorageFailure(
                "sync.plan.invalid_deletion_mode",
                StorageFailureKind.Validation,
                "Two-way synchronization uses propagate or disabled deletion semantics.");
        }

        if (request.Direction != SyncDirection.TwoWay && request.DeletionMode == SyncDeletionMode.Propagate)
        {
            return new StorageFailure(
                "sync.plan.invalid_deletion_mode",
                StorageFailureKind.Validation,
                "One-way synchronization uses mirror or disabled deletion semantics.");
        }

        return null;
    }

    private static void BuildOneWay(
        SyncPlanBuildRequest request,
        IReadOnlyDictionary<string, StorageEntry> leftEntries,
        IReadOnlyDictionary<string, StorageEntry> rightEntries,
        StringComparer comparer,
        List<PendingOperation> operations,
        List<SyncPlanningConflict> conflicts)
    {
        var source = request.Direction == SyncDirection.LeftToRight ? leftEntries : rightEntries;
        var destination = request.Direction == SyncDirection.LeftToRight ? rightEntries : leftEntries;
        var destinationRoot = request.Direction == SyncDirection.LeftToRight
            ? request.RightRoot
            : request.LeftRoot;
        var sourceDigests = request.Direction == SyncDirection.LeftToRight
            ? request.Left.PortableDigests
            : request.Right.PortableDigests;
        var destinationDigests = request.Direction == SyncDirection.LeftToRight
            ? request.Right.PortableDigests
            : request.Left.PortableDigests;
        var preDeletePrefixes = new List<string>();
        var paths = source.Keys
            .Concat(destination.Keys)
            .Distinct(comparer)
            .OrderBy(path => path, comparer);

        foreach (var path in paths)
        {
            _ = source.TryGetValue(path, out var sourceEntry);
            _ = destination.TryGetValue(path, out var destinationEntry);
            if (sourceEntry is null)
            {
                if (destinationEntry is not null && request.DeletionMode == SyncDeletionMode.Mirror)
                {
                    operations.Add(PendingOperation.Delete(destinationEntry));
                }

                continue;
            }

            if (!IsSupportedKind(sourceEntry))
            {
                AddConflict(conflicts, path, "The source item type is not safe to synchronize.");
                continue;
            }

            if (destinationEntry is null)
            {
                AddCreateOrCopy(
                    operations,
                    sourceEntry,
                    Destination(destinationRoot, path),
                    GetDigest(sourceDigests, path));
                continue;
            }

            if (request.Behavior is SyncBehavior.CopyNewFilesAToB or SyncBehavior.CopyNewFilesBToA)
            {
                continue;
            }

            if (!KindsAreCompatible(sourceEntry, destinationEntry))
            {
                if (request.DeletionMode != SyncDeletionMode.Mirror)
                {
                    AddConflict(conflicts, path, "Source and destination item types differ.");
                    continue;
                }

                operations.Add(PendingOperation.Delete(destinationEntry, beforeWrites: true));
                if (destinationEntry.IsContainer)
                {
                    preDeletePrefixes.Add(destinationEntry.Address.CanonicalRelativePath);
                }

                AddCreateOrCopy(
                    operations,
                    sourceEntry,
                    Destination(destinationRoot, path),
                    GetDigest(sourceDigests, path));
                continue;
            }

            if (sourceEntry.IsContainer)
            {
                continue;
            }

            var sourceDigest = GetDigest(sourceDigests, path);
            var destinationDigest = GetDigest(destinationDigests, path);
            if (!FilesAreKnownEqual(sourceEntry, destinationEntry, sourceDigest, destinationDigest))
            {
                operations.Add(PendingOperation.Copy(
                    sourceEntry,
                    destinationEntry.Address,
                    sourceDigest,
                    destinationDigest));
            }
        }

        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (operation.Kind == SyncPlanOperationKind.Delete &&
                preDeletePrefixes.Any(prefix => IsSameOrDescendant(
                    operation.Source.CanonicalRelativePath,
                    prefix)))
            {
                operations[index] = operation with { BeforeWrites = true };
            }
        }
    }

    private static void AddComparisonConflicts(
        IReadOnlyDictionary<string, StorageEntry> locationA,
        IReadOnlyDictionary<string, StorageEntry> locationB,
        StringComparer comparer,
        List<SyncPlanningConflict> conflicts)
    {
        foreach (var path in locationA.Keys.Concat(locationB.Keys).Distinct(comparer).OrderBy(path => path, comparer))
        {
            _ = locationA.TryGetValue(path, out var a);
            _ = locationB.TryGetValue(path, out var b);
            if (a is null || b is null || !KindsAreCompatible(a, b) ||
                a.Kind == StorageEntryKind.File && a.Size != b.Size)
            {
                conflicts.Add(new SyncPlanningConflict(
                    path,
                    SyncChangeKind.ConflictIndeterminate,
                    "The locations differ at this path (compare-only profile)."));
            }
        }
    }

    private static void BuildTwoWay(
        SyncPlanBuildRequest request,
        IReadOnlyDictionary<string, StorageEntry> leftEntries,
        IReadOnlyDictionary<string, StorageEntry> rightEntries,
        IReadOnlyDictionary<string, SyncBaselineObservation> baseline,
        StringComparer comparer,
        List<PendingOperation> operations,
        List<SyncPlanningConflict> conflicts)
    {
        var paths = leftEntries.Keys
            .Concat(rightEntries.Keys)
            .Concat(baseline.Keys)
            .Distinct(comparer)
            .OrderBy(path => path, comparer);
        foreach (var path in paths)
        {
            _ = leftEntries.TryGetValue(path, out var left);
            _ = rightEntries.TryGetValue(path, out var right);
            var hadBaseline = baseline.TryGetValue(path, out var baselineObservation);

            if (left?.IsContainer == true || right?.IsContainer == true)
            {
                BuildTwoWayContainer(
                    request,
                    path,
                    left,
                    right,
                    hadBaseline,
                    operations,
                    conflicts);
                continue;
            }

            if (left is { } unsupportedLeft && !IsSupportedKind(unsupportedLeft) ||
                right is { } unsupportedRight && !IsSupportedKind(unsupportedRight))
            {
                AddConflict(conflicts, path, "The item type is not safe to synchronize.");
                continue;
            }

            var classification = ThreeWayChangeClassifier.Classify(
                hadBaseline ? baselineObservation! : SyncBaselineObservation.Missing,
                ToObservation(left, GetDigest(request.Left.PortableDigests, path)),
                ToObservation(right, GetDigest(request.Right.PortableDigests, path)));
            switch (classification.Kind)
            {
                case SyncChangeKind.LeftCreated:
                    operations.Add(PendingOperation.Copy(
                        left!,
                        Destination(request.RightRoot, path),
                        GetDigest(request.Left.PortableDigests, path),
                        destinationDigest: null));
                    break;
                case SyncChangeKind.LeftModified:
                    operations.Add(PendingOperation.Copy(
                        left!,
                        right!.Address,
                        GetDigest(request.Left.PortableDigests, path),
                        GetDigest(request.Right.PortableDigests, path)));
                    break;
                case SyncChangeKind.RightCreated:
                    operations.Add(PendingOperation.Copy(
                        right!,
                        Destination(request.LeftRoot, path),
                        GetDigest(request.Right.PortableDigests, path),
                        destinationDigest: null));
                    break;
                case SyncChangeKind.RightModified:
                    operations.Add(PendingOperation.Copy(
                        right!,
                        left!.Address,
                        GetDigest(request.Right.PortableDigests, path),
                        GetDigest(request.Left.PortableDigests, path)));
                    break;
                case SyncChangeKind.LeftDeleted when request.DeletionMode == SyncDeletionMode.Propagate:
                    operations.Add(PendingOperation.Delete(right!));
                    break;
                case SyncChangeKind.RightDeleted when request.DeletionMode == SyncDeletionMode.Propagate:
                    operations.Add(PendingOperation.Delete(left!));
                    break;
                case SyncChangeKind.LeftDeleted:
                case SyncChangeKind.RightDeleted:
                    conflicts.Add(new SyncPlanningConflict(
                        path,
                        classification.Kind,
                        "Deletion propagation is disabled for this sync profile."));
                    break;
                case SyncChangeKind.ConflictBothCreated:
                case SyncChangeKind.ConflictBothModified:
                case SyncChangeKind.ConflictDeleteModify:
                case SyncChangeKind.ConflictIndeterminate:
                    if (request.ConflictPolicy == SyncConflictPolicy.KeepBoth && left is not null && right is not null)
                    {
                        AddKeepBothOperations(request, path, left, right, leftEntries, rightEntries, operations, conflicts);
                    }
                    else
                    {
                        conflicts.Add(new SyncPlanningConflict(
                            path,
                            classification.Kind,
                            "Both locations changed incompatibly or could not be compared safely."));
                    }
                    break;
                case SyncChangeKind.Unchanged:
                case SyncChangeKind.BothCreatedIdentical:
                case SyncChangeKind.BothModifiedIdentical:
                case SyncChangeKind.BothDeleted:
                    break;
                default:
                    throw new InvalidOperationException("Unhandled two-way change classification.");
            }
        }
    }

    private static void AddKeepBothOperations(
        SyncPlanBuildRequest request,
        string path,
        StorageEntry locationA,
        StorageEntry locationB,
        IReadOnlyDictionary<string, StorageEntry> locationAEntries,
        IReadOnlyDictionary<string, StorageEntry> locationBEntries,
        List<PendingOperation> operations,
        List<SyncPlanningConflict> conflicts)
    {
        var extension = Path.GetExtension(path);
        var stem = extension.Length == 0 ? path : path[..^extension.Length];
        var aName = stem + ".storagehub-conflict-location-a" + extension;
        var bName = stem + ".storagehub-conflict-location-b" + extension;
        if (locationAEntries.ContainsKey(aName) || locationAEntries.ContainsKey(bName) ||
            locationBEntries.ContainsKey(aName) || locationBEntries.ContainsKey(bName))
        {
            AddConflict(conflicts, path, "A deterministic Keep-both conflict name already exists.");
            return;
        }

        var aDigest = GetDigest(request.Left.PortableDigests, path);
        var bDigest = GetDigest(request.Right.PortableDigests, path);
        operations.Add(PendingOperation.Copy(locationA, Destination(request.LeftRoot, aName), aDigest, null));
        operations.Add(PendingOperation.Copy(locationA, Destination(request.RightRoot, aName), aDigest, null));
        operations.Add(PendingOperation.Copy(locationB, Destination(request.LeftRoot, bName), bDigest, null));
        operations.Add(PendingOperation.Copy(locationB, Destination(request.RightRoot, bName), bDigest, null));
        operations.Add(PendingOperation.Copy(locationA, locationB.Address, aDigest, bDigest));
    }

    private static void BuildTwoWayContainer(
        SyncPlanBuildRequest request,
        string path,
        StorageEntry? left,
        StorageEntry? right,
        bool hadBaseline,
        List<PendingOperation> operations,
        List<SyncPlanningConflict> conflicts)
    {
        if (left?.IsContainer == true && right?.IsContainer == true)
        {
            return;
        }

        if (left is not null && !left.IsContainer || right is not null && !right.IsContainer)
        {
            AddConflict(conflicts, path, "A container collides with a non-container item.");
            return;
        }

        if (hadBaseline)
        {
            if (request.DeletionMode == SyncDeletionMode.Propagate)
            {
                operations.Add(PendingOperation.Delete(left ?? right!));
            }
            else
            {
                AddConflict(conflicts, path, "A container was deleted while deletion propagation is disabled.");
            }

            return;
        }

        if (left is not null)
        {
            operations.Add(PendingOperation.CreateDirectory(Destination(request.RightRoot, path)));
        }
        else if (right is not null)
        {
            operations.Add(PendingOperation.CreateDirectory(Destination(request.LeftRoot, path)));
        }
    }

    private static IEnumerable<PendingOperation> OrderOperations(IEnumerable<PendingOperation> operations)
    {
        var snapshot = operations.ToArray();
        return snapshot
            .Where(operation => operation.Kind == SyncPlanOperationKind.Delete && operation.BeforeWrites)
            .OrderByDescending(operation => PathDepth(operation.RelativePath))
            .ThenBy(operation => operation.RelativePath, StringComparer.Ordinal)
            .Concat(snapshot
            .Where(operation => operation.Kind == SyncPlanOperationKind.CreateDirectory)
            .OrderBy(operation => PathDepth(operation.RelativePath))
            .ThenBy(operation => operation.RelativePath, StringComparer.Ordinal))
            .Concat(snapshot
                .Where(operation => operation.Kind == SyncPlanOperationKind.Copy)
                .OrderBy(operation => operation.RelativePath, StringComparer.Ordinal))
            .Concat(snapshot
                .Where(operation => operation.Kind == SyncPlanOperationKind.Delete && !operation.BeforeWrites)
                .OrderByDescending(operation => PathDepth(operation.RelativePath))
                .ThenBy(operation => operation.RelativePath, StringComparer.Ordinal));
    }

    private static SyncExecutionSnapshots CreateExecutionSnapshots(SyncPlanBuildRequest request)
    {
        var baselineCount = request.Baseline.Count;
        var left = WithUnexpectedEmpty(request.Left.Completeness, baselineCount > 0 && request.Left.Entries.Count == 0);
        var right = WithUnexpectedEmpty(request.Right.Completeness, baselineCount > 0 && request.Right.Entries.Count == 0);
        var roots = new Dictionary<ConnectionProfileId, string>
        {
            [request.Left.ProfileId] = request.Left.RootIdentity
        };
        if (roots.TryGetValue(request.Right.ProfileId, out var existing) &&
            !StringComparer.Ordinal.Equals(existing, request.Right.RootIdentity))
        {
            throw new InvalidOperationException(
                "One profile cannot bind two different live root identities in one execution plan.");
        }

        roots[request.Right.ProfileId] = request.Right.RootIdentity;
        return new SyncExecutionSnapshots(left, right, baselineCount, roots);
    }

    private static SnapshotCompleteness WithUnexpectedEmpty(
        SnapshotCompleteness source,
        bool unexpectedlyEmpty) => new(
        source.EndpointAvailable,
        source.RootIdentityVerified,
        source.EnumerationCompleted,
        source.PaginationCompleted,
        source.PermissionsIntact,
        source.UnexpectedlyEmpty || unexpectedlyEmpty,
        source.TotalItemCount);

    private static SyncItemObservation ToObservation(
        StorageEntry? entry,
        PortableContentDigest? portableDigest)
    {
        if (entry is null)
        {
            return SyncItemObservation.Missing;
        }

        var digest = portableDigest is null
            ? null
            : new ContentDigest(portableDigest.AlgorithmName, portableDigest.Value);
        return SyncItemObservation.Present(
            entry.Size ?? 0,
            digest,
            entry.Address.VersionId ?? entry.Address.EntityTag ?? entry.ETag);
    }

    private static bool FilesAreKnownEqual(
        StorageEntry left,
        StorageEntry right,
        PortableContentDigest? leftDigest,
        PortableContentDigest? rightDigest)
    {
        if (left.Kind != StorageEntryKind.File || right.Kind != StorageEntryKind.File ||
            left.Size != right.Size)
        {
            return false;
        }

        return leftDigest is not null &&
               rightDigest is not null &&
               leftDigest == rightDigest;
    }

    private static bool KindsAreCompatible(StorageEntry left, StorageEntry right) =>
        left.IsContainer == right.IsContainer &&
        (left.IsContainer || left.Kind == StorageEntryKind.File && right.Kind == StorageEntryKind.File);

    private static bool IsSupportedKind(StorageEntry entry) =>
        entry.Kind is StorageEntryKind.File or StorageEntryKind.Directory or StorageEntryKind.Prefix;

    private static StorageAddress Destination(StorageAddress root, string relativePath)
    {
        var destination = root.Append(relativePath);
        return destination.IsSuccess
            ? destination.Value
            : throw new InvalidOperationException(
                "A validated snapshot path could not be mapped to its destination root.");
    }

    private static void AddCreateOrCopy(
        List<PendingOperation> operations,
        StorageEntry source,
        StorageAddress destination,
        PortableContentDigest? sourceDigest)
    {
        operations.Add(source.IsContainer
            ? PendingOperation.CreateDirectory(destination)
            : PendingOperation.Copy(source, destination, sourceDigest, destinationDigest: null));
    }

    private static PortableContentDigest? GetDigest(
        IReadOnlyDictionary<string, PortableContentDigest> digests,
        string path) => digests.TryGetValue(path, out var digest) ? digest : null;

    private static void AddConflict(
        List<SyncPlanningConflict> conflicts,
        string path,
        string reason) =>
        conflicts.Add(new SyncPlanningConflict(path, SyncChangeKind.ConflictIndeterminate, reason));

    private static StringComparer SelectComparer(
        SyncEndpointSnapshot left,
        SyncEndpointSnapshot right) =>
        left.CaseSensitivity == StorageCaseSensitivity.Sensitive &&
        right.CaseSensitivity == StorageCaseSensitivity.Sensitive
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;

    private static bool TryCreatePlanningIndex(
        IEnumerable<KeyValuePair<string, StorageEntry>> source,
        StringComparer comparer,
        out IReadOnlyDictionary<string, StorageEntry> index)
    {
        var result = new Dictionary<string, StorageEntry>(comparer);
        foreach (var (path, entry) in source)
        {
            if (!result.TryAdd(path, entry))
            {
                index = new Dictionary<string, StorageEntry>();
                return false;
            }
        }

        index = result;
        return true;
    }

    private static bool IsSameOrDescendant(string path, string ancestor) =>
        StringComparer.Ordinal.Equals(path, ancestor) ||
        path.StartsWith(ancestor + "/", StringComparison.Ordinal);

    private static int PathDepth(string path) => path.Count(character => character == '/') + 1;

    private static StorageResult<SyncPlanBuildResult> Invalid(string code, string message) =>
        StorageResult<SyncPlanBuildResult>.Fail(new StorageFailure(
            code,
            StorageFailureKind.Validation,
            message));

    private sealed record PendingOperation(
        SyncPlanOperationKind Kind,
        StorageAddress Source,
        StorageAddress? Destination,
        long? ExpectedLength,
        PortableContentDigest? SourceDigest,
        PortableContentDigest? DestinationDigest,
        string RelativePath,
        bool BeforeWrites)
    {
        public static PendingOperation Copy(
            StorageEntry source,
            StorageAddress destination,
            PortableContentDigest? sourceDigest,
            PortableContentDigest? destinationDigest) => new(
            SyncPlanOperationKind.Copy,
            source.Address,
            destination,
            source.Size,
            sourceDigest,
            destinationDigest,
            destination.CanonicalRelativePath,
            false);

        public static PendingOperation Delete(StorageEntry target, bool beforeWrites = false) => new(
            SyncPlanOperationKind.Delete,
            target.Address,
            null,
            null,
            null,
            null,
            target.Address.CanonicalRelativePath,
            beforeWrites);

        public static PendingOperation CreateDirectory(StorageAddress target) => new(
            SyncPlanOperationKind.CreateDirectory,
            target,
            null,
            null,
            null,
            null,
            target.CanonicalRelativePath,
            false);
    }
}
