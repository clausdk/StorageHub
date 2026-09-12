using System.Globalization;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

public sealed class TransferQueueControlTests
{
    [Fact]
    public void APendingDropRendersAsAQueueRowWithoutABar()
    {
        // There is no destination and therefore no byte total yet, so the row must not paint a
        // fraction that would imply measured progress.
        var drop = new PendingDropEntry(
            "abcdef0123456789",
            "reports",
            3,
            PendingDropState.AwaitingDestination,
            Destination: null,
            Detail: null,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

        var row = TransferQueueControl.ToRow(drop);

        Assert.Equal("Copy", row.Operation);
        Assert.Equal("3 items from reports", row.Source);
        Assert.Equal("(File Explorer)", row.Destination);
        Assert.Equal("Waiting for destination", row.Status);
        Assert.Null(row.Fraction);
        Assert.Same(drop, row.Payload);
    }

    [Fact]
    public void AQueuedDropShowsWhereItWent()
    {
        var drop = new PendingDropEntry(
            "abcdef0123456789",
            "budget.xlsx",
            1,
            PendingDropState.Queued,
            @"C:\Users\sam\Desktop",
            Detail: null,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

        var row = TransferQueueControl.ToRow(drop);

        Assert.Equal("budget.xlsx", row.Source);
        Assert.Equal(@"C:\Users\sam\Desktop", row.Destination);
        Assert.Equal("Queued", row.Status);
    }

    [Fact]
    public void ADurableTransferAndAPendingDropShareTheSameRowShape()
    {
        // Both feed one diffing pass, so the grid never needs to know which kind it is showing.
        var transfer = Summary(TransferQueueState.Transferring, 25, 100);

        var transferRow = TransferQueueControl.ToRow(transfer);
        var dropRow = TransferQueueControl.ToRow(new PendingDropEntry(
            "abcdef0123456789", "reports", 1, PendingDropState.AwaitingDestination,
            null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));

        Assert.Equal(0.25D, transferRow.Fraction);
        Assert.Null(dropRow.Fraction);
        Assert.NotEqual(transferRow.Key, dropRow.Key);
        Assert.StartsWith("drop:", dropRow.Key, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TransferQueueState.Transferring, 0L, 100L, 0D)]
    [InlineData(TransferQueueState.Transferring, 25L, 100L, 0.25D)]
    [InlineData(TransferQueueState.Transferring, 100L, 100L, 1D)]
    public void ProgressFractionTracksTheCompletedShare(
        TransferQueueState state,
        long progress,
        long expected,
        double fraction)
    {
        Assert.Equal(fraction, TransferQueueControl.ProgressFraction(Summary(state, progress, expected)));
    }

    [Fact]
    public void ProgressFractionClampsReportsThatOvershootTheExpectedLength()
    {
        Assert.Equal(1D, TransferQueueControl.ProgressFraction(
            Summary(TransferQueueState.Transferring, 4_096, 100)));
    }

    [Fact]
    public void ProgressFractionIsUnknownWithoutAnExpectedLength()
    {
        Assert.Null(TransferQueueControl.ProgressFraction(
            Summary(TransferQueueState.Transferring, 40, expected: null)));
    }

    [Fact]
    public void CompletedTransfersAlwaysPaintFull()
    {
        // A rounded percentage must never leave a finished row looking short of the end.
        Assert.Equal(1D, TransferQueueControl.ProgressFraction(
            Summary(TransferQueueState.Completed, 0, expected: null)));
    }

    [Theory]
    [InlineData(TransferQueueState.Transferring, true)]
    [InlineData(TransferQueueState.Verifying, true)]
    [InlineData(TransferQueueState.Finalizing, true)]
    [InlineData(TransferQueueState.Pending, false)]
    [InlineData(TransferQueueState.Completed, false)]
    [InlineData(TransferQueueState.Failed, false)]
    public void ActiveStatesDriveTheFasterPollingCadence(TransferQueueState state, bool active)
    {
        Assert.Equal(active, TransferQueueControl.IsActiveState(state));
    }

    private static TransferQueueSummary Summary(
        TransferQueueState state,
        long progress,
        long? expected) => new(
        Guid.NewGuid(),
        TransferQueueOperation.Copy,
        Guid.NewGuid(),
        "source.bin",
        Guid.NewGuid(),
        "destination.bin",
        state,
        Revision: 1,
        Attempt: 1,
        Priority: 0,
        ExpectedBytes: expected,
        ProgressBytes: progress,
        UpdatedUtc: DateTimeOffset.UnixEpoch,
        RetryAvailableUtc: null,
        ErrorCode: null,
        ErrorSummary: null,
        CanCancel: true,
        CanRetry: false,
        NeedsReconciliation: false);

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
