using StorageHub.Contracts.Ipc;
using System.Globalization;

namespace StorageHub.Desktop;

/// <summary>Builds a bounded, non-secret activity log from durable Agent transfer and sync records.</summary>
public sealed class ActivityLogControl : UserControl
{
    private readonly ITransferQueueAgentClient _transferClient;
    private readonly ISyncManagementAgentClient _syncClient;
    private readonly bool _ownsClients;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DataGridView _grid;
    private readonly Label _status;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private int _refreshing;
    private bool _disposed;

    public ActivityLogControl()
        : this(new NamedPipeTransferQueueAgentClient(), new NamedPipeSyncManagementAgentClient(), ownsClients: true)
    {
    }

    public ActivityLogControl(
        ITransferQueueAgentClient transferClient,
        ISyncManagementAgentClient syncClient,
        bool ownsClients = false)
    {
        _transferClient = transferClient ?? throw new ArgumentNullException(nameof(transferClient));
        _syncClient = syncClient ?? throw new ArgumentNullException(nameof(syncClient));
        _ownsClients = ownsClients;
        Dock = DockStyle.Fill;
        BackColor = StorageHubTheme.Surface;
        AccessibleName = "Durable activity log";
        AccessibleDescription = "Recent transfer and synchronization state from the background Agent.";

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(8, 6, 8, 4),
            WrapContents = false,
            BackColor = StorageHubTheme.Surface
        };
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        StorageHubTheme.StyleSecondaryButton(refresh);
        refresh.Click += RefreshClicked;
        _status = new Label
        {
            Text = "Activity will load when this tab is opened.",
            AutoSize = true,
            Margin = new Padding(12, 8, 0, 0),
            ForeColor = StorageHubTheme.TextMuted,
            AccessibleDescription = "Current durable activity log status"
        };
        toolbar.Controls.Add(refresh);
        toolbar.Controls.Add(_status);

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowHeadersVisible = false,
            BackgroundColor = StorageHubTheme.Surface,
            BorderStyle = BorderStyle.None,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AccessibleName = "Recent durable activity"
        };
        _grid.Columns.Add("Updated", "Updated");
        _grid.Columns.Add("Area", "Area");
        _grid.Columns.Add("Item", "Item");
        _grid.Columns.Add("State", "State");
        _grid.Columns.Add("Details", "Details");
        _grid.Columns[0].FillWeight = 55;
        _grid.Columns[1].FillWeight = 38;
        _grid.Columns[2].FillWeight = 90;
        _grid.Columns[3].FillWeight = 55;
        _grid.Columns[4].FillWeight = 130;

        Controls.Add(_grid);
        Controls.Add(toolbar);
        _pollTimer = new System.Windows.Forms.Timer { Interval = 5_000 };
        _pollTimer.Tick += PollTimerTick;
    }

    public int DisplayedEntryCount => _grid.Rows.Count;

    public string StatusText => _status.Text;

    public Task RefreshActivityAsync(CancellationToken cancellationToken = default) =>
        RefreshCoreAsync(cancellationToken);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdatePollingState();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _pollTimer.Stop();
        base.OnHandleDestroyed(e);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdatePollingState();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _pollTimer.Stop();
            _pollTimer.Tick -= PollTimerTick;
            _pollTimer.Dispose();
            _lifetime.Cancel();
            _lifetime.Dispose();
            if (_ownsClients)
            {
                _transferClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _syncClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        base.Dispose(disposing);
    }

    private async void RefreshClicked(object? sender, EventArgs e) =>
        await RefreshCoreAsync(_lifetime.Token).ConfigureAwait(true);

    private async void PollTimerTick(object? sender, EventArgs e) =>
        await RefreshCoreAsync(_lifetime.Token).ConfigureAwait(true);

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        if (_disposed || Interlocked.Exchange(ref _refreshing, 1) != 0)
        {
            return;
        }

        _status.Text = "Refreshing durable activity…";
        _status.ForeColor = StorageHubTheme.TextMuted;
        try
        {
            var transfersTask = _transferClient.ListAsync(new TransferListRequest(
                TransferQueueIpcContract.CurrentVersion,
                Enum.GetValues<TransferQueueState>(),
                TransferQueueIpcLimits.MaximumPageSize), cancellationToken);
            var runsTask = _syncClient.ListRunsAsync(new SyncRunListRequest(
                PageSize: SyncManagementIpcLimits.MaximumPageSize), cancellationToken);
            await Task.WhenAll(transfersTask, runsTask).ConfigureAwait(true);
            var transfers = await transfersTask.ConfigureAwait(true);
            var runs = await runsTask.ConfigureAwait(true);
            if (transfers.Failure is not null)
            {
                throw new InvalidOperationException(transfers.Failure.Message);
            }

            if (runs.Failure is not null)
            {
                throw new InvalidOperationException(runs.Failure.Message);
            }

            var entries = transfers.Transfers.Select(ActivityEntry.FromTransfer)
                .Concat(runs.Runs.Select(ActivityEntry.FromRun))
                .OrderByDescending(static entry => entry.UpdatedUtc)
                .ThenBy(static entry => entry.Area, StringComparer.Ordinal)
                .Take(100)
                .ToArray();
            _grid.Rows.Clear();
            foreach (var entry in entries)
            {
                _grid.Rows.Add(
                    entry.UpdatedUtc.LocalDateTime.ToString("g", CultureInfo.CurrentCulture),
                    entry.Area,
                    entry.Item,
                    entry.State,
                    entry.Details);
            }

            _grid.ClearSelection();
            _status.Text = entries.Length == 0
                ? "No transfer or synchronization activity yet."
                : $"Showing {entries.Length} recent durable event(s).";
            _status.ForeColor = StorageHubTheme.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            _status.Text = $"Activity unavailable: {error.Message}";
            _status.ForeColor = StorageHubTheme.Danger;
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private void UpdatePollingState()
    {
        var shouldPoll = !_disposed && IsHandleCreated && Visible && FindForm()?.Visible == true;
        if (!shouldPoll)
        {
            _pollTimer.Stop();
            return;
        }

        if (!_pollTimer.Enabled)
        {
            _pollTimer.Start();
            _ = RefreshCoreAsync(_lifetime.Token);
        }
    }

    private sealed record ActivityEntry(
        DateTimeOffset UpdatedUtc,
        string Area,
        string Item,
        string State,
        string Details)
    {
        public static ActivityEntry FromTransfer(TransferQueueSummary transfer) => new(
            transfer.UpdatedUtc,
            "Transfer",
            transfer.TransferId.ToString("N")[..8],
            transfer.State.ToString(),
            transfer.ErrorSummary ?? $"{transfer.Operation}: {DisplayPath(transfer.SourcePath)} → {DisplayPath(transfer.DestinationPath)}");

        public static ActivityEntry FromRun(SyncRunSummary run) => new(
            run.UpdatedUtc,
            "Sync",
            run.SyncRunId.ToString("N")[..8],
            run.Phase.ToString(),
            run.ConflictCount == 0
                ? $"Generation {run.Generation}; {run.DispatchState}"
                : $"Generation {run.Generation}; {run.ConflictCount} conflict(s)");

        private static string DisplayPath(string value) => value.Length == 0 ? "/" : value;
    }
}
