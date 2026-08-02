using StorageHub.Contracts.Ipc;
using StorageHub.Contracts.Results;

namespace StorageHub.Desktop;

public enum PaneTransferContextKind
{
    SavedConnection,
    ThisPc,
    AdHoc,
    ConnectionsHome
}

/// <summary>An immutable pane location captured before a manual transfer is planned.</summary>
public sealed class PaneTransferContext
{
    private PaneTransferContext(
        PaneTransferContextKind kind,
        Guid? connectionId,
        string? rootIdentity,
        string relativePath)
    {
        Kind = kind;
        ConnectionId = connectionId;
        RootIdentity = rootIdentity;
        RelativePath = relativePath;
    }

    public PaneTransferContextKind Kind { get; }
    public Guid? ConnectionId { get; }
    public string? RootIdentity { get; }
    public string RelativePath { get; }

    public static StorageResult<PaneTransferContext> Create(
        PaneTransferContextKind kind,
        Guid? connectionId,
        string? rootIdentity,
        string relativePath)
    {
        if (!Enum.IsDefined(kind))
        {
            return Invalid("manual_transfer.context.kind_invalid", "The pane context kind is invalid.");
        }

        if (!IsBoundedPath(relativePath))
        {
            return Invalid(
                "manual_transfer.context.path_invalid",
                "The pane location is empty, too long, or contains unsupported characters.");
        }

        if (kind == PaneTransferContextKind.SavedConnection)
        {
            if (connectionId is null || connectionId.Value == Guid.Empty || !IsBoundedIdentity(rootIdentity))
            {
                return Invalid(
                    "manual_transfer.context.saved_identity_required",
                    "A saved connection ID and verified root identity are required.");
            }

            if (!RemoteBrowserPath.TryNormalize(relativePath, out var normalized, out _) ||
                !string.Equals(normalized, relativePath, StringComparison.Ordinal))
            {
                return Invalid(
                    "manual_transfer.context.path_not_canonical",
                    "A saved connection pane must use a canonical root-relative path.");
            }
        }
        else if (connectionId is not null || rootIdentity is not null)
        {
            return Invalid(
                "manual_transfer.context.unsaved_identity_invalid",
                "This PC, connection-home, and ad-hoc panes cannot claim a saved connection identity.");
        }

        if (kind == PaneTransferContextKind.ConnectionsHome && relativePath.Length != 0)
        {
            return Invalid(
                "manual_transfer.context.home_path_invalid",
                "The connections-home context cannot identify a storage path.");
        }

        return StorageResult<PaneTransferContext>.Success(new PaneTransferContext(
            kind,
            connectionId,
            rootIdentity,
            relativePath));
    }

    private static bool IsBoundedPath(string? value) => value is not null &&
        value.Length <= StorageIpcLimits.MaximumRelativePathLength &&
        !value.Any(char.IsControl);

    private static bool IsBoundedIdentity(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= StorageIpcLimits.MaximumOpaqueIdentityLength &&
        !value.Any(char.IsControl);

    private static StorageResult<PaneTransferContext> Invalid(string code, string message) =>
        StorageResult<PaneTransferContext>.Fail(new StorageFailure(
            code,
            StorageFailureKind.Validation,
            message));
}

/// <summary>One immutable item identity captured from the pane's unfiltered provider snapshot.</summary>
public sealed class PaneTransferItem
{
    private PaneTransferItem(
        string name,
        string relativePath,
        StorageItemKind kind,
        long? length,
        string? nativeItemId,
        string? versionId,
        string? entityTag)
    {
        Name = name;
        RelativePath = relativePath;
        Kind = kind;
        Length = length;
        NativeItemId = nativeItemId;
        VersionId = versionId;
        EntityTag = entityTag;
    }

    public string Name { get; }
    public string RelativePath { get; }
    public StorageItemKind Kind { get; }
    public long? Length { get; }
    public string? NativeItemId { get; }
    public string? VersionId { get; }
    public string? EntityTag { get; }
    public bool IsContainer => Kind is StorageItemKind.Directory or StorageItemKind.Prefix;
    public bool HasStableIdentity => VersionId is not null || EntityTag is not null;

    public static StorageResult<PaneTransferItem> Create(
        string name,
        string relativePath,
        StorageItemKind kind,
        long? length,
        string? nativeItemId = null,
        string? versionId = null,
        string? entityTag = null)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.Length > StorageIpcLimits.MaximumItemNameLength ||
            name.Any(char.IsControl) ||
            name.Contains('/') ||
            name.Contains('\\'))
        {
            return Invalid("manual_transfer.item.name_invalid", "A selected item has an invalid name.");
        }

        if (string.IsNullOrWhiteSpace(relativePath) ||
            relativePath.Length > StorageIpcLimits.MaximumRelativePathLength ||
            relativePath.Any(char.IsControl) ||
            !Enum.IsDefined(kind) ||
            length is < 0 ||
            kind is StorageItemKind.Directory or StorageItemKind.Prefix && length is not null ||
            !IsBoundedIdentity(nativeItemId) ||
            !IsBoundedIdentity(versionId) ||
            !IsBoundedIdentity(entityTag))
        {
            return Invalid(
                "manual_transfer.item.invalid",
                "A selected item contains an invalid path, kind, length, or provider identity.");
        }

        return StorageResult<PaneTransferItem>.Success(new PaneTransferItem(
            name,
            relativePath,
            kind,
            length,
            nativeItemId,
            versionId,
            entityTag));
    }

    public static StorageResult<PaneTransferItem> Create(StorageListItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Create(
            item.Name,
            item.RelativePath,
            item.Kind,
            item.Size,
            item.NativeItemId,
            item.VersionId,
            item.EntityTag);
    }

    private static bool IsBoundedIdentity(string? value) => value is null ||
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= StorageIpcLimits.MaximumOpaqueIdentityLength &&
        !value.Any(char.IsControl);

    private static StorageResult<PaneTransferItem> Invalid(string code, string message) =>
        StorageResult<PaneTransferItem>.Fail(new StorageFailure(
            code,
            StorageFailureKind.Validation,
            message));
}

public sealed class PaneSelectionSnapshot
{
    public const int MaximumSelectedItems = StorageIpcLimits.MaximumStoragePageSize;

    private PaneSelectionSnapshot(
        PaneTransferContext context,
        IReadOnlyList<PaneTransferItem> items)
    {
        Context = context;
        Items = items;
    }

    public PaneTransferContext Context { get; }
    public IReadOnlyList<PaneTransferItem> Items { get; }

    public static StorageResult<PaneSelectionSnapshot> Create(
        PaneTransferContext context,
        IEnumerable<PaneTransferItem> items)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(items);
        var snapshot = items.ToArray();
        if (snapshot.Length is < 1 or > MaximumSelectedItems || snapshot.Any(static item => item is null))
        {
            return Invalid(
                "manual_transfer.selection.count_invalid",
                $"Select between one and {MaximumSelectedItems} items.");
        }

        if (snapshot.Select(static item => item.RelativePath).Distinct(StringComparer.Ordinal).Count() !=
            snapshot.Length ||
            context.Kind == PaneTransferContextKind.SavedConnection &&
            snapshot.Any(item => !IsCanonicalRemotePath(item.RelativePath) ||
                !IsDirectChild(context.RelativePath, item.RelativePath) ||
                !HasExpectedName(item)))
        {
            return Invalid(
                "manual_transfer.selection.invalid",
                "The selected items are duplicated or do not belong to the captured pane location.");
        }

        return StorageResult<PaneSelectionSnapshot>.Success(new PaneSelectionSnapshot(
            context,
            Array.AsReadOnly(snapshot)));
    }

    internal static bool HasExpectedName(PaneTransferItem item)
    {
        var separator = item.RelativePath.LastIndexOf('/');
        return string.Equals(
            item.Name,
            item.RelativePath[(separator + 1)..],
            StringComparison.Ordinal);
    }

    internal static bool IsCanonicalRemotePath(string path) =>
        RemoteBrowserPath.TryNormalize(path, out var normalized, out _) &&
        string.Equals(path, normalized, StringComparison.Ordinal);

    internal static bool IsDirectChild(string parentPath, string itemPath)
    {
        var prefix = parentPath.Length == 0 ? string.Empty : parentPath + "/";
        if (!itemPath.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = itemPath[prefix.Length..];
        return remainder.Length > 0 && !remainder.Contains('/', StringComparison.Ordinal);
    }

    private static StorageResult<PaneSelectionSnapshot> Invalid(string code, string message) =>
        StorageResult<PaneSelectionSnapshot>.Fail(new StorageFailure(
            code,
            StorageFailureKind.Validation,
            message));
}

public sealed class PaneDestinationSnapshot
{
    public const int MaximumVisibleEntries = RemoteBrowserController.MaximumAccumulatedEntries;

    private PaneDestinationSnapshot(
        PaneTransferContext context,
        IReadOnlyList<PaneTransferItem> entries)
    {
        Context = context;
        Entries = entries;
    }

    public PaneTransferContext Context { get; }
    public IReadOnlyList<PaneTransferItem> Entries { get; }

    public static StorageResult<PaneDestinationSnapshot> Create(
        PaneTransferContext context,
        IEnumerable<PaneTransferItem> entries)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entries);
        var snapshot = entries.ToArray();
        if (snapshot.Length > MaximumVisibleEntries || snapshot.Any(static item => item is null))
        {
            return Invalid(
                "manual_transfer.destination.count_invalid",
                "The destination pane exceeds the safe snapshot limit.");
        }

        if (snapshot.Select(static item => item.RelativePath).Distinct(StringComparer.Ordinal).Count() !=
                snapshot.Length ||
            snapshot.Select(static item => item.Name).Distinct(StringComparer.Ordinal).Count() != snapshot.Length ||
            context.Kind == PaneTransferContextKind.SavedConnection &&
            snapshot.Any(item =>
                !PaneSelectionSnapshot.IsCanonicalRemotePath(item.RelativePath) ||
                !PaneSelectionSnapshot.IsDirectChild(context.RelativePath, item.RelativePath) ||
                !PaneSelectionSnapshot.HasExpectedName(item)))
        {
            return Invalid(
                "manual_transfer.destination.invalid",
                "The destination entries are duplicated or do not belong to the captured pane location.");
        }

        return StorageResult<PaneDestinationSnapshot>.Success(new PaneDestinationSnapshot(
            context,
            Array.AsReadOnly(snapshot)));
    }

    private static StorageResult<PaneDestinationSnapshot> Invalid(string code, string message) =>
        StorageResult<PaneDestinationSnapshot>.Fail(new StorageFailure(
            code,
            StorageFailureKind.Validation,
            message));
}

public sealed class ManualTransferPlan
{
    internal ManualTransferPlan(IReadOnlyList<TransferEnqueueRequest> requests)
    {
        Requests = requests;
    }

    public IReadOnlyList<TransferEnqueueRequest> Requests { get; }
}

public sealed class ManualTransferEnqueueResult
{
    internal ManualTransferEnqueueResult(
        IReadOnlyList<TransferEnqueueResponse> accepted,
        IReadOnlyList<Guid> ambiguousTransferIds,
        StorageFailure? failure)
    {
        Accepted = accepted;
        AmbiguousTransferIds = ambiguousTransferIds;
        Failure = failure;
    }

    public IReadOnlyList<TransferEnqueueResponse> Accepted { get; }
    /// <summary>
    /// Stable IDs whose enqueue acknowledgement was lost. Callers can look these exact IDs up;
    /// retrying the same operation must reuse them rather than create replacement IDs.
    /// </summary>
    public IReadOnlyList<Guid> AmbiguousTransferIds { get; }
    public StorageFailure? Failure { get; }
    public bool HasAmbiguity => AmbiguousTransferIds.Count > 0;
    public bool IsSuccess => Failure is null && !HasAmbiguity;
    public bool IsPartial => Accepted.Count > 0 && (Failure is not null || HasAmbiguity);
}

public sealed class ManualTransfersEnqueuedEventArgs : EventArgs
{
    public ManualTransfersEnqueuedEventArgs(IEnumerable<Guid> transferIds)
        : this(transferIds, [])
    {
    }

    public ManualTransfersEnqueuedEventArgs(
        IEnumerable<Guid> acceptedTransferIds,
        IEnumerable<Guid> ambiguousTransferIds)
    {
        ArgumentNullException.ThrowIfNull(acceptedTransferIds);
        ArgumentNullException.ThrowIfNull(ambiguousTransferIds);
        var accepted = acceptedTransferIds.ToArray();
        var ambiguous = ambiguousTransferIds.ToArray();
        var all = accepted.Concat(ambiguous).Distinct().ToArray();
        if (all.Length == 0 ||
            accepted.Any(static id => id == Guid.Empty) ||
            ambiguous.Any(static id => id == Guid.Empty) ||
            accepted.Length != accepted.Distinct().Count() ||
            ambiguous.Length != ambiguous.Distinct().Count() ||
            all.Length != accepted.Length + ambiguous.Length)
        {
            throw new ArgumentException(
                "At least one unique valid accepted or ambiguous transfer ID is required.");
        }

        AcceptedTransferIds = Array.AsReadOnly(accepted);
        AmbiguousTransferIds = Array.AsReadOnly(ambiguous);
        TransferIds = AcceptedTransferIds;
        RefreshTransferIds = Array.AsReadOnly(all);
    }

    /// <summary>Durably acknowledged transfer IDs retained for existing queue observers.</summary>
    public IReadOnlyList<Guid> TransferIds { get; }
    public IReadOnlyList<Guid> AcceptedTransferIds { get; }
    public IReadOnlyList<Guid> AmbiguousTransferIds { get; }
    /// <summary>All IDs worth querying, including acknowledgements that remain ambiguous.</summary>
    public IReadOnlyList<Guid> RefreshTransferIds { get; }
}

public sealed class ManualTransferEnqueueAmbiguousException : OperationCanceledException
{
    internal ManualTransferEnqueueAmbiguousException(
        IReadOnlyList<Guid> acceptedTransferIds,
        IReadOnlyList<Guid> ambiguousTransferIds,
        CancellationToken cancellationToken)
        : base(
            "Manual transfer enqueue was cancelled after an acknowledgement became ambiguous.",
            innerException: null,
            cancellationToken)
    {
        AcceptedTransferIds = acceptedTransferIds;
        AmbiguousTransferIds = ambiguousTransferIds;
    }

    public IReadOnlyList<Guid> AcceptedTransferIds { get; }
    public IReadOnlyList<Guid> AmbiguousTransferIds { get; }
}

/// <summary>Builds and submits bounded, root-fenced transfers from immutable pane snapshots.</summary>
public sealed class ManualTransferController : IAsyncDisposable
{
    private static readonly TimeSpan AmbiguousEnqueueRetryTimeout = TimeSpan.FromSeconds(3);
    private readonly ITransferQueueAgentClient _client;
    private readonly bool _ownsClient;
    private int _disposed;

    public ManualTransferController()
        : this(new NamedPipeTransferQueueAgentClient(), ownsClient: true)
    {
    }

    public ManualTransferController(ITransferQueueAgentClient client, bool ownsClient = false)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ownsClient = ownsClient;
    }

    /// <summary>Signals queue surfaces after an accepted or acknowledgement-ambiguous request.</summary>
    public event EventHandler<ManualTransfersEnqueuedEventArgs>? TransfersEnqueued;

    public StorageResult<ManualTransferPlan> BuildPlan(
        PaneSelectionSnapshot source,
        PaneDestinationSnapshot destination,
        TransferQueueOperation operation,
        TransferQueueVerification verification = TransferQueueVerification.StrongHashWhenAvailable,
        int priority = 0)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (!Enum.IsDefined(operation) ||
            !Enum.IsDefined(verification) ||
            priority is < TransferQueueIpcLimits.MinimumPriority or > TransferQueueIpcLimits.MaximumPriority)
        {
            return InvalidPlan(
                "manual_transfer.options.invalid",
                "The transfer operation, verification policy, or priority is invalid.");
        }

        var sourceContextFailure = RequireSavedContext(source.Context, source: true);
        if (sourceContextFailure is not null)
        {
            return StorageResult<ManualTransferPlan>.Fail(sourceContextFailure);
        }

        var destinationContextFailure = RequireSavedContext(destination.Context, source: false);
        if (destinationContextFailure is not null)
        {
            return StorageResult<ManualTransferPlan>.Fail(destinationContextFailure);
        }

        var destinationByName = destination.Entries.ToDictionary(
            static item => item.Name,
            StringComparer.Ordinal);
        var requests = new TransferEnqueueRequest[source.Items.Count];
        var destinationPaths = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < source.Items.Count; index++)
        {
            var item = source.Items[index];
            if (item.Kind != StorageItemKind.File || item.IsContainer)
            {
                return InvalidPlan(
                    "manual_transfer.recursion_not_supported",
                    "Manual transfer currently accepts files only; directory recursion is not yet represented safely.");
            }

            if (operation == TransferQueueOperation.Move && !item.HasStableIdentity)
            {
                return InvalidPlan(
                    "manual_transfer.move.source_identity_required",
                    "Moving a file requires its captured version ID or entity tag.");
            }

            var destinationPath = destination.Context.RelativePath.Length == 0
                ? item.Name
                : destination.Context.RelativePath + "/" + item.Name;
            if (!RemoteBrowserPath.TryNormalize(destinationPath, out var normalizedDestinationPath, out _) ||
                !string.Equals(destinationPath, normalizedDestinationPath, StringComparison.Ordinal) ||
                !destinationPaths.Add(destinationPath))
            {
                return InvalidPlan(
                    "manual_transfer.destination.path_invalid",
                    "The selected files do not produce unique canonical destination paths.");
            }

            destinationByName.TryGetValue(item.Name, out var existing);
            if (existing is { Kind: not StorageItemKind.File })
            {
                return InvalidPlan(
                    "manual_transfer.destination.container_conflict",
                    "A destination folder or non-file item already uses one of the selected names.");
            }

            if (existing is not null && (!existing.HasStableIdentity || !item.HasStableIdentity))
            {
                return InvalidPlan(
                    "manual_transfer.overwrite.identity_required",
                    "Replacing an existing file requires captured source and destination version or entity-tag evidence.");
            }

            var sourceAddress = new TransferQueueAddress(
                source.Context.ConnectionId!.Value,
                source.Context.RootIdentity!,
                item.RelativePath,
                item.NativeItemId,
                item.VersionId,
                item.EntityTag);
            var destinationAddress = new TransferQueueAddress(
                destination.Context.ConnectionId!.Value,
                destination.Context.RootIdentity!,
                destinationPath,
                existing?.NativeItemId,
                existing?.VersionId,
                existing?.EntityTag);
            var request = new TransferEnqueueRequest(
                TransferQueueIpcContract.CurrentVersion,
                Guid.NewGuid(),
                operation,
                sourceAddress,
                destinationAddress,
                item.Length,
                verification,
                priority,
                existing?.VersionId,
                existing?.EntityTag);
            if (!request.HasValidBounds)
            {
                return StorageResult<ManualTransferPlan>.Fail(new StorageFailure(
                    "manual_transfer.request.invalid",
                    StorageFailureKind.Integrity,
                    "A validated pane snapshot produced an invalid transfer request."));
            }

            if (sourceAddress.ConnectionId == destinationAddress.ConnectionId &&
                string.Equals(sourceAddress.RootIdentity, destinationAddress.RootIdentity, StringComparison.Ordinal) &&
                string.Equals(sourceAddress.RelativePath, destinationAddress.RelativePath, StringComparison.Ordinal))
            {
                return InvalidPlan(
                    "manual_transfer.same_item",
                    "A file cannot be copied or moved onto itself.");
            }

            requests[index] = request;
        }

        return StorageResult<ManualTransferPlan>.Success(new ManualTransferPlan(
            Array.AsReadOnly(requests)));
    }

    public async Task<ManualTransferEnqueueResult> EnqueueAsync(
        PaneSelectionSnapshot source,
        PaneDestinationSnapshot destination,
        TransferQueueOperation operation,
        TransferQueueVerification verification = TransferQueueVerification.StrongHashWhenAvailable,
        int priority = 0,
        CancellationToken cancellationToken = default)
    {
        var plan = BuildPlan(source, destination, operation, verification, priority);
        if (plan.IsFailure)
        {
            return new ManualTransferEnqueueResult([], [], plan.Error);
        }

        var accepted = new List<TransferEnqueueResponse>(plan.Value.Requests.Count);
        TransferEnqueueRequest? attemptedRequest = null;
        try
        {
            foreach (var request in plan.Value.Requests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attemptedRequest = request;
                TransferEnqueueResponse response;
                var retried = false;
                try
                {
                    response = await _client.EnqueueAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (IsRecoverableTransportFailure(error))
                {
                    retried = true;
                    var recovered = await RetryAmbiguousEnqueueOnceAsync(
                        request,
                        cancellationToken).ConfigureAwait(false);
                    if (recovered is null)
                    {
                        return Complete(
                            accepted,
                            [request.TransferId],
                            AmbiguousEnqueueFailure());
                    }

                    response = recovered;
                }

                var responseFailure = ValidateResponse(request, response);
                if (responseFailure?.Code == "manual_transfer.agent_response_invalid" && !retried)
                {
                    var recovered = await RetryAmbiguousEnqueueOnceAsync(
                        request,
                        cancellationToken).ConfigureAwait(false);
                    if (recovered is null)
                    {
                        return Complete(accepted, [request.TransferId], AmbiguousEnqueueFailure());
                    }

                    response = recovered;
                    responseFailure = ValidateResponse(request, response);
                }

                if (responseFailure?.Code == "manual_transfer.agent_response_invalid")
                {
                    return Complete(accepted, [request.TransferId], AmbiguousEnqueueFailure());
                }

                attemptedRequest = null;
                if (responseFailure is not null)
                {
                    return Complete(accepted, [], responseFailure);
                }

                accepted.Add(response);
            }

            return Complete(accepted, [], failure: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var acceptedIds = accepted.Select(static response => response.TransferId).ToArray();
            var ambiguousIds = attemptedRequest is null ? [] : new[] { attemptedRequest.TransferId };
            SignalQueueRefresh(acceptedIds, ambiguousIds);
            if (ambiguousIds.Length > 0)
            {
                throw new ManualTransferEnqueueAmbiguousException(
                    Array.AsReadOnly(acceptedIds),
                    Array.AsReadOnly(ambiguousIds),
                    cancellationToken);
            }

            throw;
        }
        catch (Exception error) when (
            IsRecoverableTransportFailure(error))
        {
            return Complete(
                accepted,
                attemptedRequest is null ? [] : [attemptedRequest.TransferId],
                new StorageFailure(
                    attemptedRequest is null
                        ? "manual_transfer.agent_unavailable"
                        : "manual_transfer.enqueue_ambiguous",
                    StorageFailureKind.Unavailable,
                    attemptedRequest is null
                        ? "The background agent could not enqueue the transfer."
                        : "The background agent did not confirm whether the transfer was durably enqueued.",
                    isTransient: true));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_ownsClient)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static StorageFailure? RequireSavedContext(PaneTransferContext context, bool source)
    {
        if (context.Kind == PaneTransferContextKind.SavedConnection &&
            context.ConnectionId is { } connectionId && connectionId != Guid.Empty &&
            !string.IsNullOrWhiteSpace(context.RootIdentity))
        {
            return null;
        }

        return new StorageFailure(
            source
                ? "manual_transfer.source.saved_connection_required"
                : "manual_transfer.destination.saved_connection_required",
            StorageFailureKind.Validation,
            source
                ? "Queue transfers require the source pane to use a saved connection; This PC and ad-hoc locations cannot be queued."
                : "Queue transfers require the destination pane to use a saved connection; This PC and ad-hoc locations cannot be queued.");
    }

    private ManualTransferEnqueueResult Complete(
        IReadOnlyCollection<TransferEnqueueResponse> accepted,
        IReadOnlyCollection<Guid> ambiguousTransferIds,
        StorageFailure? failure)
    {
        var snapshot = Array.AsReadOnly(accepted.ToArray());
        var ambiguousIds = ambiguousTransferIds.ToArray();
        var ambiguousSnapshot = Array.AsReadOnly(ambiguousIds);
        SignalQueueRefresh(
            snapshot.Select(static response => response.TransferId).ToArray(),
            ambiguousIds);

        return new ManualTransferEnqueueResult(snapshot, ambiguousSnapshot, failure);
    }

    private void SignalQueueRefresh(
        Guid[] acceptedTransferIds,
        Guid[] ambiguousTransferIds)
    {
        if (acceptedTransferIds.Length == 0 && ambiguousTransferIds.Length == 0)
        {
            return;
        }

        var handlers = TransfersEnqueued;
        if (handlers is null)
        {
            return;
        }

        var args = new ManualTransfersEnqueuedEventArgs(acceptedTransferIds, ambiguousTransferIds);
        foreach (EventHandler<ManualTransfersEnqueuedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception)
            {
                // Queue notifications are advisory. A disposed UI observer or extension must
                // never turn a durable agent acceptance into an apparent enqueue failure.
            }
        }
    }

    private async Task<TransferEnqueueResponse?> RetryAmbiguousEnqueueOnceAsync(
        TransferEnqueueRequest request,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(AmbiguousEnqueueRetryTimeout);
        try
        {
            // TransferId is the queue's idempotency key. Reusing the exact request can recover a
            // lost acknowledgement without creating a second hidden transfer identity.
            return await _client.EnqueueAsync(request, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception error) when (IsRecoverableTransportFailure(error))
        {
            return null;
        }
    }

    private static bool IsRecoverableTransportFailure(Exception error) => error is
        IOException or
        InvalidDataException or
        InvalidOperationException or
        TimeoutException or
        UnauthorizedAccessException;

    private static StorageFailure AmbiguousEnqueueFailure() => new(
        "manual_transfer.enqueue_ambiguous",
        StorageFailureKind.Unavailable,
        "The background agent did not confirm whether the transfer was durably enqueued. " +
        "Refresh the queue using the returned transfer ID before trying again.",
        isTransient: true);

    private static StorageFailure? ValidateResponse(
        TransferEnqueueRequest request,
        TransferEnqueueResponse response)
    {
        if (response is null ||
            response.ContractVersion != TransferQueueIpcContract.CurrentVersion ||
            response.TransferId != request.TransferId ||
            response.AlreadyExisted && !response.Accepted ||
            response.Accepted &&
                (response.Transfer is null ||
                 response.Failure is not null ||
                 response.Transfer.TransferId != request.TransferId ||
                 response.Transfer.Operation != request.Operation ||
                 response.Transfer.SourceConnectionId != request.Source.ConnectionId ||
                 !string.Equals(
                     response.Transfer.SourcePath,
                     request.Source.RelativePath,
                     StringComparison.Ordinal) ||
                 response.Transfer.DestinationConnectionId != request.Destination.ConnectionId ||
                 !string.Equals(
                     response.Transfer.DestinationPath,
                     request.Destination.RelativePath,
                     StringComparison.Ordinal)) ||
            !response.Accepted && (response.Failure is null || response.Transfer is not null))
        {
            return new StorageFailure(
                "manual_transfer.agent_response_invalid",
                StorageFailureKind.Integrity,
                "The background agent returned an invalid enqueue response.");
        }

        if (response.Accepted)
        {
            return null;
        }

        return new StorageFailure(
            response.Failure!.Code,
            MapFailureKind(response.Failure.Category),
            response.Failure.Message,
            response.Failure.IsTransient,
            response.Failure.Code);
    }

    private static StorageFailureKind MapFailureKind(StorageIpcFailureCategory category) => category switch
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
    };

    private static StorageResult<ManualTransferPlan> InvalidPlan(string code, string message) =>
        StorageResult<ManualTransferPlan>.Fail(new StorageFailure(
            code,
            StorageFailureKind.Validation,
            message));
}
