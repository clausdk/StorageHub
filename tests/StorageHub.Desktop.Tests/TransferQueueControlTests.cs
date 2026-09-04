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
            Assert.Equal(7, tabs.TabPages.Count);
            Assert.DoesNotContain(tabs.TabPages.Cast<TabPage>(), page => page.Text == "Sync Runs");
            var grid = Assert.Single(tabs.SelectedTab!.Controls.OfType<DataGridView>());
            Assert.Single(grid.Rows.Cast<DataGridViewRow>());
            Assert.Equal(1, client.ListCount);
            Assert.Contains(TransferQueueState.Preparing, client.LastStates!);
            Assert.Contains(TransferQueueState.Transferring, client.LastStates!);
            Assert.Equal("Active (3)", Assert.IsType<TabPage>(tabs.TabPages["Active"]).Text);
            Assert.Equal("Completed (5)", Assert.IsType<TabPage>(tabs.TabPages["Completed"]).Text);
        });
    }

    [Fact]
    public void Every_transfer_state_tab_queries_its_exact_durable_states()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var client = new FakeQueueClient();
            using var control = new TransferQueueControl(client);
            var tabs = Assert.Single(control.Controls.OfType<TabControl>());
            var expected = new Dictionary<string, TransferQueueState[]>
            {
                ["Active"] =
                [
                    TransferQueueState.Preparing, TransferQueueState.Connecting,
                    TransferQueueState.Transferring, TransferQueueState.Verifying,
                    TransferQueueState.Finalizing, TransferQueueState.CleanupPending
                ],
                ["Queued"] = [TransferQueueState.Pending, TransferQueueState.Retrying],
                ["Paused"] =
                [
                    TransferQueueState.Paused, TransferQueueState.BlockedCredential,
                    TransferQueueState.BlockedTrust, TransferQueueState.RestartRequired
                ],
                ["Failed"] = [TransferQueueState.Failed],
                ["Completed"] = [TransferQueueState.Completed, TransferQueueState.Cancelled],
                ["Conflicts"] = [TransferQueueState.Interrupted, TransferQueueState.NeedsReconciliation]
            };

            foreach (var (name, states) in expected)
            {
                tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(page => page.Name == name);
                control.RefreshQueueAsync().GetAwaiter().GetResult();
                Assert.Equal(states, client.LastStates);
            }
        });
    }

    [Fact]
    public void QueueTabsAndToolbarShowIconsWithoutClippingLabels()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var form = new Form { ClientSize = new Size(1_400, 500) };
            using var control = new TransferQueueControl(new FakeQueueClient());
            form.Controls.Add(control);
            form.Show();
            System.Windows.Forms.Application.DoEvents();

            var tabs = Assert.Single(control.Controls.OfType<TabControl>());
            Assert.NotNull(tabs.ImageList);
            Assert.Equal(7, tabs.ImageList.Images.Count);
            for (var index = 0; index < tabs.TabPages.Count; index++)
            {
                var page = tabs.TabPages[index];
                Assert.False(string.IsNullOrWhiteSpace(page.ImageKey));
                var required = TextRenderer.MeasureText(
                    page.Text,
                    tabs.Font,
                    Size.Empty,
                    TextFormatFlags.NoPadding).Width + tabs.ImageList.ImageSize.Width + 20;
                Assert.True(tabs.GetTabRect(index).Width >= required, $"The {page.Text} tab is clipped.");
            }

            var toolbar = Assert.Single(control.Controls.OfType<ToolStrip>());
            var commandButtons = toolbar.Items.OfType<ToolStripButton>().ToArray();
            Assert.Equal(["Refresh", "Cancel", "Retry", "Apply", "Next"], commandButtons.Select(button => button.Text));
            Assert.All(commandButtons, button =>
            {
                Assert.Equal(ToolStripItemDisplayStyle.ImageAndText, button.DisplayStyle);
                Assert.NotNull(button.Image);
            });
        });
    }

    [Fact]
    public void EveryJobListExposesSelectedAndAllHistoryClearingCommands()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var control = new TransferQueueControl(new FakeQueueClient());
            var tabs = Assert.Single(control.Controls.OfType<TabControl>());
            foreach (var grid in tabs.TabPages.Cast<TabPage>()
                .SelectMany(page => page.Controls.OfType<DataGridView>()))
            {
                var menu = Assert.IsType<ContextMenuStrip>(grid.ContextMenuStrip);
                Assert.Contains(menu.Items.Cast<ToolStripItem>(), item => item.Text == "Clear selected history");
                Assert.Contains(menu.Items.Cast<ToolStripItem>(), item => item.Text == "Clear all history...");
            }
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
                ContinuationToken: null,
                StateCounts: new Dictionary<TransferQueueState, int>
                {
                    [TransferQueueState.Preparing] = 3,
                    [TransferQueueState.Completed] = 4,
                    [TransferQueueState.Cancelled] = 1
                }));
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
