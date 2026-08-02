using System.Globalization;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

public sealed class TransferQueueControlTests
{
    [Fact]
    public void Control_stays_inert_until_shown_and_renders_an_explicit_refresh()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var client = new FakeQueueClient();
            using var form = new Form();
            using var control = new TransferQueueControl(client);
            form.Controls.Add(control);
            _ = form.Handle;
            _ = control.Handle;
            System.Windows.Forms.Application.DoEvents();
            Assert.Equal(0, client.ListCount);

            control.RefreshQueueAsync().GetAwaiter().GetResult();

            var tabs = Assert.Single(control.Controls.OfType<TabControl>());
            Assert.Equal(8, tabs.TabPages.Count);
            var grid = Assert.Single(tabs.SelectedTab!.Controls.OfType<DataGridView>());
            Assert.Single(grid.Rows.Cast<DataGridViewRow>());
            Assert.Equal(1, client.ListCount);
            Assert.Contains(TransferQueueState.Preparing, client.LastStates!);
            Assert.Contains(TransferQueueState.Transferring, client.LastStates!);
        });
    }

    private sealed class FakeQueueClient : ITransferQueueAgentClient
    {
        public int ListCount { get; private set; }
        public TransferQueueState[]? LastStates { get; private set; }

        public Task<TransferEnqueueResponse> EnqueueAsync(
            TransferEnqueueRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TransferListResponse> ListAsync(
            TransferListRequest request,
            CancellationToken cancellationToken = default)
        {
            ListCount++;
            LastStates = request.States;
            return Task.FromResult(new TransferListResponse(
                TransferQueueIpcContract.CurrentVersion,
                [new TransferQueueSummary(
                    Guid.NewGuid(),
                    TransferQueueOperation.Copy,
                    Guid.NewGuid(),
                    "source.bin",
                    Guid.NewGuid(),
                    "destination.bin",
                    TransferQueueState.Preparing,
                    Revision: 1,
                    Attempt: 1,
                    Priority: 0,
                    ExpectedBytes: 100,
                    ProgressBytes: 20,
                    UpdatedUtc: DateTimeOffset.Parse(
                        "2026-08-02T12:00:00Z",
                        CultureInfo.InvariantCulture),
                    RetryAvailableUtc: null,
                    ErrorCode: null,
                    ErrorSummary: null,
                    CanCancel: true,
                    CanRetry: false,
                    NeedsReconciliation: false)],
                ContinuationToken: null));
        }

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
