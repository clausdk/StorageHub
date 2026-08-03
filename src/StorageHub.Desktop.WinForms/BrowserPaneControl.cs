using System.ComponentModel;
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
    private readonly List<Image> _ownedImages = [];
    private readonly ImageList _browserImages;
    private readonly bool _localBrowsingEnabled;
    private readonly LocalBrowserController? _localBrowser;
    private readonly RemoteBrowserController? _remoteBrowser;
    private readonly ComboBox _connectionSelector;
    private readonly ToolStrip _navigation;
    private readonly ToolStripButton _backButton;
    private readonly ToolStripButton _forwardButton;
    private readonly ToolStripButton _upButton;
    private readonly ToolStripButton _refreshButton;
    private readonly ToolStripButton _loadMoreButton;
    private readonly ToolStripTextBox _addressBox;
    private readonly ToolStripTextBox _filterBox;
    private readonly TreeView _directoryTree;
    private readonly ListView _fileList;
    private readonly TableLayoutPanel _loadingOverlay;
    private readonly ContextMenuStrip _fileContextMenu;
    private readonly Label _connectionState;
    private readonly Panel _accentBar;
    private readonly Label _errorBanner;
    private readonly Label _summary;
    private IReadOnlyList<BrowserListItem> _allItems = [];
    private IReadOnlyList<BrowserListItem> _items = [];
    private readonly Dictionary<Guid, RemoteDirectoryCache> _remoteDirectoryCaches = [];
    private BrowserSortColumn _sortColumn = BrowserSortColumn.Name;
    private bool _sortAscending = true;
    private long _remoteCacheSequence;
    private bool _initialLoadStarted;
    private bool _updatingConnectionChoices;
    private bool _updatingTreeSelection;
    private long _uiNavigationSequence;
    private Guid? _lastReportedConnectionId;

    public BrowserPaneControl(
        string title,
        bool showLocalDefault,
        RemoteBrowserController? remoteBrowser = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        _localBrowsingEnabled = showLocalDefault;
        _localBrowser = showLocalDefault ? new LocalBrowserController() : null;
        _remoteBrowser = remoteBrowser ?? new RemoteBrowserController();
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
            Height = 64,
            ColumnCount = 3,
            RowCount = 2,
            Padding = new Padding(8, 5, 8, 5),
            BackColor = StorageHubTheme.Surface
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        using var sectionFont = StorageHubTheme.CreateSectionFont();
        var titleLabel = new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(sectionFont, sectionFont.Style),
            ForeColor = StorageHubTheme.Text,
            AutoEllipsis = true,
            UseMnemonic = false,
            Margin = new Padding(4, 0, 8, 0),
            AccessibleName = $"{title} pane"
        };

        _connectionSelector = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 24,
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
        _connectionSelector.DrawItem += DrawConnectionItem;
        _connectionSelector.SelectedIndexChanged += ConnectionSelectionChanged;

        _connectionState = new Label
        {
            Text = showLocalDefault ? "● Ready" : "○ Choose a connection",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = showLocalDefault ? StorageHubTheme.Success : StorageHubTheme.TextMuted,
            Margin = new Padding(3, 0, 0, 0),
            AccessibleName = $"{title} connection state"
        };

        var manageButton = new Button
        {
            Text = "Manage…",
            AutoSize = true,
            AccessibleName = "Open Connection Manager",
            TabIndex = 1
        };
        StorageHubTheme.StyleSecondaryButton(manageButton);
        manageButton.Margin = new Padding(8, 2, 0, 2);
        manageButton.Click += ManageConnectionsClicked;

        header.Controls.Add(titleLabel, 0, 0);
        header.SetRowSpan(titleLabel, 2);
        header.Controls.Add(_connectionSelector, 1, 0);
        header.Controls.Add(_connectionState, 1, 1);
        header.Controls.Add(manageButton, 2, 0);
        header.SetRowSpan(manageButton, 2);

        _navigation = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            Renderer = StorageHubTheme.CreateToolStripRenderer(),
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
            AccessibleName = $"{title} filter",
            AccessibleDescription = "Filter the visible items by name. Wildcards star and question mark are supported.",
            ToolTipText = "Filter by name (* and ? wildcards supported)"
        };
        _filterBox.TextChanged += FilterTextChanged;
        _navigation.Items.Add(_filterBox);
        _navigation.Layout += NavigationLayoutChanged;

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
        UpdateSortColumnHeaders();
        _fileList.RetrieveVirtualItem += RetrieveVirtualItem;
        _fileList.ColumnClick += FileListColumnClick;
        _fileList.DoubleClick += FileListDoubleClick;
        _fileList.KeyDown += FileListKeyDown;
        _fileList.ItemDrag += FileListItemDrag;
        ConfigureDropTarget(_fileList);
        ConfigureDropTarget(_directoryTree);
        _fileContextMenu = new ContextMenuStrip
        {
            AccessibleName = $"{title} transfer commands",
            Renderer = StorageHubTheme.CreateToolStripRenderer()
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
            var hasSelection = _fileList.SelectedIndices.Count > 0;
            var singleSelection = _fileList.SelectedIndices.Count == 1;
            var selectedContainer = singleSelection &&
                (uint)_fileList.SelectedIndices[0] < (uint)_items.Count &&
                _items[_fileList.SelectedIndices[0]].IsContainer;
            open.Enabled = singleSelection;
            open.Text = selectedContainer ? "Open" : "Open in external editor...";
            edit.Enabled = singleSelection && !selectedContainer;
            copy.Enabled = hasSelection;
            cut.Enabled = hasSelection;
            paste.Enabled = CanPaste?.Invoke() == true;
            copyToOtherPane.Enabled = hasSelection;
            moveToOtherPane.Enabled = hasSelection;
            selectAll.Enabled = _fileList.VirtualListSize > 0;
            inspectObject.Enabled = singleSelection;
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
            BackColor = Color.FromArgb(255, 242, 222),
            ForeColor = StorageHubTheme.Warning,
            AccessibleName = $"{title} browser error",
            AccessibleRole = AccessibleRole.Alert
        };

        Controls.Add(browserSplit);
        Controls.Add(_summary);
        Controls.Add(_errorBanner);
        Controls.Add(_navigation);
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
    }

    /// <summary>Raised by pane copy/move hooks; the workspace owns resolving the opposite pane.</summary>
    public event EventHandler<PaneTransferRequestedEventArgs>? TransferRequested;

    public event EventHandler<PaneSelectionStagedEventArgs>? SelectionStaged;

    public event EventHandler? PasteRequested;

    public event EventHandler? EditRequested;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<bool>? CanPaste { get; set; }

    /// <summary>Raised when an immutable selection from another pane is dropped onto this pane.</summary>
    public event EventHandler<PaneTransferDropRequestedEventArgs>? TransferDropRequested;

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

    public StorageResult<PaneDestinationSnapshot> CaptureDestinationSnapshot()
    {
        var context = CaptureTransferContext();
        if (context.IsFailure)
        {
            return StorageResult<PaneDestinationSnapshot>.Fail(context.Error);
        }

        var items = new List<PaneTransferItem>(_allItems.Count);
        foreach (var item in _allItems)
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

    public void SetItems(IEnumerable<BrowserListItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _allItems = items as IReadOnlyList<BrowserListItem> ?? items.ToArray();
        SortItems();
        ApplyFilter();
    }

    /// <summary>Navigates the currently selected local or saved-connection surface backward.</summary>
    public void NavigateBack() => BackClicked(this, EventArgs.Empty);

    /// <summary>Navigates the currently selected local or saved-connection surface forward.</summary>
    public void NavigateForward() => ForwardClicked(this, EventArgs.Empty);

    /// <summary>Navigates to the parent of the currently selected location.</summary>
    public void NavigateUp() => UpClicked(this, EventArgs.Empty);

    /// <summary>Reloads the current location or the saved-connections home.</summary>
    public void Reload() => RefreshClicked(this, EventArgs.Empty);

    /// <summary>Selects every visible item in the pane without selecting filtered-out items.</summary>
    public void SelectAllVisibleItems()
    {
        _fileList.BeginUpdate();
        try
        {
            for (var index = 0; index < _fileList.VirtualListSize; index++)
            {
                _fileList.SelectedIndices.Add(index);
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
            _connectionSelector.DrawItem -= DrawConnectionItem;
            _connectionSelector.SelectedIndexChanged -= ConnectionSelectionChanged;
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
            _fileList.ColumnClick -= FileListColumnClick;
            _fileList.DoubleClick -= FileListDoubleClick;
            _fileList.KeyDown -= FileListKeyDown;
            _fileList.ItemDrag -= FileListItemDrag;
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
        e.Item = new ListViewItem([value.Name, value.Size, value.Type, value.Modified, value.Status])
        {
            ToolTipText = value.Name,
            ImageKey = GetItemImageKey(value)
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

    private void SortItems()
    {
        var direction = _sortAscending ? 1 : -1;
        _allItems = _allItems
            .OrderByDescending(static item => item.IsContainer)
            .ThenBy(item => item, Comparer<BrowserListItem>.Create((left, right) =>
            {
                var compared = _sortColumn switch
                {
                    BrowserSortColumn.Name => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name),
                    BrowserSortColumn.Size => Nullable.Compare(left.Length, right.Length),
                    BrowserSortColumn.Type => StringComparer.CurrentCultureIgnoreCase.Compare(left.Type, right.Type),
                    BrowserSortColumn.Modified => Nullable.Compare(left.ModifiedUtc, right.ModifiedUtc),
                    BrowserSortColumn.Status => StringComparer.CurrentCultureIgnoreCase.Compare(left.Status, right.Status),
                    _ => 0
                };
                compared = compared == 0
                    ? StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name)
                    : compared;
                return direction * compared;
            }))
            .ToArray();
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
                PresentSnapshot(result.Snapshot);
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

    private async Task LoadMoreRemoteAsync()
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
            result = await _remoteBrowser.LoadMoreAsync();
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

                PresentRemoteSnapshot(result.Snapshot);
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

    private void PresentRemoteSnapshot(RemoteBrowserSnapshot snapshot)
    {
        _addressBox.Text = snapshot.DisplayPath;
        SetItems(snapshot.Entries.Select(static entry => new BrowserListItem(
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
            entry.LastModifiedUtc)));
        _connectionState.Text = "● Ready";
        _connectionState.ForeColor = StorageHubTheme.Success;
        _connectionState.AccessibleDescription = $"Showing {snapshot.Connection.DisplayName} {snapshot.DisplayPath}";
        UpdateRemoteDirectoryTree(snapshot);
        UpdateSummaryText();
        if (_lastReportedConnectionId != snapshot.Connection.ConnectionId)
        {
            _lastReportedConnectionId = snapshot.Connection.ConnectionId;
            ConnectionOpened?.Invoke(this, new ConnectionOpenedEventArgs(snapshot.Connection));
        }
    }

    private void ReplaceRemoteConnectionChoices(IReadOnlyList<ConnectionSummary> connections)
    {
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
            foreach (var connection in connections)
            {
                _connectionSelector.Items.Add(new ConnectionCardModel(
                    connection.DisplayName,
                    MapProvider(connection.Provider),
                    connection.FolderPath ?? "Saved connection",
                    connection.IsEnabled ? "Ready to test" : "Disabled",
                    connection.IsFavorite,
                    connection.ConnectionId,
                    connection.IsEnabled,
                    connection.AccentColor));
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

    private void UpdateRemoteDirectoryTree(RemoteBrowserSnapshot snapshot)
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
            MaximumCachedDirectoriesPerConnection);
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
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown storage provider.")
    };

    private void PresentSnapshot(LocalBrowserSnapshot snapshot)
    {
        _addressBox.Text = snapshot.Location.DisplayText;
        SetItems(snapshot.Entries.Select(static entry => new BrowserListItem(
            entry.Name,
            entry.Length is null ? string.Empty : UiFormatting.FormatBytes(entry.Length.Value),
            entry.Type,
            LocalBrowserPresentation.FormatModified(entry.Modified),
            entry.Status,
            entry.FullPath,
            entry.IsContainer,
            entry.IsContainer ? StorageItemKind.Directory : StorageItemKind.File,
            entry.Length,
            ModifiedUtc: entry.Modified)));
        _connectionState.Text = "● Ready";
        _connectionState.ForeColor = StorageHubTheme.Success;
        _connectionState.AccessibleDescription = $"Showing {snapshot.Location.DisplayText}";
        UpdateDirectoryTree(snapshot);
    }

    private void ApplyFilter()
    {
        var filter = _filterBox.Text;
        _items = string.IsNullOrWhiteSpace(filter)
            ? _allItems
            : _allItems.Where(item => LocalBrowserPresentation.MatchesFilter(item.Name, filter)).ToArray();
        _fileList.VirtualListSize = _items.Count;
        _fileList.Invalidate();
        UpdateSummaryText();
    }

    private void UpdateSummaryText()
    {
        var filter = _filterBox.Text;
        var countText = _items.Count == _allItems.Count
            ? $"{_items.Count:N0} items"
            : $"{_items.Count:N0} of {_allItems.Count:N0} items";
        _summary.Text = IsRemoteSnapshotSelected && _remoteBrowser?.CurrentSnapshot?.HasMore == true
            ? countText + " | more available"
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
        _loadMoreButton.Visible = !_localBrowsingEnabled || remoteActive;
        _loadMoreButton.Enabled = remoteActive && _remoteBrowser!.CurrentSnapshot!.HasMore;
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

    private void LoadMoreClicked(object? sender, EventArgs e) => _ = LoadMoreRemoteAsync();

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

    private void FileListDoubleClick(object? sender, EventArgs e) => OpenOrEditSelected();

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

    private void FileListItemDrag(object? sender, ItemDragEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        var selection = CaptureSelectionSnapshot();
        if (selection.IsFailure)
        {
            return;
        }

        var data = new DataObject();
        data.SetData(PaneDragDataFormat, autoConvert: false, new PaneDragPayload(this, selection.Value));
        _ = _fileList.DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move);
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
            control.BackColor = Color.FromArgb(235, 242, 253);
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
        if (effect == DragDropEffects.None || TryGetPaneDragPayload(e.Data) is not { } payload)
        {
            return;
        }

        var operation = effect == DragDropEffects.Move
            ? TransferQueueOperation.Move
            : TransferQueueOperation.Copy;
        TransferDropRequested?.Invoke(
            this,
            new PaneTransferDropRequestedEventArgs(payload.SourcePane, payload.Selection, operation));
    }

    private DragDropEffects GetDropEffect(DragEventArgs e)
    {
        var payload = TryGetPaneDragPayload(e.Data);
        if (payload is null || ReferenceEquals(payload.SourcePane, this))
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
        if ((uint)index < (uint)_items.Count && _items[index].IsContainer)
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
        if (_fileList.SelectedIndices.Count == 1)
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
        if (_fileList.SelectedIndices.Count > 0)
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

    private void DrawConnectionItem(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index >= 0 && _connectionSelector.Items[e.Index] is ConnectionCardModel card)
        {
            var accent = StorageHubTheme.ParseAccent(card.AccentHex);
            using var brush = new SolidBrush(accent);
            e.Graphics.FillEllipse(brush, e.Bounds.Left + 6, e.Bounds.Top + (e.Bounds.Height - 9) / 2, 9, 9);
            var textColor = (e.State & DrawItemState.Selected) != 0 ? SystemColors.HighlightText : StorageHubTheme.Text;
            TextRenderer.DrawText(
                e.Graphics,
                card.Name,
                e.Font ?? Font,
                new Rectangle(e.Bounds.Left + 22, e.Bounds.Top, e.Bounds.Width - 24, e.Bounds.Height),
                textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        e.DrawFocusRectangle();
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
        if (IsLocalConnectionSelected)
        {
            _addressBox.Text = _localBrowser!.CurrentLocation.DisplayText;
            _ = NavigateLocalAsync(LocalBrowserNavigationKind.Refresh);
        }
        else if (IsAnyConnectionsHomeSelected)
        {
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
        var localActive = IsLocalConnectionSelected;
        var remoteActive = IsRemoteSnapshotSelected;
        _connectionState.Text = localActive || remoteActive ? "● Ready" : "○ Choose a saved connection";
        _connectionState.ForeColor = localActive || remoteActive
            ? StorageHubTheme.Success
            : StorageHubTheme.TextMuted;
        _connectionState.AccessibleDescription = card.State;
        _addressBox.Text = localActive
            ? _localBrowser!.CurrentLocation.DisplayText
            : remoteActive
                ? _remoteBrowser!.CurrentSnapshot!.DisplayPath
                : "Connections";
        UpdateNavigationButtons();
    }

    private void ManageConnectionsClicked(object? sender, EventArgs e)
    {
        using var dialog = new ConnectionManagerForm();
        _ = dialog.ShowDialog(FindForm());
        _ = LoadRemoteConnectionsAsync(preserveCurrentSurface: !IsAnyConnectionsHomeSelected);
    }

    private enum BrowserSortColumn
    {
        Name,
        Size,
        Type,
        Modified,
        Status
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
            int maximumListings)
        {
            LastAccess = sequence;
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

public sealed class PaneSelectionStagedEventArgs(
    PaneSelectionSnapshot selection,
    TransferQueueOperation operation) : EventArgs
{
    public PaneSelectionSnapshot Selection { get; } = selection;

    public TransferQueueOperation Operation { get; } = operation;
}

internal sealed record PaneDragPayload(BrowserPaneControl SourcePane, PaneSelectionSnapshot Selection);

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
    DateTimeOffset? ModifiedUtc = null);

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
