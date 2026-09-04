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
            BackColor = StorageHubTheme.SurfaceMuted,
            AccessibleName = $"{name} workspace layout"
        };
        toolbar.Items.Add(new ToolStripLabel("Drag pane headers to swap or dock panes"));
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(new ToolStripLabel("Empty") { Name = "WorkspaceClipboardStatus" });
        toolbar.Items.Add(new ToolStripButton("Paste to active pane") { Name = "WorkspaceClipboardPaste", Enabled = false });
        toolbar.Items.Add(new ToolStripButton("Clear") { Name = "WorkspaceClipboardClear", Enabled = false });

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

    private void ActivatePane(Guid id)
    {
        if (_activePaneId == id) return;
        _activePaneId = id;
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
        var frame = new Panel { Dock = DockStyle.Fill, AllowDrop = true, BackColor = StorageHubTheme.Border, Tag = paneId };
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
        header.MouseDown += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
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
        frame.Paint += (_, args) => PaintDockingCue(frame, paneId, args.Graphics);
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
            }
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
    internal NewWorkspaceForm(WorkspaceLayout layout)
    {
        Text = "New Workspace";
        AccessibleName = "New workspace pane chooser";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(620, 190);
        var choices = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Padding = new Padding(14) };
        for (var count = 1; count <= 4; count++)
        {
            choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            var captured = count;
            var button = new Button
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(7),
                Text = $"{count} pane{(count == 1 ? string.Empty : "s")}\n{Describe(count, layout)}",
                AccessibleName = $"Create workspace with {count} panes"
            };
            button.Click += (_, _) => { PaneCount = captured; DialogResult = DialogResult.OK; Close(); };
            choices.Controls.Add(button, count - 1, 0);
        }
        Controls.Add(choices);
    }

    internal int PaneCount { get; private set; }

    private static string Describe(int count, WorkspaceLayout layout) => count switch
    {
        1 => "Single",
        2 => layout == WorkspaceLayout.SideBySide ? "Side by side" : "Top / bottom",
        3 => "Large + stacked",
        _ => "2 × 2 grid"
    };
}
