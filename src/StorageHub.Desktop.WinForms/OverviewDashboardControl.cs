using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

public sealed class OverviewDashboardControl : UserControl
{
    private static readonly TransferQueueState[] ActiveStates =
    [
        TransferQueueState.Preparing,
        TransferQueueState.Connecting,
        TransferQueueState.Transferring,
        TransferQueueState.Verifying,
        TransferQueueState.Finalizing,
        TransferQueueState.CleanupPending
    ];

    private static readonly TransferQueueState[] QueuedStates =
    [
        TransferQueueState.Pending,
        TransferQueueState.Retrying
    ];

    private static readonly TransferQueueState[] AttentionStates =
    [
        TransferQueueState.Failed,
        TransferQueueState.Interrupted,
        TransferQueueState.NeedsReconciliation,
        TransferQueueState.BlockedCredential,
        TransferQueueState.BlockedTrust
    ];

    private readonly IRemoteStorageAgentClient _storageClient;
    private readonly ITransferQueueAgentClient _transferClient;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Label _agentValue;
    private readonly Label _activeValue;
    private readonly Label _queuedValue;
    private readonly Label _attentionValue;
    private readonly ListView _connections;
    private readonly ListView _attention;
    private readonly Label _status;
    private readonly List<ConnectionSummary> _recentConnections = [];
    private IReadOnlyList<ConnectionSummary> _savedConnections = [];
    private int _refreshing;
    private bool _disposed;

    public OverviewDashboardControl()
        : this(new NamedPipeRemoteStorageAgentClient(), new NamedPipeTransferQueueAgentClient())
    {
    }

    internal OverviewDashboardControl(
        IRemoteStorageAgentClient storageClient,
        ITransferQueueAgentClient transferClient)
    {
        _storageClient = storageClient;
        _transferClient = transferClient;
        Dock = DockStyle.Fill;
        BackColor = StorageHubTheme.Canvas;
        AutoScroll = true;
        AccessibleName = "StorageHub overview";

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            Padding = new Padding(28, 24, 28, 28),
            BackColor = StorageHubTheme.Canvas
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        var heading = new Label
        {
            Text = "Welcome to StorageHub",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 20F),
            ForeColor = StorageHubTheme.Text,
            Margin = new Padding(0, 0, 0, 2)
        };
        var subtitle = new Label
        {
            Text = "Your connections, transfers, and items that need attention in one place.",
            AutoSize = true,
            ForeColor = StorageHubTheme.TextMuted,
            Margin = new Padding(1, 0, 0, 16)
        };
        content.Controls.Add(heading);
        content.Controls.Add(subtitle);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 18)
        };
        actions.Controls.Add(CreateActionButton("New workspace", UiGlyph.Add, (_, _) => NewWorkspaceRequested?.Invoke(this, EventArgs.Empty), primary: true));
        actions.Controls.Add(CreateActionButton("Connections", UiGlyph.Connections, (_, _) => ConnectionsRequested?.Invoke(this, EventArgs.Empty)));
        actions.Controls.Add(CreateActionButton("Sync tasks", UiGlyph.Compare, (_, _) => SyncTasksRequested?.Invoke(this, EventArgs.Empty)));
        actions.Controls.Add(CreateActionButton("Refresh", UiGlyph.Refresh, async (_, _) => await RefreshAsync()));
        content.Controls.Add(actions);

        var metrics = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 104,
            ColumnCount = 4,
            Margin = new Padding(0, 0, 0, 18)
        };
        for (var index = 0; index < 4; index++)
        {
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        }

        _agentValue = AddMetric(metrics, 0, "Agent", "Starting", UiGlyph.Connections, StorageHubTheme.Primary);
        _activeValue = AddMetric(metrics, 1, "Active transfers", "0", UiGlyph.Run, StorageHubTheme.Success);
        _queuedValue = AddMetric(metrics, 2, "Queued", "0", UiGlyph.More, StorageHubTheme.Primary);
        _attentionValue = AddMetric(metrics, 3, "Needs attention", "0", UiGlyph.Warning, StorageHubTheme.Warning);
        content.Controls.Add(metrics);

        var lists = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 330,
            ColumnCount = 2,
            Margin = Padding.Empty
        };
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        _connections = CreateList("Recent connections", "Opened this session, followed by saved favorites", UiGlyph.Connections, out var connectionCard);
        _connections.Columns[1].Text = "Provider";
        _connections.Columns[2].Text = "Details";
        _attention = CreateList("Needs attention", "Failed, blocked, or conflicting transfers", UiGlyph.Warning, out var attentionCard);
        connectionCard.Margin = new Padding(0, 0, 8, 0);
        attentionCard.Margin = new Padding(8, 0, 0, 0);
        lists.Controls.Add(connectionCard, 0, 0);
        lists.Controls.Add(attentionCard, 1, 0);
        content.Controls.Add(lists);

        _status = new Label
        {
            Text = "Overview will load when StorageHub is shown.",
            AutoSize = true,
            ForeColor = StorageHubTheme.TextMuted,
            Margin = new Padding(0, 12, 0, 0)
        };
        content.Controls.Add(_status);
        Controls.Add(content);
    }

    public event EventHandler? NewWorkspaceRequested;

    public event EventHandler? ConnectionsRequested;

    public event EventHandler? SyncTasksRequested;

    public void UpdateAgentStatus(ShellStatusSnapshot status)
    {
        _agentValue.Text = status.AgentState switch
        {
            AgentConnectionState.Connected => "Connected",
            AgentConnectionState.RecoveryOnly => "Recovery mode",
            AgentConnectionState.Disconnected => "Offline",
            _ => "Starting"
        };
        _agentValue.ForeColor = status.AgentState switch
        {
            AgentConnectionState.Connected => StorageHubTheme.Success,
            AgentConnectionState.RecoveryOnly => StorageHubTheme.Warning,
            AgentConnectionState.Disconnected => StorageHubTheme.Danger,
            _ => StorageHubTheme.TextMuted
        };
    }

    public void RecordRecentConnection(ConnectionSummary connection)
    {
        _recentConnections.RemoveAll(candidate => candidate.ConnectionId == connection.ConnectionId);
        _recentConnections.Insert(0, connection);
        if (_recentConnections.Count > 12)
        {
            _recentConnections.RemoveRange(12, _recentConnections.Count - 12);
        }

        PopulateConnections(_savedConnections);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _refreshing, 1) != 0 || _disposed)
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            _status.Text = "Refreshing overview...";
            var connectionsTask = _storageClient.ListConnectionsAsync(new ConnectionListRequest(
                IncludeDisabled: false,
                Limit: StorageIpcLimits.MaximumConnectionResults), linked.Token);
            var activeTask = ListTransfersAsync(ActiveStates, linked.Token);
            var queuedTask = ListTransfersAsync(QueuedStates, linked.Token);
            var attentionTask = ListTransfersAsync(AttentionStates, linked.Token);
            await Task.WhenAll(connectionsTask, activeTask, queuedTask, attentionTask).ConfigureAwait(true);

            var connectionResponse = await connectionsTask.ConfigureAwait(true);
            var active = await activeTask.ConfigureAwait(true);
            var queued = await queuedTask.ConfigureAwait(true);
            var attention = await attentionTask.ConfigureAwait(true);
            ThrowIfFailure(connectionResponse.Failure);

            _activeValue.Text = FormatBoundedCount(active);
            _queuedValue.Text = FormatBoundedCount(queued);
            _attentionValue.Text = FormatBoundedCount(attention);
            PopulateConnections(connectionResponse.Connections);
            PopulateAttention(attention.Transfers);
            _status.Text = $"Updated {DateTime.Now:t}";
            _status.ForeColor = StorageHubTheme.TextMuted;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _status.Text = $"Overview unavailable: {exception.Message}";
            _status.ForeColor = StorageHubTheme.Warning;
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    protected override async void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && IsHandleCreated)
        {
            await RefreshAsync();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _lifetime.Cancel();
            _storageClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _transferClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _lifetime.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task<TransferListResponse> ListTransfersAsync(TransferQueueState[] states, CancellationToken cancellationToken)
    {
        var response = await _transferClient.ListAsync(new TransferListRequest(
            TransferQueueIpcContract.CurrentVersion,
            states,
            PageSize: 25), cancellationToken).ConfigureAwait(true);
        ThrowIfFailure(response.Failure);
        return response;
    }

    private void PopulateConnections(IEnumerable<ConnectionSummary> connections)
    {
        _savedConnections = connections.ToArray();
        _connections.BeginUpdate();
        try
        {
            _connections.Items.Clear();
            foreach (var connection in _recentConnections
                .Concat(_savedConnections
                    .OrderByDescending(static value => value.IsFavorite)
                    .ThenBy(static value => value.DisplayName, StringComparer.CurrentCultureIgnoreCase))
                .DistinctBy(static value => value.ConnectionId)
                .Take(12))
            {
                var item = new ListViewItem(connection.DisplayName, "connection")
                {
                    Tag = connection.ConnectionId,
                    ToolTipText = connection.FolderPath ?? connection.Provider.ToString()
                };
                item.SubItems.Add(connection.Provider.ToString());
                item.SubItems.Add(connection.IsFavorite ? "Favorite" : connection.FolderPath ?? string.Empty);
                _connections.Items.Add(item);
            }

            if (_connections.Items.Count == 0)
            {
                _connections.Items.Add(new ListViewItem("No saved connections yet", "empty"));
            }
        }
        finally
        {
            _connections.EndUpdate();
        }
    }

    private void PopulateAttention(IEnumerable<TransferQueueSummary> transfers)
    {
        _attention.BeginUpdate();
        try
        {
            _attention.Items.Clear();
            foreach (var transfer in transfers.OrderByDescending(static value => value.UpdatedUtc).Take(12))
            {
                var item = new ListViewItem(DescribeTransfer(transfer), "warning")
                {
                    Tag = transfer.TransferId,
                    ToolTipText = transfer.ErrorSummary ?? transfer.State.ToString()
                };
                item.SubItems.Add(transfer.State.ToString());
                item.SubItems.Add(transfer.UpdatedUtc.LocalDateTime.ToString(
                    "g",
                    System.Globalization.CultureInfo.CurrentCulture));
                _attention.Items.Add(item);
            }

            if (_attention.Items.Count == 0)
            {
                _attention.Items.Add(new ListViewItem("Nothing needs attention", "ok"));
            }
        }
        finally
        {
            _attention.EndUpdate();
        }
    }

    private static string DescribeTransfer(TransferQueueSummary transfer)
    {
        var path = string.IsNullOrWhiteSpace(transfer.SourcePath) ? transfer.DestinationPath : transfer.SourcePath;
        return $"{transfer.Operation}: {path}";
    }

    private static string FormatBoundedCount(TransferListResponse response) =>
        response.ContinuationToken is null
            ? response.Transfers.Length.ToString(System.Globalization.CultureInfo.CurrentCulture)
            : $"{response.Transfers.Length}+";

    private static void ThrowIfFailure(StorageIpcFailure? failure)
    {
        if (failure is not null)
        {
            throw new InvalidOperationException(failure.Message);
        }
    }

    private static Label AddMetric(TableLayoutPanel host, int column, string title, string value, UiGlyph glyph, Color accent)
    {
        var card = CreateCard();
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(column == 0 ? 0 : 6, 0, column == 3 ? 0 : 6, 0);
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(12, 10, 12, 10),
            Margin = Padding.Empty
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        var icon = new PictureBox
        {
            Image = UiIconFactory.Create(glyph, accent, 24),
            SizeMode = PictureBoxSizeMode.CenterImage,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        };
        var valueLabel = new Label
        {
            Text = value,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            Font = new Font("Segoe UI Semibold", 15F),
            ForeColor = StorageHubTheme.Text,
            Margin = Padding.Empty
        };
        var titleLabel = new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
            ForeColor = StorageHubTheme.TextMuted,
            Margin = Padding.Empty
        };
        grid.Controls.Add(icon, 0, 0);
        grid.SetRowSpan(icon, 2);
        grid.Controls.Add(valueLabel, 1, 0);
        grid.Controls.Add(titleLabel, 1, 1);
        card.Controls.Add(grid);
        host.Controls.Add(card, column, 0);
        return valueLabel;
    }

    private static ListView CreateList(string title, string subtitle, UiGlyph glyph, out Panel card)
    {
        card = CreateCard();
        card.Dock = DockStyle.Fill;
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(14, 10, 14, 14),
            Margin = Padding.Empty
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var icon = new PictureBox
        {
            Image = UiIconFactory.Create(glyph, StorageHubTheme.Primary, 20),
            SizeMode = PictureBoxSizeMode.CenterImage,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        };
        var titleLabel = new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = StorageHubTheme.CreateSectionFont(),
            ForeColor = StorageHubTheme.Text,
            Margin = Padding.Empty
        };
        var subtitleLabel = new Label
        {
            Text = subtitle,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = StorageHubTheme.TextMuted,
            Margin = Padding.Empty
        };
        var images = new ImageList { ImageSize = new Size(18, 18), ColorDepth = ColorDepth.Depth32Bit };
        images.Images.Add("connection", UiIconFactory.Create(UiGlyph.Connections, StorageHubTheme.Primary, 18));
        images.Images.Add("warning", UiIconFactory.Create(UiGlyph.Warning, StorageHubTheme.Warning, 18));
        images.Images.Add("ok", UiIconFactory.Create(UiGlyph.Test, StorageHubTheme.Success, 18));
        images.Images.Add("empty", UiIconFactory.Create(UiGlyph.More, StorageHubTheme.TextMuted, 18));
        var list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            SmallImageList = images,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 8, 0, 0),
            BackColor = StorageHubTheme.Surface,
            ForeColor = StorageHubTheme.Text,
            ShowItemToolTips = true
        };
        StorageHubTheme.ConfigureList(list);
        list.Columns.Add("Name", 240);
        list.Columns.Add("State", 110);
        list.Columns.Add("Updated", 130);
        grid.Controls.Add(icon, 0, 0);
        grid.SetRowSpan(icon, 2);
        grid.Controls.Add(titleLabel, 1, 0);
        grid.Controls.Add(subtitleLabel, 1, 1);
        grid.Controls.Add(list, 0, 2);
        grid.SetColumnSpan(list, 2);
        card.Controls.Add(grid);
        return list;
    }

    private static Panel CreateCard() => new()
    {
        BackColor = StorageHubTheme.Surface,
        BorderStyle = BorderStyle.FixedSingle
    };

    private static Button CreateActionButton(string text, UiGlyph glyph, EventHandler handler, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            Image = UiIconFactory.Create(glyph, primary ? Color.White : StorageHubTheme.Text, 18),
            ImageAlign = ContentAlignment.MiddleLeft,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            AutoSize = true,
            Margin = new Padding(0, 0, 8, 0)
        };
        if (primary)
        {
            StorageHubTheme.StylePrimaryButton(button);
        }
        else
        {
            StorageHubTheme.StyleSecondaryButton(button);
        }

        button.Click += handler;
        return button;
    }
}
