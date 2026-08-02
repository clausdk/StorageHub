using StorageHub.Contracts.Ipc;
using StorageHub.Contracts.Results;

namespace StorageHub.Desktop.Tests;

public sealed class ManualTransferControllerTests
{
    [Fact]
    public void BuildPlanPreservesSourceAndExactDestinationIdentityForOverwrite()
    {
        var sourceId = Guid.NewGuid();
        var destinationId = Guid.NewGuid();
        var sourceItem = Item(
            "report.bin",
            "source/report.bin",
            length: 42,
            nativeItemId: "source-native",
            versionId: "source-version",
            entityTag: "source-etag");
        var existing = Item(
            "report.bin",
            "destination/report.bin",
            length: 12,
            nativeItemId: "destination-native",
            versionId: "destination-version",
            entityTag: "destination-etag");
        var selection = Selection(Saved(sourceId, "source-root", "source"), sourceItem);
        var destination = Destination(
            Saved(destinationId, "destination-root", "destination"),
            existing);
        var controller = new ManualTransferController(new FakeTransferClient());

        var result = controller.BuildPlan(selection, destination, TransferQueueOperation.Copy);

        Assert.True(result.IsSuccess);
        var request = Assert.Single(result.Value.Requests);
        Assert.Equal(42, request.ExpectedLength);
        Assert.Equal("source-native", request.Source.NativeItemId);
        Assert.Equal("source-version", request.Source.VersionId);
        Assert.Equal("source-etag", request.Source.EntityTag);
        Assert.Equal("destination/report.bin", request.Destination.RelativePath);
        Assert.Equal("destination-native", request.Destination.NativeItemId);
        Assert.Equal("destination-version", request.Destination.VersionId);
        Assert.Equal("destination-etag", request.Destination.EntityTag);
        Assert.Equal("destination-version", request.ExpectedDestinationVersionId);
        Assert.Equal("destination-etag", request.ExpectedDestinationEntityTag);
    }

    [Fact]
    public void BuildPlanLeavesAbsentDestinationCreateOnly()
    {
        var selection = Selection(
            Saved(Guid.NewGuid(), "source-root", "source"),
            Item("new.txt", "source/new.txt", length: 5));
        var destination = Destination(
            Saved(Guid.NewGuid(), "destination-root", "destination"));
        var controller = new ManualTransferController(new FakeTransferClient());

        var result = controller.BuildPlan(selection, destination, TransferQueueOperation.Copy);

        Assert.True(result.IsSuccess);
        var request = Assert.Single(result.Value.Requests);
        Assert.Null(request.Destination.NativeItemId);
        Assert.Null(request.Destination.VersionId);
        Assert.Null(request.Destination.EntityTag);
        Assert.Null(request.ExpectedDestinationVersionId);
        Assert.Null(request.ExpectedDestinationEntityTag);
    }

    [Fact]
    public async Task ThisPcAndAdHocContextsFailClearlyBeforeQueueIo()
    {
        var client = new FakeTransferClient();
        var controller = new ManualTransferController(client);
        var thisPc = Context(
            PaneTransferContextKind.ThisPc,
            connectionId: null,
            rootIdentity: null,
            @"C:\Data");
        var localSelection = Selection(thisPc, Item("file.txt", @"C:\Data\file.txt", length: 1));
        var savedDestination = Destination(Saved(Guid.NewGuid(), "destination-root", "destination"));

        var sourceFailure = await controller.EnqueueAsync(
            localSelection,
            savedDestination,
            TransferQueueOperation.Copy);

        Assert.False(sourceFailure.IsSuccess);
        Assert.Equal("manual_transfer.source.saved_connection_required", sourceFailure.Failure?.Code);
        Assert.Contains("This PC", sourceFailure.Failure?.Message, StringComparison.Ordinal);
        Assert.Empty(client.Requests);

        var savedSelection = Selection(
            Saved(Guid.NewGuid(), "source-root", "source"),
            Item("file.txt", "source/file.txt", length: 1));
        var adHoc = Context(
            PaneTransferContextKind.AdHoc,
            connectionId: null,
            rootIdentity: null,
            "temporary.example");

        var destinationFailure = controller.BuildPlan(
            savedSelection,
            Destination(adHoc),
            TransferQueueOperation.Copy);

        Assert.True(destinationFailure.IsFailure);
        Assert.Equal("manual_transfer.destination.saved_connection_required", destinationFailure.Error.Code);
    }

    [Fact]
    public void ContainersAndUnfencedMovesAreRejected()
    {
        var source = Saved(Guid.NewGuid(), "source-root", "source");
        var destination = Destination(Saved(Guid.NewGuid(), "destination-root", "destination"));
        var controller = new ManualTransferController(new FakeTransferClient());
        var directory = Selection(source, Item(
            "folder",
            "source/folder",
            kind: StorageItemKind.Directory));

        var recursive = controller.BuildPlan(directory, destination, TransferQueueOperation.Copy);
        var move = controller.BuildPlan(
            Selection(source, Item("file.txt", "source/file.txt", length: 1)),
            destination,
            TransferQueueOperation.Move);

        Assert.True(recursive.IsFailure);
        Assert.Equal("manual_transfer.recursion_not_supported", recursive.Error.Code);
        Assert.True(move.IsFailure);
        Assert.Equal("manual_transfer.move.source_identity_required", move.Error.Code);
    }

    [Fact]
    public void ExistingDestinationRequiresBothSourceAndDestinationIdentityEvidence()
    {
        var sourceContext = Saved(Guid.NewGuid(), "source-root", "source");
        var destinationContext = Saved(Guid.NewGuid(), "destination-root", "destination");
        var controller = new ManualTransferController(new FakeTransferClient());
        var unfencedSource = Selection(
            sourceContext,
            Item("file.txt", "source/file.txt", length: 1));
        var fencedDestination = Destination(
            destinationContext,
            Item(
                "file.txt",
                "destination/file.txt",
                length: 1,
                versionId: "destination-version"));

        var sourceFailure = controller.BuildPlan(
            unfencedSource,
            fencedDestination,
            TransferQueueOperation.Copy);

        Assert.True(sourceFailure.IsFailure);
        Assert.Equal("manual_transfer.overwrite.identity_required", sourceFailure.Error.Code);

        var fencedSource = Selection(
            sourceContext,
            Item(
                "file.txt",
                "source/file.txt",
                length: 1,
                entityTag: "source-etag"));
        var unfencedDestination = Destination(
            destinationContext,
            Item("file.txt", "destination/file.txt", length: 1));

        var destinationFailure = controller.BuildPlan(
            fencedSource,
            unfencedDestination,
            TransferQueueOperation.Copy);

        Assert.True(destinationFailure.IsFailure);
        Assert.Equal("manual_transfer.overwrite.identity_required", destinationFailure.Error.Code);
    }

    [Fact]
    public void SelectionSnapshotIsBoundedAndImmutable()
    {
        var context = Saved(Guid.NewGuid(), "root", string.Empty);
        var items = new List<PaneTransferItem> { Item("one.txt", "one.txt", length: 1) };

        var snapshot = PaneSelectionSnapshot.Create(context, items);
        items.Clear();
        var oversized = PaneSelectionSnapshot.Create(
            context,
            Enumerable.Range(0, PaneSelectionSnapshot.MaximumSelectedItems + 1)
                .Select(index => Item($"file-{index}.txt", $"file-{index}.txt", length: index)));

        Assert.True(snapshot.IsSuccess);
        Assert.Single(snapshot.Value.Items);
        Assert.True(oversized.IsFailure);
        Assert.Equal("manual_transfer.selection.count_invalid", oversized.Error.Code);
    }

    [Fact]
    public void SavedPaneSnapshotsRejectNonCanonicalOrMismatchedItemAddresses()
    {
        var source = Saved(Guid.NewGuid(), "root", "source");
        var destination = Saved(Guid.NewGuid(), "root", "destination");

        var nonCanonical = PaneSelectionSnapshot.Create(
            source,
            [Item(".", "source/.", kind: StorageItemKind.File, length: 1)]);
        var mismatched = PaneDestinationSnapshot.Create(
            destination,
            [Item("claimed.txt", "destination/actual.txt", length: 1, versionId: "version-1")]);

        Assert.True(nonCanonical.IsFailure);
        Assert.Equal("manual_transfer.selection.invalid", nonCanonical.Error.Code);
        Assert.True(mismatched.IsFailure);
        Assert.Equal("manual_transfer.destination.invalid", mismatched.Error.Code);
    }

    [Fact]
    public async Task EnqueueSubmitsEveryRequestAndSignalsQueueRefreshOnce()
    {
        var client = new FakeTransferClient
        {
            Handler = static request => Accepted(request)
        };
        await using var controller = new ManualTransferController(client);
        ManualTransfersEnqueuedEventArgs? notification = null;
        var notificationCount = 0;
        controller.TransfersEnqueued += (_, args) =>
        {
            notification = args;
            notificationCount++;
        };
        var selection = Selection(
            Saved(Guid.NewGuid(), "source-root", "source"),
            Item("one.txt", "source/one.txt", length: 1),
            Item("two.txt", "source/two.txt", length: 2));
        var destination = Destination(Saved(Guid.NewGuid(), "destination-root", "destination"));

        var result = await controller.EnqueueAsync(
            selection,
            destination,
            TransferQueueOperation.Copy);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Accepted.Count);
        Assert.Equal(2, client.Requests.Count);
        Assert.All(client.Requests, request =>
            Assert.Equal(TransferQueueVerification.StrongHashWhenAvailable, request.Verification));
        Assert.Equal(1, notificationCount);
        Assert.Equal(client.Requests.Select(static request => request.TransferId), notification?.TransferIds);
    }

    [Fact]
    public async Task LostAcknowledgementRetriesExactlyOnceWithTheSameTransferId()
    {
        var invocation = 0;
        var client = new FakeTransferClient
        {
            Handler = request => ++invocation == 1
                ? throw new IOException("lost response")
                : Accepted(request) with { AlreadyExisted = true }
        };
        await using var controller = new ManualTransferController(client);
        var selection = Selection(
            Saved(Guid.NewGuid(), "source-root", "source"),
            Item("one.txt", "source/one.txt", length: 1));

        var result = await controller.EnqueueAsync(
            selection,
            Destination(Saved(Guid.NewGuid(), "destination-root", "destination")),
            TransferQueueOperation.Copy);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.AmbiguousTransferIds);
        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(client.Requests[0], client.Requests[1]);
        Assert.Equal(client.Requests[0].TransferId, Assert.Single(result.Accepted).TransferId);
    }

    [Fact]
    public async Task TwoLostAcknowledgementsReturnTheExplicitOriginalAmbiguityId()
    {
        var client = new FakeTransferClient
        {
            Handler = _ => throw new IOException("transport secret=password")
        };
        await using var controller = new ManualTransferController(client);
        ManualTransfersEnqueuedEventArgs? notification = null;
        controller.TransfersEnqueued += (_, args) => notification = args;
        var selection = Selection(
            Saved(Guid.NewGuid(), "source-root", "source"),
            Item("one.txt", "source/one.txt", length: 1));

        var result = await controller.EnqueueAsync(
            selection,
            Destination(Saved(Guid.NewGuid(), "destination-root", "destination")),
            TransferQueueOperation.Copy);

        Assert.False(result.IsSuccess);
        Assert.True(result.HasAmbiguity);
        Assert.Equal("manual_transfer.enqueue_ambiguous", result.Failure?.Code);
        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(client.Requests[0], client.Requests[1]);
        var ambiguousId = Assert.Single(result.AmbiguousTransferIds);
        Assert.Equal(client.Requests[0].TransferId, ambiguousId);
        Assert.Equal(ambiguousId, Assert.Single(notification!.AmbiguousTransferIds));
        Assert.Empty(notification.AcceptedTransferIds);
        Assert.Empty(notification.TransferIds);
        Assert.Equal(ambiguousId, Assert.Single(notification.RefreshTransferIds));
        Assert.DoesNotContain("password", result.Failure?.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ThrowingObserversCannotMaskDurableAcceptanceOrOtherObservers()
    {
        var client = new FakeTransferClient();
        await using var controller = new ManualTransferController(client);
        var safeObserverCount = 0;
        controller.TransfersEnqueued += (_, _) => throw new ObjectDisposedException("closed window");
        controller.TransfersEnqueued += (_, _) => safeObserverCount++;
        var selection = Selection(
            Saved(Guid.NewGuid(), "source-root", "source"),
            Item("one.txt", "source/one.txt", length: 1));

        var result = await controller.EnqueueAsync(
            selection,
            Destination(Saved(Guid.NewGuid(), "destination-root", "destination")),
            TransferQueueOperation.Copy);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Accepted);
        Assert.Equal(1, safeObserverCount);
    }

    [Fact]
    public async Task AgentRejectionReportsPartialSuccessAndStillSignalsQueueRefresh()
    {
        var invocation = 0;
        var client = new FakeTransferClient
        {
            Handler = request => ++invocation == 1
                ? Accepted(request)
                : new TransferEnqueueResponse(
                    TransferQueueIpcContract.CurrentVersion,
                    request.TransferId,
                    Accepted: false,
                    AlreadyExisted: false,
                    Failure: new StorageIpcFailure(
                        "transfer.destination.changed",
                        StorageIpcFailureCategory.Conflict,
                        "The destination changed.",
                        IsTransient: false))
        };
        await using var controller = new ManualTransferController(client);
        var notificationCount = 0;
        controller.TransfersEnqueued += (_, _) => notificationCount++;
        var selection = Selection(
            Saved(Guid.NewGuid(), "source-root", "source"),
            Item("one.txt", "source/one.txt", length: 1),
            Item("two.txt", "source/two.txt", length: 2));

        var result = await controller.EnqueueAsync(
            selection,
            Destination(Saved(Guid.NewGuid(), "destination-root", "destination")),
            TransferQueueOperation.Copy);

        Assert.False(result.IsSuccess);
        Assert.True(result.IsPartial);
        Assert.Single(result.Accepted);
        Assert.Equal(StorageFailureKind.Conflict, result.Failure?.Kind);
        Assert.Equal(1, notificationCount);
    }

    [Fact]
    public async Task CancellationAfterAnAcceptedRequestStillSignalsQueueRefresh()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new FakeTransferClient
        {
            Handler = request =>
            {
                cancellation.Cancel();
                return Accepted(request);
            }
        };
        await using var controller = new ManualTransferController(client);
        var notificationCount = 0;
        controller.TransfersEnqueued += (_, args) =>
        {
            Assert.Single(args.TransferIds);
            notificationCount++;
        };
        var selection = Selection(
            Saved(Guid.NewGuid(), "source-root", "source"),
            Item("one.txt", "source/one.txt", length: 1),
            Item("two.txt", "source/two.txt", length: 2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.EnqueueAsync(
            selection,
            Destination(Saved(Guid.NewGuid(), "destination-root", "destination")),
            TransferQueueOperation.Copy,
            cancellationToken: cancellation.Token));

        Assert.Single(client.Requests);
        Assert.Equal(1, notificationCount);
    }

    [Fact]
    public async Task CancellationDuringEnqueueCarriesTheAttemptedTransferIdAsAmbiguous()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeTransferClient
        {
            AsyncHandler = async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable");
            }
        };
        await using var controller = new ManualTransferController(client);
        ManualTransfersEnqueuedEventArgs? notification = null;
        controller.TransfersEnqueued += (_, args) => notification = args;
        using var cancellation = new CancellationTokenSource();
        var selection = Selection(
            Saved(Guid.NewGuid(), "source-root", "source"),
            Item("one.txt", "source/one.txt", length: 1));
        var enqueue = controller.EnqueueAsync(
            selection,
            Destination(Saved(Guid.NewGuid(), "destination-root", "destination")),
            TransferQueueOperation.Copy,
            cancellationToken: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        var error = await Assert.ThrowsAsync<ManualTransferEnqueueAmbiguousException>(() => enqueue);

        var request = Assert.Single(client.Requests);
        Assert.Equal(request.TransferId, Assert.Single(error.AmbiguousTransferIds));
        Assert.Empty(error.AcceptedTransferIds);
        Assert.Equal(request.TransferId, Assert.Single(notification!.AmbiguousTransferIds));
        Assert.Empty(notification.TransferIds);
    }

    [Fact]
    public async Task TransportDetailsAreSanitized()
    {
        var client = new FakeTransferClient
        {
            Handler = _ => throw new IOException("pipe failed password=hunter2")
        };
        await using var controller = new ManualTransferController(client);
        var selection = Selection(
            Saved(Guid.NewGuid(), "source-root", "source"),
            Item("file.txt", "source/file.txt", length: 1));

        var result = await controller.EnqueueAsync(
            selection,
            Destination(Saved(Guid.NewGuid(), "destination-root", "destination")),
            TransferQueueOperation.Copy);

        Assert.False(result.IsSuccess);
        Assert.Equal("manual_transfer.enqueue_ambiguous", result.Failure?.Code);
        Assert.Single(result.AmbiguousTransferIds);
        Assert.DoesNotContain("hunter2", result.Failure?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnauthorizedDetailsAreSanitizedByController()
    {
        var client = new FakeTransferClient
        {
            Handler = _ => throw new UnauthorizedAccessException("ACL user=secret-principal")
        };
        await using var controller = new ManualTransferController(client);
        var selection = Selection(
            Saved(Guid.NewGuid(), "source-root", "source"),
            Item("file.txt", "source/file.txt", length: 1));

        var result = await controller.EnqueueAsync(
            selection,
            Destination(Saved(Guid.NewGuid(), "destination-root", "destination")),
            TransferQueueOperation.Copy);

        Assert.Equal("manual_transfer.enqueue_ambiguous", result.Failure?.Code);
        Assert.DoesNotContain("secret-principal", result.Failure?.Message, StringComparison.Ordinal);
        Assert.Single(result.AmbiguousTransferIds);
    }

    private static PaneTransferContext Saved(Guid connectionId, string rootIdentity, string path) =>
        Context(PaneTransferContextKind.SavedConnection, connectionId, rootIdentity, path);

    private static PaneTransferContext Context(
        PaneTransferContextKind kind,
        Guid? connectionId,
        string? rootIdentity,
        string path)
    {
        var result = PaneTransferContext.Create(kind, connectionId, rootIdentity, path);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private static PaneTransferItem Item(
        string name,
        string path,
        StorageItemKind kind = StorageItemKind.File,
        long? length = null,
        string? nativeItemId = null,
        string? versionId = null,
        string? entityTag = null)
    {
        var result = PaneTransferItem.Create(
            name,
            path,
            kind,
            length,
            nativeItemId,
            versionId,
            entityTag);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private static PaneSelectionSnapshot Selection(
        PaneTransferContext context,
        params PaneTransferItem[] items)
    {
        var result = PaneSelectionSnapshot.Create(context, items);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private static PaneDestinationSnapshot Destination(
        PaneTransferContext context,
        params PaneTransferItem[] items)
    {
        var result = PaneDestinationSnapshot.Create(context, items);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private static TransferEnqueueResponse Accepted(TransferEnqueueRequest request) => new(
        TransferQueueIpcContract.CurrentVersion,
        request.TransferId,
        Accepted: true,
        AlreadyExisted: false,
        Transfer: new TransferQueueSummary(
            request.TransferId,
            request.Operation,
            request.Source.ConnectionId,
            request.Source.RelativePath,
            request.Destination.ConnectionId,
            request.Destination.RelativePath,
            TransferQueueState.Pending,
            Revision: 0,
            Attempt: 0,
            request.Priority,
            request.ExpectedLength,
            ProgressBytes: 0,
            DateTimeOffset.UtcNow,
            RetryAvailableUtc: null,
            ErrorCode: null,
            ErrorSummary: null,
            CanCancel: true,
            CanRetry: false,
            NeedsReconciliation: false));

    private sealed class FakeTransferClient : ITransferQueueAgentClient
    {
        public List<TransferEnqueueRequest> Requests { get; } = [];

        public Func<TransferEnqueueRequest, TransferEnqueueResponse> Handler { get; init; } = Accepted;
        public Func<TransferEnqueueRequest, CancellationToken, Task<TransferEnqueueResponse>>? AsyncHandler
        {
            get;
            init;
        }

        public Task<TransferEnqueueResponse> EnqueueAsync(
            TransferEnqueueRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return AsyncHandler is null
                ? Task.FromResult(Handler(request))
                : AsyncHandler(request, cancellationToken);
        }

        public Task<TransferListResponse> ListAsync(
            TransferListRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TransferStatusResponse> GetStatusAsync(
            TransferStatusRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TransferMutationResponse> CancelAsync(
            TransferCancelRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TransferMutationResponse> RetryAsync(
            TransferRetryRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TransferMutationResponse> ReconcileAsync(
            TransferReconcileRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
