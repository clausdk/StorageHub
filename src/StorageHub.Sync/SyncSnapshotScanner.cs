using System.Collections.Concurrent;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Capabilities;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Abstractions;
using StorageHub.Storage.Models;

namespace StorageHub.Sync;

public enum SyncPortableHashMode
{
    Disabled = 0,
    FilesWithoutStableIdentity = 1,
    AllFiles = 2,
}

public sealed record SyncSnapshotScanOptions
{
    public SyncSnapshotScanOptions(
        int pageSize = 1_000,
        int maximumEntries = 1_000_000,
        int maximumDirectories = 250_000,
        int maximumPages = 1_000_000,
        SyncPortableHashMode portableHashMode = SyncPortableHashMode.Disabled,
        int maximumHashedFiles = 10_000,
        long maximumHashBytesPerFile = 1024L * 1024 * 1024,
        long maximumTotalHashBytes = 10L * 1024 * 1024 * 1024,
        int maximumConcurrentHashes = 2,
        bool requirePortableHashCapability = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, StorageListRequest.MaximumPageSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDirectories, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPages, 1);
        if (!Enum.IsDefined(portableHashMode))
        {
            throw new ArgumentOutOfRangeException(nameof(portableHashMode));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maximumHashedFiles, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumHashBytesPerFile, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumTotalHashBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumConcurrentHashes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumConcurrentHashes, 32);
        PageSize = pageSize;
        MaximumEntries = maximumEntries;
        MaximumDirectories = maximumDirectories;
        MaximumPages = maximumPages;
        PortableHashMode = portableHashMode;
        MaximumHashedFiles = maximumHashedFiles;
        MaximumHashBytesPerFile = maximumHashBytesPerFile;
        MaximumTotalHashBytes = maximumTotalHashBytes;
        MaximumConcurrentHashes = maximumConcurrentHashes;
        RequirePortableHashCapability = requirePortableHashCapability;
    }

    public int PageSize { get; }

    public int MaximumEntries { get; }

    public int MaximumDirectories { get; }

    public int MaximumPages { get; }

    public SyncPortableHashMode PortableHashMode { get; }

    public int MaximumHashedFiles { get; }

    public long MaximumHashBytesPerFile { get; }

    public long MaximumTotalHashBytes { get; }

    public int MaximumConcurrentHashes { get; }

    public bool RequirePortableHashCapability { get; }

    /// <summary>
    /// Safe orchestration default: obtain portable evidence for files that lack a provider version
    /// or conditional ETag identity, while retaining hard file, byte, and concurrency budgets.
    /// </summary>
    public static SyncSnapshotScanOptions SynchronizationDefault => new(
        portableHashMode: SyncPortableHashMode.FilesWithoutStableIdentity);
}

/// <summary>
/// Produces a fail-closed, paginated tree snapshot without following symbolic links. Every page,
/// directory, entry, and continuation token is bounded and provider output is revalidated against
/// the requested root.
/// </summary>
public static class SyncSnapshotScanner
{
    public static async ValueTask<StorageResult<SyncEndpointSnapshot>> ScanAsync(
        IStorageEndpointSession session,
        StorageAddress root,
        SyncSnapshotScanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(root);
        options ??= new SyncSnapshotScanOptions();

        var rootValidation = ValidateRoot(session, root);
        if (rootValidation is not null)
        {
            return Failure(rootValidation);
        }

        if (!session.Capabilities.Supports(StorageFeature.List))
        {
            return Failure(new StorageFailure(
                "sync.scan.list_unsupported",
                StorageFailureKind.Unsupported,
                "The endpoint does not support directory or prefix listing."));
        }

        var pathComparer = session.Capabilities.CaseSensitivity == StorageCaseSensitivity.Sensitive
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;
        var entries = new SortedDictionary<string, StorageEntry>(pathComparer);
        var pendingDirectories = new Queue<StorageAddress>();
        var visitedDirectories = new HashSet<string>(pathComparer);
        pendingDirectories.Enqueue(root);
        _ = visitedDirectories.Add(root.CanonicalRelativePath);
        var pageCount = 0;

        try
        {
            while (pendingDirectories.TryDequeue(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? continuationToken = null;
                var seenContinuationTokens = new HashSet<string>(StringComparer.Ordinal);
                do
                {
                    if (++pageCount > options.MaximumPages)
                    {
                        return Failure(LimitFailure("page"));
                    }

                    var listed = await session.ListAsync(
                        directory,
                        new StorageListRequest(
                            Recursive: false,
                            PageSize: options.PageSize,
                            ContinuationToken: continuationToken),
                        cancellationToken).ConfigureAwait(false);
                    if (listed.IsFailure)
                    {
                        return Failure(new StorageFailure(
                            "sync.scan.list_failed",
                            listed.Error.Kind,
                            "The endpoint snapshot could not be completed.",
                            listed.Error.IsTransient,
                            listed.Error.ProviderCode,
                            listed.Error.DiagnosticId));
                    }

                    foreach (var entry in listed.Value.Entries)
                    {
                        var relative = GetRelativePath(root, session, entry);
                        if (relative.IsFailure)
                        {
                            return Failure(relative.Error);
                        }

                        if (relative.Value.Length == 0)
                        {
                            continue;
                        }

                        if (!entries.TryAdd(relative.Value, entry))
                        {
                            return Failure(new StorageFailure(
                                "sync.scan.path_collision",
                                StorageFailureKind.Conflict,
                                "The endpoint returned duplicate or case-colliding paths."));
                        }

                        if (entries.Count > options.MaximumEntries)
                        {
                            return Failure(LimitFailure("entry"));
                        }

                        if (entry.IsContainer &&
                            visitedDirectories.Add(entry.Address.CanonicalRelativePath))
                        {
                            if (visitedDirectories.Count > options.MaximumDirectories)
                            {
                                return Failure(LimitFailure("directory"));
                            }

                            pendingDirectories.Enqueue(entry.Address);
                        }
                    }

                    continuationToken = listed.Value.ContinuationToken;
                    if (continuationToken is not null &&
                        (!seenContinuationTokens.Add(continuationToken) ||
                         continuationToken.Any(char.IsControl)))
                    {
                        return Failure(new StorageFailure(
                            "sync.scan.continuation_cycle",
                            StorageFailureKind.Provider,
                            "The endpoint returned an invalid or repeated continuation token."));
                    }
                }
                while (continuationToken is not null);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(new StorageFailure(
                "sync.scan.cancelled",
                StorageFailureKind.Cancelled,
                "The endpoint snapshot was cancelled."));
        }

        var digestResult = await ComputePortableDigestsAsync(
            session,
            entries,
            options,
            cancellationToken).ConfigureAwait(false);
        if (digestResult.IsFailure)
        {
            return Failure(digestResult.Error);
        }

        var completeness = SnapshotCompleteness.Complete(entries.Count);
        return StorageResult<SyncEndpointSnapshot>.Success(new SyncEndpointSnapshot(
            session.ProfileId,
            session.RootIdentity,
            entries,
            completeness,
            session.Capabilities.CaseSensitivity,
            digestResult.Value));
    }

    private static async ValueTask<StorageResult<IReadOnlyDictionary<string, PortableContentDigest>>>
        ComputePortableDigestsAsync(
            IStorageEndpointSession session,
            IReadOnlyDictionary<string, StorageEntry> entries,
            SyncSnapshotScanOptions options,
            CancellationToken cancellationToken)
    {
        if (options.PortableHashMode == SyncPortableHashMode.Disabled)
        {
            return StorageResult<IReadOnlyDictionary<string, PortableContentDigest>>.Success(
                new Dictionary<string, PortableContentDigest>());
        }

        if (session is not IStoragePortableChecksumSession checksumSession)
        {
            return options.RequirePortableHashCapability
                ? StorageResult<IReadOnlyDictionary<string, PortableContentDigest>>.Fail(
                    new StorageFailure(
                        "sync.scan.portable_hash_unsupported",
                        StorageFailureKind.Unsupported,
                        "The endpoint does not expose explicit portable SHA-256 checksums."))
                : StorageResult<IReadOnlyDictionary<string, PortableContentDigest>>.Success(
                    new Dictionary<string, PortableContentDigest>());
        }

        var candidates = entries
            .Where(pair => pair.Value.Kind == StorageEntryKind.File &&
                           (options.PortableHashMode == SyncPortableHashMode.AllFiles ||
                            pair.Value.Address.VersionId is null &&
                            pair.Value.Address.EntityTag is null &&
                            pair.Value.ETag is null))
            .ToArray();
        if (candidates.Length > options.MaximumHashedFiles)
        {
            return PortableHashLimit("file-count");
        }

        long totalBytes = 0;
        foreach (var (_, entry) in candidates)
        {
            if (entry.Size is not long length)
            {
                return PortableHashFailure(
                    "sync.scan.portable_hash_length_unknown",
                    "Portable SHA-256 scanning requires a known file length.");
            }

            if (length > options.MaximumHashBytesPerFile)
            {
                return PortableHashLimit("per-file byte");
            }

            try
            {
                totalBytes = checked(totalBytes + length);
            }
            catch (OverflowException)
            {
                return PortableHashLimit("total byte");
            }

            if (totalBytes > options.MaximumTotalHashBytes)
            {
                return PortableHashLimit("total byte");
            }
        }

        var digests = new ConcurrentDictionary<string, PortableContentDigest>(StringComparer.Ordinal);
        StorageFailure? firstFailure = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await Parallel.ForEachAsync(
                candidates,
                new ParallelOptions
                {
                    CancellationToken = stop.Token,
                    MaxDegreeOfParallelism = options.MaximumConcurrentHashes,
                },
                async (candidate, token) =>
                {
                    var result = await checksumSession.ComputePortableChecksumAsync(
                        new PortableChecksumRequest(
                            candidate.Value,
                            options.MaximumHashBytesPerFile),
                        token).ConfigureAwait(false);
                    if (result.IsFailure)
                    {
                        if (Interlocked.CompareExchange(ref firstFailure, result.Error, null) is null)
                        {
                            stop.Cancel();
                        }

                        return;
                    }

                    if (result.Value.BytesProcessed != candidate.Value.Size)
                    {
                        if (Interlocked.CompareExchange(
                                ref firstFailure,
                                new StorageFailure(
                                    "sync.scan.portable_hash_invalid",
                                    StorageFailureKind.Integrity,
                                    "The endpoint returned portable checksum evidence for an unexpected byte count."),
                                null) is null)
                        {
                            stop.Cancel();
                        }

                        return;
                    }

                    _ = digests.TryAdd(candidate.Key, result.Value.Digest);
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (firstFailure is not null && !cancellationToken.IsCancellationRequested)
        {
            // The first checksum failure deliberately cancels sibling hash workers.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return PortableHashFailure(
                "sync.scan.cancelled",
                "The endpoint snapshot was cancelled.",
                StorageFailureKind.Cancelled);
        }
        catch (Exception)
        {
            return PortableHashFailure(
                "sync.scan.portable_hash_failed",
                "The endpoint failed unexpectedly while computing portable SHA-256 evidence.",
                StorageFailureKind.Unexpected);
        }

        if (firstFailure is not null)
        {
            return StorageResult<IReadOnlyDictionary<string, PortableContentDigest>>.Fail(
                new StorageFailure(
                    "sync.scan.portable_hash_failed",
                    firstFailure.Kind,
                    "Portable SHA-256 evidence could not be captured for the complete snapshot.",
                    firstFailure.IsTransient,
                    firstFailure.ProviderCode,
                    firstFailure.DiagnosticId));
        }

        return StorageResult<IReadOnlyDictionary<string, PortableContentDigest>>.Success(digests);
    }

    private static StorageResult<IReadOnlyDictionary<string, PortableContentDigest>> PortableHashLimit(
        string resource) => PortableHashFailure(
        "sync.scan.portable_hash_limit_exceeded",
        $"Portable SHA-256 scanning exceeded its configured {resource} limit.");

    private static StorageResult<IReadOnlyDictionary<string, PortableContentDigest>> PortableHashFailure(
        string code,
        string message,
        StorageFailureKind kind = StorageFailureKind.Validation) =>
        StorageResult<IReadOnlyDictionary<string, PortableContentDigest>>.Fail(
            new StorageFailure(code, kind, message));

    private static StorageFailure? ValidateRoot(
        IStorageEndpointSession session,
        StorageAddress root)
    {
        if (root.ProfileId != session.ProfileId ||
            !StringComparer.Ordinal.Equals(root.RootIdentity, session.RootIdentity))
        {
            return new StorageFailure(
                "sync.scan.root_mismatch",
                StorageFailureKind.Security,
                "The requested scan root does not belong to the live endpoint session.");
        }

        return null;
    }

    private static StorageResult<string> GetRelativePath(
        StorageAddress root,
        IStorageEndpointSession session,
        StorageEntry entry)
    {
        if (entry.Address.ProfileId != session.ProfileId ||
            !StringComparer.Ordinal.Equals(entry.Address.RootIdentity, session.RootIdentity))
        {
            return RelativeFailure();
        }

        var path = entry.Address.CanonicalRelativePath;
        if (root.IsRoot)
        {
            return StorageResult<string>.Success(path);
        }

        if (StringComparer.Ordinal.Equals(path, root.CanonicalRelativePath))
        {
            return StorageResult<string>.Success(string.Empty);
        }

        var prefix = root.CanonicalRelativePath + "/";
        return path.StartsWith(prefix, StringComparison.Ordinal)
            ? StorageResult<string>.Success(path[prefix.Length..])
            : RelativeFailure();
    }

    private static StorageResult<string> RelativeFailure() => StorageResult<string>.Fail(
        new StorageFailure(
            "sync.scan.outside_root",
            StorageFailureKind.Security,
            "The endpoint returned an entry outside the verified scan root."));

    private static StorageFailure LimitFailure(string resource) => new(
        "sync.scan.limit_exceeded",
        StorageFailureKind.Validation,
        $"The endpoint snapshot exceeded its configured {resource} limit.");

    private static StorageResult<SyncEndpointSnapshot> Failure(StorageFailure failure) =>
        StorageResult<SyncEndpointSnapshot>.Fail(failure);
}
