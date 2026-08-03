using Krypton.Toolkit;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

public sealed class MainForm : KryptonForm
{
    private readonly List<Image> _ownedImages = [];
    private readonly AgentStatusMonitor _agentMonitor = new();
    private readonly ManualTransferController _manualTransfers = new();
    private readonly RecursiveTransferController _recursiveTransfers;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ToolStripStatusLabel _locationStatus = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _selectionStatus = new();
    private readonly ToolStripStatusLabel _speedStatus = new();
    private readonly ToolStripStatusLabel _queueStatus = new();
    private readonly ToolStripStatusLabel _agentStatus = new() { AccessibleName = "Agent status" };
    private readonly ToolStripStatusLabel _updateStatus = new()
    {
        AccessibleName = "Update status",
        IsLink = true,
        ToolTipText = "Check for StorageHub updates"
    };
    private readonly DesktopUpdatePreferencesStore _updatePreferencesStore;
    private readonly DesktopUpdater _updater;
    private readonly PackagedDesktopLifecycle? _packagedLifecycle;
    private readonly TabControl _workspaceTabs;
    private readonly TransferQueueControl _transferQueue;
    private readonly OverviewDashboardControl _overview;
    private readonly SyncTasksOverviewControl _syncTasks;
    private readonly ExternalEditorController _externalEditor;
    private ShellStatusSnapshot _status = ShellStatusSnapshot.Initial;
    private BrowserPaneControl? _activePane;
    private PaneClipboardSnapshot? _paneClipboard;
    private bool _changingWorkspaceTabs;
    private bool _monitorStarted;
    private bool _updaterStarted;
    private bool _agentRestartPending;

    public MainForm()
        : this(DesktopUpdatePreferencesStore.CreateDefault())
    {
    }

    internal MainForm(
        DesktopUpdatePreferencesStore updatePreferencesStore,
        IDesktopUpdateEngineFactory? updateEngineFactory = null,
        PackagedDesktopLifecycle? packagedLifecycle = null)
    {
        _updatePreferencesStore = updatePreferencesStore;
        _packagedLifecycle = packagedLifecycle;
        _updater = new DesktopUpdater(updatePreferencesStore, updateEngineFactory);
        _recursiveTransfers = new RecursiveTransferController(_manualTransfers);
        _externalEditor = new ExternalEditorController(updatePreferencesStore);
        _externalEditor.FileUploaded += ExternalEditorFileUploaded;
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
            Padding = new Point(39, 5),
            HotTrack = true,
            ShowToolTips = true,
            DrawMode = TabDrawMode.OwnerDrawFixed
        };
        _overview = new OverviewDashboardControl();
        _overview.NewWorkspaceRequested += (_, _) => AddWorkspace();
        _overview.ConnectionsRequested += (_, _) => ShowConnectionManager();
        _overview.SyncTasksRequested += (_, _) => _workspaceTabs.SelectedIndex = 1;
        _syncTasks = new SyncTasksOverviewControl();
        _syncTasks.NewProfileRequested += (_, _) => ShowSyncProfileEditor();
        _syncTasks.SchedulesRequested += (_, _) => ShowSchedules();
        _workspaceTabs.TabPages.Add(CreateFixedTab("Welcome", UiGlyph.Home, _overview));
        _workspaceTabs.TabPages.Add(CreateFixedTab("Sync tasks", UiGlyph.Compare, _syncTasks));
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
        _syncTasks.ReviewRunRequested += (_, _) => _transferQueue.SelectSyncRunsTab();
        _manualTransfers.TransfersEnqueued += ManualTransfersEnqueued;
        mainSplit.Panel2.Controls.Add(_transferQueue);

        var statusStrip = BuildStatusStrip();
        Controls.Add(mainSplit);
        Controls.Add(statusStrip);
        Controls.Add(toolbar);
        Controls.Add(menu);

        ApplyStatus(_status);
        _agentMonitor.StatusChanged += AgentMonitorStatusChanged;
        _updater.StatusChanged += UpdaterStatusChanged;
        _updater.RestartRequested += UpdaterRestartRequested;
        _updateStatus.Click += UpdateStatusClicked;
        ApplyUpdateStatus(_updater.Snapshot);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!_monitorStarted)
        {
            _monitorStarted = true;
            _agentMonitor.Start();
        }

        if (!_updaterStarted)
        {
            _updaterStarted = true;
            _ = RunAutomaticUpdaterAsync();
        }

        _ = _overview.RefreshAsync(_lifetime.Token);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lifetime.Cancel();
            _manualTransfers.TransfersEnqueued -= ManualTransfersEnqueued;
            _externalEditor.FileUploaded -= ExternalEditorFileUploaded;
            _workspaceTabs.SelectedIndexChanged -= WorkspaceTabsSelectedIndexChanged;
            _workspaceTabs.DrawItem -= WorkspaceTabsDrawItem;
            _workspaceTabs.MouseDown -= WorkspaceTabsMouseDown;
            _agentMonitor.StatusChanged -= AgentMonitorStatusChanged;
            _updater.StatusChanged -= UpdaterStatusChanged;
            _updater.RestartRequested -= UpdaterRestartRequested;
            _updateStatus.Click -= UpdateStatusClicked;
            _updater.Dispose();
            _agentMonitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _recursiveTransfers.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _manualTransfers.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _externalEditor.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
            ShowItemToolTips = true,
            Padding = new Padding(6, 3, 6, 3)
        };
        foreach (var menuName in UiCommandCatalog.TopMenus)
        {
            var root = new ToolStripMenuItem(menuName)
            {
                AccessibleName = $"{menuName} menu",
                Margin = new Padding(2, 0, 2, 0),
                Padding = new Padding(5, 0, 5, 0)
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
                if (definition.Glyph is { } glyph)
                {
                    item.Image = CreateOwnedIcon(glyph, 16);
                }
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
            AccessibleDescription = "Workspace and connection commands.",
            BackColor = StorageHubTheme.Surface,
            ForeColor = StorageHubTheme.Text,
            Renderer = StorageHubTheme.CreateToolStripRenderer(),
            Padding = new Padding(5, 4, 5, 4),
            AutoSize = true
        };
        toolbar.Items.Add(CreateToolbarButton(UiGlyph.Add, "New tab", (_, _) => AddWorkspace()));
        toolbar.Items.Add(CreateToolbarButton(UiGlyph.Connections, "Connection Manager", (_, _) => ShowConnectionManager()));
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
        status.Items.Add(new ToolStripSeparator());
        status.Items.Add(_updateStatus);
        return status;
    }

    private TabPage CreateWorkspace(string title)
    {
        var page = new TabPage(CreateTabLabel(title))
        {
            BackColor = StorageHubTheme.Canvas,
            AccessibleName = $"{title} workspace",
            ToolTipText = title,
            Tag = CreateTabMetadata(UiGlyph.Folder, closable: true)
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
        source.TransferDropRequested += (_, args) => EnqueuePaneDrop(page, source, args);
        destination.TransferDropRequested += (_, args) => EnqueuePaneDrop(page, destination, args);
        source.SelectionStaged += (_, args) => StagePaneSelection(source, args);
        destination.SelectionStaged += (_, args) => StagePaneSelection(destination, args);
        source.CanPaste = () => _paneClipboard is not null;
        destination.CanPaste = () => _paneClipboard is not null;
        source.PasteRequested += (_, _) => PasteIntoPane(source);
        destination.PasteRequested += (_, _) => PasteIntoPane(destination);
        source.EditRequested += (_, _) => EditSelectedFile(source);
        destination.EditRequested += (_, _) => EditSelectedFile(destination);
        source.ObjectInspectionRequested += (_, _) => ShowObjectInspector(source);
        destination.ObjectInspectionRequested += (_, _) => ShowObjectInspector(destination);
        source.ConnectionOpened += (_, args) => _overview.RecordRecentConnection(args.Connection);
        destination.ConnectionOpened += (_, args) => _overview.RecordRecentConnection(args.Connection);
        split.Panel1.Controls.Add(source);
        split.Panel2.Controls.Add(destination);
        page.Controls.Add(split);
        return page;
    }

    private TabPage CreateFixedTab(string title, UiGlyph glyph, Control content)
    {
        var page = new TabPage(CreateTabLabel(title))
        {
            BackColor = StorageHubTheme.Canvas,
            AccessibleName = title,
            ToolTipText = title,
            Tag = CreateTabMetadata(glyph, closable: false)
        };
        page.Controls.Add(content);
        return page;
    }

    private WorkspaceTabMetadata CreateTabMetadata(UiGlyph glyph, bool closable) =>
        new(closable, CreateOwnedIcon(glyph, 16));

    private static string CreateTabLabel(string title)
    {
        const int maximumCharacters = 20;
        return title.Length <= maximumCharacters
            ? title
            : string.Concat(title.AsSpan(0, maximumCharacters - 1), "\u2026");
    }

    private Bitmap CreateOwnedIcon(UiGlyph glyph, int size)
    {
        var image = UiIconFactory.Create(glyph, StorageHubTheme.Text, size, DeviceDpi / 96F);
        _ownedImages.Add(image);
        return image;
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
        item.Click += async (_, _) =>
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
                case "Review & Run...":
                    ShowSyncProfileEditor();
                    break;
                case "Copy":
                    StageFocusedPane(TransferQueueOperation.Copy);
                    break;
                case "Cut":
                    StageFocusedPane(TransferQueueOperation.Move);
                    break;
                case "Paste":
                    if (GetActivePane() is { } pasteDestination)
                    {
                        PasteIntoPane(pasteDestination);
                    }
                    break;
                case "Enqueue":
                    EnqueueFromFocusedPane(TransferQueueOperation.Copy);
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
                    ShowSchedules();
                    break;
                case "Settings...":
                    var preferencesBefore = _updatePreferencesStore.Load();
                    using (var dialog = new SettingsForm(
                               _updatePreferencesStore,
                               _updater.SavePreferences))
                    {
                        _ = dialog.ShowDialog(this);
                    }
                    var preferencesAfter = _updatePreferencesStore.Load();
                    if (!ConcurrencyEquals(preferencesBefore, preferencesAfter))
                    {
                        await ApplyConcurrencySettingsAsync();
                    }
                    break;
                case "Check for Updates...":
                    await CheckForUpdatesManuallyAsync();
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

        var metadata = page.Tag as WorkspaceTabMetadata;
        var iconBounds = new Rectangle(bounds.Left + 7, bounds.Top + Math.Max(0, (bounds.Height - 16) / 2), 16, 16);
        if (metadata is not null)
        {
            e.Graphics.DrawImage(metadata.Icon, iconBounds);
        }

        var closeBounds = GetWorkspaceCloseBounds(bounds);
        var textRight = metadata?.Closable == true ? closeBounds.Left - 5 : bounds.Right - 7;
        var textBounds = Rectangle.FromLTRB(iconBounds.Right + 5, bounds.Top, textRight, bounds.Bottom);
        TextRenderer.DrawText(
            e.Graphics,
            page.Text,
            Font,
            textBounds,
            StorageHubTheme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (metadata?.Closable != true)
        {
            return;
        }

        using var pen = new Pen(StorageHubTheme.TextMuted, Math.Max(1F, DeviceDpi / 96F * 1.4F));
        e.Graphics.DrawLine(pen, closeBounds.Left + 4, closeBounds.Top + 4, closeBounds.Right - 4, closeBounds.Bottom - 4);
        e.Graphics.DrawLine(pen, closeBounds.Right - 4, closeBounds.Top + 4, closeBounds.Left + 4, closeBounds.Bottom - 4);
    }

    private void WorkspaceTabsMouseDown(object? sender, MouseEventArgs e)
    {
        for (var index = 0; index < _workspaceTabs.TabPages.Count - 1; index++)
        {
            if (_workspaceTabs.TabPages[index].Tag is WorkspaceTabMetadata { Closable: true } &&
                GetWorkspaceCloseBounds(_workspaceTabs.GetTabRect(index)).Contains(e.Location))
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
        var workspaceNumber = _workspaceTabs.TabPages
            .Cast<TabPage>()
            .Count(static page => page.Tag is WorkspaceTabMetadata { Closable: true }) + 1;
        var page = CreateWorkspace($"Workspace {workspaceNumber}");
        _workspaceTabs.TabPages.Insert(insertAt, page);
        _workspaceTabs.SelectedTab = page;
    }

    private void CloseActiveWorkspace()
    {
        var selected = _workspaceTabs.SelectedTab;
        if (selected?.Tag is not WorkspaceTabMetadata { Closable: true })
        {
            return;
        }

        CloseWorkspaceAt(_workspaceTabs.TabPages.IndexOf(selected));
    }

    private void CloseWorkspaceAt(int index)
    {
        if ((uint)index >= (uint)(_workspaceTabs.TabPages.Count - 1) ||
            _workspaceTabs.TabPages[index].Tag is not WorkspaceTabMetadata { Closable: true })
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
        _ = _overview.RefreshAsync(_lifetime.Token);
    }

    private void ShowSyncProfileEditor()
    {
        using var dialog = new SyncProfileEditorForm();
        _ = dialog.ShowDialog(this);
        if (dialog.LastGeneratedRun is { } run)
        {
            _syncTasks.RecordRun(run);
        }

        _ = _syncTasks.RefreshAsync(_lifetime.Token);
    }

    private void ShowSchedules()
    {
        using var dialog = new ScheduleManagerForm();
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
        "Paste" or
        "Select All" or
        "Properties" or
        "Refresh" or
        "Back" or
        "Forward" or
        "Up" or
        "Connection Manager..." or
        "Enqueue" or
        "Review & Run..." or
        "Sync Profiles..." or
        "Schedules..." or
        "Settings..." or
        "Check for Updates..." or
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

    private void StageFocusedPane(TransferQueueOperation operation)
    {
        var pane = GetActivePane();
        if (pane is null)
        {
            return;
        }

        var selection = pane.CaptureSelectionSnapshot();
        if (selection.IsFailure)
        {
            ShowManualTransferFailure(selection.Error.Message);
            return;
        }

        StagePaneSelection(pane, new PaneSelectionStagedEventArgs(selection.Value, operation));
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
        if (page?.Tag is not WorkspaceTabMetadata { Closable: true })
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
        TransferQueueOperation operation,
        PaneSelectionSnapshot? capturedSelection = null)
    {
        if (_lifetime.IsCancellationRequested)
        {
            return;
        }

        PaneSelectionSnapshot selection;
        if (capturedSelection is null)
        {
            var captured = source.CaptureSelectionSnapshot();
            if (captured.IsFailure)
            {
                ShowManualTransferFailure(captured.Error.Message);
                return;
            }

            selection = captured.Value;
        }
        else
        {
            selection = capturedSelection;
        }

        var destinationSnapshot = destination.CaptureDestinationSnapshot();
        if (destinationSnapshot.IsFailure)
        {
            ShowManualTransferFailure(destinationSnapshot.Error.Message);
            return;
        }

        try
        {
            var result = selection.Items.Any(static item => item.IsContainer)
                ? await _recursiveTransfers.EnqueueAsync(
                    selection,
                    destinationSnapshot.Value,
                    operation,
                    _lifetime.Token).ConfigureAwait(true)
                : await _manualTransfers.EnqueueAsync(
                    selection,
                    destinationSnapshot.Value,
                    operation,
                    cancellationToken: _lifetime.Token).ConfigureAwait(true);
            if (result.HasAmbiguity)
            {
                ShowManualTransferFailure(DescribeAmbiguousEnqueue(
                    selection.Items.Count,
                    result.Accepted.Count,
                    result.AmbiguousTransferIds));
                return;
            }

            if (result.Failure is null)
            {
                if (selection.Items.Any(static item => item.IsContainer))
                {
                    _locationStatus.Text = result.Accepted.Count == 0
                        ? "The empty destination folder was created."
                        : $"Queued {result.Accepted.Count:N0} file(s) from the recursive folder manifest.";
                    if (result.Accepted.Count == 0)
                    {
                        destination.Reload();
                    }
                }

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
                    selection.Items.Count,
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

    private void EnqueuePaneDrop(
        TabPage workspace,
        BrowserPaneControl destination,
        PaneTransferDropRequestedEventArgs args)
    {
        if (ReferenceEquals(args.SourcePane, destination) ||
            !workspace.Contains(args.SourcePane) ||
            !workspace.Contains(destination))
        {
            return;
        }

        _ = EnqueueManualTransferAsync(
            args.SourcePane,
            destination,
            args.Operation,
            args.Selection);
    }

    private void StagePaneSelection(BrowserPaneControl source, PaneSelectionStagedEventArgs args)
    {
        _paneClipboard = new PaneClipboardSnapshot(source, args.Selection, args.Operation);
        var verb = args.Operation == TransferQueueOperation.Move ? "Cut" : "Copied";
        _locationStatus.Text = $"{verb} {args.Selection.Items.Count:N0} item(s). Choose a destination and paste.";
    }

    private void PasteIntoPane(BrowserPaneControl destination)
    {
        if (_paneClipboard is not { } clipboard)
        {
            return;
        }

        _ = EnqueueManualTransferAsync(
            clipboard.SourcePane,
            destination,
            clipboard.Operation,
            clipboard.Selection);
    }

    private async void EditSelectedFile(BrowserPaneControl pane)
    {
        var selected = pane.CaptureSelectionSnapshot();
        if (selected.IsFailure)
        {
            ShowManualTransferFailure(selected.Error.Message);
            return;
        }

        var snapshot = selected.Value;
        if (snapshot.Context.Kind != PaneTransferContextKind.SavedConnection ||
            snapshot.Context.ConnectionId is not { } connectionId ||
            string.IsNullOrWhiteSpace(snapshot.Context.RootIdentity) ||
            snapshot.Items.Count != 1 ||
            snapshot.Items[0] is not { Kind: StorageItemKind.File } item)
        {
            ShowManualTransferFailure("External editing requires exactly one file opened through a saved connection.");
            return;
        }

        var address = new ObjectInspectorAddress(
            connectionId,
            snapshot.Context.RootIdentity,
            item.RelativePath,
            item.NativeItemId,
            item.VersionId,
            item.EntityTag);
        if (!address.HasValidBounds)
        {
            ShowManualTransferFailure("The selected file does not have a valid bounded object identity.");
            return;
        }

        try
        {
            await _externalEditor.OpenAsync(
                this,
                address,
                item.Name,
                item.Length,
                _lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or TimeoutException)
        {
            _ = MessageBox.Show(
                this,
                $"StorageHub could not open the external editor. {error.Message}",
                "External editor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void ExternalEditorFileUploaded(object? sender, EventArgs e)
    {
        GetActivePane()?.Reload();
        _locationStatus.Text = "Edited file uploaded successfully.";
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
                if (_agentRestartPending && e.Status.ActiveTransfers + e.Status.ActiveSyncRuns == 0)
                {
                    _ = RestartAgentForConcurrencyAsync();
                }
            }));
        }
        catch (InvalidOperationException)
        {
            // The window handle can disappear between the guard and BeginInvoke during shutdown.
        }
    }

    private async Task ApplyConcurrencySettingsAsync()
    {
        if (_packagedLifecycle is null)
        {
            _locationStatus.Text = "Concurrency settings saved; restart StorageHub to apply them.";
            return;
        }

        if (_status.ActiveJobs > 0)
        {
            _agentRestartPending = true;
            _locationStatus.Text = "Concurrency settings saved; the Agent will restart when active work is safely idle.";
            return;
        }

        await RestartAgentForConcurrencyAsync();
    }

    private async Task RestartAgentForConcurrencyAsync()
    {
        if (_packagedLifecycle is null)
        {
            return;
        }

        if (_status.ActiveJobs > 0)
        {
            _agentRestartPending = true;
            return;
        }

        _agentRestartPending = false;
        _locationStatus.Text = "Applying concurrency settings to the background Agent…";
        var stopped = await _packagedLifecycle.TryStopAgentAsync(AgentShutdownReason.Restart, _lifetime.Token);
        var started = stopped
            ? await _packagedLifecycle.EnsureAgentAsync(_lifetime.Token)
            : new AgentEnsureResult(AgentEnsureStatus.LaunchFailed);
        _locationStatus.Text = started.IsReady
            ? "Adaptive concurrency settings are active."
            : "Concurrency settings were saved, but the background Agent could not restart.";
    }

    private static bool ConcurrencyEquals(
        DesktopUpdatePreferences left,
        DesktopUpdatePreferences right) =>
        left.AdaptiveConcurrency == right.AdaptiveConcurrency &&
        left.MinimumConcurrency == right.MinimumConcurrency &&
        left.MaximumTransferConcurrency == right.MaximumTransferConcurrency &&
        left.PerConnectionConcurrency == right.PerConnectionConcurrency &&
        left.MaximumSyncConcurrency == right.MaximumSyncConcurrency;

    private async Task RunAutomaticUpdaterAsync()
    {
        try
        {
            await _updater.RunAutomaticAsync(_lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            // Window shutdown or an update-settings opt-out cancels background update work.
        }
    }

    private async void UpdateStatusClicked(object? sender, EventArgs e) =>
        await CheckForUpdatesManuallyAsync();

    private async Task CheckForUpdatesManuallyAsync()
    {
        if (_updater.Snapshot.State == DesktopUpdateState.ReadyToRestart)
        {
            PromptToRestartForUpdate(_updater.Snapshot.Version);
            return;
        }

        DesktopUpdateSnapshot snapshot;
        try
        {
            snapshot = await _updater.CheckForUpdatesAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }

        switch (snapshot.State)
        {
            case DesktopUpdateState.UpdateAvailable:
                var download = MessageBox.Show(
                    this,
                    $"StorageHub {snapshot.Version} is available. Download it now?",
                    "StorageHub update",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information);
                if (download != DialogResult.Yes)
                {
                    return;
                }

                try
                {
                    snapshot = await _updater.DownloadAvailableAsync(_lifetime.Token);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    return;
                }

                if (snapshot.State == DesktopUpdateState.ReadyToRestart)
                {
                    PromptToRestartForUpdate(snapshot.Version);
                }
                else if (snapshot.State == DesktopUpdateState.Failed)
                {
                    ShowUpdateMessage(snapshot.Message, MessageBoxIcon.Warning);
                }

                break;
            case DesktopUpdateState.UpToDate:
                ShowUpdateMessage("You already have the newest release available on your selected channel.", MessageBoxIcon.Information);
                break;
            case DesktopUpdateState.Unavailable:
                ShowUpdateMessage(
                    "Automatic updates are available only in an installed StorageHub build. Portable and developer builds are never modified.",
                    MessageBoxIcon.Information);
                break;
            case DesktopUpdateState.Failed:
                ShowUpdateMessage(snapshot.Message, MessageBoxIcon.Warning);
                break;
        }
    }

    private void PromptToRestartForUpdate(string? version)
    {
        var restart = MessageBox.Show(
            this,
            $"StorageHub {version ?? "update"} is downloaded and integrity-checked. Restart now to install it silently?\n\nDurable queued work is preserved.",
            "StorageHub update ready",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information);
        if (restart == DialogResult.Yes && !_updater.ApplyAndRestart())
        {
            ShowUpdateMessage("StorageHub could not start the updater. Try again after reopening the application.", MessageBoxIcon.Warning);
        }
    }

    private void ShowUpdateMessage(string message, MessageBoxIcon icon) =>
        _ = MessageBox.Show(
            this,
            message,
            "StorageHub updates",
            MessageBoxButtons.OK,
            icon);

    private void UpdaterStatusChanged(object? sender, DesktopUpdateSnapshot snapshot)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        if (!InvokeRequired)
        {
            ApplyUpdateStatus(snapshot);
            return;
        }

        try
        {
            BeginInvoke(new Action(() =>
            {
                if (!IsDisposed && !Disposing)
                {
                    ApplyUpdateStatus(snapshot);
                }
            }));
        }
        catch (InvalidOperationException)
        {
            // The window handle can disappear between the guard and BeginInvoke during shutdown.
        }
    }

    private void UpdaterRestartRequested(object? sender, EventArgs e)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action(Close));
            }
            catch (InvalidOperationException)
            {
                // The updater has already been staged; normal process shutdown will release it.
            }

            return;
        }

        Close();
    }

    private void ApplyUpdateStatus(DesktopUpdateSnapshot snapshot)
    {
        _updateStatus.Text = snapshot.Message;
        _updateStatus.AccessibleDescription = snapshot.Message;
        _updateStatus.ForeColor = snapshot.State switch
        {
            DesktopUpdateState.ReadyToRestart => StorageHubTheme.Success,
            DesktopUpdateState.Installing => StorageHubTheme.Success,
            DesktopUpdateState.UpdateAvailable => StorageHubTheme.Warning,
            DesktopUpdateState.Failed => StorageHubTheme.Danger,
            _ => StorageHubTheme.TextMuted
        };
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
        _overview.UpdateAgentStatus(status);
    }

    private sealed record WorkspaceTabMetadata(bool Closable, Image Icon);

    private sealed record PaneClipboardSnapshot(
        BrowserPaneControl SourcePane,
        PaneSelectionSnapshot Selection,
        TransferQueueOperation Operation);
}
