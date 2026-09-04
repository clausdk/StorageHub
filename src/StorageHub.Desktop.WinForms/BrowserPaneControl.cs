using System.ComponentModel;
using System.Runtime.InteropServices;
using StorageHub.Contracts.Ipc;
using StorageHub.Contracts.Results;

namespace StorageHub.Desktop;

public sealed class BrowserPaneControl : UserControl
{
    private const string PaneDragDataFormat = "StorageHub.PaneSelection.v1";
    private const int DragShiftKeyState = 4;
    private const int MaximumCachedConnections = 32;
    private const int MaximumCachedDirectoriesPerConnection = 2_000;
    private const int MaximumTreeDirectories = 2_000;
    private const int MaximumTreeChildrenPerDirectory = 500;
    private static readonly BrowserListItem ParentNavigationItem = new(
        "..",
        string.Empty,
        "Parent folder",
        string.Empty,
        "Go up one level",
        IsContainer: true,
        Kind: StorageItemKind.Directory,
        IsParentNavigation: true);
    private readonly List<Image> _ownedImages = [];
    private readonly ImageList _browserImages;
    private readonly WindowsShellIconProvider _shellIcons;
    private readonly PagedListingIndex _listingIndex;
    private readonly bool _localBrowsingEnabled;
    private readonly LocalBrowserController? _localBrowser;
    private readonly RemoteBrowserController? _remoteBrowser;
    private readonly Func<Guid, string, SshTerminalForm> _sshTerminalFactory;
    private readonly ComboBox _connectionSelector;
    private readonly ToolStrip _navigation;
    private readonly ToolStripButton _backButton;
    private readonly ToolStripButton _forwardButton;
    private readonly ToolStripButton _upButton;
    private readonly ToolStripButton _refreshButton;
    private readonly ToolStripButton _loadMoreButton;
    private readonly ToolStripTextBox _addressBox;
    private readonly ToolStripTextBox _filterBox;
    private readonly ToolStrip _fileCommands;
    private readonly ToolStripButton _copyButton;
    private readonly ToolStripButton _moveButton;
    private readonly ToolStripButton _pasteButton;
    private readonly ToolStripButton _deleteButton;
    private readonly ToolStripDropDownButton _moreButton;
    private readonly TreeView _directoryTree;
    private readonly ListView _fileList;
    private readonly TableLayoutPanel _loadingOverlay;
    private readonly ContextMenuStrip _fileContextMenu;
    private readonly Label _connectionState;
    private readonly Label _connectionNameLabel;
    private readonly Label _connectionTypeBadge;
    private readonly Label _providerBadge;
    private readonly Panel _accentBar;
    private readonly Label _errorBanner;
    private readonly Label _summary;
    private readonly Panel _contentHost;
    private readonly Panel _browserSurface;
    private IReadOnlyList<BrowserListItem> _allItems = [];
    private IReadOnlyList<BrowserListItem> _items = [];
    private readonly Dictionary<Guid, RemoteDirectoryCache> _remoteDirectoryCaches = [];
    private BrowserSortColumn _sortColumn = BrowserSortColumn.Name;
    private bool _sortAscending = true;
    private long _remoteCacheSequence;
    private bool _initialLoadStarted;
    private bool _updatingConnectionChoices;
    private bool _updatingTreeSelection;
    private bool _remotePageLoadInProgress;
    private bool _localPageLoadInProgress;
    private long _uiNavigationSequence;
    private Guid? _lastReportedConnectionId;
    private SshTerminalForm? _embeddedTerminal;
    private Guid? _embeddedTerminalConnectionId;

    public BrowserPaneControl(
        string title,
        bool showLocalDefault,
        RemoteBrowserController? remoteBrowser = null,
        Func<Guid, string, SshTerminalForm>? sshTerminalFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        _localBrowsingEnabled = showLocalDefault;
        _localBrowser = showLocalDefault ? new LocalBrowserController() : null;
        _remoteBrowser = remoteBrowser ?? new RemoteBrowserController();
        _listingIndex = new PagedListingIndex();
        _sshTerminalFactory = sshTerminalFactory ?? ((connectionId, displayName) =>
            new SshTerminalForm(connectionId, displayName));
        Dock = DockStyle.Fill;
        AccessibleName = $"{title} browser pane";
        AccessibleDescription = $"Browse and select items on the {title.ToLowerInvariant()} endpoint.";
        BackColor = StorageHubTheme.Surface;
        AutoScaleMode = AutoScaleMode.Dpi;

        _accentBar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 3,
            BackColor = StorageHubTheme.Primary,
            AccessibleName = $"{title} provider color"
        };

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 88,
            MinimumSize = new Size(0, 88),
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(8, 7, 8, 7),
            BackColor = StorageHubTheme.Surface
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        using var sectionFont = StorageHubTheme.CreateSectionFont();
        _connectionNameLabel = new Label
        {
            Text = showLocalDefault ? "This PC" : "Connections Home",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            Font = new Font(sectionFont, sectionFont.Style),
            ForeColor = StorageHubTheme.Text,
            AutoEllipsis = true,
            UseMnemonic = false,
            Margin = new Padding(4, 0, 8, 0),
            AccessibleName = $"{title} active connection"
        };
        _connectionTypeBadge = CreatePaneBadge("STORAGE", StorageHubTheme.Primary);
        _providerBadge = CreatePaneBadge("LOCAL", StorageHubTheme.TextMuted);
        var badges = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(1, 1, 0, 0)
        };
        badges.Controls.Add(_connectionTypeBadge);
        badges.Controls.Add(_providerBadge);
        var identity = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Margin = Padding.Empty
        };
        identity.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        identity.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        identity.Controls.Add(_connectionNameLabel, 0, 0);
        identity.Controls.Add(badges, 0, 1);
        var identityFrame = CreateHeaderFrame(new Padding(8, 3, 8, 3), new Padding(0, 0, 0, 0));
        identityFrame.AccessibleName = $"{title} connection identity";
        identityFrame.Controls.Add(identity);

        _connectionSelector = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            DrawMode = DrawMode.OwnerDrawVariable,
            ItemHeight = 32,
            IntegralHeight = false,
            DropDownHeight = 260,
            BackColor = StorageHubTheme.Surface,
            ForeColor = StorageHubTheme.Text,
            Margin = new Padding(0, 2, 0, 2),
            AccessibleName = $"{title} connection",
            AccessibleDescription = "Select the local or remote connection displayed in this pane."
        };
        if (showLocalDefault)
        {
            _connectionSelector.Items.AddRange(
            [
                new ConnectionCardModel("This PC", StorageProviderKind.Local, "Local drives", "Ready", true),
                new ConnectionCardModel("Connections Home", StorageProviderKind.Local, "Saved connections", "Browse")
            ]);
        }
        else
        {
            _connectionSelector.Items.Add(new ConnectionCardModel(
                "Connections Home",
                StorageProviderKind.Local,
                "Saved connections",
                "Browse"));
        }

        _connectionSelector.SelectedIndex = 0;
        _connectionSelector.DropDown += ConnectionSelectorDropDown;
        _connectionSelector.MeasureItem += MeasureConnectionItem;
        _connectionSelector.DrawItem += DrawConnectionItem;
        _connectionSelector.SelectedIndexChanged += ConnectionSelectionChanged;

        _connectionState = new Label
        {
            Text = showLocalDefault ? "● Ready" : "○ Choose a connection",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = showLocalDefault ? StorageHubTheme.Success : StorageHubTheme.TextMuted,
            Margin = new Padding(3, 0, 0, 0),
            Padding = new Padding(1, 2, 1, 2),
            MinimumSize = new Size(0, 28),
            AccessibleName = $"{title} connection state"
        };
        var selectorGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        selectorGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        selectorGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        selectorGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        selectorGrid.Controls.Add(_connectionSelector, 0, 0);
        selectorGrid.Controls.Add(_connectionState, 0, 1);
        var selectorFrame = CreateHeaderFrame(new Padding(6, 2, 6, 2), new Padding(8, 0, 8, 0));
        selectorFrame.AccessibleName = $"{title} connection selection and status";
        selectorFrame.Controls.Add(selectorGrid);

        var manageButton = new Button
        {
            Text = "Manage…",
            Dock = DockStyle.Fill,
            AccessibleName = "Open Connection Manager",
            TabIndex = 1
        };
        StorageHubTheme.StyleSecondaryButton(manageButton);
        manageButton.Margin = Padding.Empty;
        manageButton.Click += ManageConnectionsClicked;
        var manageFrame = CreateHeaderFrame(new Padding(5), Padding.Empty);
        manageFrame.AccessibleName = $"{title} connection actions";
        manageFrame.Controls.Add(manageButton);

        header.Controls.Add(identityFrame, 0, 0);
        header.Controls.Add(selectorFrame, 1, 0);
        header.Controls.Add(manageFrame, 2, 0);

        _navigation = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            BackColor = StorageHubTheme.SurfaceMuted,
            ForeColor = StorageHubTheme.Text,
            ImageScalingSize = new Size(18, 18),
            Padding = new Padding(4, 3, 4, 3),
            AccessibleName = $"{title} navigation"
        };
        _backButton = CreateButton(UiGlyph.Back, "Back");
        _forwardButton = CreateButton(UiGlyph.Forward, "Forward");
        _upButton = CreateButton(UiGlyph.Up, "Up");
        _refreshButton = CreateButton(UiGlyph.Refresh, "Refresh");
        _loadMoreButton = new ToolStripButton("Load more")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            AccessibleName = "Load next page",
            AccessibleDescription = "Load the next bounded page from the selected remote folder.",
            ToolTipText = "Load the next page",
            Visible = !showLocalDefault,
            Enabled = false
        };
        _backButton.Enabled = false;
        _forwardButton.Enabled = false;
        _upButton.Enabled = false;
        _backButton.Click += BackClicked;
        _forwardButton.Click += ForwardClicked;
        _upButton.Click += UpClicked;
        _refreshButton.Click += RefreshClicked;
        _loadMoreButton.Click += LoadMoreClicked;
        _navigation.Items.Add(_backButton);
        _navigation.Items.Add(_forwardButton);
        _navigation.Items.Add(_upButton);
        _navigation.Items.Add(new ToolStripSeparator());
        _addressBox = new ToolStripTextBox
        {
            AutoSize = false,
            Width = 330,
            Text = showLocalDefault ? "This PC" : "Connections",
            BackColor = StorageHubTheme.Surface,
            ForeColor = StorageHubTheme.Text,
            AccessibleName = $"{title} address",
            AccessibleDescription = showLocalDefault
                ? "Enter an absolute local or UNC folder path, then press Enter."
                : "The address on the selected remote connection."
        };
        _addressBox.KeyDown += AddressBoxKeyDown;
        _navigation.Items.Add(_addressBox);
        _navigation.Items.Add(_refreshButton);
        _navigation.Items.Add(_loadMoreButton);
        _navigation.Items.Add(new ToolStripSeparator());
        _navigation.Items.Add(new ToolStripLabel("Filter:") { ForeColor = StorageHubTheme.TextMuted });
        _filterBox = new ToolStripTextBox
        {
            AutoSize = false,
            Width = 120,
            BackColor = StorageHubTheme.Surface,
            ForeColor = StorageHubTheme.Text,
            AccessibleName = $"{title} filter",
            AccessibleDescription = "Filter the visible items by name. Wildcards star and question mark are supported.",
            ToolTipText = "Filter by name (* and ? wildcards supported)"
        };
        _filterBox.TextChanged += FilterTextChanged;
        _navigation.Items.Add(_filterBox);
        _navigation.Layout += NavigationLayoutChanged;

        _fileCommands = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            BackColor = StorageHubTheme.Surface,
            ForeColor = StorageHubTheme.Text,
            ImageScalingSize = new Size(16, 16),
            Padding = new Padding(6, 2, 6, 2),
            AccessibleName = $"{title} file commands"
        };
        _fileCommands.Items.Add(new ToolStripLabel("FILES")
        {
            ForeColor = StorageHubTheme.TextMuted,
            Font = new Font("Segoe UI Semibold", 7.5F, FontStyle.Bold)
        });
        _fileCommands.Items.Add(new ToolStripSeparator());
        _copyButton = CreateCommandButton(UiGlyph.File, "Copy", "Stage selected items for copying");
        _moveButton = CreateCommandButton(UiGlyph.Forward, "Move", "Stage selected items for moving");
        _pasteButton = CreateCommandButton(UiGlyph.Save, "Paste", "Review and paste the StorageHub clipboard here");
        _deleteButton = CreateCommandButton(UiGlyph.Delete, "Delete", "Review and delete selected items");
        _copyButton.Click += (_, _) => StageSelection(TransferQueueOperation.Copy);
        _moveButton.Click += (_, _) => StageSelection(TransferQueueOperation.Move);
        _pasteButton.Click += (_, _) => PasteRequested?.Invoke(this, EventArgs.Empty);
        _deleteButton.Click += (_, _) => DeleteRequested?.Invoke(this, EventArgs.Empty);
        _fileCommands.Items.Add(_copyButton);
        _fileCommands.Items.Add(_moveButton);
        _fileCommands.Items.Add(_pasteButton);
        _fileCommands.Items.Add(new ToolStripSeparator());
        _fileCommands.Items.Add(_deleteButton);

        _moreButton = CreateMoreCommandsButton(title);
        _fileCommands.Items.Add(_moreButton);

        _directoryTree = new TreeView
        {
            Dock = DockStyle.Fill,
            HideSelection = false,
            BorderStyle = BorderStyle.None,
            BackColor = StorageHubTheme.Surface,
            ForeColor = StorageHubTheme.Text,
            ShowNodeToolTips = true,
            AccessibleName = $"{title} directory tree",
            AccessibleDescription = "Folders and connection roots."
        };
        _browserImages = CreateBrowserImageList();
        _shellIcons = new WindowsShellIconProvider(_browserImages);
        _directoryTree.ImageList = _browserImages;
        var rootNode = _directoryTree.Nodes.Add(showLocalDefault ? "This PC" : "Connections");
        rootNode.Tag = showLocalDefault ? LocalBrowserLocation.ThisPc : null;
        SetTreeNodeIcon(rootNode, "connection");
        _directoryTree.AfterSelect += DirectoryTreeAfterSelect;

        _fileList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            HideSelection = false,
            VirtualMode = true,
            BorderStyle = BorderStyle.None,
            BackColor = StorageHubTheme.Surface,
            ForeColor = StorageHubTheme.Text,
            AccessibleName = $"{title} file list",
            AccessibleDescription = "A virtualized list of files, folders, and storage objects."
        };
        _fileList.SmallImageList = _browserImages;
        _fileList.Columns.Add("Name", 280);
        _fileList.Columns.Add("Size", 100, HorizontalAlignment.Right);
        _fileList.Columns.Add("Type", 120);
        _fileList.Columns.Add("Modified", 155);
        _fileList.Columns.Add("Status", 100);
        StorageHubTheme.ConfigureList(_fileList);
        UpdateSortColumnHeaders();
        _fileList.RetrieveVirtualItem += RetrieveVirtualItem;
        _fileList.CacheVirtualItems += FileListCacheVirtualItems;
        _fileList.ColumnClick += FileListColumnClick;
        _fileList.DoubleClick += FileListDoubleClick;
        _fileList.KeyDown += FileListKeyDown;
        _fileList.ItemDrag += FileListItemDrag;
        _fileList.SelectedIndexChanged += FileSelectionChanged;
        ConfigureDropTarget(_fileList);
        ConfigureDropTarget(_directoryTree);
        _fileContextMenu = new ContextMenuStrip
        {
            Renderer = DesktopAppearanceService.MenuRenderer,
            AccessibleName = $"{title} transfer commands",
        };
        var open = CreateContextMenuItem("Open", UiGlyph.Folder);
        var edit = CreateContextMenuItem("Edit in external editor...", UiGlyph.File);
        var copy = CreateContextMenuItem("Copy", UiGlyph.File, "Ctrl+C");
        var cut = CreateContextMenuItem("Cut", UiGlyph.Forward, "Ctrl+X");
        var paste = CreateContextMenuItem("Paste", UiGlyph.Save, "Ctrl+V");
        var copyToOtherPane = CreateContextMenuItem("Copy to other pane", UiGlyph.Forward);
        var moveToOtherPane = CreateContextMenuItem("Move to other pane", UiGlyph.Run);
        var refresh = CreateContextMenuItem("Refresh", UiGlyph.Refresh, "F5");
        var selectAll = CreateContextMenuItem("Select all", UiGlyph.Test, "Ctrl+A");
        var inspectObject = CreateContextMenuItem("Properties...", UiGlyph.Info);
        open.Font = new Font(open.Font, FontStyle.Bold);
        open.Click += (_, _) => OpenOrEditSelected();
        edit.Click += (_, _) => RaiseEditRequested();
        copy.Click += (_, _) => StageSelection(TransferQueueOperation.Copy);
        cut.Click += (_, _) => StageSelection(TransferQueueOperation.Move);
        paste.Click += (_, _) => PasteRequested?.Invoke(this, EventArgs.Empty);
        copyToOtherPane.Click += (_, _) => RaiseTransferRequested(TransferQueueOperation.Copy);
        moveToOtherPane.Click += (_, _) => RaiseTransferRequested(TransferQueueOperation.Move);
        refresh.Click += (_, _) => Reload();
        selectAll.Click += (_, _) => SelectAllVisibleItems();
        inspectObject.Click += (_, _) => ObjectInspectionRequested?.Invoke(this, EventArgs.Empty);
        _fileContextMenu.Items.Add(open);
        _fileContextMenu.Items.Add(edit);
        _fileContextMenu.Items.Add(new ToolStripSeparator());
        _fileContextMenu.Items.Add(copy);
        _fileContextMenu.Items.Add(cut);
        _fileContextMenu.Items.Add(paste);
        _fileContextMenu.Items.Add(new ToolStripSeparator());
        _fileContextMenu.Items.Add(copyToOtherPane);
        _fileContextMenu.Items.Add(moveToOtherPane);
        _fileContextMenu.Items.Add(new ToolStripSeparator());
        _fileContextMenu.Items.Add(refresh);
        _fileContextMenu.Items.Add(selectAll);
        _fileContextMenu.Items.Add(new ToolStripSeparator());
        _fileContextMenu.Items.Add(inspectObject);
        _fileContextMenu.Opening += (_, _) =>
        {
            var hasSelection = HasTransferableSelection();
            var singleSelection = _fileList.SelectedIndices.Count == 1;
            var selectedItem = singleSelection && (uint)_fileList.SelectedIndices[0] < (uint)_items.Count
                ? _items[_fileList.SelectedIndices[0]]
                : null;
            var selectedContainer = selectedItem is { IsContainer: true };
            var selectedParent = selectedItem is { IsParentNavigation: true };
            open.Enabled = singleSelection;
            open.Text = selectedParent
                ? "Go up one level"
                : selectedContainer ? "Open" : "Open in external editor...";
            edit.Enabled = singleSelection && !selectedContainer && !selectedParent;
            copy.Enabled = hasSelection;
            cut.Enabled = hasSelection;
            paste.Enabled = CanPaste?.Invoke() == true;
            copyToOtherPane.Enabled = hasSelection;
            moveToOtherPane.Enabled = hasSelection;
            selectAll.Enabled = _items.Any(static item => !item.IsParentNavigation);
            inspectObject.Enabled = singleSelection && !selectedParent;
        };
        _fileList.ContextMenuStrip = _fileContextMenu;
        _fileList.MouseDown += FileListMouseDown;

        _loadingOverlay = CreateLoadingOverlay();

        var browserSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            Size = new Size(650, 500),
            SplitterDistance = 185,
            FixedPanel = FixedPanel.Panel1,
            Panel1MinSize = 130,
            Panel2MinSize = 240,
            BackColor = StorageHubTheme.Border,
            AccessibleName = $"{title} tree and item list"
        };
        browserSplit.Panel1.Padding = new Padding(8, 7, 4, 7);
        browserSplit.Panel2.Padding = new Padding(4, 7, 8, 7);
        browserSplit.Panel1.BackColor = StorageHubTheme.Surface;
        browserSplit.Panel2.BackColor = StorageHubTheme.Surface;
        browserSplit.Panel1.Controls.Add(_directoryTree);
        browserSplit.Panel2.Controls.Add(_fileList);
        browserSplit.Panel2.Controls.Add(_loadingOverlay);

        _summary = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 28,
            Padding = new Padding(9, 6, 9, 3),
            Text = "0 items",
            ForeColor = StorageHubTheme.TextMuted,
            BackColor = StorageHubTheme.Surface,
            AccessibleName = $"{title} item summary"
        };

        _errorBanner = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 34,
            Padding = new Padding(10, 8, 10, 6),
            Visible = false,
            BackColor = StorageHubTheme.SurfaceMuted,
            ForeColor = StorageHubTheme.Warning,
            AccessibleName = $"{title} browser error",
            AccessibleRole = AccessibleRole.Alert
        };

        _browserSurface = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = StorageHubTheme.Surface,
            AccessibleName = $"{title} storage browser"
        };
        _browserSurface.Controls.Add(browserSplit);
        _browserSurface.Controls.Add(_summary);
        _browserSurface.Controls.Add(_errorBanner);
        _browserSurface.Controls.Add(_navigation);
        _browserSurface.Controls.Add(_fileCommands);
        _contentHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = StorageHubTheme.Surface,
            AccessibleName = $"{title} pane content"
        };
        _contentHost.Controls.Add(_browserSurface);

        Controls.Add(_contentHost);
        Controls.Add(header);
        Controls.Add(_accentBar);

        if (showLocalDefault)
        {
            SetItems([new BrowserListItem("Loading local drives…", string.Empty, "Status", string.Empty, string.Empty)]);
        }
        else
        {
            SetItems([new BrowserListItem(
                "Loading saved connections…",
                string.Empty,
                "Status",
                string.Empty,
                string.Empty)]);
        }

        UpdateConnectionPresentation();
        RefreshCommandState();
    }

    /// <summary>Raised by pane copy/move hooks; the workspace owns resolving the opposite pane.</summary>
    public event EventHandler<PaneTransferRequestedEventArgs>? TransferRequested;

    public event EventHandler<PaneSelectionStagedEventArgs>? SelectionStaged;

    public event EventHandler? PasteRequested;

    public event EventHandler? DeleteRequested;

    public event EventHandler? EditRequested;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<bool>? CanPaste { get; set; }

    /// <summary>Creates an inert Explorer marker; StorageHub performs the eventual transfer.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<PaneSelectionSnapshot, CancellationToken, Task<ExplorerDropBeginResponse>>? BeginExplorerDropAsync { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<string, CancellationToken, Task<ExplorerDropCommitResponse>>? CommitExplorerDropAsync { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? ExplorerDropUnavailableReason { get; set; }

    /// <summary>Raised when an immutable selection from another pane is dropped onto this pane.</summary>
    public event EventHandler<PaneTransferDropRequestedEventArgs>? TransferDropRequested;

    /// <summary>Raised only for ordinary Explorer FileDrop data over a saved connection.</summary>
    public event EventHandler<ShellImportDropRequestedEventArgs>? ShellImportDropRequested;

    /// <summary>Raised when one selected saved-connection object should be inspected read-only.</summary>
    public event EventHandler? ObjectInspectionRequested;

    public event EventHandler<ConnectionOpenedEventArgs>? ConnectionOpened;

    public StorageResult<PaneSelectionSnapshot> CaptureSelectionSnapshot()
    {
        var context = CaptureTransferContext();
        if (context.IsFailure)
        {
            return StorageResult<PaneSelectionSnapshot>.Fail(context.Error);
        }

        var items = new List<PaneTransferItem>(_fileList.SelectedIndices.Count);
        foreach (var index in _fileList.SelectedIndices.Cast<int>().Order())
        {
            if ((uint)index >= (uint)_items.Count)
            {
                return SelectionSnapshotFailure("The pane selection changed while it was being captured.");
            }

            var mapped = MapTransferItem(_items[index]);
            if (mapped.IsFailure)
            {
                return StorageResult<PaneSelectionSnapshot>.Fail(mapped.Error);
            }

            items.Add(mapped.Value);
        }

        return PaneSelectionSnapshot.Create(context.Value, items);
    }

    public StorageResult<PaneDestinationSnapshot> CaptureDestinationSnapshot(
        IReadOnlyCollection<string>? relevantNames = null)
    {
        var context = CaptureTransferContext();
        if (context.IsFailure)
        {
            return StorageResult<PaneDestinationSnapshot>.Fail(context.Error);
        }

        if (_localBrowser?.CurrentSnapshot?.HasMore == true ||
            _remoteBrowser?.CurrentSnapshot?.HasMore == true)
        {
            return StorageResult<PaneDestinationSnapshot>.Fail(new StorageFailure(
                "manual_transfer.destination.index_incomplete",
                StorageFailureKind.Conflict,
                "Finish indexing the destination folder before reviewing transfer conflicts."));
        }

        var indexedItems = relevantNames is null ? _allItems : _listingIndex.FindByNames(relevantNames);
        var items = new List<PaneTransferItem>(indexedItems.Count);
        foreach (var item in indexedItems)
        {
            var mapped = MapTransferItem(item);
            if (mapped.IsFailure)
            {
                return StorageResult<PaneDestinationSnapshot>.Fail(mapped.Error);
            }

            items.Add(mapped.Value);
        }

        return PaneDestinationSnapshot.Create(context.Value, items);
    }

    public async Task<bool> EnsureListingCompleteAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_localPageLoadInProgress || _remotePageLoadInProgress)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(true);
                continue;
            }

            if (IsLocalConnectionSelected && _localBrowser?.CurrentSnapshot is { HasMore: true } local)
            {
                var before = local.IndexedEntryCount;
                await LoadMoreLocalAsync().ConfigureAwait(true);
                var after = _localBrowser.CurrentSnapshot;
                if (after is { HasMore: true } && after.IndexedEntryCount <= before) return false;
                continue;
            }

            if (IsRemoteSnapshotSelected && _remoteBrowser?.CurrentSnapshot is { HasMore: true } remote)
            {
                var before = remote.IndexedEntryCount;
                await LoadMoreRemoteAsync(automatic: true).ConfigureAwait(true);
                var after = _remoteBrowser.CurrentSnapshot;
                if (after is { HasMore: true } && after.IndexedEntryCount <= before) return false;
                continue;
            }

            return true;
        }
    }

    public StorageResult<TransferQueueAddress> CaptureShellImportDestination(string? relativePath = null)
    {
        var context = CaptureTransferContext();
        if (context.IsFailure || context.Value.Kind != PaneTransferContextKind.SavedConnection ||
            context.Value.ConnectionId is not { } id || string.IsNullOrWhiteSpace(context.Value.RootIdentity))
        {
            return StorageResult<TransferQueueAddress>.Fail(new StorageFailure(
                "shell-transfer.destination.invalid", StorageFailureKind.Validation,
                "Explorer drops require an open saved connection folder."));
        }
        return StorageResult<TransferQueueAddress>.Success(new TransferQueueAddress(
            id, context.Value.RootIdentity, relativePath ?? context.Value.RelativePath));
    }

    public void SetItems(IEnumerable<BrowserListItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _fileList.SelectedIndices.Clear();
        _listingIndex.Reset(items);
        _allItems = _listingIndex.CreateView(_sortColumn, _sortAscending);
        ApplyFilter();
    }

    private void AppendItems(IEnumerable<BrowserListItem> items)
    {
        var selectedLocations = CaptureSelectedLocations();
        _fileList.SelectedIndices.Clear();
        _listingIndex.Append(items);
        _allItems = _listingIndex.CreateView(_sortColumn, _sortAscending);
        ApplyFilter();
        RestoreSelectedLocations(selectedLocations);
    }

    /// <summary>Navigates the currently selected local or saved-connection surface backward.</summary>
    public void NavigateBack() => BackClicked(this, EventArgs.Empty);

    /// <summary>Navigates the currently selected local or saved-connection surface forward.</summary>
    public void NavigateForward() => ForwardClicked(this, EventArgs.Empty);

    /// <summary>Navigates to the parent of the currently selected location.</summary>
    public void NavigateUp() => UpClicked(this, EventArgs.Empty);

    /// <summary>Reloads the current location or the saved-connections home.</summary>
    public void Reload() => RefreshClicked(this, EventArgs.Empty);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string PaneDisplayName => _connectionNameLabel.Text;

    public void RefreshCommandState()
    {
        var storageVisible = !IsSshClientSelected;
        var hasSelection = storageVisible && HasTransferableSelection();
        _copyButton.Enabled = hasSelection;
        _moveButton.Enabled = hasSelection;
        _deleteButton.Enabled = hasSelection;
        _pasteButton.Enabled = storageVisible && CanPaste?.Invoke() == true;
        _moreButton.Enabled = storageVisible;
    }

    /// <summary>Selects every visible item in the pane without selecting filtered-out items.</summary>
    public void SelectAllVisibleItems()
    {
        _fileList.BeginUpdate();
        try
        {
            for (var index = 0; index < _fileList.VirtualListSize; index++)
            {
                if ((uint)index < (uint)_items.Count && !_items[index].IsParentNavigation)
                {
                    _fileList.SelectedIndices.Add(index);
                }
            }
        }
        finally
        {
            _fileList.EndUpdate();
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_localBrowsingEnabled && !_initialLoadStarted)
        {
            _initialLoadStarted = true;
            _ = InitializeLocalPaneAsync();
        }
        else if (!_localBrowsingEnabled && !_initialLoadStarted)
        {
            _initialLoadStarted = true;
            _ = LoadRemoteConnectionsAsync();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _uiNavigationSequence++;
            if (!_connectionSelector.IsDisposed)
            {
                _connectionSelector.DrawItem -= DrawConnectionItem;
                _connectionSelector.MeasureItem -= MeasureConnectionItem;
                _connectionSelector.DropDown -= ConnectionSelectorDropDown;
                _connectionSelector.SelectedIndexChanged -= ConnectionSelectionChanged;
            }
            DisposeEmbeddedTerminal();
            _backButton.Click -= BackClicked;
            _forwardButton.Click -= ForwardClicked;
            _upButton.Click -= UpClicked;
            _refreshButton.Click -= RefreshClicked;
            _loadMoreButton.Click -= LoadMoreClicked;
            _addressBox.KeyDown -= AddressBoxKeyDown;
            _filterBox.TextChanged -= FilterTextChanged;
            _navigation.Layout -= NavigationLayoutChanged;
            _directoryTree.AfterSelect -= DirectoryTreeAfterSelect;
            _fileList.RetrieveVirtualItem -= RetrieveVirtualItem;
            _fileList.CacheVirtualItems -= FileListCacheVirtualItems;
            _fileList.ColumnClick -= FileListColumnClick;
            _fileList.DoubleClick -= FileListDoubleClick;
            _fileList.KeyDown -= FileListKeyDown;
            _fileList.ItemDrag -= FileListItemDrag;
            _fileList.SelectedIndexChanged -= FileSelectionChanged;
            _fileList.MouseDown -= FileListMouseDown;
            UnconfigureDropTarget(_fileList);
            UnconfigureDropTarget(_directoryTree);
            _fileContextMenu.Dispose();
            if (_localBrowser is not null)
            {
                _localBrowser.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            if (_remoteBrowser is not null)
            {
                _remoteBrowser.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

        }

        base.Dispose(disposing);

        if (disposing)
        {
            _browserImages.Dispose();
            _shellIcons.Dispose();
            _listingIndex.Dispose();
            foreach (var image in _ownedImages)
            {
                image.Dispose();
            }

            _ownedImages.Clear();
        }
    }

    private void RetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if ((uint)e.ItemIndex >= (uint)_items.Count)
        {
            e.Item = new ListViewItem(string.Empty);
            return;
        }

        var value = _items[e.ItemIndex];
        var imageKey = _shellIcons.GetKey(value, IsLocalConnectionSelected);
        // Virtual ListView rows do not consistently resolve ImageKey after ImageList additions.
        // Give the native control the concrete slot as well as the stable cache key.
        e.Item = new ListViewItem([value.Name, value.Size, value.Type, value.Modified, value.Status])
        {
            ToolTipText = value.IsParentNavigation ? value.Status : value.Name,
            ImageKey = imageKey,
            ImageIndex = _browserImages.Images.IndexOfKey(imageKey)
        };
    }

    private void FileListColumnClick(object? sender, ColumnClickEventArgs e)
    {
        if (!Enum.IsDefined((BrowserSortColumn)e.Column))
        {
            return;
        }

        var selected = (BrowserSortColumn)e.Column;
        if (_sortColumn == selected)
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortColumn = selected;
            _sortAscending = true;
        }

        SortItems();
        UpdateSortColumnHeaders();
        ApplyFilter();
    }

    private void FileSelectionChanged(object? sender, EventArgs e) => RefreshCommandState();

    private void FileListCacheVirtualItems(object? sender, CacheVirtualItemsEventArgs e)
    {
        var threshold = IsLocalConnectionSelected
            ? LocalBrowserController.DefaultPageSize
            : RemoteBrowserController.DefaultPageSize;
        if (e.EndIndex >= Math.Max(0, _fileList.VirtualListSize - threshold))
        {
            RequestAutomaticPrefetch();
        }
    }

    private void RequestAutomaticPrefetch()
    {
        if (IsDisposed || Disposing || !_fileList.IsHandleCreated ||
            _remotePageLoadInProgress || _localPageLoadInProgress)
        {
            return;
        }

        var localMore = IsLocalConnectionSelected && _localBrowser?.CurrentSnapshot?.HasMore == true;
        var remoteMore = IsRemoteSnapshotSelected && _remoteBrowser?.CurrentSnapshot?.HasMore == true;
        if (!localMore && !remoteMore) return;

        var topIndex = 0;
        try
        {
            topIndex = _fileList.TopItem?.Index ?? 0;
        }
        catch (InvalidOperationException)
        {
        }

        var approximateVisibleRows = Math.Max(10, _fileList.ClientSize.Height / Math.Max(1, _fileList.Font.Height) + 4);
        var threshold = localMore ? LocalBrowserController.DefaultPageSize : RemoteBrowserController.DefaultPageSize;
        if (topIndex + approximateVisibleRows >= Math.Max(0, _fileList.VirtualListSize - threshold))
        {
            if (localMore) _ = LoadMoreLocalAsync();
            else _ = LoadMoreRemoteAsync(automatic: true);
        }
    }

    private void SortItems()
    {
        _allItems = _listingIndex.CreateView(_sortColumn, _sortAscending);
    }

    private void UpdateSortColumnHeaders()
    {
        string[] labels = ["Name", "Size", "Type", "Modified", "Status"];
        for (var index = 0; index < labels.Length; index++)
        {
            _fileList.Columns[index].Text = index == (int)_sortColumn
                ? $"{labels[index]} {(_sortAscending ? "↑" : "↓")}"
                : labels[index];
        }
    }

    private static string GetItemImageKey(BrowserListItem item) => item switch
    {
        { IsContainer: true, Kind: StorageItemKind.Other } => "connection",
        { IsContainer: true } => "folder",
        _ => "file"
    };

    private async Task NavigateLocalAsync(
        LocalBrowserNavigationKind kind,
        LocalBrowserLocation? location = null)
    {
        if (_localBrowser is null || !IsLocalConnectionSelected || IsDisposed || Disposing)
        {
            return;
        }

        var sequence = ++_uiNavigationSequence;
        SetNavigationBusy(true);
        HideError();
        LocalBrowserNavigationResult result;
        try
        {
            result = await _localBrowser.NavigateAsync(kind, location);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (sequence != _uiNavigationSequence || IsDisposed || Disposing)
        {
            return;
        }

        SetNavigationBusy(false);
        switch (result.Status)
        {
            case LocalBrowserNavigationStatus.Succeeded when result.Snapshot is not null:
                PresentSnapshot(result.Snapshot, result.AppendedPage);
                if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                {
                    ShowNotice(result.ErrorMessage);
                }

                break;
            case LocalBrowserNavigationStatus.Failed:
                ShowError(result.ErrorMessage ?? "StorageHub could not open this location.");
                _addressBox.Text = _localBrowser.CurrentLocation.DisplayText;
                break;
            case LocalBrowserNavigationStatus.Canceled:
            case LocalBrowserNavigationStatus.Superseded:
            case LocalBrowserNavigationStatus.NoTarget:
                break;
            default:
                throw new InvalidOperationException("The local browser returned an invalid navigation result.");
        }

        UpdateNavigationButtons();
    }

    private async Task LoadMoreLocalAsync()
    {
        if (_localBrowser is null || !IsLocalConnectionSelected || _localPageLoadInProgress ||
            IsDisposed || Disposing)
        {
            return;
        }

        _localPageLoadInProgress = true;
        UpdateSummaryText();
        try
        {
            var result = await _localBrowser.LoadMoreAsync();
            if (IsDisposed || Disposing) return;
            if (result.Status == LocalBrowserNavigationStatus.Succeeded && result.Snapshot is not null)
            {
                PresentSnapshot(result.Snapshot, appendPage: true);
                UpdateNavigationButtons();
            }
            else if (result.Status == LocalBrowserNavigationStatus.Failed)
            {
                ShowError(result.ErrorMessage ?? "StorageHub could not continue indexing this folder.");
            }
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _localPageLoadInProgress = false;
            if (!IsDisposed && !Disposing) UpdateSummaryText();
        }
    }

    private async Task LoadRemoteConnectionsAsync(bool preserveCurrentSurface = false)
    {
        if (_remoteBrowser is null || IsDisposed || Disposing)
        {
            return;
        }

        var sequence = preserveCurrentSurface ? _uiNavigationSequence : ++_uiNavigationSequence;
        if (!preserveCurrentSurface)
        {
            SetNavigationBusy(true);
            HideError();
        }

        RemoteConnectionLoadResult result;
        try
        {
            result = await _remoteBrowser.LoadConnectionsAsync();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (sequence != _uiNavigationSequence || IsDisposed || Disposing)
        {
            return;
        }

        if (!preserveCurrentSurface)
        {
            SetNavigationBusy(false);
        }

        switch (result.Status)
        {
            case RemoteBrowserOperationStatus.Succeeded:
                ReplaceRemoteConnectionChoices(result.Connections);
                if (IsAnyConnectionsHomeSelected)
                {
                    PresentConnectionsHome(result.Connections);
                }
                else
                {
                    UpdateConnectionPresentation();
                }

                break;
            case RemoteBrowserOperationStatus.Failed:
                if (IsAnyConnectionsHomeSelected)
                {
                    PresentConnectionsHome(_remoteBrowser.Connections);
                    ShowError(result.ErrorMessage ?? "Saved connections are temporarily unavailable.");
                }

                break;
            case RemoteBrowserOperationStatus.Canceled:
            case RemoteBrowserOperationStatus.Superseded:
                break;
            default:
                throw new InvalidOperationException("The remote browser returned an invalid connection-load result.");
        }

        UpdateNavigationButtons();
    }

    private async Task SelectRemoteConnectionAsync(Guid connectionId)
    {
        if (_remoteBrowser is null || IsDisposed || Disposing)
        {
            return;
        }

        var sequence = ++_uiNavigationSequence;
        SetNavigationBusy(true);
        _connectionState.Text = "● Testing connection…";
        HideError();
        RemoteBrowserNavigationResult result;
        try
        {
            result = await _remoteBrowser.SelectConnectionAsync(connectionId);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (sequence != _uiNavigationSequence || IsDisposed || Disposing)
        {
            return;
        }

        SetNavigationBusy(false);
        PresentRemoteResult(result);
    }

    private async Task NavigateRemoteAsync(
        RemoteBrowserNavigationKind kind,
        string? relativePath = null)
    {
        if (_remoteBrowser is null || !IsRemoteSnapshotSelected || IsDisposed || Disposing)
        {
            return;
        }

        var sequence = ++_uiNavigationSequence;
        SetNavigationBusy(true);
        HideError();
        RemoteBrowserNavigationResult result;
        try
        {
            result = await _remoteBrowser.NavigateAsync(kind, relativePath);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (sequence != _uiNavigationSequence || IsDisposed || Disposing)
        {
            return;
        }

        SetNavigationBusy(false);
        PresentRemoteResult(result);
    }

    private async Task LoadMoreRemoteAsync(bool automatic = false)
    {
        if (_remoteBrowser is null || !IsRemoteSnapshotSelected || IsDisposed || Disposing ||
            _remotePageLoadInProgress)
        {
            return;
        }

        _remotePageLoadInProgress = true;
        var sequence = ++_uiNavigationSequence;
        if (!automatic)
        {
            SetNavigationBusy(true);
            HideError();
        }
        else
        {
            UpdateSummaryText();
        }
        RemoteBrowserNavigationResult result;
        try
        {
            result = await _remoteBrowser.LoadMoreAsync();
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        finally
        {
            _remotePageLoadInProgress = false;
        }

        if (sequence != _uiNavigationSequence || IsDisposed || Disposing)
        {
            return;
        }

        if (!automatic)
        {
            SetNavigationBusy(false);
        }
        PresentRemoteResult(result);
    }

    private void PresentRemoteResult(RemoteBrowserNavigationResult result)
    {
        switch (result.Status)
        {
            case RemoteBrowserOperationStatus.Succeeded when result.Snapshot is not null:
                if (result.UnavailablePath is { Length: > 0 } unavailablePath &&
                    _remoteDirectoryCaches.TryGetValue(result.Snapshot.Connection.ConnectionId, out var cache))
                {
                    cache.Invalidate(unavailablePath);
                }

                PresentRemoteSnapshot(result.Snapshot, result.AppendedPage);
                if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                {
                    ShowNotice(result.ErrorMessage);
                }

                break;
            case RemoteBrowserOperationStatus.Failed:
                ShowError(result.ErrorMessage ?? "The remote location could not be opened.");
                if (IsRemoteSnapshotSelected && _remoteBrowser?.CurrentSnapshot is { } current)
                {
                    _addressBox.Text = current.DisplayPath;
                }
                else
                {
                    _addressBox.Text = "/";
                    SetItems([new BrowserListItem(
                        "Connection could not be opened",
                        string.Empty,
                        "Status",
                        string.Empty,
                        string.Empty)]);
                }

                break;
            case RemoteBrowserOperationStatus.Canceled:
            case RemoteBrowserOperationStatus.Superseded:
            case RemoteBrowserOperationStatus.NoTarget:
                break;
            default:
                throw new InvalidOperationException("The remote browser returned an invalid navigation result.");
        }

        UpdateNavigationButtons();
    }

    private void PresentRemoteSnapshot(RemoteBrowserSnapshot snapshot, bool appendPage = false)
    {
        _addressBox.Text = snapshot.DisplayPath;
        var pageItems = snapshot.Entries.Select(static entry => new BrowserListItem(
            entry.Name,
            entry.Size is null ? string.Empty : UiFormatting.FormatBytes(entry.Size.Value),
            DescribeRemoteType(entry),
            entry.LastModifiedUtc is null
                ? string.Empty
                : entry.LastModifiedUtc.Value.LocalDateTime.ToString(
                    "g",
                    System.Globalization.CultureInfo.CurrentCulture),
            string.Empty,
            entry.RelativePath,
            entry.IsContainer,
            entry.Kind,
            entry.Size,
            entry.NativeItemId,
            entry.VersionId,
            entry.EntityTag,
            entry.LastModifiedUtc));
        if (appendPage)
        {
            AppendItems(pageItems);
        }
        else
        {
            SetItems(pageItems);
        }
        _connectionState.Text = "● Ready";
        _connectionState.ForeColor = StorageHubTheme.Success;
        _connectionState.AccessibleDescription = $"Showing {snapshot.Connection.DisplayName} {snapshot.DisplayPath}";
        UpdateRemoteDirectoryTree(snapshot, appendPage);
        UpdateSummaryText();
        if (snapshot.HasMore)
        {
            BeginInvoke(new Action(RequestAutomaticPrefetch));
        }
        if (_lastReportedConnectionId != snapshot.Connection.ConnectionId)
        {
            _lastReportedConnectionId = snapshot.Connection.ConnectionId;
            ConnectionOpened?.Invoke(this, new ConnectionOpenedEventArgs(snapshot.Connection));
        }
    }

    private void ReplaceRemoteConnectionChoices(IReadOnlyList<ConnectionSummary> connections)
    {
        connections = connections
            .Where(static connection =>
                connection.Type == ConnectionProfileType.Storage ||
                connection is
                {
                    Type: ConnectionProfileType.Client,
                    Provider: StorageConnectionProvider.Ssh
                })
            .ToArray();
        var activeConnectionIds = connections.Select(static connection => connection.ConnectionId).ToHashSet();
        foreach (var cachedConnectionId in _remoteDirectoryCaches.Keys
            .Where(id => !activeConnectionIds.Contains(id))
            .ToArray())
        {
            _remoteDirectoryCaches.Remove(cachedConnectionId);
        }

        var previous = _connectionSelector.SelectedItem as ConnectionCardModel;
        var previousConnectionId = previous?.ConnectionId;
        var previousWasThisPc = _localBrowsingEnabled &&
            string.Equals(previous?.Name, "This PC", StringComparison.Ordinal);
        _updatingConnectionChoices = true;
        _connectionSelector.BeginUpdate();
        try
        {
            _connectionSelector.Items.Clear();
            if (_localBrowsingEnabled)
            {
                _connectionSelector.Items.Add(new ConnectionCardModel(
                    "This PC",
                    StorageProviderKind.Local,
                    "Local drives",
                    "Ready",
                    IsFavorite: true));
            }

            _connectionSelector.Items.Add(new ConnectionCardModel(
                "Connections Home",
                StorageProviderKind.Local,
                "Saved connections",
                "Browse"));
            var savedCards = connections.Select(connection => new ConnectionCardModel(
                    connection.DisplayName,
                    MapProvider(connection.Provider),
                    connection.FolderPath ?? "Saved connection",
                    connection.IsEnabled ? DescribeConnectionHealth(connection.Health) : "Disabled",
                    connection.IsFavorite,
                    connection.ConnectionId,
                    connection.IsEnabled,
                    connection.AccentColor,
                    connection.FolderPath,
                    connection.Tags))
                .OrderBy(static card => ConnectionGroupSortKey(card), StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(static card => card.Name, StringComparer.CurrentCultureIgnoreCase);
            foreach (var card in savedCards)
            {
                _connectionSelector.Items.Add(card);
            }

            var homeIndex = _localBrowsingEnabled ? 1 : 0;
            var selectedIndex = previousWasThisPc ? 0 : homeIndex;
            if (previousConnectionId is { } selectedConnectionId)
            {
                for (var index = homeIndex + 1; index < _connectionSelector.Items.Count; index++)
                {
                    if (_connectionSelector.Items[index] is ConnectionCardModel
                        {
                            ConnectionId: { } candidateId
                        } && candidateId == selectedConnectionId)
                    {
                        selectedIndex = index;
                        break;
                    }
                }
            }

            _connectionSelector.SelectedIndex = selectedIndex;
        }
        finally
        {
            _connectionSelector.EndUpdate();
            _updatingConnectionChoices = false;
        }
    }

    private void PresentConnectionsHome(IReadOnlyList<ConnectionSummary> connections)
    {
        ShowBrowserSurface();
        _addressBox.Text = "Connections";
        _directoryTree.Nodes.Clear();
        var connectionsRoot = _directoryTree.Nodes.Add("Connections");
        SetTreeNodeIcon(connectionsRoot, "connection");
        if (connections.Count == 0)
        {
            SetItems([new BrowserListItem(
                "No enabled saved connections",
                string.Empty,
                "Status",
                string.Empty,
                "Use Manage… to add one")]);
        }
        else
        {
            SetItems(connections.Select(static connection => new BrowserListItem(
                connection.DisplayName,
                string.Empty,
                connection.Provider.ToString().ToUpperInvariant(),
                string.Empty,
                connection.IsFavorite ? "Favorite" : "Saved",
                connection.ConnectionId.ToString("D"),
                IsContainer: true)));
        }

        _connectionState.Text = "○ Choose a saved connection";
        _connectionState.ForeColor = StorageHubTheme.TextMuted;
        _loadMoreButton.Enabled = false;
    }

    private void UpdateRemoteDirectoryTree(RemoteBrowserSnapshot snapshot, bool appendPage)
    {
        var cache = GetRemoteDirectoryCache(snapshot.Connection.ConnectionId);
        cache.Update(
            snapshot.RelativePath,
            snapshot.Entries
                .Where(static entry => entry.IsContainer)
                .Take(MaximumTreeChildrenPerDirectory)
                .Select(static entry => new CachedRemoteDirectory(entry.Name, entry.RelativePath))
                .ToArray(),
            isComplete: !snapshot.HasMore,
            ++_remoteCacheSequence,
            MaximumCachedDirectoriesPerConnection,
            appendPage);
        var expandedPaths = _directoryTree.Nodes.Count == 1 &&
            _directoryTree.Nodes[0].Tag is RemoteTreeRoot existingRoot &&
            existingRoot.ConnectionId == snapshot.Connection.ConnectionId
                ? FlattenTree(_directoryTree.Nodes[0])
                    .Where(static node => node.IsExpanded && node.Tag is string)
                    .Select(static node => (string)node.Tag!)
                    .ToHashSet(StringComparer.Ordinal)
                : [];

        _updatingTreeSelection = true;
        _directoryTree.BeginUpdate();
        try
        {
            _directoryTree.Nodes.Clear();
            var root = _directoryTree.Nodes.Add(snapshot.Connection.DisplayName);
            root.Tag = new RemoteTreeRoot(snapshot.Connection.ConnectionId);
            root.ToolTipText = snapshot.Connection.DisplayName;
            SetTreeNodeIcon(root, "connection");
            var remaining = MaximumTreeDirectories;
            AddCachedRemoteChildren(root, string.Empty, cache, expandedPaths, 0, ref remaining);
            var current = EnsureRemotePathNode(root, snapshot.RelativePath);

            root.Expand();
            current.Expand();
            _directoryTree.SelectedNode = current;
            current.EnsureVisible();
        }
        finally
        {
            _directoryTree.EndUpdate();
            _updatingTreeSelection = false;
        }
    }

    private RemoteDirectoryCache GetRemoteDirectoryCache(Guid connectionId)
    {
        if (_remoteDirectoryCaches.TryGetValue(connectionId, out var existing))
        {
            existing.LastAccess = ++_remoteCacheSequence;
            return existing;
        }

        if (_remoteDirectoryCaches.Count >= MaximumCachedConnections)
        {
            var oldest = _remoteDirectoryCaches.MinBy(static pair => pair.Value.LastAccess).Key;
            _remoteDirectoryCaches.Remove(oldest);
        }

        var created = new RemoteDirectoryCache { LastAccess = ++_remoteCacheSequence };
        _remoteDirectoryCaches.Add(connectionId, created);
        return created;
    }

    private static void AddCachedRemoteChildren(
        TreeNode parent,
        string parentPath,
        RemoteDirectoryCache cache,
        IReadOnlySet<string> expandedPaths,
        int depth,
        ref int remaining)
    {
        if (depth >= 256 || remaining <= 0 || !cache.Listings.TryGetValue(parentPath, out var listing))
        {
            return;
        }

        foreach (var directory in listing.Children
            .OrderBy(static child => child.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (string.Equals(directory.Path, parentPath, StringComparison.Ordinal) ||
                !string.Equals(RemoteBrowserPath.GetParent(directory.Path), parentPath, StringComparison.Ordinal))
            {
                continue;
            }

            if (remaining-- <= 0)
            {
                break;
            }

            var node = parent.Nodes.Add(directory.Name);
            node.Tag = directory.Path;
            node.ToolTipText = directory.Path;
            SetTreeNodeIcon(node, "folder");
            AddCachedRemoteChildren(node, directory.Path, cache, expandedPaths, depth + 1, ref remaining);
            if (expandedPaths.Contains(directory.Path))
            {
                node.Expand();
            }
        }
    }

    private static TreeNode EnsureRemotePathNode(TreeNode root, string relativePath)
    {
        var current = root;
        var path = string.Empty;
        foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path = path.Length == 0 ? segment : path + "/" + segment;
            var existing = current.Nodes
                .Cast<TreeNode>()
                .FirstOrDefault(node => node.Tag is string nodePath &&
                    string.Equals(nodePath, path, StringComparison.Ordinal));
            current = existing ?? current.Nodes.Add(segment);
            current.Tag = path;
            current.ToolTipText = path;
            SetTreeNodeIcon(current, "folder");
        }

        return current;
    }

    private static IEnumerable<TreeNode> FlattenTree(TreeNode root)
    {
        yield return root;
        foreach (TreeNode child in root.Nodes)
        {
            foreach (var descendant in FlattenTree(child))
            {
                yield return descendant;
            }
        }
    }

    private static string DescribeRemoteType(StorageListItem entry) => entry.Kind switch
    {
        StorageItemKind.Directory => "Folder",
        StorageItemKind.Prefix => "Prefix",
        StorageItemKind.SymbolicLink => "Symbolic link",
        StorageItemKind.File when !string.IsNullOrWhiteSpace(entry.ContentType) => entry.ContentType,
        StorageItemKind.File => LocalBrowserPresentation.DescribeFileType(Path.GetExtension(entry.Name)),
        _ => "Storage item"
    };

    private static StorageProviderKind MapProvider(StorageConnectionProvider provider) => provider switch
    {
        StorageConnectionProvider.Local => StorageProviderKind.Local,
        StorageConnectionProvider.S3 => StorageProviderKind.S3,
        StorageConnectionProvider.Ftp => StorageProviderKind.Ftp,
        StorageConnectionProvider.Ftps => StorageProviderKind.Ftps,
        StorageConnectionProvider.Sftp => StorageProviderKind.Sftp,
        StorageConnectionProvider.Ssh => StorageProviderKind.Ssh,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown storage provider.")
    };

    private void PresentSnapshot(LocalBrowserSnapshot snapshot, bool appendPage = false)
    {
        _addressBox.Text = snapshot.Location.DisplayText;
        var pageItems = snapshot.Entries.Select(static entry => new BrowserListItem(
            entry.Name,
            entry.Length is null ? string.Empty : UiFormatting.FormatBytes(entry.Length.Value),
            entry.Type,
            LocalBrowserPresentation.FormatModified(entry.Modified),
            entry.Status,
            entry.FullPath,
            entry.IsContainer,
            entry.IsContainer ? StorageItemKind.Directory : StorageItemKind.File,
            entry.Length,
            ModifiedUtc: entry.Modified));
        if (appendPage) AppendItems(pageItems);
        else SetItems(pageItems);
        _connectionState.Text = "● Ready";
        _connectionState.ForeColor = StorageHubTheme.Success;
        _connectionState.AccessibleDescription = $"Showing {snapshot.Location.DisplayText}";
        if (!appendPage) UpdateDirectoryTree(snapshot);
        if (snapshot.HasMore) BeginInvoke(new Action(RequestAutomaticPrefetch));
    }

    private void ApplyFilter()
    {
        var selectedLocations = CaptureSelectedLocations();
        _fileList.SelectedIndices.Clear();
        var filter = _filterBox.Text;
        var filteredItems = _listingIndex.CreateView(_sortColumn, _sortAscending, filter);
        _items = ComposeVisibleItems(filteredItems, CanNavigateUpInCurrentSurface());
        _shellIcons.Prime(_items, IsLocalConnectionSelected);
        _fileList.VirtualListSize = _items.Count;
        // VirtualListSize can remain unchanged when one folder replaces another. Force the
        // native list to discard its old visual cache and repaint all newly supplied rows.
        if (_fileList.IsHandleCreated && _items.Count > 0)
        {
            _fileList.RedrawItems(0, _items.Count - 1, invalidateOnly: true);
        }
        _fileList.Invalidate(invalidateChildren: true);
        _fileList.Update();
        RestoreSelectedLocations(selectedLocations);
        UpdateSummaryText();
    }

    private string[] CaptureSelectedLocations() => _fileList.SelectedIndices.Cast<int>()
        .Where(index => (uint)index < (uint)_items.Count)
        .Select(index => _items[index].Location)
        .Where(static location => !string.IsNullOrWhiteSpace(location))
        .Cast<string>()
        .ToArray();

    private void RestoreSelectedLocations(string[] locations)
    {
        if (locations.Length == 0) return;
        var parentOffset = CanNavigateUpInCurrentSurface() ? 1 : 0;
        _fileList.BeginUpdate();
        try
        {
            foreach (var location in locations)
            {
                var index = _listingIndex.FindIndex(
                    _sortColumn,
                    _sortAscending,
                    _filterBox.Text,
                    location);
                if (index is { } found) _fileList.SelectedIndices.Add(found + parentOffset);
            }
        }
        finally
        {
            _fileList.EndUpdate();
        }
    }

    private void UpdateSummaryText()
    {
        var filter = _filterBox.Text;
        var visibleItemCount = _items.Count(static item => !item.IsParentNavigation);
        var countText = visibleItemCount == _allItems.Count
            ? $"{visibleItemCount:N0} items"
            : $"{visibleItemCount:N0} of {_allItems.Count:N0} items";
        var hasMore = IsRemoteSnapshotSelected && _remoteBrowser?.CurrentSnapshot?.HasMore == true ||
            IsLocalConnectionSelected && _localBrowser?.CurrentSnapshot?.HasMore == true;
        _summary.Text = hasMore
            ? countText + (_remotePageLoadInProgress || _localPageLoadInProgress
                ? " | indexing next page…"
                : " | more available")
            : countText;
        _summary.AccessibleDescription = string.IsNullOrWhiteSpace(filter)
            ? _summary.Text
            : $"{_summary.Text} match filter {filter}";
    }

    private void UpdateDirectoryTree(LocalBrowserSnapshot snapshot)
    {
        if (_directoryTree.Nodes.Count == 0)
        {
            return;
        }

        _updatingTreeSelection = true;
        _directoryTree.BeginUpdate();
        try
        {
            var root = _directoryTree.Nodes[0];
            root.Text = "This PC";
            root.Tag = LocalBrowserLocation.ThisPc;
            SetTreeNodeIcon(root, "connection");
            if (snapshot.Location.IsThisPc)
            {
                root.Nodes.Clear();
                foreach (var drive in snapshot.Entries.Where(static entry => entry.IsContainer))
                {
                    if (TryCreateLocation(drive.FullPath, out var driveLocation))
                    {
                        var driveNode = new TreeNode(drive.Name)
                        {
                            Tag = driveLocation,
                            ToolTipText = drive.FullPath
                        };
                        SetTreeNodeIcon(driveNode, "folder");
                        root.Nodes.Add(driveNode);
                    }
                }

                root.Expand();
                _directoryTree.SelectedNode = root;
                return;
            }

            var currentNode = EnsureLocationNode(root, snapshot.Location);
            currentNode.Nodes.Clear();
            foreach (var directory in snapshot.Entries.Where(static entry => entry.IsContainer).Take(500))
            {
                if (TryCreateLocation(directory.FullPath, out var childLocation))
                {
                    var directoryNode = new TreeNode(directory.Name)
                    {
                        Tag = childLocation,
                        ToolTipText = directory.FullPath
                    };
                    SetTreeNodeIcon(directoryNode, "folder");
                    currentNode.Nodes.Add(directoryNode);
                }
            }

            currentNode.Expand();
            _directoryTree.SelectedNode = currentNode;
            currentNode.EnsureVisible();
        }
        finally
        {
            _directoryTree.EndUpdate();
            _updatingTreeSelection = false;
        }
    }

    private static TreeNode EnsureLocationNode(TreeNode root, LocalBrowserLocation location)
    {
        var ancestors = new Stack<LocalBrowserLocation>();
        var candidate = location;
        for (var depth = 0; !candidate.IsThisPc && depth < 256; depth++)
        {
            ancestors.Push(candidate);
            candidate = candidate.GetParent();
        }

        var node = root;
        while (ancestors.TryPop(out var ancestor))
        {
            var existing = node.Nodes
                .Cast<TreeNode>()
                .FirstOrDefault(child => child.Tag is LocalBrowserLocation childLocation &&
                    childLocation.IsSameLocation(ancestor));
            if (existing is null)
            {
                existing = node.Nodes.Add(GetLocationNodeText(ancestor));
                existing.Tag = ancestor;
                existing.ToolTipText = ancestor.DisplayText;
                SetTreeNodeIcon(existing, "folder");
            }

            node = existing;
        }

        return node;
    }

    private static string GetLocationNodeText(LocalBrowserLocation location)
    {
        if (location.DirectoryPath is null)
        {
            return "This PC";
        }

        var name = Path.GetFileName(location.DirectoryPath);
        return string.IsNullOrWhiteSpace(name) ? location.DirectoryPath : name;
    }

    private static bool TryCreateLocation(string path, out LocalBrowserLocation location)
    {
        try
        {
            location = LocalBrowserLocation.FromDirectory(path);
            return true;
        }
        catch (Exception error) when (LocalBrowserErrors.IsExpected(error))
        {
            location = LocalBrowserLocation.ThisPc;
            return false;
        }
    }

    private void SetNavigationBusy(bool isBusy)
    {
        if (isBusy)
        {
            _connectionState.Text = "● Loading…";
            _connectionState.ForeColor = StorageHubTheme.Primary;
        }
        else if (IsLocalConnectionSelected ||
            IsRemoteSnapshotSelected)
        {
            _connectionState.Text = "● Ready";
            _connectionState.ForeColor = StorageHubTheme.Success;
        }
        else
        {
            _connectionState.Text = "○ Choose a saved connection";
            _connectionState.ForeColor = StorageHubTheme.TextMuted;
        }

        _fileList.UseWaitCursor = isBusy;
        _loadingOverlay.Visible = isBusy;
        if (isBusy)
        {
            _loadingOverlay.BringToFront();
            _summary.Text = "Loading…";
        }
        else
        {
            UpdateSummaryText();
        }
        _refreshButton.ToolTipText = isBusy ? "Cancel current load and refresh" : "Refresh";
    }

    private void UpdateNavigationButtons()
    {
        var localActive = _localBrowser is not null && IsLocalConnectionSelected;
        var remoteActive = _remoteBrowser is not null && IsRemoteSnapshotSelected;
        _backButton.Enabled = localActive
            ? _localBrowser!.CanGoBack
            : remoteActive && _remoteBrowser!.CanGoBack;
        _forwardButton.Enabled = localActive
            ? _localBrowser!.CanGoForward
            : remoteActive && _remoteBrowser!.CanGoForward;
        _upButton.Enabled = localActive
            ? !_localBrowser!.CurrentLocation.IsThisPc
            : remoteActive && _remoteBrowser!.CanGoUp;
        _refreshButton.Enabled = localActive || remoteActive || IsAnyConnectionsHomeSelected;
        var localHasMore = localActive && _localBrowser?.CurrentSnapshot?.HasMore == true;
        var remoteHasMore = remoteActive && _remoteBrowser!.CurrentSnapshot!.HasMore;
        _loadMoreButton.Visible = !_localBrowsingEnabled || remoteActive || localHasMore;
        _loadMoreButton.Enabled = localHasMore || remoteHasMore;
        _addressBox.ReadOnly = !(localActive || remoteActive);
        _addressBox.AccessibleDescription = localActive
            ? "Enter an absolute local or UNC folder path, then press Enter."
            : remoteActive
                ? "Enter a path relative to the selected saved connection root, then press Enter."
                : "Choose a saved connection to browse its storage root.";
        _filterBox.Enabled = localActive || remoteActive || IsAnyConnectionsHomeSelected;
    }

    private void ShowError(string message)
    {
        _errorBanner.ForeColor = StorageHubTheme.Warning;
        _errorBanner.Text = $"⚠ {message}";
        _errorBanner.AccessibleDescription = message;
        _errorBanner.Visible = true;
        _connectionState.Text = "⚠ Location unavailable";
        _connectionState.ForeColor = StorageHubTheme.Warning;
    }

    private void ShowNotice(string message)
    {
        _errorBanner.Text = $"⚠ {message}";
        _errorBanner.AccessibleDescription = message;
        _errorBanner.Visible = true;
        _connectionState.Text = "● Ready";
        _connectionState.ForeColor = StorageHubTheme.Success;
    }

    private void HideError()
    {
        _errorBanner.Visible = false;
        _errorBanner.Text = string.Empty;
        _errorBanner.AccessibleDescription = string.Empty;
    }

    private bool IsLocalConnectionSelected =>
        _localBrowsingEnabled &&
        _connectionSelector.SelectedItem is ConnectionCardModel { Name: "This PC" };

    private bool IsConnectionsHomeSelected =>
        !_localBrowsingEnabled &&
        _connectionSelector.SelectedItem is ConnectionCardModel { ConnectionId: null };

    private bool IsLocalConnectionsHomeSelected =>
        _localBrowsingEnabled &&
        _connectionSelector.SelectedItem is ConnectionCardModel { Name: "Connections Home" };

    private bool IsAnyConnectionsHomeSelected =>
        IsConnectionsHomeSelected || IsLocalConnectionsHomeSelected;

    private bool IsRemoteSnapshotSelected =>
        _connectionSelector.SelectedItem is ConnectionCardModel { ConnectionId: { } connectionId } &&
        _remoteBrowser?.CurrentSnapshot?.Connection.ConnectionId == connectionId;

    private bool IsSshClientSelected =>
        _connectionSelector.SelectedItem is ConnectionCardModel
        {
            Type: ConnectionProfileType.Client,
            Provider: StorageProviderKind.Ssh,
            ConnectionId: not null
        };

    private void BackClicked(object? sender, EventArgs e)
    {
        _ = IsLocalConnectionSelected
            ? NavigateLocalAsync(LocalBrowserNavigationKind.Back)
            : NavigateRemoteAsync(RemoteBrowserNavigationKind.Back);
    }

    private void ForwardClicked(object? sender, EventArgs e)
    {
        _ = IsLocalConnectionSelected
            ? NavigateLocalAsync(LocalBrowserNavigationKind.Forward)
            : NavigateRemoteAsync(RemoteBrowserNavigationKind.Forward);
    }

    private void UpClicked(object? sender, EventArgs e)
    {
        _ = IsLocalConnectionSelected
            ? NavigateLocalAsync(LocalBrowserNavigationKind.Up)
            : NavigateRemoteAsync(RemoteBrowserNavigationKind.Up);
    }

    private void RefreshClicked(object? sender, EventArgs e)
    {
        if (IsLocalConnectionSelected)
        {
            _ = NavigateLocalAsync(LocalBrowserNavigationKind.Refresh);
        }
        else if (IsAnyConnectionsHomeSelected)
        {
            _ = LoadRemoteConnectionsAsync();
        }
        else
        {
            _ = NavigateRemoteAsync(RemoteBrowserNavigationKind.Refresh);
        }
    }

    private void LoadMoreClicked(object? sender, EventArgs e)
    {
        if (IsLocalConnectionSelected) _ = LoadMoreLocalAsync();
        else _ = LoadMoreRemoteAsync();
    }

    private void AddressBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter)
        {
            return;
        }

        if (IsLocalConnectionSelected)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            if (!LocalBrowserLocation.TryParseAddress(_addressBox.Text, out var location, out var errorMessage))
            {
                ShowError(errorMessage ?? "The address is not a valid folder path.");
                return;
            }

            _ = NavigateLocalAsync(LocalBrowserNavigationKind.Navigate, location);
            return;
        }

        if (!IsRemoteSnapshotSelected)
        {
            return;
        }

        e.Handled = true;
        e.SuppressKeyPress = true;
        if (!RemoteBrowserPath.TryNormalize(_addressBox.Text, out var remotePath, out var remoteError))
        {
            ShowError(remoteError ?? "The address is not a valid remote path.");
            return;
        }

        _ = NavigateRemoteAsync(RemoteBrowserNavigationKind.Navigate, remotePath);
    }

    private void FilterTextChanged(object? sender, EventArgs e) => ApplyFilter();

    private void FileListDoubleClick(object? sender, EventArgs e)
    {
        OpenOrEditSelected();
    }

    private static string DescribeConnectionHealth(ConnectionHealthSnapshot? health) => health switch
    {
        null => "Not tested",
        { State: ConnectionHealthState.Healthy } => $"Healthy · {health.ElapsedMilliseconds:N0} ms",
        { RequiresCredentialAction: true } => "Credentials need attention",
        { RequiresTrustAction: true } => "Trust decision required",
        { State: ConnectionHealthState.Unavailable } => "Unavailable",
        _ => "Needs attention"
    };

    private void FileListMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right)
        {
            return;
        }

        var item = _fileList.GetItemAt(e.X, e.Y);
        if (item is null)
        {
            _fileList.SelectedIndices.Clear();
        }
        else if (!item.Selected)
        {
            _fileList.SelectedIndices.Clear();
            item.Selected = true;
            item.Focused = true;
        }
    }

    private async void FileListItemDrag(object? sender, ItemDragEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var selection = CaptureSelectionSnapshot();
        if (selection.IsFailure) return;

        var payload = new PaneDragPayload(this, selection.Value);
        var data = new DataObject();
        data.SetData(PaneDragDataFormat, autoConvert: false, payload);
        ExplorerDropBeginResponse? explorerDrop = null;
        if (selection.Value.Context.Kind == PaneTransferContextKind.ThisPc)
        {
            var paths = _fileList.SelectedIndices.Cast<int>()
                .Where(index => (uint)index < (uint)_items.Count)
                .Select(index => _items[index].Location)
                .Where(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
                .Cast<string>().ToArray();
            if (paths.Length > 0) data.SetData(DataFormats.FileDrop, paths);
        }
        else if (selection.Value.Context.Kind == PaneTransferContextKind.SavedConnection && BeginExplorerDropAsync is not null)
        {
            try
            {
                explorerDrop = await BeginExplorerDropAsync(selection.Value, CancellationToken.None).ConfigureAwait(true);
                if (explorerDrop.Failure is not null || string.IsNullOrWhiteSpace(explorerDrop.DropToken) ||
                    string.IsNullOrWhiteSpace(explorerDrop.MarkerPath) || !Directory.Exists(explorerDrop.MarkerPath))
                {
                    ShowError(explorerDrop.Failure?.Message ?? "StorageHub could not initialize the Explorer drop.");
                    return;
                }
                data.SetData(DataFormats.FileDrop, new[] { explorerDrop.MarkerPath });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
                InvalidOperationException or TimeoutException or System.Text.Json.JsonException or OperationCanceledException)
            {
                ShowError($"StorageHub could not initialize the Explorer drop: {error.Message}");
                return;
            }
        }

        var allowedEffects = selection.Value.Context.Kind == PaneTransferContextKind.SavedConnection
            ? DragDropEffects.Copy
            : DragDropEffects.Copy | DragDropEffects.Move;
        try
        {
            _ = _fileList.DoDragDrop(data, allowedEffects);
        }
        catch (Exception error) when (error is ExternalException or InvalidOperationException)
        {
            ShowError($"Windows could not start the drag operation: {error.Message}");
            return;
        }

        if (selection.Value.Context.Kind == PaneTransferContextKind.SavedConnection &&
            explorerDrop is null && !payload.InternalDropHandled &&
            !string.IsNullOrWhiteSpace(ExplorerDropUnavailableReason))
        {
            ShowError(ExplorerDropUnavailableReason);
        }

        if (explorerDrop is not null && CommitExplorerDropAsync is not null)
        {
            try
            {
                var committed = await CommitExplorerDropAsync(explorerDrop.DropToken!, CancellationToken.None).ConfigureAwait(true);
                if (!payload.InternalDropHandled && committed.Accepted)
                {
                    _errorBanner.Text = $"Queued in StorageHub → {committed.DestinationPath}";
                    _errorBanner.ForeColor = StorageHubTheme.Success;
                    _errorBanner.Visible = true;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
                InvalidOperationException or TimeoutException or System.Text.Json.JsonException or OperationCanceledException)
            {
                if (!payload.InternalDropHandled)
                    ShowError($"StorageHub could not queue the Explorer drop: {error.Message}");
            }
        }
    }

    internal static string CreateShellExportKey(PaneSelectionSnapshot selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return string.Join("\n",
            new[]
            {
                selection.Context.ConnectionId?.ToString("D") ?? string.Empty,
                selection.Context.RootIdentity ?? string.Empty
            }.Concat(selection.Items
                .OrderBy(static item => item.RelativePath, StringComparer.Ordinal)
                .Select(item => string.Join("|", item.RelativePath, item.VersionId, item.EntityTag))));
    }

    private void ConfigureDropTarget(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += DropTargetDragEnter;
        control.DragOver += DropTargetDragOver;
        control.DragLeave += DropTargetDragLeave;
        control.DragDrop += DropTargetDragDrop;
    }

    private void UnconfigureDropTarget(Control control)
    {
        control.DragEnter -= DropTargetDragEnter;
        control.DragOver -= DropTargetDragOver;
        control.DragLeave -= DropTargetDragLeave;
        control.DragDrop -= DropTargetDragDrop;
    }

    private void DropTargetDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = GetDropEffect(e);
        if (e.Effect != DragDropEffects.None && sender is Control control)
        {
            control.BackColor = StorageHubTheme.CurrentPalette.Selection;
        }
    }

    private void DropTargetDragOver(object? sender, DragEventArgs e) => e.Effect = GetDropEffect(e);

    private void DropTargetDragLeave(object? sender, EventArgs e)
    {
        if (sender is Control control)
        {
            control.BackColor = StorageHubTheme.Surface;
        }
    }

    private void DropTargetDragDrop(object? sender, DragEventArgs e)
    {
        DropTargetDragLeave(sender, EventArgs.Empty);
        var effect = GetDropEffect(e);
        if (TryGetPaneDragPayload(e.Data) is null && e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            var treeTarget = sender is TreeView tree && tree.GetNodeAt(e.X, e.Y) is { Tag: string path }
                ? path : null;
            var destination = CaptureShellImportDestination(treeTarget);
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (destination.IsSuccess && paths is { Length: > 0 })
            {
                ShellImportDropRequested?.Invoke(this, new ShellImportDropRequestedEventArgs(paths, destination.Value));
            }
            return;
        }
        if (effect == DragDropEffects.None || TryGetPaneDragPayload(e.Data) is not { } payload)
        {
            return;
        }

        var operation = effect == DragDropEffects.Move
            ? TransferQueueOperation.Move
            : TransferQueueOperation.Copy;
        payload.InternalDropHandled = true;
        TransferDropRequested?.Invoke(
            this,
            new PaneTransferDropRequestedEventArgs(payload.SourcePane, payload.Selection, operation));
    }

    private DragDropEffects GetDropEffect(DragEventArgs e)
    {
        var payload = TryGetPaneDragPayload(e.Data);
        if (payload is null)
        {
            return e.Data?.GetDataPresent(DataFormats.FileDrop) == true && CaptureShellImportDestination().IsSuccess
                ? DragDropEffects.Copy : DragDropEffects.None;
        }
        if (ReferenceEquals(payload.SourcePane, this))
        {
            return DragDropEffects.None;
        }

        var shiftPressed = (e.KeyState & DragShiftKeyState) != 0;
        return shiftPressed && e.AllowedEffect.HasFlag(DragDropEffects.Move)
            ? DragDropEffects.Move
            : e.AllowedEffect.HasFlag(DragDropEffects.Copy)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
    }

    private static PaneDragPayload? TryGetPaneDragPayload(IDataObject? data)
    {
        if (data?.GetDataPresent(PaneDragDataFormat, autoConvert: false) != true)
        {
            return null;
        }

        return data.GetData(PaneDragDataFormat, autoConvert: false) as PaneDragPayload;
    }

    private void FileListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode is Keys.C or Keys.X)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            StageSelection(e.KeyCode == Keys.C
                ? TransferQueueOperation.Copy
                : TransferQueueOperation.Move);
            return;
        }

        if (e.Control && e.KeyCode == Keys.V)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            PasteRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (e.KeyCode != Keys.Enter)
        {
            return;
        }

        e.Handled = true;
        e.SuppressKeyPress = true;
        OpenOrEditSelected();
    }

    private void OpenOrEditSelected()
    {
        if (_fileList.SelectedIndices.Count != 1)
        {
            return;
        }

        var index = _fileList.SelectedIndices[0];
        if ((uint)index < (uint)_items.Count && _items[index].IsParentNavigation)
        {
            NavigateUp();
        }
        else if ((uint)index < (uint)_items.Count && _items[index].IsContainer)
        {
            OpenSelectedContainer();
        }
        else
        {
            RaiseEditRequested();
        }
    }

    private void RaiseEditRequested()
    {
        if (_fileList.SelectedIndices.Count == 1 &&
            (uint)_fileList.SelectedIndices[0] < (uint)_items.Count &&
            !_items[_fileList.SelectedIndices[0]].IsParentNavigation)
        {
            EditRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void StageSelection(TransferQueueOperation operation)
    {
        var selection = CaptureSelectionSnapshot();
        if (selection.IsSuccess)
        {
            SelectionStaged?.Invoke(this, new PaneSelectionStagedEventArgs(selection.Value, operation));
        }
    }

    private ToolStripMenuItem CreateContextMenuItem(string text, UiGlyph glyph, string? shortcut = null)
    {
        var image = UiIconFactory.Create(glyph, StorageHubTheme.Text, 18, DeviceDpi / 96F);
        _ownedImages.Add(image);
        return new ToolStripMenuItem(text)
        {
            Image = image,
            ShortcutKeyDisplayString = shortcut,
            AccessibleName = text
        };
    }

    private void RaiseTransferRequested(TransferQueueOperation operation)
    {
        if (HasTransferableSelection())
        {
            TransferRequested?.Invoke(this, new PaneTransferRequestedEventArgs(operation));
        }
    }

    private async Task InitializeLocalPaneAsync()
    {
        await NavigateLocalAsync(LocalBrowserNavigationKind.Refresh).ConfigureAwait(true);
        if (IsLocalConnectionSelected && !IsDisposed && !Disposing)
        {
            await LoadRemoteConnectionsAsync(preserveCurrentSurface: true).ConfigureAwait(true);
        }
    }

    private StorageResult<PaneTransferContext> CaptureTransferContext()
    {
        if (IsSshClientSelected)
        {
            return StorageResult<PaneTransferContext>.Fail(new StorageFailure(
                "manual_transfer.pane.client_not_storage",
                StorageFailureKind.Validation,
                "Choose a storage folder in this pane before transferring files."));
        }

        if (IsRemoteSnapshotSelected && _remoteBrowser?.CurrentSnapshot is { } remote)
        {
            return PaneTransferContext.Create(
                PaneTransferContextKind.SavedConnection,
                remote.Connection.ConnectionId,
                remote.RootIdentity,
                remote.RelativePath);
        }

        if (IsLocalConnectionSelected && _localBrowser is not null)
        {
            return PaneTransferContext.Create(
                PaneTransferContextKind.ThisPc,
                connectionId: null,
                rootIdentity: null,
                relativePath: _localBrowser.CurrentLocation.DirectoryPath ?? string.Empty);
        }

        if (IsAnyConnectionsHomeSelected)
        {
            return PaneTransferContext.Create(
                PaneTransferContextKind.ConnectionsHome,
                connectionId: null,
                rootIdentity: null,
                relativePath: string.Empty);
        }

        return PaneTransferContext.Create(
            PaneTransferContextKind.AdHoc,
            connectionId: null,
            rootIdentity: null,
            relativePath: _addressBox.Text);
    }

    private static StorageResult<PaneTransferItem> MapTransferItem(BrowserListItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Location))
        {
            return StorageResult<PaneTransferItem>.Fail(new StorageFailure(
                "manual_transfer.pane.item_unavailable",
                StorageFailureKind.Validation,
                "The pane contains an item that is not a transferable storage object."));
        }

        return PaneTransferItem.Create(
            item.Name,
            item.Location,
            item.Kind,
            item.Length,
            item.NativeItemId,
            item.VersionId,
            item.EntityTag);
    }

    private static StorageResult<PaneSelectionSnapshot> SelectionSnapshotFailure(string message) =>
        StorageResult<PaneSelectionSnapshot>.Fail(new StorageFailure(
            "manual_transfer.pane.selection_changed",
            StorageFailureKind.Conflict,
            message));

    private void OpenSelectedContainer()
    {
        if (_fileList.SelectedIndices.Count != 1)
        {
            return;
        }

        var index = _fileList.SelectedIndices[0];
        if ((uint)index >= (uint)_items.Count)
        {
            return;
        }

        var selected = _items[index];
        if (selected.IsParentNavigation)
        {
            NavigateUp();
            return;
        }

        if (!selected.IsContainer || string.IsNullOrWhiteSpace(selected.Location))
        {
            return;
        }

        if (IsLocalConnectionSelected && TryCreateLocation(selected.Location, out var location))
        {
            _ = NavigateLocalAsync(LocalBrowserNavigationKind.Navigate, location);
        }
        else if (IsRemoteSnapshotSelected)
        {
            _ = NavigateRemoteAsync(RemoteBrowserNavigationKind.Navigate, selected.Location);
        }
        else if (IsAnyConnectionsHomeSelected && Guid.TryParse(selected.Location, out var connectionId))
        {
            for (var choiceIndex = 0; choiceIndex < _connectionSelector.Items.Count; choiceIndex++)
            {
                if (_connectionSelector.Items[choiceIndex] is ConnectionCardModel { ConnectionId: { } candidateId } &&
                    candidateId == connectionId)
                {
                    _connectionSelector.SelectedIndex = choiceIndex;
                    break;
                }
            }
        }
    }

    private void DirectoryTreeAfterSelect(object? sender, TreeViewEventArgs e)
    {
        if (_updatingTreeSelection)
        {
            return;
        }

        if (IsLocalConnectionSelected &&
            e.Node?.Tag is LocalBrowserLocation location &&
            _localBrowser is not null &&
            !_localBrowser.CurrentLocation.IsSameLocation(location))
        {
            _ = NavigateLocalAsync(LocalBrowserNavigationKind.Navigate, location);
        }
        else if (IsRemoteSnapshotSelected &&
            e.Node?.Tag is string remotePath &&
            !string.Equals(_remoteBrowser?.CurrentSnapshot?.RelativePath, remotePath, StringComparison.Ordinal))
        {
            _ = NavigateRemoteAsync(RemoteBrowserNavigationKind.Navigate, remotePath);
        }
        else if (IsRemoteSnapshotSelected &&
            e.Node?.Tag is RemoteTreeRoot &&
            _remoteBrowser?.CurrentSnapshot?.RelativePath.Length > 0)
        {
            _ = NavigateRemoteAsync(RemoteBrowserNavigationKind.Navigate, string.Empty);
        }
    }

    private void NavigationLayoutChanged(object? sender, LayoutEventArgs e)
    {
        var occupiedWidth = _navigation.Padding.Horizontal + 12;
        foreach (ToolStripItem item in _navigation.Items)
        {
            if (!ReferenceEquals(item, _addressBox))
            {
                occupiedWidth += item.Width + item.Margin.Horizontal;
            }
        }

        var desiredWidth = Math.Max(160, _navigation.ClientSize.Width - occupiedWidth);
        if (_addressBox.Width != desiredWidth)
        {
            _addressBox.Width = desiredWidth;
        }
    }

    private ToolStripButton CreateButton(UiGlyph glyph, string accessibleName)
    {
        var image = UiIconFactory.Create(glyph, StorageHubTheme.Text, 18, DeviceDpi / 96F);
        _ownedImages.Add(image);
        return new ToolStripButton
        {
            Image = image,
            DisplayStyle = ToolStripItemDisplayStyle.Image,
            AccessibleName = accessibleName,
            AccessibleDescription = accessibleName,
            ToolTipText = accessibleName
        };
    }

    private ToolStripButton CreateCommandButton(UiGlyph glyph, string text, string description)
    {
        var image = UiIconFactory.Create(glyph, StorageHubTheme.Text, 16, DeviceDpi / 96F);
        _ownedImages.Add(image);
        return new ToolStripButton(text)
        {
            Image = image,
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            AccessibleName = text,
            AccessibleDescription = description,
            ToolTipText = description,
            Enabled = false
        };
    }

    private ToolStripDropDownButton CreateMoreCommandsButton(string paneTitle)
    {
        var image = UiIconFactory.Create(UiGlyph.More, StorageHubTheme.Text, 16, DeviceDpi / 96F);
        _ownedImages.Add(image);
        var button = new ToolStripDropDownButton
        {
            Alignment = ToolStripItemAlignment.Right,
            Image = image,
            DisplayStyle = ToolStripItemDisplayStyle.Image,
            AccessibleName = $"{paneTitle} more file commands",
            AccessibleDescription = "Open additional commands for this file pane.",
            ToolTipText = "More file commands",
            ShowDropDownArrow = false
        };

        var open = CreateContextMenuItem("Open", UiGlyph.Folder);
        var edit = CreateContextMenuItem("Edit in external editor...", UiGlyph.File);
        var copy = CreateContextMenuItem("Copy", UiGlyph.File, "Ctrl+C");
        var move = CreateContextMenuItem("Move", UiGlyph.Forward, "Ctrl+X");
        var paste = CreateContextMenuItem("Paste", UiGlyph.Save, "Ctrl+V");
        var delete = CreateContextMenuItem("Delete", UiGlyph.Delete, "Delete");
        var copyToOtherPane = CreateContextMenuItem("Copy to other pane", UiGlyph.Forward);
        var moveToOtherPane = CreateContextMenuItem("Move to other pane", UiGlyph.Run);
        var refresh = CreateContextMenuItem("Refresh", UiGlyph.Refresh, "F5");
        var selectAll = CreateContextMenuItem("Select all", UiGlyph.Test, "Ctrl+A");
        var properties = CreateContextMenuItem("Properties...", UiGlyph.Info);

        open.Font = new Font(open.Font, FontStyle.Bold);
        open.Click += (_, _) => OpenOrEditSelected();
        edit.Click += (_, _) => RaiseEditRequested();
        copy.Click += (_, _) => StageSelection(TransferQueueOperation.Copy);
        move.Click += (_, _) => StageSelection(TransferQueueOperation.Move);
        paste.Click += (_, _) => PasteRequested?.Invoke(this, EventArgs.Empty);
        delete.Click += (_, _) => DeleteRequested?.Invoke(this, EventArgs.Empty);
        copyToOtherPane.Click += (_, _) => RaiseTransferRequested(TransferQueueOperation.Copy);
        moveToOtherPane.Click += (_, _) => RaiseTransferRequested(TransferQueueOperation.Move);
        refresh.Click += (_, _) => Reload();
        selectAll.Click += (_, _) => SelectAllVisibleItems();
        properties.Click += (_, _) => ObjectInspectionRequested?.Invoke(this, EventArgs.Empty);

        button.DropDownItems.Add(open);
        button.DropDownItems.Add(edit);
        button.DropDownItems.Add(new ToolStripSeparator());
        button.DropDownItems.Add(copy);
        button.DropDownItems.Add(move);
        button.DropDownItems.Add(paste);
        button.DropDownItems.Add(delete);
        button.DropDownItems.Add(new ToolStripSeparator());
        button.DropDownItems.Add(copyToOtherPane);
        button.DropDownItems.Add(moveToOtherPane);
        button.DropDownItems.Add(new ToolStripSeparator());
        button.DropDownItems.Add(refresh);
        button.DropDownItems.Add(selectAll);
        button.DropDownItems.Add(new ToolStripSeparator());
        button.DropDownItems.Add(properties);
        button.DropDown.Renderer = DesktopAppearanceService.MenuRenderer;
        button.DropDownOpening += (_, _) =>
        {
            var hasSelection = HasTransferableSelection();
            var singleSelection = _fileList.SelectedIndices.Count == 1;
            var selectedItem = singleSelection && (uint)_fileList.SelectedIndices[0] < (uint)_items.Count
                ? _items[_fileList.SelectedIndices[0]]
                : null;
            var selectedContainer = selectedItem is { IsContainer: true };
            var selectedParent = selectedItem is { IsParentNavigation: true };
            open.Enabled = singleSelection;
            open.Text = selectedParent
                ? "Go up one level"
                : selectedContainer ? "Open" : "Open in external editor...";
            edit.Enabled = singleSelection && !selectedContainer && !selectedParent;
            copy.Enabled = hasSelection;
            move.Enabled = hasSelection;
            paste.Enabled = CanPaste?.Invoke() == true;
            delete.Enabled = hasSelection;
            copyToOtherPane.Enabled = hasSelection;
            moveToOtherPane.Enabled = hasSelection;
            selectAll.Enabled = _items.Any(static item => !item.IsParentNavigation);
            properties.Enabled = singleSelection && !selectedParent;
        };
        return button;
    }

    internal static IReadOnlyList<BrowserListItem> ComposeVisibleItems(
        IReadOnlyList<BrowserListItem> items,
        bool canNavigateUp)
    {
        ArgumentNullException.ThrowIfNull(items);
        return canNavigateUp ? new ParentPrefixedReadOnlyList(items) : items;
    }

    private bool CanNavigateUpInCurrentSurface() =>
        IsLocalConnectionSelected
            ? _localBrowser is not null && !_localBrowser.CurrentLocation.IsThisPc
            : IsRemoteSnapshotSelected && _remoteBrowser?.CanGoUp == true;

    private bool HasTransferableSelection()
    {
        if (_fileList.SelectedIndices.Count == 0)
        {
            return false;
        }

        foreach (var index in _fileList.SelectedIndices.Cast<int>())
        {
            if ((uint)index >= (uint)_items.Count || _items[index].IsParentNavigation)
            {
                return false;
            }
        }

        return true;
    }

    private sealed class ParentPrefixedReadOnlyList(IReadOnlyList<BrowserListItem> items)
        : IReadOnlyList<BrowserListItem>
    {
        public int Count => checked(items.Count + 1);
        public BrowserListItem this[int index] => index switch
        {
            0 => ParentNavigationItem,
            > 0 when index <= items.Count => items[index - 1],
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
        public IEnumerator<BrowserListItem> GetEnumerator()
        {
            yield return ParentNavigationItem;
            foreach (var item in items) yield return item;
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private ImageList CreateBrowserImageList()
    {
        var scale = DeviceDpi / 96F;
        var imageSize = Math.Max(16, (int)Math.Round(18 * scale, MidpointRounding.AwayFromZero));
        var images = new ImageList
        {
            ColorDepth = ColorDepth.Depth32Bit,
            ImageSize = new Size(imageSize, imageSize),
            TransparentColor = Color.Transparent
        };
        images.Images.Add("connection", UiIconFactory.Create(UiGlyph.Connections, StorageHubTheme.Primary, 18, scale));
        images.Images.Add("folder", UiIconFactory.Create(UiGlyph.Folder, Color.FromArgb(218, 154, 45), 18, scale));
        images.Images.Add("file", UiIconFactory.Create(UiGlyph.File, StorageHubTheme.TextMuted, 18, scale));
        return images;
    }

    private static TableLayoutPanel CreateLoadingOverlay()
    {
        var overlay = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = StorageHubTheme.Surface,
            ColumnCount = 1,
            RowCount = 3,
            Visible = false,
            AccessibleName = "Folder loading indicator",
            AccessibleRole = AccessibleRole.ProgressBar
        };
        overlay.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        overlay.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        overlay.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        overlay.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        var content = new FlowLayoutPanel
        {
            Anchor = AnchorStyles.None,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false
        };
        content.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "Fetching folder…",
            Font = StorageHubTheme.CreateSectionFont(),
            ForeColor = StorageHubTheme.Text,
            Margin = new Padding(0, 0, 0, 10)
        });
        content.Controls.Add(new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 28,
            Size = new Size(220, 8),
            AccessibleName = "Fetching folder contents"
        });
        overlay.Controls.Add(content, 0, 1);
        return overlay;
    }

    private static void SetTreeNodeIcon(TreeNode node, string key)
    {
        node.ImageKey = key;
        node.SelectedImageKey = key;
    }

    private static Label CreatePaneBadge(string text, Color color) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = color,
        BackColor = StorageHubTheme.SurfaceMuted,
        Padding = new Padding(6, 2, 6, 2),
        Margin = new Padding(2, 0, 4, 0),
        Font = new Font("Segoe UI Semibold", 7.5F, FontStyle.Bold, GraphicsUnit.Point)
    };

    private static Panel CreateHeaderFrame(Padding padding, Padding margin) => new()
    {
        Dock = DockStyle.Fill,
        Padding = padding,
        Margin = margin,
        BackColor = StorageHubTheme.SurfaceMuted,
        BorderStyle = BorderStyle.FixedSingle
    };

    private void MeasureConnectionItem(object? sender, MeasureItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _connectionSelector.Items.Count)
        {
            e.ItemHeight = 32;
            return;
        }

        e.ItemHeight = GetConnectionItemHeight(e.Index);
    }

    private int GetConnectionItemHeight(int index) => IsFirstConnectionInGroup(index) ? 76 : 54;

    private void ConnectionSelectorDropDown(object? sender, EventArgs e)
    {
        if (_connectionSelector.Items.Count == 0)
        {
            return;
        }

        using var badgeFont = new Font("Segoe UI Semibold", 7.5F, FontStyle.Bold, GraphicsUnit.Point);
        using var groupFont = new Font("Segoe UI Semibold", 7.5F, FontStyle.Bold, GraphicsUnit.Point);
        var desiredWidth = _connectionSelector.Width;
        var desiredHeight = 4;
        for (var index = 0; index < _connectionSelector.Items.Count; index++)
        {
            if (_connectionSelector.Items[index] is not ConnectionCardModel card)
            {
                continue;
            }

            desiredHeight += GetConnectionItemHeight(index);
            var badge = card.Type == ConnectionProfileType.Client
                ? $"CLIENT · {card.Provider.ToString().ToUpperInvariant()}"
                : card.Provider == StorageProviderKind.Local
                    ? "SYSTEM · LOCAL"
                    : $"STORAGE · {card.Provider.ToString().ToUpperInvariant()}";
            var nameAndBadge = TextRenderer.MeasureText(card.Name, _connectionSelector.Font).Width +
                TextRenderer.MeasureText(badge, badgeFont).Width + 92;
            var detailWidth = TextRenderer.MeasureText(card.Endpoint, _connectionSelector.Font).Width + 76;
            var groupWidth = TextRenderer.MeasureText(
                GetConnectionGroupLabel(card).ToUpperInvariant(),
                groupFont).Width + 44;
            desiredWidth = Math.Max(desiredWidth, Math.Max(nameAndBadge, Math.Max(detailWidth, groupWidth)));
        }

        var workingArea = Screen.FromControl(_connectionSelector).WorkingArea;
        _connectionSelector.DropDownWidth = Math.Min(
            Math.Max(_connectionSelector.Width, desiredWidth),
            Math.Max(_connectionSelector.Width, workingArea.Width - 48));
        _connectionSelector.DropDownHeight = Math.Min(
            desiredHeight,
            Math.Max(180, Math.Min(560, workingArea.Height - 96)));
    }

    private void DrawConnectionItem(object? sender, DrawItemEventArgs e)
    {
        using var background = new SolidBrush(StorageHubTheme.Surface);
        e.Graphics.FillRectangle(background, e.Bounds);
        if (e.Index < 0 || _connectionSelector.Items[e.Index] is not ConnectionCardModel card)
        {
            return;
        }

        var compact = (e.State & DrawItemState.ComboBoxEdit) != 0;
        if (compact)
        {
            DrawCompactConnectionItem(e.Graphics, e.Bounds, card, e.Font ?? Font);
            return;
        }

        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var firstInGroup = IsFirstConnectionInGroup(e.Index);
        var groupTop = firstInGroup ? 22 : 0;
        if (firstInGroup)
        {
            using var groupFont = new Font("Segoe UI Semibold", 7.5F, FontStyle.Bold, GraphicsUnit.Point);
            TextRenderer.DrawText(
                e.Graphics,
                GetConnectionGroupLabel(card).ToUpperInvariant(),
                groupFont,
                new Rectangle(e.Bounds.Left + 12, e.Bounds.Top + 2, e.Bounds.Width - 24, 18),
                StorageHubTheme.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        var cardBounds = new Rectangle(
            e.Bounds.Left + 8,
            e.Bounds.Top + groupTop + 2,
            Math.Max(1, e.Bounds.Width - 16),
            Math.Max(1, e.Bounds.Height - groupTop - 5));
        var selected = (e.State & DrawItemState.Selected) != 0;
        using var cardFill = new SolidBrush(selected
            ? StorageHubTheme.CurrentPalette.Selection
            : StorageHubTheme.SurfaceMuted);
        using var border = new Pen(selected
            ? StorageHubTheme.ParseAccent(card.AccentHex)
            : StorageHubTheme.Border,
            selected ? 1.8F : 1F);
        using var cardPath = CreateRoundedRectangle(cardBounds, 10);
        e.Graphics.FillPath(cardFill, cardPath);
        e.Graphics.DrawPath(border, cardPath);

        var accent = StorageHubTheme.ParseAccent(card.AccentHex);
        using var accentBrush = new SolidBrush(accent);
        e.Graphics.FillEllipse(accentBrush, cardBounds.Left + 12, cardBounds.Top + 11, 9, 9);

        var badgeText = card.Type == ConnectionProfileType.Client
            ? $"CLIENT · {card.Provider.ToString().ToUpperInvariant()}"
            : card.Provider == StorageProviderKind.Local
                ? "SYSTEM · LOCAL"
                : $"STORAGE · {card.Provider.ToString().ToUpperInvariant()}";
        using var badgeFont = new Font("Segoe UI Semibold", 7.5F, FontStyle.Bold, GraphicsUnit.Point);
        var badgeSize = TextRenderer.MeasureText(badgeText, badgeFont, Size.Empty, TextFormatFlags.NoPadding);
        var badgeBounds = new Rectangle(
            cardBounds.Right - badgeSize.Width - 19,
            cardBounds.Top + Math.Max(6, (cardBounds.Height - 22) / 2),
            badgeSize.Width + 12,
            20);
        using var badgeFill = new SolidBrush(Color.FromArgb(selected ? 48 : 28, accent));
        using var badgePath = CreateRoundedRectangle(badgeBounds, 9);
        e.Graphics.FillPath(badgeFill, badgePath);
        TextRenderer.DrawText(
            e.Graphics,
            badgeText,
            badgeFont,
            badgeBounds,
            selected ? StorageHubTheme.Text : accent,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        var textRight = Math.Max(cardBounds.Left + 70, badgeBounds.Left - 10);
        TextRenderer.DrawText(
            e.Graphics,
            card.Name,
            e.Font ?? Font,
            Rectangle.FromLTRB(cardBounds.Left + 29, cardBounds.Top + 5, textRight, cardBounds.Top + 24),
            StorageHubTheme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        using var detailFont = new Font(e.Font ?? Font, FontStyle.Regular);
        TextRenderer.DrawText(
            e.Graphics,
            card.Endpoint,
            detailFont,
            Rectangle.FromLTRB(cardBounds.Left + 29, cardBounds.Top + 23, textRight, cardBounds.Bottom - 3),
            StorageHubTheme.TextMuted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private static void DrawCompactConnectionItem(
        Graphics graphics,
        Rectangle bounds,
        ConnectionCardModel card,
        Font font)
    {
        var accent = StorageHubTheme.ParseAccent(card.AccentHex);
        using var accentBrush = new SolidBrush(accent);
        graphics.FillEllipse(accentBrush, bounds.Left + 7, bounds.Top + Math.Max(2, (bounds.Height - 9) / 2), 9, 9);
        var type = card.Type == ConnectionProfileType.Client
            ? "CLIENT"
            : card.Provider == StorageProviderKind.Local ? "LOCAL" : "STORAGE";
        var typeWidth = TextRenderer.MeasureText(type, font).Width + 10;
        var typeBounds = new Rectangle(bounds.Right - typeWidth - 5, bounds.Top + 3, typeWidth, Math.Max(18, bounds.Height - 6));
        TextRenderer.DrawText(
            graphics,
            type,
            font,
            typeBounds,
            accent,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(
            graphics,
            card.Name,
            font,
            Rectangle.FromLTRB(bounds.Left + 23, bounds.Top, typeBounds.Left - 5, bounds.Bottom),
            StorageHubTheme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private bool IsFirstConnectionInGroup(int index) => index == 0 ||
        _connectionSelector.Items[index - 1] is not ConnectionCardModel previous ||
        _connectionSelector.Items[index] is not ConnectionCardModel current ||
        !string.Equals(
            GetConnectionGroupLabel(previous),
            GetConnectionGroupLabel(current),
            StringComparison.OrdinalIgnoreCase);

    private static string ConnectionGroupSortKey(ConnectionCardModel card) =>
        $"{(card.IsEnabled ? card.IsFavorite ? 0 : card.Type == ConnectionProfileType.Storage ? 1 : 2 : 3)}|" +
        GetConnectionGroupLabel(card);

    private static string GetConnectionGroupLabel(ConnectionCardModel card)
    {
        if (card.ConnectionId is null)
        {
            return "On this device";
        }

        if (!card.IsEnabled)
        {
            return "Disabled";
        }

        if (card.IsFavorite)
        {
            return "Favorites";
        }

        if (!string.IsNullOrWhiteSpace(card.FolderPath))
        {
            return card.FolderPath.Trim();
        }

        return card.Type == ConnectionProfileType.Client
            ? $"Clients · {card.Descriptor.DisplayName}"
            : $"Storage · {card.Descriptor.DisplayName}";
    }

    private static System.Drawing.Drawing2D.GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void ConnectionSelectionChanged(object? sender, EventArgs e)
    {
        if (_updatingConnectionChoices)
        {
            return;
        }

        _uiNavigationSequence++;
        _localBrowser?.CancelCurrentNavigation();
        _remoteBrowser?.CancelCurrentOperation();
        SetNavigationBusy(false);
        UpdateConnectionPresentation();
        if (IsSshClientSelected &&
            _connectionSelector.SelectedItem is ConnectionCardModel
            {
                ConnectionId: { } clientId
            } client)
        {
            ShowSshClientSurface(clientId, client.Name);
        }
        else if (IsLocalConnectionSelected)
        {
            ShowBrowserSurface();
            _addressBox.Text = _localBrowser!.CurrentLocation.DisplayText;
            _ = NavigateLocalAsync(LocalBrowserNavigationKind.Refresh);
        }
        else if (IsAnyConnectionsHomeSelected)
        {
            ShowBrowserSurface();
            HideError();
            _filterBox.Text = string.Empty;
            _addressBox.Text = "Connections";
            SetItems([new BrowserListItem(
                "Loading saved connections…",
                string.Empty,
                "Status",
                string.Empty,
                string.Empty)]);
            _ = LoadRemoteConnectionsAsync();
        }
        else if (_connectionSelector.SelectedItem is ConnectionCardModel { ConnectionId: { } connectionId })
        {
            ShowBrowserSurface();
            _filterBox.Text = string.Empty;
            _ = SelectRemoteConnectionAsync(connectionId);
        }
    }

    private void UpdateConnectionPresentation()
    {
        if (_connectionSelector.SelectedItem is not ConnectionCardModel card)
        {
            return;
        }

        _accentBar.BackColor = StorageHubTheme.ParseAccent(card.AccentHex);
        _connectionNameLabel.Text = card.Name;
        _connectionNameLabel.AccessibleDescription = $"Active connection: {card.Name}";
        _connectionTypeBadge.Text = card.Type == ConnectionProfileType.Client ? "CLIENT" : "STORAGE";
        _connectionTypeBadge.ForeColor = StorageHubTheme.ParseAccent(card.AccentHex);
        _providerBadge.Text = card.Provider.ToString().ToUpperInvariant();
        var localActive = IsLocalConnectionSelected;
        var remoteActive = IsRemoteSnapshotSelected;
        var clientActive = IsSshClientSelected;
        _connectionState.Text = clientActive
            ? "● SSH terminal"
            : localActive || remoteActive
                ? "● Ready"
                : "○ Choose a saved connection";
        _connectionState.ForeColor = localActive || remoteActive || clientActive
            ? StorageHubTheme.Success
            : StorageHubTheme.TextMuted;
        _connectionState.AccessibleDescription = card.State;
        _addressBox.Text = localActive
            ? _localBrowser!.CurrentLocation.DisplayText
            : remoteActive
                ? _remoteBrowser!.CurrentSnapshot!.DisplayPath
                : clientActive
                    ? "SSH terminal"
                    : "Connections";
        UpdateNavigationButtons();
        RefreshCommandState();
    }

    private void ShowSshClientSurface(Guid connectionId, string displayName)
    {
        if (_embeddedTerminalConnectionId == connectionId && _embeddedTerminal is not null)
        {
            _browserSurface.Visible = false;
            _embeddedTerminal.Visible = true;
            return;
        }

        DisposeEmbeddedTerminal();
        _browserSurface.Visible = false;
        var terminal = _sshTerminalFactory(connectionId, displayName);
        terminal.TopLevel = false;
        terminal.FormBorderStyle = FormBorderStyle.None;
        terminal.Dock = DockStyle.Fill;
        terminal.ShowInTaskbar = false;
        _embeddedTerminal = terminal;
        _embeddedTerminalConnectionId = connectionId;
        _contentHost.Controls.Add(terminal);
        terminal.BringToFront();
        terminal.Show();
        _ = terminal.StartSessionAsync();
    }

    private void ShowBrowserSurface()
    {
        DisposeEmbeddedTerminal();
        _browserSurface.Visible = true;
        _browserSurface.BringToFront();
    }

    private void DisposeEmbeddedTerminal()
    {
        var terminal = _embeddedTerminal;
        _embeddedTerminal = null;
        _embeddedTerminalConnectionId = null;
        if (terminal is null)
        {
            return;
        }

        _contentHost.Controls.Remove(terminal);
        terminal.Dispose();
    }

    private void ManageConnectionsClicked(object? sender, EventArgs e)
    {
        using var dialog = new ConnectionManagerForm();
        _ = dialog.ShowDialog(FindForm());
        _ = LoadRemoteConnectionsAsync(preserveCurrentSurface: !IsAnyConnectionsHomeSelected);
    }

    private sealed record RemoteTreeRoot(Guid ConnectionId);

    private sealed record CachedRemoteDirectory(string Name, string Path);

    private sealed record CachedRemoteListing(
        IReadOnlyList<CachedRemoteDirectory> Children,
        bool IsComplete,
        long UpdatedSequence);

    private sealed class RemoteDirectoryCache
    {
        internal Dictionary<string, CachedRemoteListing> Listings { get; } = new(StringComparer.Ordinal);

        internal long LastAccess { get; set; }

        internal void Update(
            string path,
            IReadOnlyList<CachedRemoteDirectory> children,
            bool isComplete,
            long sequence,
            int maximumListings,
            bool append)
        {
            LastAccess = sequence;
            if (append && Listings.TryGetValue(path, out var existing))
            {
                children = existing.Children.Concat(children)
                    .DistinctBy(static child => child.Path, StringComparer.Ordinal)
                    .ToArray();
            }
            Listings[path] = new CachedRemoteListing(children, isComplete, sequence);
            while (Listings.Count > maximumListings)
            {
                var oldest = Listings
                    .Where(pair => pair.Key.Length != 0 && !string.Equals(pair.Key, path, StringComparison.Ordinal))
                    .MinBy(static pair => pair.Value.UpdatedSequence);
                if (oldest.Key is null)
                {
                    break;
                }

                Listings.Remove(oldest.Key);
            }
        }

        internal void Invalidate(string path)
        {
            foreach (var cachedPath in Listings.Keys
                .Where(candidate => string.Equals(candidate, path, StringComparison.Ordinal) ||
                    candidate.StartsWith(path + "/", StringComparison.Ordinal))
                .ToArray())
            {
                Listings.Remove(cachedPath);
            }

            var parentPath = RemoteBrowserPath.GetParent(path);
            if (Listings.TryGetValue(parentPath, out var parent))
            {
                Listings[parentPath] = parent with
                {
                    Children = parent.Children
                        .Where(child => !string.Equals(child.Path, path, StringComparison.Ordinal))
                        .ToArray()
                };
            }
        }
    }
}

public sealed class ConnectionOpenedEventArgs(ConnectionSummary connection) : EventArgs
{
    public ConnectionSummary Connection { get; } = connection;
}

public sealed class PaneTransferDropRequestedEventArgs(
    BrowserPaneControl sourcePane,
    PaneSelectionSnapshot selection,
    TransferQueueOperation operation) : EventArgs
{
    public BrowserPaneControl SourcePane { get; } = sourcePane;

    public PaneSelectionSnapshot Selection { get; } = selection;

    public TransferQueueOperation Operation { get; } = operation;
}

public sealed class ShellImportDropRequestedEventArgs(string[] sourcePaths, TransferQueueAddress destination) : EventArgs
{
    public string[] SourcePaths { get; } = sourcePaths;
    public TransferQueueAddress Destination { get; } = destination;
}

public sealed class PaneSelectionStagedEventArgs(
    PaneSelectionSnapshot selection,
    TransferQueueOperation operation) : EventArgs
{
    public PaneSelectionSnapshot Selection { get; } = selection;

    public TransferQueueOperation Operation { get; } = operation;
}

internal sealed class PaneDragPayload(BrowserPaneControl sourcePane, PaneSelectionSnapshot selection)
{
    public BrowserPaneControl SourcePane { get; } = sourcePane;
    public PaneSelectionSnapshot Selection { get; } = selection;
    public bool InternalDropHandled { get; set; }
}

internal enum BrowserSortColumn
{
    Name,
    Size,
    Type,
    Modified,
    Status
}

public sealed record BrowserListItem(
    string Name,
    string Size,
    string Type,
    string Modified,
    string Status,
    string? Location = null,
    bool IsContainer = false,
    StorageItemKind Kind = StorageItemKind.Other,
    long? Length = null,
    string? NativeItemId = null,
    string? VersionId = null,
    string? EntityTag = null,
    DateTimeOffset? ModifiedUtc = null,
    bool IsParentNavigation = false);

public sealed class PaneTransferRequestedEventArgs : EventArgs
{
    public PaneTransferRequestedEventArgs(TransferQueueOperation operation)
    {
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }

        Operation = operation;
    }

    public TransferQueueOperation Operation { get; }
}
