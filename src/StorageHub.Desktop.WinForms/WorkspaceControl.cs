namespace StorageHub.Desktop;

public sealed class WorkspaceControl : UserControl
{
    private const string PaneHeaderDragFormat = "StorageHub.WorkspacePane.v1";
    private const int MinimumPaneWidth = 280;
    private const int MinimumPaneHeight = 180;
    private readonly Panel _layoutHost;
    private readonly Dictionary<Guid, BrowserPaneControl> _panes = [];
    private readonly Action<BrowserPaneControl>? _configurePane;
    private bool _hydrating;
    private Guid _activePaneId;
    private string _workspaceName;
    private Guid? _dropTargetPaneId;
    private WorkspaceDockEdge? _dropTargetEdge;

    public WorkspaceControl(
        string name,
        WorkspaceLayoutModel layout,
        Action<BrowserPaneControl>? configurePane = null,
        IReadOnlyDictionary<Guid, BrowserPaneState>? initialStates = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _workspaceName = name.Trim();
        _hydrating = initialStates is not null;
        LayoutModel = layout ?? throw new ArgumentNullException(nameof(layout));
        _configurePane = configurePane;
        Dock = DockStyle.Fill;
        AccessibleName = $"{name} workspace";
        AccessibleDescription = "A resizable workspace containing one to four equal-capability panes.";

        var toolbar = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            ImageScalingSize = new Size(16, 16),
            BackColor = StorageHubTheme.SurfaceMuted,
            AccessibleName = $"{name} workspace layout"
        };
        var hint = new ToolStripLabel("Drag pane headers to swap or dock panes")
        {
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            ForeColor = StorageHubTheme.TextMuted
        };
        _ = StorageHubTheme.TrackIcon(hint, UiGlyph.Layers, 16, UiIconTone.Muted);
        var clipboardStatus = new ToolStripLabel("Empty")
        {
            Name = "WorkspaceClipboardStatus",
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            ToolTipText = "StorageHub's staged file selection"
        };
        _ = StorageHubTheme.TrackIcon(clipboardStatus, UiGlyph.Copy, 16, UiIconTone.Muted);
        var paste = new ToolStripButton("Paste to active pane")
        {
            Name = "WorkspaceClipboardPaste",
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            Enabled = false
        };
        _ = StorageHubTheme.TrackIcon(paste, UiGlyph.Paste, 16);
        var clear = new ToolStripButton("Clear")
        {
            Name = "WorkspaceClipboardClear",
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            Enabled = false
        };
        _ = StorageHubTheme.TrackIcon(clear, UiGlyph.Close, 16);
        toolbar.Items.Add(hint);
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(clipboardStatus);
        toolbar.Items.Add(paste);
        toolbar.Items.Add(clear);

        _layoutHost = new Panel { Dock = DockStyle.Fill, BackColor = StorageHubTheme.Border };
        Controls.Add(_layoutHost);
        Controls.Add(toolbar);

        var order = layout.PaneIds;
        for (var index = 0; index < order.Count; index++)
        {
            var state = initialStates?.GetValueOrDefault(order[index]);
            CreatePane(order[index], state?.ContentKind == PaneContentKind.ThisPc || state is null && index == 0);
        }
        _activePaneId = order[0];
        RebuildLayout(markDirty: false);
    }

    public event EventHandler? WorkspaceChanged;
    public event EventHandler? ActivePaneChanged;
    public event EventHandler<WorkspacePaneEventArgs>? PaneCreated;

    public WorkspaceLayoutModel LayoutModel { get; }
    public string? FilePath { get; private set; }
    public bool IsDirty { get; private set; } = true;
    public Guid ActivePaneId => _activePaneId;
    public IReadOnlyList<BrowserPaneControl> Panes => LayoutModel.PaneIds.Select(id => _panes[id]).ToArray();
    public BrowserPaneControl? ActivePane => _panes.GetValueOrDefault(_activePaneId);

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string WorkspaceName
    {
        get => _workspaceName;
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            value = value.Trim();
            if (string.Equals(value, _workspaceName, StringComparison.Ordinal)) return;
            _workspaceName = value;
            AccessibleName = $"{value} workspace";
            MarkDirty();
        }
    }

    public void SetClipboardPresentation(string text, bool enabled)
    {
        foreach (var toolbar in Controls.OfType<ToolStrip>())
        {
            if (toolbar.Items["WorkspaceClipboardStatus"] is ToolStripLabel status) status.Text = text;
            if (toolbar.Items["WorkspaceClipboardPaste"] is ToolStripButton paste) paste.Enabled = enabled;
            if (toolbar.Items["WorkspaceClipboardClear"] is ToolStripButton clear) clear.Enabled = enabled;
        }
        foreach (var pane in _panes.Values) pane.RefreshCommandState();
    }

    public bool SplitPane(Guid paneId, WorkspaceDockEdge edge)
    {
        if (!CanSplit(paneId, edge)) return false;
        var id = Guid.NewGuid();
        if (!LayoutModel.Split(paneId, edge, id)) return false;
        CreatePane(id, showLocalDefault: false);
        _activePaneId = id;
        RebuildLayout();
        return true;
    }

    public bool ClosePane(Guid paneId)
    {
        if (!LayoutModel.Close(paneId)) return false;
        var pane = _panes[paneId];
        _panes.Remove(paneId);
        pane.Dispose();
        if (_activePaneId == paneId) _activePaneId = LayoutModel.PaneIds[0];
        RebuildLayout();
        return true;
    }

    public bool SwapPanes(Guid first, Guid second)
    {
        if (!LayoutModel.Swap(first, second)) return false;
        RebuildLayout();
        return true;
    }

    public bool MovePane(Guid moving, Guid target, WorkspaceDockEdge edge)
    {
        if (!CanDock(target, edge) || !LayoutModel.MoveBeside(moving, target, edge)) return false;
        RebuildLayout();
        return true;
    }

    internal WorkspaceFileDocument CaptureDocument() => WorkspaceFileStore.Capture(
        WorkspaceName,
        _activePaneId,
        LayoutModel.Root,
        _panes.ToDictionary(pair => pair.Key, pair => pair.Value.CaptureState()));

    public void Save(string path)
    {
        WorkspaceFileStore.Save(path, CaptureDocument());
        FilePath = Path.GetFullPath(Path.ChangeExtension(path, ".shw"));
        IsDirty = false;
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task HydrateAsync(
        IReadOnlyDictionary<Guid, BrowserPaneState> states,
        Guid activePaneId,
        bool reconnectRemote,
        CancellationToken cancellationToken = default)
    {
        _hydrating = true;
        try
        {
            foreach (var paneId in LayoutModel.PaneIds)
                await _panes[paneId].RestoreStateAsync(states[paneId], reconnectRemote, cancellationToken).ConfigureAwait(true);
            _activePaneId = activePaneId;
            RefreshActivePanePresentation();
            ActivePaneChanged?.Invoke(this, EventArgs.Empty);
            IsDirty = false;
        }
        finally { _hydrating = false; }
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AssociateFile(string path)
    {
        FilePath = Path.GetFullPath(path);
        IsDirty = false;
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    private BrowserPaneControl CreatePane(Guid id, bool showLocalDefault)
    {
        var pane = new BrowserPaneControl("Pane", showLocalDefault);
        pane.Enter += (_, _) => ActivatePane(id);
        pane.StateChanged += (_, _) => MarkDirty();
        _configurePane?.Invoke(pane);
        _panes.Add(id, pane);
        PaneCreated?.Invoke(this, new WorkspacePaneEventArgs(id, pane));
        return pane;
    }

    internal void ActivatePane(Guid id)
    {
        if (!_panes.ContainsKey(id)) return;
        if (_activePaneId == id) return;
        _activePaneId = id;
        RefreshActivePanePresentation();
        ActivePaneChanged?.Invoke(this, EventArgs.Empty);
        MarkDirty();
    }

    private void RebuildLayout(bool markDirty = true)
    {
        foreach (var pane in _panes.Values) pane.Parent?.Controls.Remove(pane);
        if (_layoutHost.Controls.Count > 0)
        {
            var old = _layoutHost.Controls[0];
            _layoutHost.Controls.Clear();
            old.Dispose();
        }
        _layoutHost.Controls.Add(BuildNode(LayoutModel.Root));
        RenumberHeaders();
        RefreshActivePanePresentation();
        ActivePaneChanged?.Invoke(this, EventArgs.Empty);
        if (markDirty) MarkDirty();
    }

    private Control BuildNode(WorkspaceLayoutNode node)
    {
        if (node is WorkspacePaneLeaf leaf) return BuildPaneFrame(leaf.PaneId);
        var model = (WorkspaceSplitNode)node;
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = model.Orientation == WorkspaceSplitOrientation.Vertical ? Orientation.Vertical : Orientation.Horizontal,
            BackColor = StorageHubTheme.Border,
            SplitterWidth = 6,
            AccessibleName = "Resizable workspace pane split"
        };
        split.Panel1.Controls.Add(BuildNode(model.First));
        split.Panel2.Controls.Add(BuildNode(model.Second));
        split.Layout += (_, _) => ApplyRatio(split, model.Ratio);
        split.SplitterMoved += (_, _) =>
        {
            var available = split.Orientation == Orientation.Vertical ? split.ClientSize.Width : split.ClientSize.Height;
            if (available > split.SplitterWidth)
            {
                LayoutModel.SetRatio(model, (double)split.SplitterDistance / (available - split.SplitterWidth));
                MarkDirty();
            }
        };
        return split;
    }

    private Panel BuildPaneFrame(Guid paneId)
    {
        var frame = new Panel { Dock = DockStyle.Fill, AllowDrop = true, BackColor = StorageHubTheme.Border, Tag = paneId,
            Padding = new Padding(Math.Max(2, (int)Math.Round(3 * DeviceDpi / 96F))) };
        var header = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            BackColor = StorageHubTheme.SurfaceMuted,
            AccessibleName = "Pane header",
            Tag = paneId
        };
        var title = new ToolStripLabel("Pane") { Name = "PaneTitle", Font = new Font(Font, FontStyle.Bold) };
        var actions = new ToolStripDropDownButton("Pane actions") { Alignment = ToolStripItemAlignment.Right };
        AddAction(actions, "Split Right", () => SplitPane(paneId, WorkspaceDockEdge.Right), LayoutModel.PaneCount < 4);
        AddAction(actions, "Split Below", () => SplitPane(paneId, WorkspaceDockEdge.Bottom), LayoutModel.PaneCount < 4);
        AddAction(actions, "Close Pane", () => ClosePane(paneId), LayoutModel.PaneCount > 1);
        var move = new ToolStripMenuItem("Move/Swap Pane");
        foreach (var target in LayoutModel.PaneIds.Where(id => id != paneId))
        {
            var targetNumber = LayoutModel.PaneIds.ToList().IndexOf(target) + 1;
            var targetMenu = new ToolStripMenuItem($"Pane {targetNumber}");
            targetMenu.DropDownItems.Add("Swap", null, (_, _) => SwapPanes(paneId, target));
            foreach (var edge in Enum.GetValues<WorkspaceDockEdge>())
                targetMenu.DropDownItems.Add($"Move {edge}", null, (_, _) => MovePane(paneId, target, edge));
            move.DropDownItems.Add(targetMenu);
        }
        actions.DropDownItems.Add(move);
        header.Items.Add(title);
        header.Items.Add(actions);
        header.ItemClicked += (_, _) => ActivatePane(paneId);
        frame.MouseDown += (_, _) => ActivatePane(paneId);
        Point? dragStart = null;
        header.MouseDown += (_, args) =>
        {
            ActivatePane(paneId);
            dragStart = args.Button == MouseButtons.Left && header.GetItemAt(args.Location) is not ToolStripDropDownItem
                ? args.Location : null;
        };
        header.MouseUp += (_, _) => dragStart = null;
        header.MouseMove += (_, args) =>
        {
            if (args.Button == MouseButtons.Left && dragStart is { } start)
            {
                var threshold = new Rectangle(start.X - SystemInformation.DragSize.Width / 2,
                    start.Y - SystemInformation.DragSize.Height / 2, SystemInformation.DragSize.Width, SystemInformation.DragSize.Height);
                if (threshold.Contains(args.Location)) return;
                dragStart = null;
                var data = new DataObject();
                data.SetData(PaneHeaderDragFormat, paneId.ToString("D"));
                header.DoDragDrop(data, DragDropEffects.Move);
            }
        };
        frame.DragEnter += (_, args) =>
        {
            args.Effect = TryReadDraggedPane(args.Data, out var moving) && moving != paneId ? DragDropEffects.Move : DragDropEffects.None;
            if (args.Effect != DragDropEffects.None)
            {
                _dropTargetPaneId = paneId;
                _dropTargetEdge = null;
                frame.Invalidate();
            }
        };
        frame.DragOver += (_, args) =>
        {
            if (_dropTargetPaneId != paneId) return;
            _dropTargetEdge = HitEdge(frame.ClientRectangle, frame.PointToClient(new Point(args.X, args.Y)));
            if (header.Items["PaneTitle"] is ToolStripLabel cue)
                cue.Text = _dropTargetEdge is null ? "Drop to swap" : $"Drop to dock {_dropTargetEdge.ToString()!.ToLowerInvariant()}";
            frame.Invalidate();
        };
        frame.DragLeave += (_, _) => ClearDockingCue(frame);
        frame.DragDrop += (_, args) =>
        {
            ClearDockingCue(frame);
            if (!TryReadDraggedPane(args.Data, out var moving) || moving == paneId) return;
            var point = frame.PointToClient(new Point(args.X, args.Y));
            var edge = HitEdge(frame.ClientRectangle, point);
            if (edge is null) SwapPanes(moving, paneId); else MovePane(moving, paneId, edge.Value);
        };
        frame.Paint += (_, args) =>
        {
            var thickness = frame.Padding.Left;
            var color = paneId == _activePaneId ? StorageHubTheme.Primary : StorageHubTheme.Border;
            using var border = new SolidBrush(color);
            args.Graphics.FillRectangle(border, 0, 0, frame.Width, thickness);
            args.Graphics.FillRectangle(border, 0, frame.Height - thickness, frame.Width, thickness);
            args.Graphics.FillRectangle(border, 0, 0, thickness, frame.Height);
            args.Graphics.FillRectangle(border, frame.Width - thickness, 0, thickness, frame.Height);
            PaintDockingCue(frame, paneId, args.Graphics);
        };
        frame.Controls.Add(_panes[paneId]);
        frame.Controls.Add(header);
        return frame;
    }

    private static void AddAction(ToolStripDropDownButton menu, string text, Action action, bool enabled)
    {
        var item = new ToolStripMenuItem(text) { Enabled = enabled };
        item.Click += (_, _) => action();
        menu.DropDownItems.Add(item);
    }

    private void RenumberHeaders()
    {
        var order = LayoutModel.PaneIds;
        foreach (var header in Descendants<ToolStrip>(_layoutHost).Where(control => control.Tag is Guid))
            if (header.Tag is Guid id && header.Items["PaneTitle"] is ToolStripLabel title)
            {
                var label = $"Pane {order.ToList().IndexOf(id) + 1}";
                title.Text = label;
                header.AccessibleName = $"{label} header";
                _panes[id].AccessibleName = $"{label} browser pane";
                if (id == _activePaneId) title.Text += " (Active)";
            }
    }

    private void RefreshActivePanePresentation()
    {
        RenumberHeaders();
        foreach (var frame in Descendants<Panel>(_layoutHost).Where(panel => panel.Tag is Guid))
        {
            frame.AccessibleDescription = Equals(frame.Tag, _activePaneId) ? "Active pane" : "Inactive pane";
            frame.Invalidate();
        }
    }

    internal void FocusNextPane()
    {
        var ids = LayoutModel.PaneIds;
        var index = ids.ToList().IndexOf(_activePaneId);
        ActivatePane(ids[(index + 1) % ids.Count]);
        ActivePane?.FocusContent();
    }

    private static IEnumerable<T> Descendants<T>(Control root) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private bool CanSplit(Guid paneId, WorkspaceDockEdge edge)
    {
        if (LayoutModel.PaneCount >= WorkspaceLayoutModel.MaximumPanes || !_panes.TryGetValue(paneId, out var pane)) return false;
        return edge is WorkspaceDockEdge.Left or WorkspaceDockEdge.Right
            ? pane.Width >= MinimumPaneWidth * 2
            : pane.Height >= MinimumPaneHeight * 2;
    }

    private bool CanDock(Guid target, WorkspaceDockEdge edge)
    {
        if (!_panes.TryGetValue(target, out var pane)) return false;
        return edge is WorkspaceDockEdge.Left or WorkspaceDockEdge.Right
            ? pane.Width >= MinimumPaneWidth * 2
            : pane.Height >= MinimumPaneHeight * 2;
    }

    private static void ApplyRatio(SplitContainer split, double ratio)
    {
        var available = split.Orientation == Orientation.Vertical ? split.ClientSize.Width : split.ClientSize.Height;
        var minimum = split.Orientation == Orientation.Vertical ? MinimumPaneWidth : MinimumPaneHeight;
        if (available <= split.SplitterWidth + minimum * 2) return;
        var distance = (int)Math.Round((available - split.SplitterWidth) * ratio);
        distance = Math.Clamp(distance, minimum, available - split.SplitterWidth - minimum);
        if (split.SplitterDistance != distance) split.SplitterDistance = distance;
    }

    private static WorkspaceDockEdge? HitEdge(Rectangle bounds, Point point)
    {
        var x = (double)point.X / Math.Max(1, bounds.Width);
        var y = (double)point.Y / Math.Max(1, bounds.Height);
        const double edge = .25;
        if (x < edge) return WorkspaceDockEdge.Left;
        if (x > 1 - edge) return WorkspaceDockEdge.Right;
        if (y < edge) return WorkspaceDockEdge.Top;
        if (y > 1 - edge) return WorkspaceDockEdge.Bottom;
        return null;
    }

    private void ClearDockingCue(Control frame)
    {
        _dropTargetPaneId = null;
        _dropTargetEdge = null;
        frame.Invalidate();
        RenumberHeaders();
    }

    private void PaintDockingCue(Control frame, Guid paneId, Graphics graphics)
    {
        if (_dropTargetPaneId != paneId) return;
        var bounds = frame.ClientRectangle;
        bounds.Inflate(-12, -12);
        var target = _dropTargetEdge switch
        {
            WorkspaceDockEdge.Left => new Rectangle(bounds.Left, bounds.Top, bounds.Width / 3, bounds.Height),
            WorkspaceDockEdge.Right => new Rectangle(bounds.Right - bounds.Width / 3, bounds.Top, bounds.Width / 3, bounds.Height),
            WorkspaceDockEdge.Top => new Rectangle(bounds.Left, bounds.Top, bounds.Width, bounds.Height / 3),
            WorkspaceDockEdge.Bottom => new Rectangle(bounds.Left, bounds.Bottom - bounds.Height / 3, bounds.Width, bounds.Height / 3),
            _ => new Rectangle(bounds.Left + bounds.Width / 4, bounds.Top + bounds.Height / 4, bounds.Width / 2, bounds.Height / 2)
        };
        using var brush = new SolidBrush(Color.FromArgb(150, StorageHubTheme.Primary));
        graphics.FillRectangle(brush, target);
        TextRenderer.DrawText(graphics, _dropTargetEdge is null ? "Swap panes" : $"Dock {_dropTargetEdge.ToString()!.ToLowerInvariant()}",
            Font, target, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private static bool TryReadDraggedPane(IDataObject? data, out Guid paneId) =>
        Guid.TryParse(data?.GetData(PaneHeaderDragFormat) as string, out paneId);

    private void MarkDirty()
    {
        if (_hydrating) return;
        IsDirty = true;
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class WorkspacePaneEventArgs(Guid paneId, BrowserPaneControl pane) : EventArgs
{
    public Guid PaneId { get; } = paneId;
    public BrowserPaneControl Pane { get; } = pane;
}

internal sealed class NewWorkspaceForm : Form
{
    private const int Columns = 3;

    private readonly CheckBox _remember;
    private readonly List<Bitmap> _previews = [];

    internal NewWorkspaceForm(WorkspaceLayout layout)
    {
        Text = "New Workspace";
        AccessibleName = "New workspace layout chooser";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(660, 424);

        var rows = (WorkspacePreset.All.Count + Columns - 1) / Columns;
        var choices = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = Columns,
            RowCount = rows,
            Padding = new Padding(14)
        };
        for (var column = 0; column < Columns; column++)
        {
            choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / Columns));
        }

        for (var row = 0; row < rows; row++)
        {
            choices.RowStyles.Add(new RowStyle(SizeType.Percent, 100F / rows));
        }

        for (var index = 0; index < WorkspacePreset.All.Count; index++)
        {
            var preset = WorkspacePreset.All[index];
            var preview = WorkspacePreset.CreatePreview(preset, 104, 68, StorageHubTheme.Primary);
            _previews.Add(preview);
            var button = new Button
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(7),
                Text = $"{preset.Title}\n{preset.Description}",
                Image = preview,
                ImageAlign = ContentAlignment.TopCenter,
                TextAlign = ContentAlignment.BottomCenter,
                TextImageRelation = TextImageRelation.ImageAboveText,
                Padding = new Padding(0, 10, 0, 8),
                AccessibleName = $"Create workspace with {preset.Label}"
            };
            button.Click += (_, _) =>
            {
                PaneCount = preset.PaneCount;
                // An orientation the preset does not use is left as the caller's, so picking a
                // single pane never silently rewrites the stored default.
                PaneLayout = preset.OrientationMatters ? preset.Layout : layout;
                DialogResult = DialogResult.OK;
                Close();
            };
            choices.Controls.Add(button, index % Columns, index / Columns);
        }

        _remember = new CheckBox
        {
            Text = "Use this for new workspaces and stop asking",
            AutoSize = true,
            Dock = DockStyle.Bottom,
            Padding = new Padding(21, 0, 21, 14),
            AccessibleName = "Remember this layout",
            AccessibleDescription =
                "New workspaces use the arrangement you pick here. Change it later in Settings, under Workspace."
        };
        // Bottom-docked controls are laid out in reverse add order, so the checkbox goes in first
        // for the fill panel to take the space above it.
        Controls.Add(choices);
        Controls.Add(_remember);
        PaneLayout = layout;
    }

    internal int PaneCount { get; private set; }

    /// <summary>The orientation the chosen preset needs, which may differ from the stored default.</summary>
    internal WorkspaceLayout PaneLayout { get; private set; }

    /// <summary>Whether the chosen arrangement should become the stored default.</summary>
    internal bool RememberChoice => _remember.Checked;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var preview in _previews)
            {
                preview.Dispose();
            }

            _previews.Clear();
        }

        base.Dispose(disposing);
    }

}
