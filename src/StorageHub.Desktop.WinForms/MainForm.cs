using Krypton.Toolkit;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

public sealed class MainForm : KryptonForm
{
    private readonly List<Image> _ownedImages = [];
    private readonly AgentStatusMonitor _agentMonitor = new();
    private readonly ManualTransferController _manualTransfers = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ToolStripStatusLabel _locationStatus = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _selectionStatus = new();
    private readonly ToolStripStatusLabel _speedStatus = new();
    private readonly ToolStripStatusLabel _queueStatus = new();
    private readonly ToolStripStatusLabel _agentStatus = new() { AccessibleName = "Agent status" };
    private readonly TabControl _workspaceTabs;
    private readonly TransferQueueControl _transferQueue;
    private ShellStatusSnapshot _status = ShellStatusSnapshot.Initial;
    private BrowserPaneControl? _activePane;
    private bool _changingWorkspaceTabs;
    private bool _monitorStarted;

    public MainForm()
    {
        Text = "StorageHub";
        AccessibleName = "StorageHub file manager";
        AccessibleDescription = "A secure dual-pane file manager for local and remote storage.";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1120, 720);
        Size = new Size(1500, 920);
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = StorageHubTheme.Canvas;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        var menu = BuildMenu();
        MainMenuStrip = menu;
        var toolbar = BuildToolbar();

        _workspaceTabs = new TabControl
        {
            Dock = DockStyle.Fill,
            AccessibleName = "Workspace tabs",
            AccessibleDescription = "Each tab contains independent source and destination browser panes.",
            Padding = new Point(16, 5),
            HotTrack = true,
            DrawMode = TabDrawMode.OwnerDrawFixed
        };
        _workspaceTabs.TabPages.Add(CreateWorkspace("Local ↔ Connections"));
        _workspaceTabs.TabPages.Add(new TabPage("+")
        {
            ToolTipText = "New workspace",
            AccessibleName = "New workspace tab"
        });
        _workspaceTabs.SelectedIndexChanged += WorkspaceTabsSelectedIndexChanged;
        _workspaceTabs.DrawItem += WorkspaceTabsDrawItem;
        _workspaceTabs.MouseDown += WorkspaceTabsMouseDown;

        var mainSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            Size = new Size(1400, 760),
            SplitterDistance = 615,
            Panel2MinSize = 145,
            BackColor = StorageHubTheme.Border,
            AccessibleName = "Workspace and job queue"
        };
        mainSplit.Panel1.BackColor = StorageHubTheme.Canvas;
        mainSplit.Panel2.BackColor = StorageHubTheme.Surface;
        mainSplit.Panel1.Controls.Add(_workspaceTabs);
        _transferQueue = new TransferQueueControl();
        _manualTransfers.TransfersEnqueued += ManualTransfersEnqueued;
        mainSplit.Panel2.Controls.Add(_transferQueue);

        var statusStrip = BuildStatusStrip();
        Controls.Add(mainSplit);
        Controls.Add(statusStrip);
        Controls.Add(toolbar);
        Controls.Add(menu);

        ApplyStatus(_status);
        _agentMonitor.StatusChanged += AgentMonitorStatusChanged;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!_monitorStarted)
        {
            _monitorStarted = true;
            _agentMonitor.Start();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lifetime.Cancel();
            _manualTransfers.TransfersEnqueued -= ManualTransfersEnqueued;
            _workspaceTabs.SelectedIndexChanged -= WorkspaceTabsSelectedIndexChanged;
            _workspaceTabs.DrawItem -= WorkspaceTabsDrawItem;
            _workspaceTabs.MouseDown -= WorkspaceTabsMouseDown;
            _agentMonitor.StatusChanged -= AgentMonitorStatusChanged;
            _agentMonitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _manualTransfers.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _lifetime.Dispose();
        }

        base.Dispose(disposing);

        if (disposing)
        {
            foreach (var image in _ownedImages)
            {
                image.Dispose();
            }

            _ownedImages.Clear();
        }
    }

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip
        {
            AccessibleName = "Main menu",
            BackColor = StorageHubTheme.Surface,
            ForeColor = StorageHubTheme.Text,
            Renderer = StorageHubTheme.CreateToolStripRenderer(),
            ShowItemToolTips = true
        };
        foreach (var menuName in UiCommandCatalog.TopMenus)
        {
            var root = new ToolStripMenuItem(menuName)
            {
                AccessibleName = $"{menuName} menu"
            };
            foreach (var command in UiCommandCatalog.Commands[menuName])
            {
                if (!IsAvailableCommand(command))
                {
                    continue;
                }

                var definition = UiCommandCatalog.GetDefinition(menuName, command);
                var item = new ToolStripMenuItem(command)
                {
                    Tag = definition.Id,
                    ShortcutKeys = definition.Shortcut,
                    ToolTipText = definition.Description,
                    AccessibleName = command,
                    AccessibleDescription = definition.Description
                };
                WireCommand(item, command);
                root.DropDownItems.Add(item);
            }

            if (root.DropDownItems.Count > 0)
            {
                menu.Items.Add(root);
            }
            else
            {
                root.Dispose();
            }
        }

        return menu;
    }

    private ToolStrip BuildToolbar()
    {
        var toolbar = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            ImageScalingSize = new Size(20, 20),
            AccessibleName = "Main toolbar",
            AccessibleDescription = "Workspace, navigation, and connection commands.",
            BackColor = StorageHubTheme.Surface,
            ForeColor = StorageHubTheme.Text,
            Renderer = StorageHubTheme.CreateToolStripRenderer(),
            Padding = new Padding(5, 4, 5, 4),
            AutoSize = true
        };
        toolbar.Items.Add(CreateToolbarButton(UiGlyph.Add, "New tab", (_, _) => AddWorkspace()));
        toolbar.Items.Add(CreateToolbarButton(UiGlyph.Connections, "Connection Manager", (_, _) => ShowConnectionManager()));
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(CreateToolbarButton(UiGlyph.Back, "Back", (_, _) => NavigateActivePane(PaneNavigation.Back)));
        toolbar.Items.Add(CreateToolbarButton(UiGlyph.Forward, "Forward", (_, _) => NavigateActivePane(PaneNavigation.Forward)));
        toolbar.Items.Add(CreateToolbarButton(UiGlyph.Up, "Up", (_, _) => NavigateActivePane(PaneNavigation.Up)));
        toolbar.Items.Add(CreateToolbarButton(UiGlyph.Refresh, "Refresh", (_, _) => NavigateActivePane(PaneNavigation.Refresh)));
        return toolbar;
    }

    private StatusStrip BuildStatusStrip()
    {
        var status = new StatusStrip
        {
            AccessibleName = "Application status",
            BackColor = StorageHubTheme.Surface,
            ForeColor = StorageHubTheme.TextMuted,
            Renderer = StorageHubTheme.CreateToolStripRenderer(),
            SizingGrip = true
        };
        _locationStatus.AccessibleName = "Current location";
        _selectionStatus.AccessibleName = "Selection summary";
        _speedStatus.AccessibleName = "Transfer speed";
        _queueStatus.AccessibleName = "Queue summary";
        status.Items.Add(_locationStatus);
        status.Items.Add(_selectionStatus);
        status.Items.Add(new ToolStripSeparator());
        status.Items.Add(_speedStatus);
        status.Items.Add(new ToolStripSeparator());
        status.Items.Add(_queueStatus);
        status.Items.Add(new ToolStripSeparator());
        status.Items.Add(_agentStatus);
        return status;
    }

    private TabPage CreateWorkspace(string title)
    {
        var page = new TabPage(title)
        {
            BackColor = StorageHubTheme.Canvas,
            AccessibleName = $"{title} workspace"
        };
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            Size = new Size(1300, 600),
            SplitterDistance = 700,
            Panel1MinSize = 360,
            Panel2MinSize = 360,
            BackColor = StorageHubTheme.Border,
            AccessibleName = "Source and destination panes"
        };
        split.Panel1.Padding = new Padding(0, 0, 3, 0);
        split.Panel2.Padding = new Padding(3, 0, 0, 0);
        var source = new BrowserPaneControl("Source", showLocalDefault: true);
        var destination = new BrowserPaneControl("Destination", showLocalDefault: false);
        source.Enter += ActivePaneEntered;
        destination.Enter += ActivePaneEntered;
        source.TransferRequested += (_, args) =>
            _ = EnqueueManualTransferAsync(source, destination, args.Operation);
        destination.TransferRequested += (_, args) =>
            _ = EnqueueManualTransferAsync(destination, source, args.Operation);
        source.ObjectInspectionRequested += (_, _) => ShowObjectInspector(source);
        destination.ObjectInspectionRequested += (_, _) => ShowObjectInspector(destination);
        split.Panel1.Controls.Add(source);
        split.Panel2.Controls.Add(destination);
        page.Controls.Add(split);
        return page;
    }

    private ToolStripButton CreateToolbarButton(
        UiGlyph glyph,
        string toolTip,
        EventHandler click)
    {
        var image = UiIconFactory.Create(glyph, StorageHubTheme.Text, 20, DeviceDpi / 96F);
        _ownedImages.Add(image);
        var button = new ToolStripButton
        {
            Image = image,
            DisplayStyle = ToolStripItemDisplayStyle.Image,
            ToolTipText = toolTip,
            AccessibleName = toolTip,
            AccessibleDescription = toolTip,
            AutoToolTip = true
        };
        button.Click += click;

        return button;
    }

    private void WireCommand(ToolStripMenuItem item, string command)
    {
        item.Click += (_, _) =>
        {
            switch (command)
            {
                case "New Workspace Tab":
                    AddWorkspace();
                    break;
                case "Close Tab":
                    CloseActiveWorkspace();
                    break;
                case "Connection Manager...":
                    ShowConnectionManager();
                    break;
                case "Sync Profiles...":
                case "Preview Sync...":
                    using (var dialog = new SyncProfileEditorForm())
                    {
                        _ = dialog.ShowDialog(this);
                    }
                    break;
                case "Copy":
                case "Enqueue":
                    EnqueueFromFocusedPane(TransferQueueOperation.Copy);
                    break;
                case "Cut":
                    EnqueueFromFocusedPane(TransferQueueOperation.Move);
                    break;
                case "Properties":
                    ShowObjectInspectorFromFocusedPane();
                    break;
                case "Select All":
                    GetActivePane()?.SelectAllVisibleItems();
                    break;
                case "Refresh":
                    NavigateActivePane(PaneNavigation.Refresh);
                    break;
                case "Back":
                    NavigateActivePane(PaneNavigation.Back);
                    break;
                case "Forward":
                    NavigateActivePane(PaneNavigation.Forward);
                    break;
                case "Up":
                    NavigateActivePane(PaneNavigation.Up);
                    break;
                case "Run Sync":
                    _transferQueue.SelectSyncRunsTab();
                    break;
                case "Schedules...":
                    using (var dialog = new ScheduleManagerForm())
                    {
                        _ = dialog.ShowDialog(this);
                    }
                    break;
                case "About StorageHub":
                    _ = MessageBox.Show(
                        this,
                        $"StorageHub {DesktopApplicationVersion.Current}\nOpen-source secure storage manager\nPowered by CodeLogic and CL.Storage",
                        "About StorageHub",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    break;
                case "Exit":
                    Close();
                    break;
            }
        };
    }

    private void WorkspaceTabsSelectedIndexChanged(object? sender, EventArgs e)
    {
        if (!_changingWorkspaceTabs &&
            _workspaceTabs.SelectedIndex == _workspaceTabs.TabPages.Count - 1)
        {
            AddWorkspace();
        }
    }

    private void WorkspaceTabsDrawItem(object? sender, DrawItemEventArgs e)
    {
        var page = _workspaceTabs.TabPages[e.Index];
        var selected = e.Index == _workspaceTabs.SelectedIndex;
        var bounds = _workspaceTabs.GetTabRect(e.Index);
        var background = selected ? StorageHubTheme.Surface : StorageHubTheme.SurfaceMuted;
        using var brush = new SolidBrush(background);
        e.Graphics.FillRectangle(brush, bounds);

        if (page == _workspaceTabs.TabPages[^1])
        {
            TextRenderer.DrawText(
                e.Graphics,
                page.Text,
                Font,
                bounds,
                StorageHubTheme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        var closeBounds = GetWorkspaceCloseBounds(bounds);
        var textBounds = Rectangle.FromLTRB(bounds.Left + 8, bounds.Top, closeBounds.Left - 5, bounds.Bottom);
        TextRenderer.DrawText(
            e.Graphics,
            page.Text,
            Font,
            textBounds,
            StorageHubTheme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        using var pen = new Pen(StorageHubTheme.TextMuted, Math.Max(1F, DeviceDpi / 96F * 1.4F));
        e.Graphics.DrawLine(pen, closeBounds.Left + 4, closeBounds.Top + 4, closeBounds.Right - 4, closeBounds.Bottom - 4);
        e.Graphics.DrawLine(pen, closeBounds.Right - 4, closeBounds.Top + 4, closeBounds.Left + 4, closeBounds.Bottom - 4);
    }

    private void WorkspaceTabsMouseDown(object? sender, MouseEventArgs e)
    {
        for (var index = 0; index < _workspaceTabs.TabPages.Count - 1; index++)
        {
            if (GetWorkspaceCloseBounds(_workspaceTabs.GetTabRect(index)).Contains(e.Location))
            {
                CloseWorkspaceAt(index);
                return;
            }
        }
    }

    private static Rectangle GetWorkspaceCloseBounds(Rectangle tabBounds)
    {
        const int closeSize = 16;
        return new Rectangle(
            tabBounds.Right - closeSize - 6,
            tabBounds.Top + Math.Max(0, (tabBounds.Height - closeSize) / 2),
            closeSize,
            closeSize);
    }

    private void AddWorkspace()
    {
        var insertAt = _workspaceTabs.TabPages.Count - 1;
        var page = CreateWorkspace($"Workspace {insertAt + 1}");
        _workspaceTabs.TabPages.Insert(insertAt, page);
        _workspaceTabs.SelectedTab = page;
    }

    private void CloseActiveWorkspace()
    {
        var selected = _workspaceTabs.SelectedTab;
        if (selected is null || selected == _workspaceTabs.TabPages[^1])
        {
            return;
        }

        CloseWorkspaceAt(_workspaceTabs.TabPages.IndexOf(selected));
    }

    private void CloseWorkspaceAt(int index)
    {
        if ((uint)index >= (uint)(_workspaceTabs.TabPages.Count - 1))
        {
            return;
        }

        var page = _workspaceTabs.TabPages[index];
        _changingWorkspaceTabs = true;
        try
        {
            _workspaceTabs.TabPages.RemoveAt(index);
            if (_workspaceTabs.TabPages.Count == 1)
            {
                _workspaceTabs.SelectedIndex = -1;
            }
        }
        finally
        {
            _changingWorkspaceTabs = false;
        }

        if (_activePane is not null && page.Contains(_activePane))
        {
            _activePane = null;
        }

        page.Dispose();
    }

    private void ShowConnectionManager()
    {
        using var dialog = new ConnectionManagerForm();
        _ = dialog.ShowDialog(this);
    }

    private void ActivePaneEntered(object? sender, EventArgs e)
    {
        _activePane = sender as BrowserPaneControl;
    }

    private BrowserPaneControl? GetActivePane()
    {
        if (!TryGetActiveWorkspacePanes(out var source, out var destination))
        {
            return null;
        }

        return _activePane == destination || destination.ContainsFocus
            ? destination
            : source;
    }

    private void NavigateActivePane(PaneNavigation navigation)
    {
        var pane = GetActivePane();
        switch (navigation)
        {
            case PaneNavigation.Back:
                pane?.NavigateBack();
                break;
            case PaneNavigation.Forward:
                pane?.NavigateForward();
                break;
            case PaneNavigation.Up:
                pane?.NavigateUp();
                break;
            case PaneNavigation.Refresh:
                pane?.Reload();
                break;
        }
    }

    private static bool IsAvailableCommand(string command) => command is
        "New Workspace Tab" or
        "Close Tab" or
        "Exit" or
        "Cut" or
        "Copy" or
        "Select All" or
        "Properties" or
        "Refresh" or
        "Back" or
        "Forward" or
        "Up" or
        "Connection Manager..." or
        "Enqueue" or
        "Preview Sync..." or
        "Sync Profiles..." or
        "Schedules..." or
        "About StorageHub";

    private enum PaneNavigation
    {
        Back,
        Forward,
        Up,
        Refresh
    }

    private void EnqueueFromFocusedPane(TransferQueueOperation operation)
    {
        if (!TryGetActiveWorkspacePanes(out var source, out var destination))
        {
            return;
        }

        if (destination.ContainsFocus)
        {
            (source, destination) = (destination, source);
        }

        _ = EnqueueManualTransferAsync(source, destination, operation);
    }

    private void ShowObjectInspectorFromFocusedPane()
    {
        if (!TryGetActiveWorkspacePanes(out var source, out var destination))
        {
            return;
        }

        ShowObjectInspector(destination.ContainsFocus ? destination : source);
    }

    private void ShowObjectInspector(BrowserPaneControl pane)
    {
        var selected = pane.CaptureSelectionSnapshot();
        if (selected.IsFailure)
        {
            ShowObjectInspectorFailure(selected.Error.Message);
            return;
        }

        var snapshot = selected.Value;
        if (snapshot.Items.Count != 1 || snapshot.Items[0].Kind != StorageItemKind.File)
        {
            ShowObjectInspectorFailure("Select exactly one file to inspect.");
            return;
        }

        var context = snapshot.Context;
        if (context.Kind != PaneTransferContextKind.SavedConnection ||
            context.ConnectionId is not { } connectionId ||
            string.IsNullOrWhiteSpace(context.RootIdentity))
        {
            ShowObjectInspectorFailure(
                "Object inspection currently requires a file opened through a saved connection.");
            return;
        }

        var item = snapshot.Items[0];
        var address = new ObjectInspectorAddress(
            connectionId,
            context.RootIdentity,
            item.RelativePath,
            item.NativeItemId,
            item.VersionId,
            item.EntityTag);
        if (!address.HasValidBounds)
        {
            ShowObjectInspectorFailure("The selected file does not have a valid bounded object identity.");
            return;
        }

        using var dialog = new ObjectInspectorForm(address);
        _ = dialog.ShowDialog(this);
    }

    private bool TryGetActiveWorkspacePanes(
        out BrowserPaneControl source,
        out BrowserPaneControl destination)
    {
        source = null!;
        destination = null!;
        var page = _workspaceTabs.SelectedTab;
        if (page is null || page == _workspaceTabs.TabPages[^1])
        {
            return false;
        }

        var split = page.Controls.OfType<SplitContainer>().SingleOrDefault();
        source = split?.Panel1.Controls.OfType<BrowserPaneControl>().SingleOrDefault()!;
        destination = split?.Panel2.Controls.OfType<BrowserPaneControl>().SingleOrDefault()!;
        return source is not null && destination is not null;
    }

    private async Task EnqueueManualTransferAsync(
        BrowserPaneControl source,
        BrowserPaneControl destination,
        TransferQueueOperation operation)
    {
        if (_lifetime.IsCancellationRequested)
        {
            return;
        }

        var selection = source.CaptureSelectionSnapshot();
        if (selection.IsFailure)
        {
            ShowManualTransferFailure(selection.Error.Message);
            return;
        }

        var destinationSnapshot = destination.CaptureDestinationSnapshot();
        if (destinationSnapshot.IsFailure)
        {
            ShowManualTransferFailure(destinationSnapshot.Error.Message);
            return;
        }

        try
        {
            var result = await _manualTransfers.EnqueueAsync(
                selection.Value,
                destinationSnapshot.Value,
                operation,
                cancellationToken: _lifetime.Token).ConfigureAwait(true);
            if (result.HasAmbiguity)
            {
                ShowManualTransferFailure(DescribeAmbiguousEnqueue(
                    selection.Value.Items.Count,
                    result.Accepted.Count,
                    result.AmbiguousTransferIds));
                return;
            }

            if (result.Failure is null)
            {
                return;
            }

            var message = result.IsPartial
                ? $"{result.Accepted.Count} transfer(s) were durably accepted before the next request failed. {result.Failure.Message}"
                : result.Failure.Message;
            ShowManualTransferFailure(message);
        }
        catch (ManualTransferEnqueueAmbiguousException error)
        {
            if (!_lifetime.IsCancellationRequested)
            {
                ShowManualTransferFailure(DescribeAmbiguousEnqueue(
                    selection.Value.Items.Count,
                    error.AcceptedTransferIds.Count,
                    error.AmbiguousTransferIds));
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // The window is closing. Any already accepted jobs remain in the durable queue.
        }
        catch (Exception error) when (
            error is IOException or InvalidDataException or InvalidOperationException or TimeoutException)
        {
            ShowManualTransferFailure("The background agent could not enqueue the transfer. Please retry.");
        }
    }

    private void ManualTransfersEnqueued(object? sender, ManualTransfersEnqueuedEventArgs e)
    {
        if (IsDisposed || Disposing || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed || Disposing)
                {
                    return;
                }

                var queuedJobs = (int)Math.Min(
                    int.MaxValue,
                    (long)_status.QueuedJobs + e.AcceptedTransferIds.Count);
                ApplyStatus(_status with { QueuedJobs = queuedJobs });
                _ = _transferQueue.RefreshQueueAsync(_lifetime.Token);
            }));
        }
        catch (InvalidOperationException)
        {
            // The window handle can disappear between the guard and BeginInvoke during shutdown.
        }
    }

    private static string FormatTransferIds(IEnumerable<Guid> transferIds) =>
        string.Join(", ", transferIds.Select(static transferId => transferId.ToString("D")));

    private static string DescribeAmbiguousEnqueue(
        int selectedCount,
        int acceptedCount,
        IReadOnlyCollection<Guid> ambiguousTransferIds)
    {
        var unsubmittedCount = Math.Max(0, selectedCount - acceptedCount - ambiguousTransferIds.Count);
        var unsubmitted = unsubmittedCount == 0
            ? string.Empty
            : $" {unsubmittedCount} later selected file(s) were not submitted.";
        return $"{acceptedCount} transfer(s) were durably acknowledged. " +
            $"The agent did not confirm whether transfer ID(s) {FormatTransferIds(ambiguousTransferIds)} were durably enqueued." +
            unsubmitted +
            " Check the queue for those exact IDs before submitting replacement jobs.";
    }

    private void ShowManualTransferFailure(string message)
    {
        if (IsDisposed || Disposing || _lifetime.IsCancellationRequested)
        {
            return;
        }

        _ = MessageBox.Show(
            this,
            message,
            "Transfer queue",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private void ShowObjectInspectorFailure(string message)
    {
        if (IsDisposed || Disposing || _lifetime.IsCancellationRequested)
        {
            return;
        }

        _ = MessageBox.Show(
            this,
            message,
            "Object inspector",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void AgentMonitorStatusChanged(object? sender, AgentMonitorStatusEventArgs e)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed)
                {
                    return;
                }

                ApplyStatus(_status with
                {
                    AgentState = e.Status.State,
                    ActiveJobs = e.Status.ActiveTransfers + e.Status.ActiveSyncRuns
                });
                _agentStatus.ToolTipText = e.Status.Detail;
            }));
        }
        catch (InvalidOperationException)
        {
            // The window handle can disappear between the guard and BeginInvoke during shutdown.
        }
    }

    private void ApplyStatus(ShellStatusSnapshot status)
    {
        _status = status;
        _locationStatus.Text = status.Location;
        _selectionStatus.Text = status.SelectionText;
        _speedStatus.Text = status.TransferRateText;
        _queueStatus.Text = status.QueueText;
        _agentStatus.Text = status.AgentText;
        _agentStatus.ForeColor = status.AgentState switch
        {
            AgentConnectionState.Connected => StorageHubTheme.Success,
            AgentConnectionState.RecoveryOnly => StorageHubTheme.Warning,
            AgentConnectionState.Disconnected => StorageHubTheme.Danger,
            _ => StorageHubTheme.TextMuted
        };
    }
}
