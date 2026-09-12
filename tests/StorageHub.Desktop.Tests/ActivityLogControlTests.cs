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

    [Fact]
    public void Repeated_refreshes_reuse_rows_instead_of_rebuilding_them()
    {
        // Clearing and re-adding made every row blink out and back on each poll. Reusing the row
        // objects is what proves the grid is reconciled rather than rebuilt.
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var control = new ActivityLogControl(new ActivityQueueClient(), new FakeSyncManagementClient());

            control.RefreshActivityAsync().GetAwaiter().GetResult();
            var first = control.RowIdentities();
            control.RefreshActivityAsync().GetAwaiter().GetResult();
            var second = control.RowIdentities();

            Assert.Equal(2, control.DisplayedEntryCount);
            Assert.Equal(first, second);
        });
    }

    [Fact]
    public void A_shorter_page_drops_only_the_surplus_rows()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var transfers = new ActivityQueueClient();
            using var control = new ActivityLogControl(transfers, new FakeSyncManagementClient());
            control.RefreshActivityAsync().GetAwaiter().GetResult();
            var before = control.RowIdentities()[0];

            transfers.ReturnNothing = true;
            control.RefreshActivityAsync().GetAwaiter().GetResult();

            // The sync run remains, and it is still the same row object.
            Assert.Equal(1, control.DisplayedEntryCount);
            Assert.Equal(before, control.RowIdentities()[0]);
        });
    }

    private sealed class ActivityQueueClient : ITransferQueueAgentClient
    {
        public int ListCount { get; private set; }

        public bool ReturnNothing { get; set; }

        public Task<TransferListResponse> ListAsync(TransferListRequest request, CancellationToken cancellationToken = default)
        {
            ListCount++;
            if (ReturnNothing)
            {
                return Task.FromResult(new TransferListResponse(
                    TransferQueueIpcContract.CurrentVersion, [], null));
            }

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
