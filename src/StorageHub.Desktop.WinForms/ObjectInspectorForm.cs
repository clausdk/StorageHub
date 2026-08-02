using System.Globalization;
using Krypton.Toolkit;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

/// <summary>A standalone modern, read-only view over advanced exact-object details.</summary>
public sealed class ObjectInspectorForm : KryptonForm
{
    private readonly ObjectInspectorController _controller;
    private readonly bool _ownsController;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DataGridView _versionsGrid;
    private readonly DataGridView _metadataGrid;
    private readonly DataGridView _tagsGrid;
    private readonly TabPage _versionsTab;
    private readonly TabPage _metadataTab;
    private readonly TabPage _tagsTab;
    private readonly Label _versionsNotice;
    private readonly Label _metadataNotice;
    private readonly Label _tagsNotice;
    private readonly Label _status;
    private readonly ToolStripMenuItem _loadMoreMenu;
    private readonly Button _loadMoreButton;
    private bool _initialLoadStarted;
    private bool _disposed;

    public ObjectInspectorForm(ObjectInspectorAddress address)
        : this(
            new ObjectInspectorController(
                new NamedPipeObjectInspectorAgentClient(),
                address,
                ownsClient: true),
            ownsController: true)
    {
    }

    public ObjectInspectorForm(
        IObjectInspectorAgentClient client,
        ObjectInspectorAddress address,
        bool ownsClient = false)
        : this(
            new ObjectInspectorController(client, address, ownsClient),
            ownsController: true)
    {
    }

    public ObjectInspectorForm(
        ObjectInspectorController controller,
        bool ownsController = false)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _ownsController = ownsController;
        Text = CreateWindowTitle(controller.State.Address.RelativePath);
        AccessibleName = "Storage object inspector";
        AccessibleDescription =
            "Read-only object versions, portable metadata, and tags from the background agent.";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(860, 560);
        Size = new Size(1120, 720);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = StorageHubTheme.Canvas;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        var menu = new MenuStrip
        {
            Dock = DockStyle.Top,
            BackColor = StorageHubTheme.Surface,
            Renderer = StorageHubTheme.CreateToolStripRenderer(),
            AccessibleName = "Object inspector commands"
        };
        var objectMenu = new ToolStripMenuItem("&Object");
        var refreshMenu = new ToolStripMenuItem("&Refresh", null, RefreshClicked)
        {
            ShortcutKeys = Keys.F5
        };
        _loadMoreMenu = new ToolStripMenuItem("Load &more versions", null, LoadMoreClicked)
        {
            Enabled = false
        };
        var closeMenu = new ToolStripMenuItem("&Close", null, (_, _) => Close())
        {
            ShortcutKeys = Keys.Control | Keys.W
        };
        objectMenu.DropDownItems.Add(refreshMenu);
        objectMenu.DropDownItems.Add(_loadMoreMenu);
        objectMenu.DropDownItems.Add(new ToolStripSeparator());
        objectMenu.DropDownItems.Add(closeMenu);
        var viewMenu = new ToolStripMenuItem("&View");
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("&Versions", null, (_, _) => SelectTab(0)));
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("&Metadata", null, (_, _) => SelectTab(1)));
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("&Tags", null, (_, _) => SelectTab(2)));
        menu.Items.Add(objectMenu);
        menu.Items.Add(viewMenu);
        MainMenuStrip = menu;

        var heading = BuildHeading(controller.State.Address);

        _versionsGrid = CreateGrid("Object versions");
        _versionsGrid.Columns.Add("Current", "Current");
        _versionsGrid.Columns.Add("Version", "Version ID");
        _versionsGrid.Columns.Add("Size", "Size");
        _versionsGrid.Columns.Add("Modified", "Modified (UTC)");
        _versionsGrid.Columns.Add("DeleteMarker", "Delete marker");
        _versionsGrid.Columns.Add("EntityTag", "Entity tag");
        _versionsGrid.Columns[0].FillWeight = 35;
        _versionsGrid.Columns[1].FillWeight = 150;
        _versionsGrid.Columns[2].FillWeight = 55;
        _versionsGrid.Columns[3].FillWeight = 90;
        _versionsGrid.Columns[4].FillWeight = 55;
        _versionsGrid.Columns[5].FillWeight = 120;

        _metadataGrid = CreateGrid("Object metadata");
        _metadataGrid.Columns.Add("Name", "Name");
        _metadataGrid.Columns.Add("Value", "Value");
        _metadataGrid.Columns[0].FillWeight = 35;
        _metadataGrid.Columns[1].FillWeight = 65;

        _tagsGrid = CreateGrid("Object tags");
        _tagsGrid.Columns.Add("Name", "Name");
        _tagsGrid.Columns.Add("Value", "Value");
        _tagsGrid.Columns[0].FillWeight = 40;
        _tagsGrid.Columns[1].FillWeight = 60;

        _versionsNotice = CreateNotice("Version history has not been loaded.");
        _metadataNotice = CreateNotice("Metadata has not been loaded.");
        _tagsNotice = CreateNotice("Tags have not been loaded.");
        _loadMoreButton = new Button
        {
            Name = "LoadMoreObjectVersions",
            Text = "Load more versions",
            AutoSize = true,
            Enabled = false,
            AccessibleName = "Load the next object version page"
        };
        StorageHubTheme.StyleSecondaryButton(_loadMoreButton);
        _loadMoreButton.Click += LoadMoreClicked;

        _versionsTab = new TabPage("Versions")
        {
            BackColor = StorageHubTheme.Surface,
            Padding = new Padding(10)
        };
        var versionFooter = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(5),
            BackColor = StorageHubTheme.Surface
        };
        versionFooter.Controls.Add(_loadMoreButton);
        _versionsTab.Controls.Add(_versionsGrid);
        _versionsTab.Controls.Add(versionFooter);
        _versionsTab.Controls.Add(_versionsNotice);

        _metadataTab = CreateDataTab("Metadata", _metadataGrid, _metadataNotice);
        _tagsTab = CreateDataTab("Tags", _tagsGrid, _tagsNotice);
        var tabs = new TabControl
        {
            Name = "ObjectInspectorTabs",
            Dock = DockStyle.Fill,
            Padding = new Point(18, 6),
            AccessibleName = "Object detail categories"
        };
        tabs.TabPages.Add(_versionsTab);
        tabs.TabPages.Add(_metadataTab);
        tabs.TabPages.Add(_tagsTab);
        _tabs = tabs;

        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            Padding = new Padding(16, 13, 16, 8),
            BackColor = StorageHubTheme.Surface
        };
        _status = new Label
        {
            Name = "ObjectInspectorStatus",
            Dock = DockStyle.Fill,
            Text = "The inspector connects to the background agent when shown.",
            ForeColor = StorageHubTheme.TextMuted,
            AutoEllipsis = true,
            AccessibleName = "Object inspector status"
        };
        footer.Controls.Add(_status);

        Controls.Add(tabs);
        Controls.Add(footer);
        Controls.Add(heading);
        Controls.Add(menu);
        ApplyState(controller.State);
    }

    private readonly TabControl _tabs;

    public string StatusText => _status.Text;
    public int DisplayedVersionCount => _versionsGrid.Rows.Count;
    public int DisplayedMetadataCount => _metadataGrid.Rows.Count;
    public int DisplayedTagCount => _tagsGrid.Rows.Count;
    public bool CanLoadMoreVersions => _loadMoreButton.Enabled;

    public async Task LoadInspectorAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        SetBusy("Loading version history, metadata, and tags…");
        var state = await _controller.RefreshAsync(linked.Token).ConfigureAwait(true);
        ApplyState(state);
    }

    public async Task LoadMoreVersionsAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        SetBusy("Loading the next version page…");
        var state = await _controller.LoadMoreVersionsAsync(linked.Token).ConfigureAwait(true);
        ApplyState(state);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_initialLoadStarted)
        {
            return;
        }

        _initialLoadStarted = true;
        try
        {
            await LoadInspectorAsync(_lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Closing the window cancels and retires any in-flight pipe exchange.
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            ShowFailure(error);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
            if (_ownsController)
            {
                _controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        base.Dispose(disposing);
    }

    private static Panel BuildHeading(ObjectInspectorAddress address)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 104,
            Padding = new Padding(18, 14, 18, 10),
            BackColor = StorageHubTheme.Surface
        };
        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 30,
            Text = address.RelativePath,
            Font = new Font("Segoe UI Semibold", 14F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = StorageHubTheme.Text,
            AutoEllipsis = true,
            AccessibleName = "Inspected object path"
        };
        var identity = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            Text = $"Connection {address.ConnectionId:D}",
            ForeColor = StorageHubTheme.TextMuted,
            AutoEllipsis = true
        };
        var safety = new Label
        {
            Dock = DockStyle.Fill,
            Text = "READ ONLY  •  Version pages, metadata, and tags only — no signed links or mutation commands.",
            ForeColor = StorageHubTheme.Success,
            AccessibleName = "Read-only inspector safety notice"
        };
        panel.Controls.Add(safety);
        panel.Controls.Add(identity);
        panel.Controls.Add(title);
        return panel;
    }

    private static string CreateWindowTitle(string relativePath)
    {
        const int maximumTitlePathLength = 96;
        var titlePath = relativePath;
        if (titlePath.Length > maximumTitlePathLength)
        {
            titlePath = titlePath[^maximumTitlePathLength..];
            if (char.IsLowSurrogate(titlePath[0]))
            {
                titlePath = titlePath[1..];
            }

            titlePath = "…" + titlePath;
        }

        return $"Object Inspector — {titlePath}";
    }

    private static DataGridView CreateGrid(string accessibleName)
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
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            BackgroundColor = StorageHubTheme.Surface,
            BorderStyle = BorderStyle.None,
            GridColor = StorageHubTheme.Border,
            ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText,
            AccessibleName = accessibleName
        };
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = StorageHubTheme.SurfaceMuted;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = StorageHubTheme.Text;
        grid.DefaultCellStyle.ForeColor = StorageHubTheme.Text;
        grid.DefaultCellStyle.BackColor = StorageHubTheme.Surface;
        grid.DefaultCellStyle.SelectionBackColor = StorageHubTheme.Primary;
        return grid;
    }

    private static Label CreateNotice(string text) => new()
    {
        Dock = DockStyle.Top,
        Height = 34,
        Padding = new Padding(5, 7, 5, 5),
        Text = text,
        ForeColor = StorageHubTheme.TextMuted,
        BackColor = StorageHubTheme.Surface,
        AutoEllipsis = true
    };

    private static TabPage CreateDataTab(string title, DataGridView grid, Label notice)
    {
        var tab = new TabPage(title)
        {
            BackColor = StorageHubTheme.Surface,
            Padding = new Padding(10)
        };
        tab.Controls.Add(grid);
        tab.Controls.Add(notice);
        return tab;
    }

    private void ApplyState(ObjectInspectorState state)
    {
        _versionsGrid.Rows.Clear();
        foreach (var version in state.Versions)
        {
            _versionsGrid.Rows.Add(
                version.IsLatest ? "Latest" : string.Empty,
                version.VersionId,
                version.Size?.ToString("N0", CultureInfo.CurrentCulture) ?? "—",
                version.LastModifiedUtc?.ToString(
                    "yyyy-MM-dd HH:mm:ss 'UTC'",
                    CultureInfo.InvariantCulture) ?? "—",
                version.IsDeleteMarker ? "Yes" : "No",
                version.EntityTag ?? "—");
        }

        _metadataGrid.Rows.Clear();
        foreach (var entry in state.Metadata)
        {
            _metadataGrid.Rows.Add(entry.Name, entry.Value);
        }

        _tagsGrid.Rows.Clear();
        foreach (var entry in state.Tags)
        {
            _tagsGrid.Rows.Add(entry.Name, entry.Value);
        }

        _versionsTab.Text = $"Versions ({state.Versions.Count})";
        _metadataTab.Text = $"Metadata ({state.Metadata.Count})";
        _tagsTab.Text = $"Tags ({state.Tags.Count})";
        SetNotice(_versionsNotice, state.VersionsFailure, state.Versions.Count, "version");
        SetNotice(_metadataNotice, state.MetadataFailure, state.Metadata.Count, "metadata field");
        SetNotice(_tagsNotice, state.TagsFailure, state.Tags.Count, "tag");
        _loadMoreButton.Enabled = state.CanLoadMoreVersions;
        _loadMoreMenu.Enabled = state.CanLoadMoreVersions;

        var failures = new[]
        {
            state.VersionsFailure,
            state.MetadataFailure,
            state.TagsFailure
        }.Count(static failure => failure is not null);
        _status.Text = failures == 0
            ? $"Loaded {state.Versions.Count} version(s), {state.Metadata.Count} metadata field(s), and {state.Tags.Count} tag(s)."
            : $"Object loaded with {failures} unavailable detail section(s).";
        _status.ForeColor = failures == 0 ? StorageHubTheme.Success : StorageHubTheme.Warning;
    }

    private static void SetNotice(
        Label label,
        StorageIpcFailure? failure,
        int count,
        string itemName)
    {
        label.Text = failure?.Message ??
            (count == 0 ? $"No {itemName}s were returned." : $"Loaded {count} {itemName}(s).");
        label.ForeColor = failure is null ? StorageHubTheme.TextMuted : StorageHubTheme.Warning;
    }

    private void SetBusy(string message)
    {
        _status.Text = message;
        _status.ForeColor = StorageHubTheme.TextMuted;
        _loadMoreButton.Enabled = false;
        _loadMoreMenu.Enabled = false;
    }

    private void ShowFailure(Exception error)
    {
        _status.Text = error switch
        {
            UnauthorizedAccessException =>
                "StorageHub could not authenticate to the local background agent.",
            TimeoutException => "The object inspector request timed out.",
            _ => "The object inspector could not load details from the background agent."
        };
        _status.ForeColor = StorageHubTheme.Warning;
    }

    private void SelectTab(int index) => _tabs.SelectedIndex = index;

    private async void RefreshClicked(object? sender, EventArgs e)
    {
        try
        {
            await LoadInspectorAsync(_lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            ShowFailure(error);
        }
    }

    private async void LoadMoreClicked(object? sender, EventArgs e)
    {
        try
        {
            await LoadMoreVersionsAsync(_lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            ShowFailure(error);
        }
    }
}
