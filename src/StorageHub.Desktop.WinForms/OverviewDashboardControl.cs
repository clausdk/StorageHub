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

    /// <summary>How many rows each card shows. Unrelated to the workspace caps.</summary>
    private const int MaximumListRows = 12;


    private readonly IRemoteStorageAgentClient _storageClient;
    private readonly ITransferQueueAgentClient _transferClient;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Label _agentValue;
    private readonly Label _activeValue;
    private readonly Label _queuedValue;
    private readonly Label _attentionValue;
    private readonly ListView _connections;
    private readonly ListView _attention;
    private readonly ListView _workspaces;
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
        AccessibleName = "StorageHub overview";

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
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

        var metrics = new DashboardRow
        {
            Dock = DockStyle.Top,
            Height = 104,
            Columns = 4,
            Margin = new Padding(0, 0, 0, 18),
            BackColor = Color.Transparent
        };
        _agentValue = AddMetric(metrics, "Agent", "Starting", UiGlyph.Server, UiIconTone.Primary);
        _activeValue = AddMetric(metrics, "Active transfers", "0", UiGlyph.Run, UiIconTone.Success);
        _queuedValue = AddMetric(metrics, "Queued", "0", UiGlyph.Queue, UiIconTone.Primary);
        _attentionValue = AddMetric(metrics, "Needs attention", "0", UiGlyph.Warning, UiIconTone.Warning);
        content.Controls.Add(metrics);

        // A full-width row of its own rather than a third column beside the two lists: at the
        // shell's 1120px minimum width, three columns leave each one narrower than the columns
        // its ListView already declares, so every card would grow a horizontal scrollbar.
        _workspaces = CreateList(
            "Workspaces",
            "Pinned layouts first, then the ones you opened most recently",
            UiGlyph.Add,
            out var workspaceCard);
        _workspaces.Columns[0].Width = 220;
        _workspaces.Columns[1].Text = "Location";
        _workspaces.Columns[1].Width = 420;
        _workspaces.Columns[2].Text = "State";
        _workspaces.Columns[2].Width = 110;
        // No ListViewGroups: the rows arrive pinned-first and the State column already says which
        // list each came from, so grouping would add native-control fragility for no information.
        _workspaces.MouseDoubleClick += (_, args) => OpenWorkspaceAt(_workspaces.HitTest(args.Location).Item);
        _workspaces.KeyDown += WorkspaceListKeyDown;
        _workspaces.ContextMenuStrip = BuildWorkspaceMenu();
        var workspaceRow = new DashboardRow
        {
            Dock = DockStyle.Top,
            Height = 214,
            Margin = new Padding(0, 0, 0, 18),
            BackColor = Color.Transparent
        };
        workspaceRow.Controls.Add(workspaceCard);
        content.Controls.Add(workspaceRow);

        var lists = new DashboardRow
        {
            Dock = DockStyle.Top,
            Height = 330,
            Columns = 2,
            Margin = Padding.Empty,
            BackColor = Color.Transparent
        };
        _connections = CreateList("Recent connections", "Opened this session, followed by saved favorites", UiGlyph.Connections, out var connectionCard);
        _connections.Columns[1].Text = "Provider";
        _connections.Columns[2].Text = "Details";
        _attention = CreateList("Needs attention", "Failed, blocked, or conflicting transfers", UiGlyph.Warning, out var attentionCard);
        lists.Controls.Add(connectionCard);
        lists.Controls.Add(attentionCard);
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

    internal event EventHandler<WorkspaceShortcutEventArgs>? WorkspaceOpenRequested;

    internal event EventHandler<WorkspaceShortcutEventArgs>? WorkspacePinToggleRequested;

    internal event EventHandler<WorkspaceShortcutEventArgs>? WorkspaceRemoveRequested;

    /// <summary>
    /// The connections read on the last refresh. Exposed so the shell can build a favourites menu
    /// without opening a second pipe client with its own lifetime and disposal path.
    /// </summary>
    internal IReadOnlyList<ConnectionSummary> SavedConnections => _savedConnections;

    /// <summary>
    /// The favourites worth offering as a navigation target: enabled, marked favourite, and of a
    /// kind a browser pane can actually open. Without the last filter the menu would list
    /// connections that do nothing when clicked.
    /// </summary>
    internal static IReadOnlyList<ConnectionSummary> SelectFavoriteConnections(
        IEnumerable<ConnectionSummary>? connections,
        int maximum = 15) =>
        connections is null || maximum <= 0
            ? []
            : [.. connections
                .Where(static connection =>
                    connection.IsFavorite &&
                    connection.IsEnabled &&
                    connection is
                    {
                        Type: ConnectionProfileType.Storage
                    } or
                    {
                        Type: ConnectionProfileType.Client,
                        Provider: StorageConnectionProvider.Ssh
                    })
                .DistinctBy(static connection => connection.ConnectionId)
                .OrderBy(static connection => connection.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Take(maximum)];

    /// <summary>
    /// Replaces the workspace card's contents. The shell supplies the rows already ordered and
    /// de-duplicated, and decides which files look present: this control performs no IO.
    /// </summary>
    internal void ShowWorkspaceShortcuts(IReadOnlyList<WorkspaceShortcutView> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _workspaces.BeginUpdate();
        try
        {
            _workspaces.Items.Clear();
            foreach (var entry in entries)
            {
                var item = new ListViewItem(
                    entry.Entry.DisplayName,
                    entry.LooksPresent ? "connection" : "warning")
                {
                    Tag = entry,
                    ToolTipText = entry.Entry.Path,
                    // Dimmed, never disabled: clicking a missing entry is how it gets pruned.
                    ForeColor = entry.LooksPresent ? StorageHubTheme.Text : StorageHubTheme.TextMuted
                };
                item.SubItems.Add(entry.Entry.Path);
                // The State column, not just the colour, is what tells a screen reader the file
                // is gone.
                item.SubItems.Add(entry.LooksPresent
                    ? entry.IsPinned ? "Pinned" : "Recent"
                    : "Missing");
                _workspaces.Items.Add(item);
            }

            if (_workspaces.Items.Count == 0)
            {
                var empty = new ListViewItem("No workspaces yet", "empty");
                empty.SubItems.Add("Save a workspace to pin it here");
                _workspaces.Items.Add(empty);
            }
        }
        finally
        {
            _workspaces.EndUpdate();
        }
    }

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

    private static Label AddMetric(DashboardRow host, string title, string value, UiGlyph glyph, UiIconTone tone)
    {
        var accent = StorageHubTheme.ToneColor(tone);
        var card = CreateCard(accent);
        // The rail and the rounded corners are painted by the card, so its content has to let
        // them through rather than covering the whole client area with an opaque panel.
        card.Padding = new Padding(5, 2, 2, 2);
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = Color.Transparent,
            Padding = new Padding(10, 10, 12, 10),
            Margin = Padding.Empty
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        var icon = new PictureBox
        {
            SizeMode = PictureBoxSizeMode.CenterImage,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        };
        _ = StorageHubTheme.TrackIcon(icon, glyph, 24, tone);
        var valueLabel = new Label
        {
            Text = value,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            Font = new Font("Segoe UI Semibold", 17F),
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
        host.Controls.Add(card);
        return valueLabel;
    }

    private static ListView CreateList(string title, string subtitle, UiGlyph glyph, out UiCard card)
    {
        card = CreateCard();
        card.Dock = DockStyle.Fill;
        card.Padding = new Padding(2);
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            BackColor = Color.Transparent,
            Padding = new Padding(12, 10, 12, 12),
            Margin = Padding.Empty
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var icon = new PictureBox
        {
            SizeMode = PictureBoxSizeMode.CenterImage,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        };
        _ = StorageHubTheme.TrackIcon(icon, glyph, 20, glyph == UiGlyph.Warning ? UiIconTone.Warning : UiIconTone.Primary);
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
        images.Images.Add("empty", UiIconFactory.Create(UiGlyph.Info, StorageHubTheme.TextMuted, 18));
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

    private ContextMenuStrip BuildWorkspaceMenu()
    {
        var menu = new ContextMenuStrip { Renderer = DesktopAppearanceService.MenuRenderer };
        var open = new ToolStripMenuItem("Open");
        var pin = new ToolStripMenuItem("Pin");
        var remove = new ToolStripMenuItem("Remove from list");
        var copy = new ToolStripMenuItem("Copy path");
        open.Click += (_, _) => OpenWorkspaceAt(SelectedWorkspace());
        pin.Click += (_, _) => Raise(WorkspacePinToggleRequested, SelectedWorkspace());
        remove.Click += (_, _) => Raise(WorkspaceRemoveRequested, SelectedWorkspace());
        copy.Click += (_, _) =>
        {
            if (SelectedWorkspace()?.Tag is WorkspaceShortcutView view)
            {
                Clipboard.SetText(view.Entry.Path);
            }
        };
        menu.Items.AddRange([open, pin, remove, copy]);
        menu.Opening += (_, args) =>
        {
            if (SelectedWorkspace()?.Tag is not WorkspaceShortcutView view)
            {
                args.Cancel = true;
                return;
            }

            pin.Text = view.IsPinned ? "Unpin" : "Pin";
        };
        return menu;
    }

    private ListViewItem? SelectedWorkspace() => _workspaces.SelectedItems.Count == 1
        ? _workspaces.SelectedItems[0]
        : null;

    private void WorkspaceListKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.KeyCode != Keys.Enter) return;
        args.Handled = true;
        OpenWorkspaceAt(SelectedWorkspace());
    }

    private void OpenWorkspaceAt(ListViewItem? item) => Raise(WorkspaceOpenRequested, item);

    private void Raise(EventHandler<WorkspaceShortcutEventArgs>? handler, ListViewItem? item)
    {
        if (item?.Tag is WorkspaceShortcutView view)
        {
            handler?.Invoke(this, new WorkspaceShortcutEventArgs(view));
        }
    }

    /// <summary>
    /// A band of equal-width cards across the page. The row divides its own width between its
    /// children rather than delegating to proportional columns, so the tiles line up from one
    /// arithmetic step instead of from a table's preferred-size negotiation with its contents.
    /// </summary>
    private sealed class DashboardRow : Panel
    {
        private const int Gutter = 12;

        /// <summary>How many equal columns the children are spread across.</summary>
        [System.ComponentModel.DefaultValue(1)]
        public int Columns { get; init; } = 1;

        public override Size GetPreferredSize(Size proposedSize) => new(0, Height);

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            var count = Math.Max(1, Columns);
            var available = ClientSize.Width - (Gutter * (count - 1));
            if (available <= 0 || Controls.Count == 0)
            {
                return;
            }

            var cell = available / count;
            var index = 0;
            foreach (Control child in Controls)
            {
                var left = index * (cell + Gutter);
                // The last column absorbs the rounding remainder so the band ends flush.
                var width = index == count - 1 ? ClientSize.Width - left : cell;
                child.Bounds = new Rectangle(left, 0, Math.Max(1, width), ClientSize.Height);
                index++;
                if (index >= count)
                {
                    break;
                }
            }
        }
    }

    private static UiCard CreateCard(Color? accent = null) => new()
    {
        BackColor = StorageHubTheme.Surface,
        Accent = accent
    };

    private static Button CreateActionButton(string text, UiGlyph glyph, EventHandler handler, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            ImageAlign = ContentAlignment.MiddleLeft,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            AutoSize = true,
            Margin = new Padding(0, 0, 8, 0)
        };
        _ = StorageHubTheme.TrackIcon(button, glyph, 18, primary ? UiIconTone.OnPrimary : UiIconTone.Text);
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
