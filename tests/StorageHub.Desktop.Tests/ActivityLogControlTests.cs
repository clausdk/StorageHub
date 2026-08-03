using System.Globalization;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

public sealed class ActivityLogControlTests
{
    [Fact]
    public void Activity_log_merges_durable_transfer_and_sync_records()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var transferClient = new ActivityQueueClient();
            var syncClient = new FakeSyncManagementClient();
            using var control = new ActivityLogControl(transferClient, syncClient);

            control.RefreshActivityAsync().GetAwaiter().GetResult();

            Assert.Equal(2, control.DisplayedEntryCount);
            Assert.Contains("2 recent durable", control.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, transferClient.ListCount);
            Assert.Equal(1, syncClient.RunListCount);
        });
    }

    private sealed class ActivityQueueClient : ITransferQueueAgentClient
    {
        public int ListCount { get; private set; }

        public Task<TransferListResponse> ListAsync(TransferListRequest request, CancellationToken cancellationToken = default)
        {
            ListCount++;
            return Task.FromResult(new TransferListResponse(
                TransferQueueIpcContract.CurrentVersion,
                [new TransferQueueSummary(
                    Guid.NewGuid(), TransferQueueOperation.Copy, Guid.NewGuid(), "a.txt", Guid.NewGuid(), "b.txt",
                    TransferQueueState.Completed, 2, 1, 0, 10, 10,
                    DateTimeOffset.Parse("2026-08-03T20:00:00Z", CultureInfo.InvariantCulture), null,
                    null, null, false, false, false)],
                null));
        }

        public Task<TransferEnqueueResponse> EnqueueAsync(TransferEnqueueRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TransferStatusResponse> GetStatusAsync(TransferStatusRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TransferMutationResponse> CancelAsync(TransferCancelRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TransferMutationResponse> RetryAsync(TransferRetryRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TransferMutationResponse> ReconcileAsync(TransferReconcileRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
