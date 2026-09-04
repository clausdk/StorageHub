using StorageHub.Contracts.Ipc;
using StorageHub.Contracts.Results;

namespace StorageHub.Desktop;

/// <summary>Expands selected remote folders into a bounded immutable file-transfer plan.</summary>
internal sealed class RecursiveTransferController : IAsyncDisposable
{
    private const int MaximumManifestEntries = 10_000;
    private const int MaximumManifestPages = 1_000;
    private const long MaximumCombinedPathCharacters = 4_000_000;
    private readonly ManualTransferController _transfers;
    private readonly IRemoteStorageAgentClient _storage;
    private readonly IObjectInspectorAgentClient _mutations;
    private readonly bool _ownsClients;
    private bool _disposed;

    internal RecursiveTransferController(ManualTransferController transfers)
        : this(
            transfers,
            new NamedPipeRemoteStorageAgentClient(),
            new NamedPipeObjectInspectorAgentClient(),
            ownsClients: true)
    {
    }

    internal RecursiveTransferController(
        ManualTransferController transfers,
        IRemoteStorageAgentClient storage,
        IObjectInspectorAgentClient mutations,
        bool ownsClients = false)
    {
        _transfers = transfers ?? throw new ArgumentNullException(nameof(transfers));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _mutations = mutations ?? throw new ArgumentNullException(nameof(mutations));
        _ownsClients = ownsClients;
    }

    internal async Task<ManualTransferEnqueueResult> EnqueueAsync(
        PaneSelectionSnapshot source,
        PaneDestinationSnapshot destination,
        TransferQueueOperation operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (operation == TransferQueueOperation.Move && source.Items.Any(static item => item.IsContainer))
        {
            return Failure(
                "manual_transfer.folder_move_not_supported",
                "Folder moves are not enabled yet because deleting source directories requires durable child-job dependencies. Copy the folder first.");
        }

        if (source.Context.Kind != PaneTransferContextKind.SavedConnection ||
            destination.Context.Kind != PaneTransferContextKind.SavedConnection ||
            source.Context.ConnectionId is null ||
            destination.Context.ConnectionId is null)
        {
            return Failure(
                "manual_transfer.saved_connections_required",
                "Recursive transfers require saved connections on both panes.");
        }

        try
        {
            var manifest = await BuildManifestAsync(source, destination, operation, cancellationToken)
                .ConfigureAwait(false);
            if (manifest.Failure is not null)
            {
                return new ManualTransferEnqueueResult([], [], manifest.Failure);
            }

            foreach (var directoryPath in manifest.Directories
                .OrderBy(static path => path.Count(static character => character == '/'))
                .ThenBy(static path => path, StringComparer.Ordinal))
            {
                var ensured = await _mutations.EnsureDirectoryAsync(new StorageDirectoryEnsureRequest(
                    EditableFileIpcContract.CurrentVersion,
                    new ObjectInspectorAddress(
                        destination.Context.ConnectionId.Value,
                        destination.Context.RootIdentity!,
                        directoryPath)), cancellationToken).ConfigureAwait(false);
                if (ensured.Failure is not null)
                {
                    return new ManualTransferEnqueueResult([], [], MapFailure(ensured.Failure));
                }
            }

            if (manifest.Requests.Count == 0)
            {
                return new ManualTransferEnqueueResult([], [], failure: null);
            }

            return await _transfers.EnqueuePlanAsync(
                new ManualTransferPlan(manifest.Requests),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
            InvalidOperationException or TimeoutException or System.Text.Json.JsonException)
        {
            return Failure(
                "manual_transfer.manifest_unavailable",
                "StorageHub could not build the recursive transfer manifest.",
                isTransient: true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsClients)
        {
            await _storage.DisposeAsync().ConfigureAwait(false);
            await _mutations.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<ManifestBuildResult> BuildManifestAsync(
        PaneSelectionSnapshot source,
        PaneDestinationSnapshot destination,
        TransferQueueOperation operation,
        CancellationToken cancellationToken)
    {
        var sourceFiles = new List<(PaneTransferItem Item, string DestinationPath)>();
        var sourceEntryKinds = new Dictionary<string, StorageItemKind>(StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal);
        var destinationEntries = destination.Entries.ToDictionary(
            static item => item.RelativePath,
            StringComparer.Ordinal);
        long combinedPathCharacters = 0;

        foreach (var selected in source.Items)
        {
            if (!selected.IsContainer)
            {
                var destinationPath = Combine(destination.Context.RelativePath, selected.Name);
                sourceFiles.Add((selected, destinationPath));
                combinedPathCharacters += selected.RelativePath.Length + destinationPath.Length;
                continue;
            }

            var targetRoot = Combine(destination.Context.RelativePath, selected.Name);
            directories.Add(targetRoot);
            var sourceEntries = await ListTreeAsync(
                source.Context.ConnectionId!.Value,
                source.Context.RootIdentity!,
                selected.RelativePath,
                allowNotFound: false,
                cancellationToken).ConfigureAwait(false);
            if (sourceEntries.Failure is not null)
            {
                return ManifestBuildResult.Fail(sourceEntries.Failure);
            }

            var existingEntries = await ListTreeAsync(
                destination.Context.ConnectionId!.Value,
                destination.Context.RootIdentity!,
                targetRoot,
                allowNotFound: true,
                cancellationToken).ConfigureAwait(false);
            if (existingEntries.Failure is not null)
            {
                return ManifestBuildResult.Fail(existingEntries.Failure);
            }

            foreach (var existing in existingEntries.Entries)
            {
                var mapped = PaneTransferItem.Create(existing);
                if (mapped.IsFailure)
                {
                    return ManifestBuildResult.Fail(new StorageFailure(
                        "manual_transfer.destination_manifest_invalid",
                        StorageFailureKind.Integrity,
                        "The destination returned an invalid recursive entry."));
                }

                if (!destinationEntries.TryAdd(existing.RelativePath, mapped.Value) &&
                    !string.Equals(existing.RelativePath, targetRoot, StringComparison.Ordinal))
                {
                    return ManifestBuildResult.Fail(new StorageFailure(
                        "manual_transfer.destination_manifest_invalid",
                        StorageFailureKind.Integrity,
                        "The destination returned duplicated recursive entries."));
                }
            }

            foreach (var entry in sourceEntries.Entries)
            {
                if (sourceEntryKinds.TryGetValue(entry.RelativePath, out var priorKind))
                {
                    return ManifestBuildResult.Fail(new StorageFailure(
                        priorKind != entry.Kind
                            ? "manual_transfer.source_kind_collision"
                            : "manual_transfer.source_manifest_duplicate",
                        StorageFailureKind.Integrity,
                        priorKind != entry.Kind
                            ? "The source returned a file and folder with the same path."
                            : "The source returned a duplicated recursive entry."));
                }
                sourceEntryKinds.Add(entry.RelativePath, entry.Kind);

                if (!TryGetDescendantSuffix(selected.RelativePath, entry.RelativePath, out var suffix))
                {
                    return ManifestBuildResult.Fail(new StorageFailure(
                        "manual_transfer.source_manifest_invalid",
                        StorageFailureKind.Integrity,
                        "The source returned an entry outside the selected folder."));
                }

                var targetPath = suffix.Length == 0 ? targetRoot : Combine(targetRoot, suffix);
                combinedPathCharacters += entry.RelativePath.Length + targetPath.Length;
                if (entry.Kind is StorageItemKind.Directory or StorageItemKind.Prefix)
                {
                    directories.Add(targetPath);
                    continue;
                }

                if (entry.Kind != StorageItemKind.File || entry.IsContainer)
                {
                    return ManifestBuildResult.Fail(new StorageFailure(
                        "manual_transfer.recursive_item_unsupported",
                        StorageFailureKind.Unsupported,
                        "The selected folder contains a symbolic link or provider item that cannot be transferred safely."));
                }

                var mapped = PaneTransferItem.Create(entry);
                if (mapped.IsFailure)
                {
                    return ManifestBuildResult.Fail(mapped.Error);
                }

                sourceFiles.Add((mapped.Value, targetPath));
            }
        }

        if (sourceFiles.Count + directories.Count > MaximumManifestEntries ||
            combinedPathCharacters > MaximumCombinedPathCharacters)
        {
            return ManifestBuildResult.Fail(new StorageFailure(
                "manual_transfer.manifest_limit_exceeded",
                StorageFailureKind.Validation,
                $"A recursive transfer is limited to {MaximumManifestEntries:N0} files and folders."));
        }

        var requests = new List<TransferEnqueueRequest>(sourceFiles.Count);
        var targetPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (item, destinationPath) in sourceFiles)
        {
            if (directories.Contains(destinationPath))
            {
                return ManifestBuildResult.Fail(new StorageFailure(
                    "manual_transfer.source_kind_collision",
                    StorageFailureKind.Integrity,
                    "The recursive manifest mapped a file and folder to the same destination path."));
            }

            if (!targetPaths.Add(destinationPath))
            {
                return ManifestBuildResult.Fail(new StorageFailure(
                    "manual_transfer.destination_duplicate",
                    StorageFailureKind.Validation,
                    "More than one selected file maps to the same destination path."));
            }

            destinationEntries.TryGetValue(destinationPath, out var existing);
            if (existing is { Kind: not StorageItemKind.File })
            {
                return ManifestBuildResult.Fail(new StorageFailure(
                    "manual_transfer.destination_container_conflict",
                    StorageFailureKind.Conflict,
                    "A destination folder or non-file item conflicts with a source file."));
            }

            if (existing is not null && (!existing.HasStableIdentity || !item.HasStableIdentity))
            {
                return ManifestBuildResult.Fail(new StorageFailure(
                    "manual_transfer.overwrite_identity_required",
                    StorageFailureKind.Conflict,
                    "Replacing an existing file requires stable source and destination identity evidence."));
            }

            var request = new TransferEnqueueRequest(
                TransferQueueIpcContract.CurrentVersion,
                Guid.NewGuid(),
                operation,
                new TransferQueueAddress(
                    source.Context.ConnectionId!.Value,
                    source.Context.RootIdentity!,
                    item.RelativePath,
                    item.NativeItemId,
                    item.VersionId,
                    item.EntityTag),
                new TransferQueueAddress(
                    destination.Context.ConnectionId!.Value,
                    destination.Context.RootIdentity!,
                    destinationPath,
                    existing?.NativeItemId,
                    existing?.VersionId,
                    existing?.EntityTag),
                item.Length,
                TransferQueueVerification.StrongHashWhenAvailable,
                ExpectedDestinationVersionId: existing?.VersionId,
                ExpectedDestinationEntityTag: existing?.EntityTag);
            if (!request.HasValidBounds || IsSameAddress(request.Source, request.Destination))
            {
                return ManifestBuildResult.Fail(new StorageFailure(
                    "manual_transfer.request_invalid",
                    StorageFailureKind.Validation,
                    "The recursive manifest produced an invalid or self-referencing transfer."));
            }

            requests.Add(request);
        }

        return new ManifestBuildResult(requests, directories, null);
    }

    private async Task<TreeListResult> ListTreeAsync(
        Guid connectionId,
        string expectedRootIdentity,
        string relativePath,
        bool allowNotFound,
        CancellationToken cancellationToken)
    {
        var entries = new List<StorageListItem>();
        var continuationTokens = new HashSet<string>(StringComparer.Ordinal);
        string? continuation = null;
        for (var pageNumber = 0; pageNumber < MaximumManifestPages; pageNumber++)
        {
            var response = await _storage.ListStorageAsync(new StorageListPageRequest(
                StorageIpcContract.CurrentVersion,
                connectionId,
                relativePath,
                PageSize: StorageIpcLimits.MaximumStableIdentityPageSize,
                ContinuationToken: continuation,
                Recursive: true), cancellationToken).ConfigureAwait(false);
            if (response.Failure is not null)
            {
                if (response.Failure.Category == StorageIpcFailureCategory.Unsupported &&
                    continuation is null)
                {
                    return await ListTreeBreadthFirstAsync(
                        connectionId,
                        expectedRootIdentity,
                        relativePath,
                        allowNotFound,
                        cancellationToken).ConfigureAwait(false);
                }

                return allowNotFound && response.Failure.Category == StorageIpcFailureCategory.NotFound
                    ? new TreeListResult([], null)
                    : new TreeListResult([], MapFailure(response.Failure));
            }

            if (!string.Equals(response.RootIdentity, expectedRootIdentity, StringComparison.Ordinal))
            {
                return new TreeListResult([], new StorageFailure(
                    "manual_transfer.root_identity_changed",
                    StorageFailureKind.Integrity,
                    "The connection root identity changed while the recursive manifest was being built."));
            }

            entries.AddRange(response.Entries);
            if (entries.Count > MaximumManifestEntries)
            {
                return new TreeListResult([], new StorageFailure(
                    "manual_transfer.manifest_limit_exceeded",
                    StorageFailureKind.Validation,
                    $"A recursive transfer is limited to {MaximumManifestEntries:N0} files and folders."));
            }

            continuation = response.ContinuationToken;
            if (continuation is null)
            {
                return new TreeListResult(entries, null);
            }

            if (!continuationTokens.Add(continuation))
            {
                return new TreeListResult([], new StorageFailure(
                    "manual_transfer.repeated_page_token",
                    StorageFailureKind.Integrity,
                    "The provider repeated a recursive listing page token."));
            }
        }

        return new TreeListResult([], new StorageFailure(
            "manual_transfer.page_limit_exceeded",
            StorageFailureKind.Validation,
            "The recursive listing exceeded its bounded page limit."));
    }

    private async Task<TreeListResult> ListTreeBreadthFirstAsync(
        Guid connectionId,
        string expectedRootIdentity,
        string relativePath,
        bool allowNotFound,
        CancellationToken cancellationToken)
    {
        var entries = new List<StorageListItem>();
        var directories = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { relativePath };
        directories.Enqueue(relativePath);
        var pageCount = 0;
        while (directories.Count > 0)
        {
            var directory = directories.Dequeue();
            var continuationTokens = new HashSet<string>(StringComparer.Ordinal);
            string? continuation = null;
            do
            {
                if (++pageCount > MaximumManifestPages)
                {
                    return new TreeListResult([], new StorageFailure(
                        "manual_transfer.page_limit_exceeded",
                        StorageFailureKind.Validation,
                        "The recursive listing exceeded its bounded page limit."));
                }

                var response = await _storage.ListStorageAsync(new StorageListPageRequest(
                    StorageIpcContract.CurrentVersion,
                    connectionId,
                    directory,
                    PageSize: StorageIpcLimits.MaximumStableIdentityPageSize,
                    ContinuationToken: continuation,
                    Recursive: false), cancellationToken).ConfigureAwait(false);
                if (response.Failure is not null)
                {
                    if (allowNotFound && directory == relativePath &&
                        response.Failure.Category == StorageIpcFailureCategory.NotFound)
                    {
                        return new TreeListResult([], null);
                    }

                    return new TreeListResult([], MapFailure(response.Failure));
                }

                if (!string.Equals(response.RootIdentity, expectedRootIdentity, StringComparison.Ordinal))
                {
                    return new TreeListResult([], new StorageFailure(
                        "manual_transfer.root_identity_changed",
                        StorageFailureKind.Integrity,
                        "The connection root identity changed while the recursive manifest was being built."));
                }

                foreach (var entry in response.Entries)
                {
                    entries.Add(entry);
                    if (entry.Kind is StorageItemKind.Directory or StorageItemKind.Prefix &&
                        visited.Add(entry.RelativePath))
                    {
                        directories.Enqueue(entry.RelativePath);
                    }
                }

                if (entries.Count > MaximumManifestEntries)
                {
                    return new TreeListResult([], new StorageFailure(
                        "manual_transfer.manifest_limit_exceeded",
                        StorageFailureKind.Validation,
                        $"A recursive transfer is limited to {MaximumManifestEntries:N0} files and folders."));
                }

                continuation = response.ContinuationToken;
                if (continuation is not null && !continuationTokens.Add(continuation))
                {
                    return new TreeListResult([], new StorageFailure(
                        "manual_transfer.repeated_page_token",
                        StorageFailureKind.Integrity,
                        "The provider repeated a recursive listing page token."));
                }
            }
            while (continuation is not null);
        }

        return new TreeListResult(entries, null);
    }

    private static string Combine(string parent, string child) =>
        parent.Length == 0 ? child : $"{parent}/{child}";

    private static bool TryGetDescendantSuffix(string root, string path, out string suffix)
    {
        if (string.Equals(root, path, StringComparison.Ordinal))
        {
            suffix = string.Empty;
            return true;
        }

        var prefix = root.Length == 0 ? string.Empty : root + "/";
        if (path.StartsWith(prefix, StringComparison.Ordinal) && path.Length > prefix.Length)
        {
            suffix = path[prefix.Length..];
            return true;
        }

        suffix = string.Empty;
        return false;
    }

    private static bool IsSameAddress(TransferQueueAddress left, TransferQueueAddress right) =>
        left.ConnectionId == right.ConnectionId &&
        string.Equals(left.RootIdentity, right.RootIdentity, StringComparison.Ordinal) &&
        string.Equals(left.RelativePath, right.RelativePath, StringComparison.Ordinal);

    private static ManualTransferEnqueueResult Failure(string code, string message, bool isTransient = false) =>
        new([], [], new StorageFailure(code, StorageFailureKind.Validation, message, isTransient));

    private static StorageFailure MapFailure(StorageIpcFailure failure) => new(
        failure.Code,
        failure.Category switch
        {
            StorageIpcFailureCategory.Validation => StorageFailureKind.Validation,
            StorageIpcFailureCategory.NotFound => StorageFailureKind.NotFound,
            StorageIpcFailureCategory.Conflict => StorageFailureKind.Conflict,
            StorageIpcFailureCategory.Unsupported => StorageFailureKind.Unsupported,
            StorageIpcFailureCategory.Unauthorized => StorageFailureKind.Unauthorized,
            StorageIpcFailureCategory.Unavailable => StorageFailureKind.Unavailable,
            StorageIpcFailureCategory.Timeout => StorageFailureKind.Timeout,
            StorageIpcFailureCategory.Cancelled => StorageFailureKind.Cancelled,
            StorageIpcFailureCategory.Integrity => StorageFailureKind.Integrity,
            StorageIpcFailureCategory.Security => StorageFailureKind.Security,
            StorageIpcFailureCategory.Provider => StorageFailureKind.Provider,
            _ => StorageFailureKind.Unexpected
        },
        failure.Message,
        failure.IsTransient);

    private sealed record TreeListResult(IReadOnlyList<StorageListItem> Entries, StorageFailure? Failure);

    private sealed record ManifestBuildResult(
        IReadOnlyList<TransferEnqueueRequest> Requests,
        IReadOnlyCollection<string> Directories,
        StorageFailure? Failure)
    {
        internal static ManifestBuildResult Fail(StorageFailure failure) => new([], [], failure);
    }
}
