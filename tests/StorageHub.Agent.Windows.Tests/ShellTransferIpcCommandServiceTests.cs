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
        public ValueTask<DurableTransferJob?> FindAsync(TransferJobId transferJobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
