using System.Text.Json;
using StorageHub.Agent.Ipc;
using StorageHub.Agent.Transfers;
using StorageHub.Contracts.Ipc;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Capabilities;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Abstractions;
using StorageHub.Storage.Models;
using StorageHub.Transfers;

namespace StorageHub.Agent.Windows.Tests;

public sealed class ShellTransferIpcCommandServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"storagehub-shell-folder-{Guid.NewGuid():N}");

    [Fact]
    public async Task Folder_drop_preserves_nested_and_empty_directories_before_queueing_files()
    {
        var dropped = Directory.CreateDirectory(Path.Combine(_root, "Dropped"));
        Directory.CreateDirectory(Path.Combine(dropped.FullName, "empty"));
        var nested = Directory.CreateDirectory(Path.Combine(dropped.FullName, "nested"));
        await File.WriteAllTextAsync(Path.Combine(dropped.FullName, "root.txt"), "root");
        await File.WriteAllBytesAsync(Path.Combine(nested.FullName, "child.bin"), [1, 2, 3]);

        var profileId = ConnectionProfileId.New();
        var session = new FakeSession(profileId, "destination-root");
        var store = new FakeStore();
        var service = new ShellTransferIpcCommandService(
            store,
            new FakeConnector(session));
        var destination = new TransferQueueAddress(
            profileId.Value,
            session.RootIdentity,
            "uploads");
        var plannedResponse = await service.HandleAsync(IpcEnvelope.Create(
            ShellTransferIpcMessageTypes.PlanImportRequest,
            Guid.NewGuid(),
            1,
            new ShellImportPlanRequest(
                ShellTransferIpcContract.CurrentVersion,
                [dropped.FullName],
                destination)));
        var planned = plannedResponse.Payload.Deserialize<ShellImportPlanResponse>();

        Assert.NotNull(planned);
        Assert.Null(planned.Failure);
        Assert.NotNull(planned.ReviewToken);
        Assert.Equal(5, planned.Items.Length);
        Assert.Contains(planned.Items, item => item.RelativePath == "Dropped/empty" && item.IsDirectory);
        Assert.Contains(planned.Items, item => item.RelativePath == "Dropped/nested" && item.IsDirectory);

        var committedResponse = await service.HandleAsync(IpcEnvelope.Create(
            ShellTransferIpcMessageTypes.CommitImportRequest,
            Guid.NewGuid(),
            1,
            new ShellImportCommitRequest(
                ShellTransferIpcContract.CurrentVersion,
                planned.ReviewToken,
                ShellImportDisposition.ReplaceFiles)));
        var committed = committedResponse.Payload.Deserialize<ShellImportCommitResponse>();

        Assert.NotNull(committed);
        Assert.Null(committed.Failure);
        Assert.True(committed.Accepted);
        Assert.Equal(2, committed.TransferIds.Length);
        Assert.Equal(
            ["uploads/Dropped", "uploads/Dropped/empty", "uploads/Dropped/nested"],
            session.CreatedDirectories);
        Assert.Contains(store.Intents, intent => intent.Destination.CanonicalRelativePath == "uploads/Dropped/root.txt");
        Assert.Contains(store.Intents, intent => intent.Destination.CanonicalRelativePath == "uploads/Dropped/nested/child.bin");
    }

    [Fact]
    public async Task Remote_folder_export_materializes_files_and_nested_folders_for_explorer()
    {
        var profileId = ConnectionProfileId.New();
        var session = new FakeExportSession(profileId, "remote-root");
        var service = new ShellTransferIpcCommandService(new FakeStore(), new FakeExportConnector(session));
        var folder = new TransferQueueAddress(profileId.Value, session.RootIdentity, "resource");

        var response = await service.HandleAsync(IpcEnvelope.Create(
            ShellTransferIpcMessageTypes.PrepareExportRequest,
            Guid.NewGuid(),
            1,
            new ShellExportPrepareRequest(
                ShellTransferIpcContract.CurrentVersion,
                [new ShellExportSource(folder, true, "resource")] )));
        var exported = response.Payload.Deserialize<ShellExportPrepareResponse>();

        Assert.NotNull(exported);
        Assert.Null(exported.Failure);
        var local = Assert.Single(exported.LocalPaths);
        try
        {
            Assert.True(Directory.Exists(local));
            Assert.Equal("root", await File.ReadAllTextAsync(Path.Combine(local, "root.txt")));
            Assert.Equal("child", await File.ReadAllTextAsync(Path.Combine(local, "nested", "child.txt")));
        }
        finally
        {
            var staging = Directory.GetParent(local)!.FullName;
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    [Fact]
    public async Task Explorer_export_discovers_files_into_the_durable_transfer_queue()
    {
        var profileId = ConnectionProfileId.New();
        var session = new FakeExportSession(profileId, "remote-root");
        var store = new FakeStore();
        var service = new ShellTransferIpcCommandService(store, new FakeExportConnector(session));
        var folder = new TransferQueueAddress(profileId.Value, session.RootIdentity, "resource");

        var startedEnvelope = await service.HandleAsync(IpcEnvelope.Create(
            ShellTransferIpcMessageTypes.StartExportRequest,
            Guid.NewGuid(),
            1,
            new ShellExportPrepareRequest(ShellTransferIpcContract.CurrentVersion,
                [new ShellExportSource(folder, true, "resource")])));
        var started = startedEnvelope.Payload.Deserialize<ShellExportStartResponse>();

        Assert.NotNull(started);
        Assert.Null(started.Failure);
        Assert.NotEqual(Guid.Empty, started.ExportId);

        ShellExportStatusResponse? status = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var statusEnvelope = await service.HandleAsync(IpcEnvelope.Create(
                ShellTransferIpcMessageTypes.ExportStatusRequest,
                Guid.NewGuid(),
                1,
                new ShellExportStatusRequest(ShellTransferIpcContract.CurrentVersion, started.ExportId)));
            status = statusEnvelope.Payload.Deserialize<ShellExportStatusResponse>();
            if (status?.State is ShellExportState.Completed or ShellExportState.Failed) break;
            await Task.Delay(20);
        }

        Assert.NotNull(status);
        Assert.Equal(ShellExportState.Completed, status.State);
        Assert.Null(status.Failure);
        Assert.Equal(3, store.Intents.Count);
        Assert.All(store.Intents, intent => Assert.StartsWith("localstage:v1:", intent.Destination.RootIdentity));
        Assert.Contains(store.Intents, intent => intent.Destination.CanonicalRelativePath.EndsWith("resource/root.txt", StringComparison.Ordinal));
        Assert.Contains(store.Intents, intent => intent.Destination.CanonicalRelativePath.EndsWith("resource/nested/child.txt", StringComparison.Ordinal));
        var exportedRoot = Assert.Single(status.LocalPaths);
        try
        {
            Assert.True(Directory.Exists(exportedRoot));
            Assert.False(File.Exists(Path.Combine(exportedRoot, "root.txt")));
        }
        finally
        {
            var staging = Directory.GetParent(exportedRoot)!.FullName;
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    [Fact]
    public async Task Explorer_drop_only_captures_destination_then_queues_deep_tree_directly()
    {
        var destination = Directory.CreateDirectory(Path.Combine(_root, "Explorer destination")).FullName;
        var profileId = ConnectionProfileId.New();
        var session = new FakeExportSession(profileId, "remote-root");
        var store = new FakeStore();
        var service = new ShellTransferIpcCommandService(store, new FakeExportConnector(session));
        var folder = new TransferQueueAddress(profileId.Value, session.RootIdentity, "resource");

        var begunEnvelope = await service.HandleAsync(IpcEnvelope.Create(
            ShellTransferIpcMessageTypes.BeginExplorerDropRequest, Guid.NewGuid(), 1,
            new ShellExportPrepareRequest(ShellTransferIpcContract.CurrentVersion,
                [new ShellExportSource(folder, true, "resource")])));
        var begun = begunEnvelope.Payload.Deserialize<ExplorerDropBeginResponse>();
        Assert.NotNull(begun);
        Assert.Null(begun.Failure);
        Assert.True(Directory.Exists(begun.MarkerPath));

        var inbox = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StorageHub", "ShellDropInbox");
        Directory.CreateDirectory(inbox);
        await File.WriteAllTextAsync(Path.Combine(inbox, begun.DropToken + ".drop"), destination);
        var committedEnvelope = await service.HandleAsync(IpcEnvelope.Create(
            ShellTransferIpcMessageTypes.CommitExplorerDropRequest, Guid.NewGuid(), 1,
            new ExplorerDropCommitRequest(ShellTransferIpcContract.CurrentVersion, begun.DropToken!)));
        var committed = committedEnvelope.Payload.Deserialize<ExplorerDropCommitResponse>();

        Assert.NotNull(committed);
        Assert.True(committed.Accepted);
        for (var attempt = 0; attempt < 50 && store.Intents.Count < 3; attempt++) await Task.Delay(20);
        Assert.Equal(3, store.Intents.Count);
        Assert.All(store.Intents, intent => Assert.StartsWith("localdest:v1:", intent.Destination.RootIdentity));
        Assert.Contains(store.Intents, intent => intent.Destination.CanonicalRelativePath == "resource/root.txt");
        Assert.Contains(store.Intents, intent => intent.Destination.CanonicalRelativePath == "resource/nested/child.txt");
        Assert.Contains(store.Intents, intent => intent.Destination.CanonicalRelativePath == "resource/nested/deeper/grandchild.txt");
        Assert.True(Directory.Exists(Path.Combine(destination, "resource", "nested", "deeper")));
        Assert.False(Directory.Exists(begun.MarkerPath));
    }

    [Fact]
    public async Task Local_staging_destination_is_compatible_with_the_normal_transfer_executor()
    {
        var staging = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StorageHub", "ShellExports", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var profileId = ConnectionProfileId.New();
            var sourceSession = new FakeExportSession(profileId, "remote-root");
            var source = StorageAddress.Create(profileId, sourceSession.RootIdentity, "resource/root.txt").Value;
            var destination = LocalStagingTransferEndpoint.CreateAddress(staging, "resource/root.txt");
            Assert.True(destination.IsSuccess);
            var opened = LocalStagingTransferEndpoint.Open(destination.Value);
            Assert.True(opened.IsSuccess);
            await using var destinationConnection = opened.Value;
            var intent = new TransferIntent(TransferJobId.New(), TransferOperationKind.Copy,
                source, destination.Value, 4, TransferVerificationPolicy.Size, DateTimeOffset.UtcNow);

            var transferred = await TransferExecutor.ExecuteAsync(intent, sourceSession,
                destinationConnection.Session);

            Assert.True(transferred.IsSuccess);
            Assert.Equal("root", await File.ReadAllTextAsync(Path.Combine(staging, "resource", "root.txt")));
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    [Fact]
    public async Task Explorer_destination_is_written_by_the_normal_transfer_executor()
    {
        var destinationRoot = Directory.CreateDirectory(Path.Combine(_root, "direct destination")).FullName;
        var profileId = ConnectionProfileId.New();
        var sourceSession = new FakeExportSession(profileId, "remote-root");
        var source = StorageAddress.Create(profileId, sourceSession.RootIdentity, "resource/root.txt").Value;
        var destination = LocalStagingTransferEndpoint.CreateDestinationAddress(
            destinationRoot, "resource/nested/root.txt");
        Assert.True(destination.IsSuccess);
        var opened = LocalStagingTransferEndpoint.Open(destination.Value);
        Assert.True(opened.IsSuccess);
        await using var destinationConnection = opened.Value;
        var intent = new TransferIntent(TransferJobId.New(), TransferOperationKind.Copy,
            source, destination.Value, 4, TransferVerificationPolicy.Size, DateTimeOffset.UtcNow);

        var transferred = await TransferExecutor.ExecuteAsync(intent, sourceSession, destinationConnection.Session);

        Assert.True(transferred.IsSuccess);
        Assert.Equal("root", await File.ReadAllTextAsync(
            Path.Combine(destinationRoot, "resource", "nested", "root.txt")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeConnector(FakeSession session) : ITransferEndpointConnector
    {
        public ValueTask<StorageResult<ITransferEndpointConnection>> OpenAsync(
            ConnectionProfileId profileId,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(
                StorageResult<ITransferEndpointConnection>.Success(new FakeConnection(session)));

        public ValueTask<StorageResult<ITransferEndpointConnection>> OpenAsync(
            StorageAddress address,
            CancellationToken cancellationToken = default) => OpenAsync(address.ProfileId, cancellationToken);
    }

    private sealed class FakeConnection(FakeSession session) : ITransferEndpointConnection
    {
        public IStorageEndpointSession Session => session;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeExportConnector(FakeExportSession session) : ITransferEndpointConnector
    {
        public ValueTask<StorageResult<ITransferEndpointConnection>> OpenAsync(ConnectionProfileId profileId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageResult<ITransferEndpointConnection>.Success(new FakeExportConnection(session)));
        public ValueTask<StorageResult<ITransferEndpointConnection>> OpenAsync(StorageAddress address, CancellationToken cancellationToken = default) =>
            OpenAsync(address.ProfileId, cancellationToken);
    }

    private sealed class FakeExportConnection(FakeExportSession session) : ITransferEndpointConnection
    {
        public IStorageEndpointSession Session => session;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeExportSession(ConnectionProfileId profileId, string rootIdentity) : IStorageEndpointSession
    {
        public ConnectionProfileId ProfileId => profileId;
        public string RootIdentity => rootIdentity;
        public EffectiveStorageCapabilities Capabilities { get; } = new([
            new KeyValuePair<StorageFeature, FeatureSupport>(StorageFeature.ReadStream, FeatureSupport.Native())]);
        public ValueTask<StorageResult> CheckHealthAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(StorageResult.Success());
        public ValueTask<StorageResult<StorageEntry>> GetEntryAsync(StorageAddress address, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageResult<StorageEntry>.Success(Entry(address.CanonicalRelativePath, StorageEntryKind.File)));
        public ValueTask<StorageResult<StoragePage>> ListAsync(StorageAddress address, StorageListRequest? request = null, CancellationToken cancellationToken = default)
        {
            var paths = address.CanonicalRelativePath switch
            {
                "resource" => new[] { Entry("resource/root.txt", StorageEntryKind.File), Entry("resource/nested", StorageEntryKind.Directory) },
                "resource/nested" => new[] { Entry("resource/nested/child.txt", StorageEntryKind.File), Entry("resource/nested/deeper", StorageEntryKind.Directory) },
                "resource/nested/deeper" => new[] { Entry("resource/nested/deeper/grandchild.txt", StorageEntryKind.File) },
                _ => []
            };
            return ValueTask.FromResult(StorageResult<StoragePage>.Success(new StoragePage(paths)));
        }
        public ValueTask<StorageResult<Stream>> OpenReadAsync(StorageReadRequest request, CancellationToken cancellationToken = default)
        {
            var text = request.Address.Name == "root.txt" ? "root" : request.Address.Name == "child.txt" ? "child" : "grandchild";
            return ValueTask.FromResult(StorageResult<Stream>.Success(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text))));
        }
        private StorageEntry Entry(string path, StorageEntryKind kind) => StorageEntry.Create(
            StorageAddress.Create(profileId, rootIdentity, path).Value, kind,
            kind == StorageEntryKind.File ? path.EndsWith("root.txt", StringComparison.Ordinal) ? 4 : path.EndsWith("child.txt", StringComparison.Ordinal) ? 5 : 10 : null).Value;
        public ValueTask<StorageResult<IStorageWriteHandle>> OpenWriteAsync(StorageWriteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<StorageResult<StorageEntry>> CreateDirectoryAsync(StorageAddress address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<StorageResult> DeleteAsync(StorageDeleteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<StorageResult<StorageEntry>> CopyAsync(StorageCopyRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<StorageResult<StorageEntry>> MoveAsync(StorageMoveRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSession(ConnectionProfileId profileId, string rootIdentity) : IStorageEndpointSession
    {
        private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
        public List<string> CreatedDirectories { get; } = [];
        public ConnectionProfileId ProfileId => profileId;
        public string RootIdentity => rootIdentity;
        public EffectiveStorageCapabilities Capabilities { get; } = new(
            [new(StorageFeature.CreateDirectory, FeatureSupport.Native())]);
        public ValueTask<StorageResult> CheckHealthAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(StorageResult.Success());
        public ValueTask<StorageResult<StorageEntry>> GetEntryAsync(StorageAddress address, CancellationToken cancellationToken = default) => ValueTask.FromResult(
            _directories.Contains(address.CanonicalRelativePath)
                ? StorageEntry.Create(address, StorageEntryKind.Directory)
                : StorageResult<StorageEntry>.Fail(new StorageFailure("storage.not_found", StorageFailureKind.NotFound, "Not found.")));
        public ValueTask<StorageResult<StorageEntry>> CreateDirectoryAsync(StorageAddress address, CancellationToken cancellationToken = default)
        {
            _directories.Add(address.CanonicalRelativePath);
            CreatedDirectories.Add(address.CanonicalRelativePath);
            return ValueTask.FromResult(StorageEntry.Create(address, StorageEntryKind.Directory));
        }
        public ValueTask<StorageResult<StoragePage>> ListAsync(StorageAddress address, StorageListRequest? request = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<StorageResult<Stream>> OpenReadAsync(StorageReadRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<StorageResult<IStorageWriteHandle>> OpenWriteAsync(StorageWriteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<StorageResult> DeleteAsync(StorageDeleteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<StorageResult<StorageEntry>> CopyAsync(StorageCopyRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<StorageResult<StorageEntry>> MoveAsync(StorageMoveRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeStore : ITransferJobStore
    {
        public List<TransferIntent> Intents { get; } = [];
        public ValueTask<bool> TryEnqueueAsync(TransferIntent intent, int priority = 0, CancellationToken cancellationToken = default) { Intents.Add(intent); return ValueTask.FromResult(true); }
        public ValueTask<DurableTransferJob?> FindAsync(TransferJobId transferJobId, CancellationToken cancellationToken = default)
        {
            var intent = Intents.SingleOrDefault(candidate => candidate.TransferJobId == transferJobId);
            if (intent is null) return ValueTask.FromResult<DurableTransferJob?>(null);
            var state = new TransferStateSnapshot(transferJobId, TransferState.Completed, 1, 1,
                DateTimeOffset.UtcNow, TransferStatusCode.None);
            return ValueTask.FromResult<DurableTransferJob?>(new DurableTransferJob(intent, state, 0, null, null, null));
        }
        public ValueTask<TransferJobClaim?> TryClaimNextAsync(TransferClaimRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<TransferStoreResult<TransferJobLease>> TryRenewLeaseAsync(TransferLeaseRenewal renewal, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<TransferStoreResult<DurableTransferJob>> TryTransitionAsync(TransferStateTransitionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<TransferStoreResult<DurableTransferJob>> TryTransitionControlStateAsync(TransferControlStateTransitionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PersistedTransferCheckpoint?> FindCheckpointAsync(TransferJobId transferJobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<TransferStoreResult<PersistedTransferCheckpoint>> TrySaveCheckpointAsync(TransferCheckpointWriteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<TransferStoreMutationStatus> TryClearCheckpointAsync(TransferCheckpointClearRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<int> RecoverInterruptedAsync(DateTimeOffset observedAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
