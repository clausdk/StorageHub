using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

/// <summary>
/// Live, optimistic-concurrency queue surface backed by the durable agent. The control remains
/// inert until it is visible in a shown form, so constructing the desktop never opens IPC.
/// </summary>
public sealed class TransferQueueControl : UserControl
{
    private static readonly QueueTabDefinition[] QueueTabs =
    [
        new("Active",
        [
            TransferQueueState.Preparing,
            TransferQueueState.Connecting,
            TransferQueueState.Transferring,
            TransferQueueState.Verifying,
            TransferQueueState.Finalizing,
            TransferQueueState.CleanupPending
        ]),
        new("Queued", [TransferQueueState.Pending, TransferQueueState.Retrying]),
        new("Paused",
        [
            TransferQueueState.Paused,
            TransferQueueState.BlockedCredential,
            TransferQueueState.BlockedTrust,
            TransferQueueState.RestartRequired
        ]),
        new("Failed", [TransferQueueState.Failed]),
        new("Completed", [TransferQueueState.Completed, TransferQueueState.Cancelled]),
        new("Conflicts", [TransferQueueState.Interrupted, TransferQueueState.NeedsReconciliation])
    ];

    private readonly ITransferQueueAgentClient _client;
    private readonly bool _ownsClient;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly TabControl _tabs;
    private readonly ToolStripButton _cancelButton;
    private readonly ToolStripButton _retryButton;
    private readonly ToolStripButton _reconcileButton;
    private readonly ToolStripButton _nextButton;
    private readonly ToolStripComboBox _reconcileAction;
    private readonly ToolStripLabel _status;
    private readonly Dictionary<TabPage, QueueTabDefinition> _definitions = [];
    private readonly Dictionary<TabPage, DataGridView> _grids = [];
    private string? _pageCursor;
    private string? _nextCursor;
    private int _refreshing;
    private bool _disposed;

    public TransferQueueControl()
        : this(new NamedPipeTransferQueueAgentClient(), ownsClient: true)
    {
    }

    public TransferQueueControl(ITransferQueueAgentClient client, bool ownsClient = false)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ownsClient = ownsClient;
        Dock = DockStyle.Fill;
        BackColor = StorageHubTheme.Surface;
        AccessibleName = "Transfer and sync queue";
        AccessibleDescription = "Durable background transfers, reconciliation actions, sync runs, and logs.";

        var toolbar = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            BackColor = StorageHubTheme.Surface,
            ForeColor = StorageHubTheme.Text,
            Renderer = StorageHubTheme.CreateToolStripRenderer(),
            AccessibleName = "Transfer queue commands",
            Padding = new Padding(4, 2, 4, 2)
        };
        var refresh = CreateButton("↻", "Refresh queue", RefreshButtonClicked);
        _cancelButton = CreateButton("■", "Cancel selected transfer", CancelButtonClicked);
        _retryButton = CreateButton("▶", "Retry selected transfer", RetryButtonClicked);
        _reconcileButton = CreateButton("✓", "Apply reconciliation action", ReconcileButtonClicked);
        _nextButton = CreateButton("Next ›", "Show the next queue page", NextButtonClicked);
        _reconcileAction = new ToolStripComboBox
        {
            Name = "ReconciliationAction",
            DropDownStyle = ComboBoxStyle.DropDownList,
            AccessibleName = "Reconciliation action",
            AutoSize = false,
            Width = 128
        };
        _reconcileAction.Items.AddRange(Enum.GetNames<TransferReconciliationAction>());
        _reconcileAction.SelectedItem = nameof(TransferReconciliationAction.Review);
        _status = new ToolStripLabel("Queue will connect when the window is shown.")
        {
            Alignment = ToolStripItemAlignment.Right,
            ForeColor = StorageHubTheme.TextMuted,
            AccessibleName = "Queue status"
        };
        toolbar.Items.Add(refresh);
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(_cancelButton);
        toolbar.Items.Add(_retryButton);
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(_reconcileAction);
        toolbar.Items.Add(_reconcileButton);
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(_nextButton);
        toolbar.Items.Add(_status);

        _tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            AccessibleName = "Transfer queue views",
            Padding = new Point(14, 4),
            HotTrack = true
        };
        foreach (var definition in QueueTabs)
        {
            AddQueueTab(definition);
        }

        AddSyncRunsTab();
        AddPlaceholderTab("Logs", "Safe agent and transfer diagnostics will appear here.");
        _tabs.SelectedIndex = 0;
        _tabs.SelectedIndexChanged += SelectedTabChanged;
        Controls.Add(_tabs);
        Controls.Add(toolbar);

        _pollTimer = new System.Windows.Forms.Timer { Interval = 2_000 };
        _pollTimer.Tick += PollTimerTick;
        UpdateActionState();
    }

    /// <summary>Refreshes the selected transfer view. Public for host commands and UI tests.</summary>
    public Task RefreshQueueAsync(CancellationToken cancellationToken = default) =>
        RefreshQueueCoreAsync(resetPage: true, cancellationToken);

    /// <summary>Selects the genuine run review surface hosted by the main window.</summary>
    public void SelectSyncRunsTab()
    {
        var page = _tabs.TabPages
            .Cast<TabPage>()
            .First(static candidate => string.Equals(candidate.Text, "Sync Runs", StringComparison.Ordinal));
        _tabs.SelectedTab = page;
    }

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
            _tabs.SelectedIndexChanged -= SelectedTabChanged;
            _lifetime.Cancel();
            _lifetime.Dispose();
            if (_ownsClient)
            {
                _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            _pollTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void AddQueueTab(QueueTabDefinition definition)
    {
        var page = new TabPage(definition.Name)
        {
            BackColor = StorageHubTheme.Surface,
            AccessibleName = $"{definition.Name} transfers"
        };
        var grid = CreateGrid(definition.Name);
        grid.SelectionChanged += (_, _) => UpdateActionState();
        page.Controls.Add(grid);
        _definitions.Add(page, definition);
        _grids.Add(page, grid);
        _tabs.TabPages.Add(page);
    }

    private void AddPlaceholderTab(string name, string description)
    {
        var page = new TabPage(name)
        {
            BackColor = StorageHubTheme.Surface,
            AccessibleName = name
        };
        page.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = description,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = StorageHubTheme.TextMuted,
            AccessibleName = description
        });
        _tabs.TabPages.Add(page);
    }

    private void AddSyncRunsTab()
    {
        var page = new TabPage("Sync Runs")
        {
            BackColor = StorageHubTheme.Surface,
            AccessibleName = "Synchronization runs"
        };
        page.Controls.Add(new SyncRunsControl());
        _tabs.TabPages.Add(page);
    }

    private static DataGridView CreateGrid(string name)
    {
        var grid = new DataGridView
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
            GridColor = StorageHubTheme.Border,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = true,
            AccessibleName = $"{name} transfer jobs",
            AccessibleDescription = $"Durable {name.ToLowerInvariant()} transfers."
        };
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = StorageHubTheme.SurfaceMuted;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = StorageHubTheme.Text;
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(4);
        grid.DefaultCellStyle.BackColor = StorageHubTheme.Surface;
        grid.DefaultCellStyle.ForeColor = StorageHubTheme.Text;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(222, 233, 251);
        grid.DefaultCellStyle.SelectionForeColor = StorageHubTheme.Text;
        grid.Columns.Add("Operation", "Operation");
        grid.Columns.Add("Source", "Source");
        grid.Columns.Add("Destination", "Destination");
        grid.Columns.Add("Progress", "Progress");
        grid.Columns.Add("Attempt", "Attempt");
        grid.Columns.Add("Status", "Status");
        grid.Columns[0].FillWeight = 55;
        grid.Columns[3].FillWeight = 65;
        grid.Columns[4].FillWeight = 45;
        return grid;
    }

    private static ToolStripButton CreateButton(string text, string description, EventHandler handler)
    {
        var button = new ToolStripButton(text)
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = description,
            AccessibleName = description,
            AccessibleDescription = description,
            AutoToolTip = true
        };
        button.Click += handler;
        return button;
    }

    private async void RefreshButtonClicked(object? sender, EventArgs e) =>
        await RefreshQueueCoreAsync(resetPage: true, _lifetime.Token).ConfigureAwait(true);

    private async void NextButtonClicked(object? sender, EventArgs e)
    {
        if (_nextCursor is null)
        {
            return;
        }

        _pageCursor = _nextCursor;
        await RefreshQueueCoreAsync(resetPage: false, _lifetime.Token).ConfigureAwait(true);
    }

    private async void CancelButtonClicked(object? sender, EventArgs e) =>
        await ApplySelectedAsync(
            static (client, transfer, token) => client.CancelAsync(
                new TransferCancelRequest(
                    TransferQueueIpcContract.CurrentVersion,
                    transfer.TransferId,
                    transfer.Revision),
                token)).ConfigureAwait(true);

    private async void RetryButtonClicked(object? sender, EventArgs e) =>
        await ApplySelectedAsync(
            static (client, transfer, token) => client.RetryAsync(
                new TransferRetryRequest(
                    TransferQueueIpcContract.CurrentVersion,
                    transfer.TransferId,
                    transfer.Revision),
                token)).ConfigureAwait(true);

    private async void ReconcileButtonClicked(object? sender, EventArgs e)
    {
        if (!Enum.TryParse<TransferReconciliationAction>(
                _reconcileAction.SelectedItem?.ToString(),
                ignoreCase: false,
                out var action))
        {
            return;
        }

        await ApplySelectedAsync(
            (client, transfer, token) => client.ReconcileAsync(
                new TransferReconcileRequest(
                    TransferQueueIpcContract.CurrentVersion,
                    transfer.TransferId,
                    transfer.Revision,
                    action),
                token)).ConfigureAwait(true);
    }

    private async Task ApplySelectedAsync(
        Func<ITransferQueueAgentClient, TransferQueueSummary, CancellationToken, Task<TransferMutationResponse>> action)
    {
        if (!TryGetSelectedTransfers(out var selected) || selected.Count == 0)
        {
            return;
        }

        SetBusy(true, "Applying queue action…");
        try
        {
            var applied = 0;
            var conflicts = 0;
            foreach (var transfer in selected)
            {
                var response = await action(_client, transfer, _lifetime.Token).ConfigureAwait(true);
                if (response.Outcome is TransferQueueMutationOutcome.Applied or
                    TransferQueueMutationOutcome.Accepted)
                {
                    applied++;
                }
                else
                {
                    conflicts++;
                }
            }

            _status.Text = conflicts == 0
                ? $"Updated {applied} transfer(s)."
                : $"Updated {applied}; {conflicts} changed or require review.";
            await RefreshQueueCoreAsync(resetPage: true, _lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // The control is closing.
        }
        catch (Exception)
        {
            SetUnavailable();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void PollTimerTick(object? sender, EventArgs e) =>
        await RefreshQueueCoreAsync(resetPage: false, _lifetime.Token).ConfigureAwait(true);

    private async Task RefreshQueueCoreAsync(bool resetPage, CancellationToken cancellationToken)
    {
        var selectedTab = _tabs.SelectedTab;
        if (_disposed || selectedTab is null || !_definitions.TryGetValue(selectedTab, out var definition) ||
            Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
        {
            return;
        }

        if (resetPage)
        {
            _pageCursor = null;
        }

        SetBusy(true, "Refreshing queue…");
        try
        {
            var response = await _client.ListAsync(
                new TransferListRequest(
                    TransferQueueIpcContract.CurrentVersion,
                    definition.States,
                    PageSize: 25,
                    ContinuationToken: _pageCursor),
                cancellationToken).ConfigureAwait(true);
            if (response.Failure is not null)
            {
                SetUnavailable();
                return;
            }

            PopulateGrid(_grids[selectedTab], response.Transfers);
            _nextCursor = response.ContinuationToken;
            _nextButton.Enabled = _nextCursor is not null;
            _status.Text = response.Transfers.Length == 0
                ? $"No {definition.Name.ToLowerInvariant()} transfers."
                : $"{response.Transfers.Length} {definition.Name.ToLowerInvariant()} transfer(s).";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A closing or superseded control does not need a UI error.
        }
        catch (Exception)
        {
            SetUnavailable();
        }
        finally
        {
            _ = Interlocked.Exchange(ref _refreshing, 0);
            SetBusy(false);
        }
    }

    private static void PopulateGrid(DataGridView grid, IEnumerable<TransferQueueSummary> transfers)
    {
        grid.Rows.Clear();
        foreach (var transfer in transfers)
        {
            var index = grid.Rows.Add(
                transfer.Operation,
                FormatEndpoint(transfer.SourceConnectionId, transfer.SourcePath),
                FormatEndpoint(transfer.DestinationConnectionId, transfer.DestinationPath),
                FormatProgress(transfer.ProgressBytes, transfer.ExpectedBytes),
                transfer.Attempt,
                FormatStatus(transfer));
            grid.Rows[index].Tag = transfer;
        }

        grid.ClearSelection();
    }

    private static string FormatEndpoint(Guid connectionId, string path) =>
        $"{connectionId.ToString("N")[..8]} · {(path.Length == 0 ? "/" : path)}";

    private static string FormatProgress(long progress, long? expected) => expected switch
    {
        > 0 => $"{Math.Min(100D, progress * 100D / expected.Value):0.#}%",
        0 => "100%",
        _ => FormatBytes(progress)
    };

    private static string FormatBytes(long value) => value switch
    {
        >= 1_099_511_627_776 => $"{value / 1_099_511_627_776D:0.##} TB",
        >= 1_073_741_824 => $"{value / 1_073_741_824D:0.##} GB",
        >= 1_048_576 => $"{value / 1_048_576D:0.##} MB",
        >= 1_024 => $"{value / 1_024D:0.##} KB",
        _ => $"{value} B"
    };

    private static string FormatStatus(TransferQueueSummary transfer) => transfer.ErrorSummary is null
        ? transfer.State.ToString()
        : $"{transfer.State}: {transfer.ErrorSummary}";

    private bool TryGetSelectedTransfers(out IReadOnlyList<TransferQueueSummary> transfers)
    {
        if (_tabs.SelectedTab is null || !_grids.TryGetValue(_tabs.SelectedTab, out var grid))
        {
            transfers = [];
            return false;
        }

        transfers = grid.SelectedRows
            .Cast<DataGridViewRow>()
            .Select(static row => row.Tag)
            .OfType<TransferQueueSummary>()
            .OrderBy(static transfer => transfer.UpdatedUtc)
            .ToArray();
        return true;
    }

    private void SelectedTabChanged(object? sender, EventArgs e)
    {
        _pageCursor = null;
        _nextCursor = null;
        _nextButton.Enabled = false;
        UpdateActionState();
        if (_pollTimer.Enabled)
        {
            _ = RefreshQueueCoreAsync(resetPage: true, _lifetime.Token);
        }
    }

    private void UpdateActionState()
    {
        _ = TryGetSelectedTransfers(out var selected);
        _cancelButton.Enabled = selected.Any(static transfer => transfer.CanCancel);
        _retryButton.Enabled = selected.Any(static transfer => transfer.CanRetry);
        var canReconcile = selected.Any(static transfer => transfer.NeedsReconciliation);
        _reconcileAction.Enabled = canReconcile;
        _reconcileButton.Enabled = canReconcile;
        if (canReconcile &&
            selected.Any(static transfer => transfer.State == TransferQueueState.NeedsReconciliation) &&
            string.Equals(
                _reconcileAction.SelectedItem?.ToString(),
                nameof(TransferReconciliationAction.Review),
                StringComparison.Ordinal))
        {
            _reconcileAction.SelectedItem = nameof(TransferReconciliationAction.Restart);
        }

        _nextButton.Enabled = _nextCursor is not null;
    }

    private void SetBusy(bool busy, string? message = null)
    {
        UseWaitCursor = busy;
        if (message is not null)
        {
            _status.Text = message;
        }

        if (!busy)
        {
            UpdateActionState();
        }
    }

    private void SetUnavailable()
    {
        _status.Text = "Agent queue unavailable; retrying automatically.";
        _nextCursor = null;
        _nextButton.Enabled = false;
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
            _ = RefreshQueueCoreAsync(resetPage: true, _lifetime.Token);
        }
    }

    private sealed record QueueTabDefinition(string Name, TransferQueueState[] States);
}
