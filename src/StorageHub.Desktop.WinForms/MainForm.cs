using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

public sealed class MainForm : Form
{
    private readonly List<Image> _ownedImages = [];
    private readonly AgentStatusMonitor _agentMonitor = new();
    private readonly ManualTransferController _manualTransfers = new();
    private readonly NamedPipeTransferQueueAgentClient _shellTransfers = new();
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
    private readonly MenuStrip _menu;
    private readonly DesktopUpdater _updater;
    private readonly PackagedDesktopLifecycle? _packagedLifecycle;
    private readonly bool _explorerDropBrokerAvailable;
    private readonly TabControl _workspaceTabs;
    private readonly TransferQueueControl _transferQueue;
    private readonly OverviewDashboardControl _overview;
    private readonly SyncTasksOverviewControl _syncTasks;
    private readonly ExternalEditorController _externalEditor;
    private readonly Icon? _windowIcon;
    private ShellStatusSnapshot _status = ShellStatusSnapshot.Initial;
    private BrowserPaneControl? _activePane;
    private PaneClipboardSnapshot? _paneClipboard;
    private bool _changingWorkspaceTabs;
    private bool _workspaceAddPending;
    private bool _monitorStarted;
    private bool _updaterStarted;
    private bool _agentRestartPending;
    private bool _agentRecoveryInProgress;
    private int _nextWorkspaceNumber = 1;

    public MainForm()
        : this(DesktopUpdatePreferencesStore.CreateDefault())
    {
    }

    internal MainForm(
        DesktopUpdatePreferencesStore updatePreferencesStore,
        IDesktopUpdateEngineFactory? updateEngineFactory = null,
        PackagedDesktopLifecycle? packagedLifecycle = null,
        bool explorerDropBrokerAvailable = true)
    {
        _updatePreferencesStore = updatePreferencesStore;
        _packagedLifecycle = packagedLifecycle;
        _explorerDropBrokerAvailable = explorerDropBrokerAvailable;
        _updater = new DesktopUpdater(updatePreferencesStore, updateEngineFactory);
        _recursiveTransfers = new RecursiveTransferController(_manualTransfers);
        _externalEditor = new ExternalEditorController(updatePreferencesStore);
        _externalEditor.FileUploaded += ExternalEditorFileUploaded;
        Text = "StorageHub";
        _windowIcon = LoadWindowIcon();
        if (_windowIcon is not null)
        {
            Icon = _windowIcon;
        }
        AccessibleName = "StorageHub file manager";
        AccessibleDescription = "A secure multi-pane file manager for local and remote storage.";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1120, 720);
        Size = new Size(1500, 920);
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = StorageHubTheme.Canvas;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        StorageHubTheme.Register(this);

        _menu = BuildMenu();
        MainMenuStrip = _menu;
        var toolbar = BuildToolbar();

        _workspaceTabs = new TabControl
        {
            Dock = DockStyle.Fill,
            AccessibleName = "Workspace tabs",
            AccessibleDescription = "Named workspaces contain one to four equal-capability browser panes.",
            HotTrack = true,
            ShowToolTips = true
        };
        StorageHubTheme.ConfigureTabs(_workspaceTabs);
        _workspaceTabs.DrawItem += WorkspaceTabsDrawItem;
        _workspaceTabs.MouseDown += WorkspaceTabsMouseDown;
        _overview = new OverviewDashboardControl();
        _overview.NewWorkspaceRequested += (_, _) => ChooseAndAddWorkspace();
        _overview.ConnectionsRequested += (_, _) => ShowConnectionManager();
        _overview.SyncTasksRequested += (_, _) => _workspaceTabs.SelectedIndex = 1;
        _syncTasks = new SyncTasksOverviewControl();
        _syncTasks.NewProfileRequested += (_, _) => ShowSyncProfileEditor();
        _syncTasks.SchedulesRequested += (_, _) => ShowSchedules();
        _workspaceTabs.TabPages.Add(CreateFixedTab("Welcome", UiGlyph.Home, _overview));
        _workspaceTabs.TabPages.Add(CreateFixedTab("Sync tasks", UiGlyph.Compare, _syncTasks));
        _workspaceTabs.TabPages.Add(new TabPage("+")
        {
            ToolTipText = "New workspace",
            AccessibleName = "New workspace tab"
        });
        _workspaceTabs.Selecting += WorkspaceTabsSelecting;
        _workspaceTabs.SelectedIndexChanged += (_, _) => UpdateWorkspaceCommandState();

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
        _transferQueue = new TransferQueueControl(_updatePreferencesStore);
        _manualTransfers.TransfersEnqueued += ManualTransfersEnqueued;
        mainSplit.Panel2.Controls.Add(_transferQueue);

        var statusStrip = BuildStatusStrip();
        Controls.Add(mainSplit);
        Controls.Add(statusStrip);
        Controls.Add(toolbar);
        Controls.Add(_menu);

        ApplyStatus(_status);
        _agentMonitor.StatusChanged += AgentMonitorStatusChanged;
        _updater.StatusChanged += UpdaterStatusChanged;
        _updater.RestartRequested += UpdaterRestartRequested;
        _updateStatus.Click += UpdateStatusClicked;
        ApplyUpdateStatus(_updater.Snapshot);
        UpdateWorkspaceCommandState();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _menu.Renderer = DesktopAppearanceService.MenuRenderer;
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

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!e.Cancel && Visible)
        {
            foreach (var page in _workspaceTabs.TabPages.Cast<TabPage>().ToArray())
            {
                if (page.Controls.OfType<WorkspaceControl>().SingleOrDefault() is not { IsDirty: true } workspace) continue;
                var choice = MessageBox.Show(this, $"Save changes to {workspace.WorkspaceName}?",
                    "Exit StorageHub", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (choice == DialogResult.Cancel || choice == DialogResult.Yes && !SaveWorkspace(workspace, page))
                {
                    e.Cancel = true;
                    break;
                }
            }
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lifetime.Cancel();
            _manualTransfers.TransfersEnqueued -= ManualTransfersEnqueued;
            _externalEditor.FileUploaded -= ExternalEditorFileUploaded;
            _workspaceTabs.Selecting -= WorkspaceTabsSelecting;
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
            _shellTransfers.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _externalEditor.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _lifetime.Dispose();
            _windowIcon?.Dispose();
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
            Renderer = DesktopAppearanceService.MenuRenderer,
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
            Padding = new Padding(5, 4, 5, 4),
            AutoSize = true
        };
        toolbar.Items.Add(CreateToolbarButton(UiGlyph.Add, "New workspace", (_, _) => ChooseAndAddWorkspace()));
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
        source.ShellImportDropRequested += (_, args) => _ = ReviewShellImportAsync(args);
        destination.ShellImportDropRequested += (_, args) => _ = ReviewShellImportAsync(args);
        if (_explorerDropBrokerAvailable)
        {
            source.BeginExplorerDropAsync = BeginExplorerDropAsync;
            destination.BeginExplorerDropAsync = BeginExplorerDropAsync;
            source.CommitExplorerDropAsync = CommitExplorerDropAsync;
            destination.CommitExplorerDropAsync = CommitExplorerDropAsync;
        }
        else
        {
            const string unavailableReason =
                "Drag to File Explorer is unavailable because the StorageHub Explorer integration could not be registered. Repair the installation or restart StorageHub.";
            source.ExplorerDropUnavailableReason = unavailableReason;
            destination.ExplorerDropUnavailableReason = unavailableReason;
        }
        source.SelectionStaged += (_, args) => StagePaneSelection(source, args);
        destination.SelectionStaged += (_, args) => StagePaneSelection(destination, args);
        source.CanPaste = () => _paneClipboard is not null;
        destination.CanPaste = () => _paneClipboard is not null;
        source.PasteRequested += (_, _) => PasteIntoPane(source);
        destination.PasteRequested += (_, _) => PasteIntoPane(destination);
        source.DeleteRequested += (_, _) => _ = ReviewDeleteAsync(source);
        destination.DeleteRequested += (_, _) => _ = ReviewDeleteAsync(destination);
        source.EditRequested += (_, _) => EditSelectedFile(source);
        destination.EditRequested += (_, _) => EditSelectedFile(destination);
        source.ObjectInspectionRequested += (_, _) => ShowObjectInspector(source);
        destination.ObjectInspectionRequested += (_, _) => ShowObjectInspector(destination);
        source.ConnectionOpened += (_, args) => _overview.RecordRecentConnection(args.Connection);
        destination.ConnectionOpened += (_, args) => _overview.RecordRecentConnection(args.Connection);
        split.Panel1.Controls.Add(source);
        split.Panel2.Controls.Add(destination);

        var layoutToolbar = new ToolStrip
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 40,
            GripStyle = ToolStripGripStyle.Hidden,
            BackColor = StorageHubTheme.SurfaceMuted,
            ForeColor = StorageHubTheme.Text,
            ImageScalingSize = new Size(20, 20),
            Padding = new Padding(6, 5, 6, 5),
            AccessibleName = $"{title} workspace layout"
        };
        var layoutMenu = new ToolStripDropDownButton("Layout: Side by side")
        {
            Image = CreateOwnedIcon(UiGlyph.Compare, 18),
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            AccessibleName = "Workspace pane layout",
            ToolTipText = "Arrange the two workspace panes"
        };
        var sideBySide = new ToolStripMenuItem("Side by side")
        {
            Checked = true,
            Image = CreateOwnedIcon(UiGlyph.Compare, 16)
        };
        var topAndBottom = new ToolStripMenuItem("Top and bottom")
        {
            Image = CreateOwnedIcon(UiGlyph.More, 16)
        };
        sideBySide.Click += (_, _) => SetWorkspaceOrientation(
            split,
            layoutMenu,
            sideBySide,
            topAndBottom,
            stacked: false);
        topAndBottom.Click += (_, _) => SetWorkspaceOrientation(
            split,
            layoutMenu,
            sideBySide,
            topAndBottom,
            stacked: true);
        layoutMenu.DropDownItems.Add(sideBySide);
        layoutMenu.DropDownItems.Add(topAndBottom);
        layoutToolbar.Items.Add(layoutMenu);
        layoutToolbar.Items.Add(new ToolStripSeparator());
        layoutToolbar.Items.Add(new ToolStripLabel("CLIPBOARD")
        {
            Image = CreateOwnedIcon(UiGlyph.File, 18),
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            ForeColor = StorageHubTheme.TextMuted,
            ToolTipText = "StorageHub's staged file selection"
        });
        var clipboardStatus = new ToolStripLabel("Empty")
        {
            Name = "WorkspaceClipboardStatus",
            ForeColor = StorageHubTheme.Text,
            ToolTipText = "Copy or move files from either storage pane"
        };
        var pasteClipboard = new ToolStripButton("Paste to active pane")
        {
            Name = "WorkspaceClipboardPaste",
            Image = CreateOwnedIcon(UiGlyph.Save, 18),
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            Enabled = false,
            ToolTipText = "Review and paste the staged selection into the active storage pane"
        };
        pasteClipboard.Click += (_, _) =>
        {
            var target = destination.ContainsFocus ? destination : source.ContainsFocus ? source : destination;
            PasteIntoPane(target);
        };
        var clearClipboard = new ToolStripButton("Clear")
        {
            Name = "WorkspaceClipboardClear",
            Image = CreateOwnedIcon(UiGlyph.Delete, 18),
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            Enabled = false,
            ToolTipText = "Clear the StorageHub clipboard"
        };
        clearClipboard.Click += (_, _) => ClearPaneClipboard();
        layoutToolbar.Items.Add(clipboardStatus);
        layoutToolbar.Items.Add(pasteClipboard);
        layoutToolbar.Items.Add(clearClipboard);

        page.Controls.Add(split);
        page.Controls.Add(layoutToolbar);
        if (_updatePreferencesStore.Load().DefaultWorkspaceLayout == WorkspaceLayout.TopAndBottom)
        {
            SetWorkspaceOrientation(
                split,
                layoutMenu,
                sideBySide,
                topAndBottom,
                stacked: true);
        }
        RefreshPaneClipboardPresentation();
        return page;
    }

    private Task<ExplorerDropBeginResponse> BeginExplorerDropAsync(
        PaneSelectionSnapshot selection,
        CancellationToken cancellationToken)
    {
        if (selection.Context.ConnectionId is not { } connectionId ||
            string.IsNullOrWhiteSpace(selection.Context.RootIdentity))
        {
            return Task.FromResult(new ExplorerDropBeginResponse(
                ShellTransferIpcContract.CurrentVersion,
                null,
                null,
                new StorageIpcFailure(
                    "shell-transfer.export.invalid",
                    StorageIpcFailureCategory.Validation,
                    "Explorer export requires an open saved connection.",
                    false)));
        }

        var sources = selection.Items.Select(item => new ShellExportSource(
            new TransferQueueAddress(
                connectionId,
                selection.Context.RootIdentity,
                item.RelativePath,
                item.NativeItemId,
                item.VersionId,
                item.EntityTag),
            item.IsContainer,
            item.Name)).ToArray();
        return _shellTransfers.BeginExplorerDropAsync(new ShellExportPrepareRequest(
            ShellTransferIpcContract.CurrentVersion,
            sources), cancellationToken);
    }

    private Task<ExplorerDropCommitResponse> CommitExplorerDropAsync(
        string token,
        CancellationToken cancellationToken) =>
        _shellTransfers.CommitExplorerDropAsync(new ExplorerDropCommitRequest(
            ShellTransferIpcContract.CurrentVersion,
            token), cancellationToken);

    private static void SetWorkspaceOrientation(
        SplitContainer split,
        ToolStripDropDownButton layoutMenu,
        ToolStripMenuItem sideBySide,
        ToolStripMenuItem topAndBottom,
        bool stacked)
    {
        split.Panel1MinSize = 0;
        split.Panel2MinSize = 0;
        split.Orientation = stacked ? Orientation.Horizontal : Orientation.Vertical;
        var available = stacked ? split.ClientSize.Height : split.ClientSize.Width;
        if (available > split.SplitterWidth)
        {
            var half = (available - split.SplitterWidth) / 2;
            split.SplitterDistance = half;
            var desiredMinimum = stacked ? 150 : 300;
            var effectiveMinimum = Math.Min(desiredMinimum, half);
            split.Panel1MinSize = effectiveMinimum;
            split.Panel2MinSize = effectiveMinimum;
        }

        sideBySide.Checked = !stacked;
        topAndBottom.Checked = stacked;
        layoutMenu.Text = stacked ? "Layout: Top and bottom" : "Layout: Side by side";
        split.AccessibleDescription = stacked
            ? "SSH or storage panes arranged from top to bottom."
            : "SSH or storage panes arranged from left to right.";
    }

    private TabPage CreateFixedTab(string title, UiGlyph glyph, Control content)
    {
        var page = new TabPage(CreateTabLabel(title))
        {
            AccessibleName = title,
            ToolTipText = title,
            Tag = CreateTabMetadata(glyph, closable: false)
        };
        page.Controls.Add(content);
        return page;
    }

    private WorkspaceTabMetadata CreateTabMetadata(UiGlyph glyph, bool closable) =>
        new(closable, CreateOwnedIcon(glyph, 16));

    private static string CreateTabLabel(string title) => title;

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
                case "New Workspace...":
                    ChooseAndAddWorkspace();
                    break;
                case "Open Workspace...":
                    await OpenWorkspaceAsync();
                    break;
                case "Save Workspace":
                    SaveActiveWorkspace(saveAs: false);
                    break;
                case "Save Workspace As...":
                    SaveActiveWorkspace(saveAs: true);
                    break;
                case "Rename Workspace...":
                    RenameActiveWorkspace();
                    break;
                case "Close Workspace":
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
                    _workspaceTabs.SelectedIndex = 1;
                    _syncTasks.ShowRunReview();
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

    private void WorkspaceTabsSelecting(object? sender, TabControlCancelEventArgs e)
    {
        if (!_changingWorkspaceTabs && e.TabPage == _workspaceTabs.TabPages[^1])
        {
            e.Cancel = true;
            if (_workspaceAddPending)
            {
                return;
            }

            _workspaceAddPending = true;
            _workspaceTabs.BeginInvoke(() =>
            {
                _workspaceAddPending = false;
                if (!_workspaceTabs.IsDisposed)
                {
                    ChooseAndAddWorkspace();
                }
            });
        }
    }

    private void WorkspaceTabsDrawItem(object? sender, DrawItemEventArgs e)
    {
        var page = _workspaceTabs.TabPages[e.Index];
        var bounds = _workspaceTabs.GetTabRect(e.Index);
        using var brush = new SolidBrush(e.Index == _workspaceTabs.SelectedIndex
            ? StorageHubTheme.Surface
            : StorageHubTheme.SurfaceMuted);
        e.Graphics.FillRectangle(brush, bounds);

        if (page == _workspaceTabs.TabPages[^1])
        {
            TextRenderer.DrawText(e.Graphics, page.Text, Font, bounds, StorageHubTheme.Text,
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
        TextRenderer.DrawText(e.Graphics, page.Text, Font,
            Rectangle.FromLTRB(iconBounds.Right + 5, bounds.Top, textRight, bounds.Bottom),
            StorageHubTheme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (metadata?.Closable == true)
        {
            using var pen = new Pen(StorageHubTheme.TextMuted, Math.Max(1F, DeviceDpi / 96F * 1.4F));
            e.Graphics.DrawLine(pen, closeBounds.Left + 4, closeBounds.Top + 4, closeBounds.Right - 4, closeBounds.Bottom - 4);
            e.Graphics.DrawLine(pen, closeBounds.Right - 4, closeBounds.Top + 4, closeBounds.Left + 4, closeBounds.Bottom - 4);
        }
    }

    private void WorkspaceTabsMouseDown(object? sender, MouseEventArgs e)
    {
        for (var index = 0; index < _workspaceTabs.TabPages.Count - 1; index++)
        {
            if (e.Button == MouseButtons.Left &&
                _workspaceTabs.TabPages[index].Tag is WorkspaceTabMetadata { Closable: true } &&
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

    private void ChooseAndAddWorkspace()
    {
        using var chooser = new NewWorkspaceForm(_updatePreferencesStore.Load().DefaultWorkspaceLayout);
        if (chooser.ShowDialog(this) == DialogResult.OK)
        {
            AddWorkspace(chooser.PaneCount);
        }
    }

    internal TabPage AddWorkspace(int paneCount = 2)
    {
        var insertAt = _workspaceTabs.TabPages.Count - 1;
        var workspaceNumber = _nextWorkspaceNumber++;
        while (_workspaceTabs.TabPages.Cast<TabPage>()
            .SelectMany(page => page.Controls.OfType<WorkspaceControl>())
            .Any(workspace => string.Equals(workspace.WorkspaceName, $"Workspace {workspaceNumber}", StringComparison.OrdinalIgnoreCase)))
        {
            workspaceNumber = _nextWorkspaceNumber++;
        }
        var name = $"Workspace {workspaceNumber}";
        var page = CreateCustomWorkspace(
            name,
            WorkspaceLayoutModel.CreatePreset(paneCount, _updatePreferencesStore.Load().DefaultWorkspaceLayout));
        _workspaceTabs.TabPages.Insert(insertAt, page);
        _workspaceTabs.SelectedTab = page;
        return page;
    }

    private TabPage CreateCustomWorkspace(
        string name,
        WorkspaceLayoutModel layout,
        IReadOnlyDictionary<Guid, BrowserPaneState>? states = null)
    {
        var page = new TabPage(name)
        {
            AccessibleName = $"{name} workspace",
            ToolTipText = name,
            Tag = CreateTabMetadata(UiGlyph.Folder, closable: true)
        };
        WorkspaceControl? workspace = null;
        workspace = new WorkspaceControl(name, layout, pane => ConfigureWorkspacePane(page, pane), states);
        workspace.WorkspaceChanged += (_, _) => UpdateWorkspaceTab(page, workspace);
        workspace.ActivePaneChanged += (_, _) => _activePane = workspace.ActivePane;
        var toolbar = workspace.Controls.OfType<ToolStrip>().Single();
        if (toolbar.Items["WorkspaceClipboardPaste"] is ToolStripButton paste)
            paste.Click += (_, _) => { if (workspace.ActivePane is { } pane) PasteIntoPane(pane); };
        if (toolbar.Items["WorkspaceClipboardClear"] is ToolStripButton clear)
            clear.Click += (_, _) => ClearPaneClipboard();
        page.Controls.Add(workspace);
        UpdateWorkspaceTab(page, workspace);
        RefreshPaneClipboardPresentation();
        return page;
    }

    private void ConfigureWorkspacePane(TabPage page, BrowserPaneControl pane)
    {
        pane.Enter += ActivePaneEntered;
        pane.TransferDropRequested += (_, args) => EnqueuePaneDrop(page, pane, args);
        pane.ShellImportDropRequested += (_, args) => _ = ReviewShellImportAsync(args);
        if (_explorerDropBrokerAvailable)
        {
            pane.BeginExplorerDropAsync = BeginExplorerDropAsync;
            pane.CommitExplorerDropAsync = CommitExplorerDropAsync;
        }
        else
        {
            pane.ExplorerDropUnavailableReason =
                "Drag to File Explorer is unavailable because the StorageHub Explorer integration could not be registered. Repair the installation or restart StorageHub.";
        }
        pane.SelectionStaged += (_, args) => StagePaneSelection(pane, args);
        pane.CanPaste = () => _paneClipboard is not null;
        pane.PasteRequested += (_, _) => PasteIntoPane(pane);
        pane.DeleteRequested += (_, _) => _ = ReviewDeleteAsync(pane);
        pane.EditRequested += (_, _) => EditSelectedFile(pane);
        pane.ObjectInspectionRequested += (_, _) => ShowObjectInspector(pane);
        pane.ConnectionOpened += (_, args) => _overview.RecordRecentConnection(args.Connection);
    }

    private static void UpdateWorkspaceTab(TabPage page, WorkspaceControl workspace)
    {
        page.Text = workspace.WorkspaceName + (workspace.IsDirty ? " *" : string.Empty);
        page.ToolTipText = workspace.FilePath ?? workspace.WorkspaceName;
        page.AccessibleName = $"{workspace.WorkspaceName} workspace";
    }

    private WorkspaceControl? GetActiveWorkspace() =>
        _workspaceTabs.SelectedTab?.Controls.OfType<WorkspaceControl>().SingleOrDefault();

    private async Task OpenWorkspaceAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Open Workspace",
            Filter = WorkspaceFileStore.Filter,
            DefaultExt = "shw",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var normalized = Path.GetFullPath(dialog.FileName);
        var existing = _workspaceTabs.TabPages.Cast<TabPage>()
            .FirstOrDefault(page => page.Controls.OfType<WorkspaceControl>().SingleOrDefault() is { FilePath: { } path } &&
                string.Equals(Path.GetFullPath(path), normalized, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) { _workspaceTabs.SelectedTab = existing; return; }
        try
        {
            var document = WorkspaceFileStore.Load(normalized);
            var layout = new WorkspaceLayoutModel(WorkspaceFileStore.ToLayout(document.Layout));
            var page = CreateCustomWorkspace(document.Name, layout, document.Panes);
            _workspaceTabs.TabPages.Insert(_workspaceTabs.TabPages.Count - 1, page);
            _workspaceTabs.SelectedTab = page;
            var workspace = page.Controls.OfType<WorkspaceControl>().Single();
            await workspace.HydrateAsync(
                document.Panes,
                document.ActivePaneId,
                _updatePreferencesStore.Load().ReconnectRemotePanesAutomatically,
                _lifetime.Token).ConfigureAwait(true);
            workspace.AssociateFile(normalized);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            _ = MessageBox.Show(this,
                $"StorageHub could not open this workspace. {error.Message}",
                "Open Workspace", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private bool SaveActiveWorkspace(bool saveAs)
    {
        var workspace = GetActiveWorkspace();
        if (workspace is null) return false;
        var path = saveAs ? null : workspace.FilePath;
        if (path is null)
        {
            using var dialog = new SaveFileDialog
            {
                Title = "Save Workspace",
                Filter = WorkspaceFileStore.Filter,
                DefaultExt = "shw",
                AddExtension = true,
                FileName = SanitizeWorkspaceFileName(workspace.WorkspaceName) + ".shw"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return false;
            path = dialog.FileName;
        }
        try
        {
            workspace.Save(path);
            if (_workspaceTabs.SelectedTab is { } page) UpdateWorkspaceTab(page, workspace);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            _ = MessageBox.Show(this, $"StorageHub could not save this workspace. {error.Message}",
                "Save Workspace", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private void RenameActiveWorkspace()
    {
        var workspace = GetActiveWorkspace();
        if (workspace is null) return;
        using var dialog = new Form
        {
            Text = "Rename Workspace",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            ClientSize = new Size(420, 120),
            MinimizeBox = false,
            MaximizeBox = false
        };
        var name = new TextBox { Text = workspace.WorkspaceName, Left = 14, Top = 16, Width = 390, MaxLength = 128 };
        var accept = new Button { Text = "Rename", DialogResult = DialogResult.OK, Left = 230, Top = 62, Width = 82 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 322, Top = 62, Width = 82 };
        dialog.Controls.AddRange([name, accept, cancel]);
        dialog.AcceptButton = accept; dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(name.Text))
            workspace.WorkspaceName = name.Text;
    }

    private static string SanitizeWorkspaceFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var value = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(value) ? "Workspace" : value;
    }

    private void UpdateWorkspaceCommandState()
    {
        var active = GetActiveWorkspace() is not null;
        var root = _menu.Items.OfType<ToolStripMenuItem>().FirstOrDefault(item => item.Text == "Workspace");
        if (root is null) return;
        foreach (ToolStripItem item in root.DropDownItems)
        {
            item.Enabled = item.Text is "New Workspace..." or "Open Workspace..." or "Exit" || active;
        }
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
        if (page.Controls.OfType<WorkspaceControl>().SingleOrDefault() is { IsDirty: true } workspace && Visible)
        {
            var choice = MessageBox.Show(this,
                $"Save changes to {workspace.WorkspaceName}?",
                "Close Workspace", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (choice == DialogResult.Cancel || choice == DialogResult.Yes && !SaveWorkspace(workspace, page)) return;
        }
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

    private bool SaveWorkspace(WorkspaceControl workspace, TabPage page)
    {
        var previous = _workspaceTabs.SelectedTab;
        _workspaceTabs.SelectedTab = page;
        var saved = SaveActiveWorkspace(saveAs: false);
        if (previous is not null && _workspaceTabs.TabPages.Contains(previous)) _workspaceTabs.SelectedTab = previous;
        return saved;
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
        var workspace = GetActiveWorkspace();
        if (workspace is null) return null;
        return _activePane is not null && workspace.Contains(_activePane)
            ? _activePane
            : workspace.ActivePane;
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
        "New Workspace..." or
        "Open Workspace..." or
        "Save Workspace" or
        "Save Workspace As..." or
        "Rename Workspace..." or
        "Close Workspace" or
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
        if (GetActivePane() is { } pane) ShowObjectInspector(pane);
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

        _locationStatus.Text = "Indexing the destination folder for conflict review…";
        if (!await destination.EnsureListingCompleteAsync(_lifetime.Token).ConfigureAwait(true))
        {
            ShowManualTransferFailure("StorageHub could not finish indexing the destination folder.");
            return;
        }

        var destinationSnapshot = destination.CaptureDestinationSnapshot(
            selection.Items.Select(static item => item.Name).ToArray());
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
        RefreshPaneClipboardPresentation();
    }

    private void PasteIntoPane(BrowserPaneControl destination)
    {
        if (_paneClipboard is not { } clipboard)
        {
            return;
        }

        var operation = clipboard.Operation == TransferQueueOperation.Move ? "MOVE" : "COPY";
        var itemSummary = clipboard.Selection.Items.Count == 1
            ? clipboard.Selection.Items[0].Name
            : $"{clipboard.Selection.Items.Count:N0} selected items";
        var decision = MessageBox.Show(
            this,
            $"You are about to {operation} {itemSummary}.\n\n" +
            $"From: {DescribePaneContext(clipboard.Selection.Context)}\n" +
            $"To: {destination.PaneDisplayName}\n\n" +
            (clipboard.Operation == TransferQueueOperation.Move
                ? "The originals are removed after the transfer completes successfully."
                : "The originals will remain in place."),
            $"Review {operation.ToLowerInvariant()}",
            MessageBoxButtons.OKCancel,
            clipboard.Operation == TransferQueueOperation.Move
                ? MessageBoxIcon.Warning
                : MessageBoxIcon.Information,
            MessageBoxDefaultButton.Button2);
        if (decision != DialogResult.OK)
        {
            return;
        }

        _ = EnqueueManualTransferAsync(
            clipboard.SourcePane,
            destination,
            clipboard.Operation,
            clipboard.Selection);
    }

    private void ClearPaneClipboard()
    {
        _paneClipboard = null;
        _locationStatus.Text = "StorageHub clipboard cleared.";
        RefreshPaneClipboardPresentation();
    }

    private void RefreshPaneClipboardPresentation()
    {
        var description = _paneClipboard is { } clipboard
            ? $"{(clipboard.Operation == TransferQueueOperation.Move ? "Move" : "Copy")} · " +
              $"{clipboard.Selection.Items.Count:N0} item(s) · {DescribePaneContext(clipboard.Selection.Context)}"
            : "Empty";
        foreach (var toolbar in FindControls<ToolStrip>(_workspaceTabs))
        {
            if (toolbar.Items["WorkspaceClipboardStatus"] is ToolStripLabel status)
            {
                status.Text = description;
            }

            if (toolbar.Items["WorkspaceClipboardPaste"] is ToolStripButton paste)
            {
                paste.Enabled = _paneClipboard is not null;
            }

            if (toolbar.Items["WorkspaceClipboardClear"] is ToolStripButton clear)
            {
                clear.Enabled = _paneClipboard is not null;
            }
        }

        foreach (var pane in FindControls<BrowserPaneControl>(_workspaceTabs))
        {
            pane.RefreshCommandState();
        }
    }

    private static IEnumerable<T> FindControls<T>(Control root) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindControls<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static string DescribePaneContext(PaneTransferContext context) => context.Kind switch
    {
        PaneTransferContextKind.SavedConnection => string.IsNullOrEmpty(context.RelativePath)
            ? "saved connection root"
            : context.RelativePath,
        PaneTransferContextKind.ThisPc => string.IsNullOrEmpty(context.RelativePath)
            ? "This PC"
            : context.RelativePath,
        _ => string.IsNullOrEmpty(context.RelativePath) ? "storage pane" : context.RelativePath
    };

    private async Task ReviewDeleteAsync(BrowserPaneControl pane)
    {
        var captured = pane.CaptureSelectionSnapshot();
        if (captured.IsFailure)
        {
            ShowManualTransferFailure(captured.Error.Message);
            return;
        }

        var selection = captured.Value;
        var preview = string.Join(
            Environment.NewLine,
            selection.Items.Take(5).Select(static item => $"  • {item.Name}"));
        if (selection.Items.Count > 5)
        {
            preview += $"{Environment.NewLine}  • …and {selection.Items.Count - 5:N0} more";
        }

        var local = selection.Context.Kind == PaneTransferContextKind.ThisPc;
        var decision = MessageBox.Show(
            this,
            $"You are about to DELETE {selection.Items.Count:N0} item(s):\n\n{preview}\n\n" +
            (local
                ? "These items will be sent to the Windows Recycle Bin."
                : "Remote items will be deleted through the saved connection. This may be permanent."),
            "Review delete",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (decision != DialogResult.OK)
        {
            return;
        }

        if (!local)
        {
            await DeleteRemoteSelectionAsync(pane, selection).ConfigureAwait(true);
            return;
        }

        try
        {
            foreach (var item in selection.Items)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                var fullPath = Path.GetFullPath(item.RelativePath);
                if (item.IsContainer)
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                        fullPath,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                else
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                        fullPath,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
            }

            pane.Reload();
            _locationStatus.Text = $"Sent {selection.Items.Count:N0} item(s) to the Recycle Bin.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowManualTransferFailure($"The selected items could not be deleted. {error.Message}");
        }

    }

    private async Task DeleteRemoteSelectionAsync(
        BrowserPaneControl pane,
        PaneSelectionSnapshot selection)
    {
        if (selection.Context.Kind != PaneTransferContextKind.SavedConnection ||
            selection.Context.ConnectionId is not { } connectionId ||
            string.IsNullOrWhiteSpace(selection.Context.RootIdentity))
        {
            ShowManualTransferFailure("Remote deletion requires a saved connection with a verified storage root.");
            return;
        }

        await using var client = new NamedPipeObjectInspectorAgentClient();
        var deleted = 0;
        foreach (var item in selection.Items)
        {
            try
            {
                var response = await client.DeleteItemAsync(
                    new StorageItemDeleteRequest(
                        EditableFileIpcContract.CurrentVersion,
                        new ObjectInspectorAddress(
                            connectionId,
                            selection.Context.RootIdentity,
                            item.RelativePath,
                            item.NativeItemId,
                            item.VersionId,
                            item.EntityTag),
                        Recursive: item.IsContainer),
                    _lifetime.Token).ConfigureAwait(true);
                if (response.Failure is not null)
                {
                    ShowManualTransferFailure(
                        deleted == 0
                            ? response.Failure.Message
                            : $"Deleted {deleted:N0} item(s), then stopped. {response.Failure.Message}");
                    pane.Reload();
                    return;
                }

                deleted++;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (
                error is IOException or InvalidDataException or InvalidOperationException or TimeoutException)
            {
                ShowManualTransferFailure(
                    deleted == 0
                        ? "The background agent could not delete the selected items."
                        : $"Deleted {deleted:N0} item(s), then the background agent became unavailable.");
                pane.Reload();
                return;
            }
        }

        pane.Reload();
        _locationStatus.Text = $"Deleted {deleted:N0} remote item(s).";
    }

    private static Icon? LoadWindowIcon()
    {
        try
        {
            var executable = Environment.ProcessPath;
            return string.IsNullOrWhiteSpace(executable)
                ? null
                : Icon.ExtractAssociatedIcon(executable);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
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
                if (e.Status.State == AgentConnectionState.Disconnected &&
                    _packagedLifecycle is not null &&
                    !_agentRecoveryInProgress &&
                    !_lifetime.IsCancellationRequested)
                {
                    _ = RecoverAgentAsync();
                }
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

    private async Task ReviewShellImportAsync(ShellImportDropRequestedEventArgs args)
    {
        try
        {
            var plan = await _shellTransfers.PlanShellImportAsync(new ShellImportPlanRequest(
                ShellTransferIpcContract.CurrentVersion, args.SourcePaths, args.Destination), _lifetime.Token).ConfigureAwait(true);
            if (plan.Failure is not null || string.IsNullOrWhiteSpace(plan.ReviewToken))
            {
                ShowManualTransferFailure(plan.Failure?.Message ?? "StorageHub could not review the dropped items.");
                return;
            }

            var conflicts = plan.Items.Count(item => item.DestinationConflict);
            var choice = conflicts == 0
                ? MessageBox.Show(this, $"Import {plan.Items.Length:N0} file(s) into this saved connection?", "Import from Explorer", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK
                    ? ShellImportDisposition.ReplaceFiles : ShellImportDisposition.Cancel
                : MessageBox.Show(this, $"{conflicts:N0} file(s) conflict at the destination.\n\nYes replaces conflicting files. No skips them. Cancel stops the import.", "Import conflicts", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning) switch
                {
                    DialogResult.Yes => ShellImportDisposition.ReplaceFiles,
                    DialogResult.No => ShellImportDisposition.SkipConflictingFiles,
                    _ => ShellImportDisposition.Cancel
                };
            var committed = await _shellTransfers.CommitShellImportAsync(new ShellImportCommitRequest(
                ShellTransferIpcContract.CurrentVersion, plan.ReviewToken, choice), _lifetime.Token).ConfigureAwait(true);
            if (committed.Failure is not null) ShowManualTransferFailure(committed.Failure.Message);
            else if (committed.Accepted) _locationStatus.Text = $"Queued {committed.TransferIds.Length:N0} Explorer import file(s).";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
            InvalidOperationException or TimeoutException or System.Text.Json.JsonException)
        {
            ShowManualTransferFailure("The background agent could not review the dropped files. Please retry.");
        }
    }

    private async Task RecoverAgentAsync()
    {
        if (_packagedLifecycle is null || _agentRecoveryInProgress || _lifetime.IsCancellationRequested)
        {
            return;
        }

        _agentRecoveryInProgress = true;
        ApplyStatus(_status with { AgentState = AgentConnectionState.Starting });
        _agentStatus.ToolTipText = "StorageHub is reconnecting to the background agent.";
        try
        {
            var result = await _packagedLifecycle.EnsureAgentAsync(_lifetime.Token);
            if (result.IsReady && !_lifetime.IsCancellationRequested)
            {
                ApplyStatus(_status with { AgentState = AgentConnectionState.Connected });
                _agentStatus.ToolTipText = result.Status == AgentEnsureStatus.Started
                    ? "The background agent was restarted and is ready."
                    : "The background agent reconnected and is ready.";
                _ = _overview.RefreshAsync(_lifetime.Token);
                _ = _syncTasks.RefreshAsync(_lifetime.Token);
                _ = _transferQueue.RefreshQueueAsync(_lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _agentRecoveryInProgress = false;
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
