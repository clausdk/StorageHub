using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

/// <summary>A per-row affordance on a saved connection.</summary>
internal enum ConnectionRowAction
{
    Edit,
    Delete
}

internal sealed record ConnectionRowMenuEventArgs(ConnectionCardModel Connection, Point ScreenLocation);

internal sealed class ConnectionSidebarControl : UserControl
{
    private readonly FlowLayoutPanel _content;
    private readonly Dictionary<Guid, ConnectionSidebarItem> _items = [];
    private readonly HashSet<string> _collapsedGroups = new(StringComparer.OrdinalIgnoreCase);

    // Registered once for the whole list rather than per row. SetConnections rebuilds every row on
    // every refresh, so per-row tracking would pile up registrations that only a theme change
    // prunes. The theme owns these bitmaps; rows borrow them and must not dispose them.
    private Image _editIcon = null!;
    private Image _deleteIcon = null!;

    internal ConnectionSidebarControl()
    {
        Dock = DockStyle.Fill;
        BackColor = StorageHubTheme.Surface;
        AccessibleName = "Saved connection groups";
        _content = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(8, 4, 8, 12),
            BackColor = StorageHubTheme.Surface
        };
        _content.ClientSizeChanged += (_, _) => ResizeRows();
        Controls.Add(_content);
        _editIcon = StorageHubTheme.TrackIcon(
            this, image => ReplaceRowIcon(ref _editIcon, image), UiGlyph.Rename, 16, UiIconTone.Text, DeviceDpi / 96F);
        _deleteIcon = StorageHubTheme.TrackIcon(
            this, image => ReplaceRowIcon(ref _deleteIcon, image), UiGlyph.Delete, 16, UiIconTone.Danger, DeviceDpi / 96F);
    }

    internal event EventHandler<ConnectionCardModel>? ConnectionSelected;

    /// <summary>Raised on double click or Enter: the caller decides what "open" means.</summary>
    internal event EventHandler<ConnectionCardModel>? ConnectionActivated;

    internal event EventHandler<ConnectionCardModel>? ConnectionEditRequested;

    internal event EventHandler<ConnectionCardModel>? ConnectionDeleteRequested;

    /// <summary>Raised with the screen location to pop a row's context menu at.</summary>
    internal event EventHandler<ConnectionRowMenuEventArgs>? ConnectionMenuRequested;

    /// <summary>
    /// Whether rows carry inline edit and delete affordances. Off by default so the control keeps
    /// behaving as a plain picker wherever it is used only to choose a connection.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool ShowRowActions { get; set; }

    internal Guid? SelectedConnectionId { get; private set; }

    internal void SetConnections(
        IEnumerable<ConnectionCardModel> connections,
        string? searchText,
        Guid? selectedConnectionId)
    {
        ArgumentNullException.ThrowIfNull(connections);
        var query = searchText?.Trim() ?? string.Empty;
        var matching = connections
            .Where(card => ConnectionPickerFilter.Matches(card, query))
            .OrderBy(static card => card.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        _content.SuspendLayout();
        try
        {
            foreach (var control in _content.Controls.Cast<Control>().ToArray())
            {
                control.Dispose();
            }

            _content.Controls.Clear();
            _items.Clear();

            // A connection appears in exactly one section: the rows are indexed by id, so listing a
            // favourite twice would leave the first copy unreachable for selection.
            AddFlatSection("Favorites", "favorites", matching.Where(static card => card.IsEnabled && card.IsFavorite));
            AddSection("Storage", matching.Where(static card =>
                card.IsEnabled && !card.IsFavorite && card.Type == ConnectionProfileType.Storage));
            AddSection("Remote clients", matching.Where(static card =>
                card.IsEnabled && !card.IsFavorite && card.Type == ConnectionProfileType.Client));
            AddFlatSection("Disabled", "disabled", matching.Where(static card => !card.IsEnabled));
            if (_content.Controls.Count == 0)
            {
                _content.Controls.Add(new Label
                {
                    AutoSize = false,
                    Height = 64,
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = StorageHubTheme.TextMuted,
                    Text = connections.Any() ? "No connections match this search" : "No saved connections yet"
                });
            }

            SelectConnection(selectedConnectionId, raiseEvent: false);
            ResizeRows();
        }
        finally
        {
            _content.ResumeLayout(true);
        }
    }

    internal void ClearSelection() => SelectConnection(null, raiseEvent: false);

    /// <summary>
    /// A section whose membership is a state rather than a place, so it is listed flat: nesting
    /// favourites under their folders would bury the shortcut the favourite exists to provide.
    /// </summary>
    private void AddFlatSection(string title, string key, IEnumerable<ConnectionCardModel> connections)
    {
        var cards = connections.ToArray();
        if (cards.Length == 0)
        {
            return;
        }

        _content.Controls.Add(new ConnectionSidebarSectionHeader(title));
        var folder = new FolderBuilder(key, title);
        folder.Connections.AddRange(cards);
        _content.Controls.Add(CreateGroup(folder, depth: 0));
    }

    private void AddSection(string title, IEnumerable<ConnectionCardModel> connections)
    {
        var cards = connections.ToArray();
        if (cards.Length == 0)
        {
            return;
        }

        _content.Controls.Add(new ConnectionSidebarSectionHeader(title));
        var root = new FolderBuilder(string.Empty, string.Empty);
        foreach (var card in cards)
        {
            var segments = SplitFolder(card.FolderPath);
            if (segments.Length == 0)
            {
                segments = ["Unsorted"];
            }

            var folder = root;
            var key = card.Type == ConnectionProfileType.Client ? "clients" : "storage";
            foreach (var segment in segments)
            {
                key = $"{key}/{segment}";
                if (!folder.Children.TryGetValue(segment, out var child))
                {
                    child = new FolderBuilder(key, segment);
                    folder.Children.Add(segment, child);
                }

                folder = child;
            }

            folder.Connections.Add(card);
        }

        foreach (var folder in root.Children.Values.OrderBy(static folder =>
                     string.Equals(folder.Label, "Unsorted", StringComparison.OrdinalIgnoreCase) ? string.Empty : folder.Label,
                     StringComparer.CurrentCultureIgnoreCase))
        {
            _content.Controls.Add(CreateGroup(folder, depth: 0));
        }
    }

    private ConnectionSidebarGroup CreateGroup(FolderBuilder folder, int depth)
    {
        var group = new ConnectionSidebarGroup(
            folder.Key,
            folder.Label,
            folder.TotalConnections,
            depth,
            expanded: !_collapsedGroups.Contains(folder.Key));
        group.ExpandedChanged += (_, expanded) =>
        {
            if (expanded)
            {
                _collapsedGroups.Remove(folder.Key);
            }
            else
            {
                _collapsedGroups.Add(folder.Key);
            }
        };
        foreach (var child in folder.Children.Values.OrderBy(static value => value.Label, StringComparer.CurrentCultureIgnoreCase))
        {
            group.AddChild(CreateGroup(child, depth + 1));
        }

        foreach (var card in folder.Connections.OrderBy(static value => value.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var item = new ConnectionSidebarItem(card)
            {
                ShowActions = ShowRowActions,
                EditIcon = _editIcon,
                DeleteIcon = _deleteIcon
            };
            item.Click += (_, _) => SelectConnection(card.ConnectionId, raiseEvent: true);
            item.Activated += (_, _) =>
            {
                SelectConnection(card.ConnectionId, raiseEvent: true);
                ConnectionActivated?.Invoke(this, card);
            };
            item.ActionInvoked += (_, action) =>
            {
                SelectConnection(card.ConnectionId, raiseEvent: true);
                if (action == ConnectionRowAction.Edit)
                {
                    ConnectionEditRequested?.Invoke(this, card);
                }
                else
                {
                    ConnectionDeleteRequested?.Invoke(this, card);
                }
            };
            item.MenuRequested += (_, location) =>
            {
                SelectConnection(card.ConnectionId, raiseEvent: true);
                ConnectionMenuRequested?.Invoke(this, new ConnectionRowMenuEventArgs(card, location));
            };
            group.AddChild(item);
            if (card.ConnectionId is { } id)
            {
                _items[id] = item;
            }
        }

        return group;
    }

    private void SelectConnection(Guid? connectionId, bool raiseEvent)
    {
        if (SelectedConnectionId is { } previous && _items.TryGetValue(previous, out var previousItem))
        {
            previousItem.Selected = false;
        }

        SelectedConnectionId = connectionId;
        if (connectionId is not { } selected || !_items.TryGetValue(selected, out var item))
        {
            SelectedConnectionId = null;
            return;
        }

        item.Selected = true;
        if (raiseEvent)
        {
            ConnectionSelected?.Invoke(this, item.Connection);
        }
    }

    private void ResizeRows()
    {
        var width = Math.Max(120, _content.ClientSize.Width - _content.Padding.Horizontal -
            (_content.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0));
        foreach (Control control in _content.Controls)
        {
            control.Width = width;
        }
    }

    /// <summary>
    /// Re-points every live row at the repainted icon after an appearance change. The rows hold a
    /// borrowed reference, so they have to be told rather than left pointing at the stale bitmap.
    /// </summary>
    private void ReplaceRowIcon(ref Image field, Image image)
    {
        field = image;
        foreach (var item in _items.Values)
        {
            item.EditIcon = _editIcon;
            item.DeleteIcon = _deleteIcon;
            item.Invalidate();
        }
    }

    private static string[] SplitFolder(string? folder) => string.IsNullOrWhiteSpace(folder)
        ? []
        : folder.Split(['/', '\\'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private sealed class FolderBuilder(string key, string label)
    {
        internal string Key { get; } = key;
        internal string Label { get; } = label;
        internal Dictionary<string, FolderBuilder> Children { get; } = new(StringComparer.CurrentCultureIgnoreCase);
        internal List<ConnectionCardModel> Connections { get; } = [];
        internal int TotalConnections => Connections.Count + Children.Values.Sum(static child => child.TotalConnections);
    }
}

internal sealed class ConnectionSidebarSectionHeader : Control
{
    internal ConnectionSidebarSectionHeader(string title)
    {
        Text = title;
        Height = 38;
        Margin = new Padding(0, 8, 0, 2);
        Font = StorageHubTheme.CreateSectionFont();
        ForeColor = StorageHubTheme.Text;
        BackColor = StorageHubTheme.Surface;
        TabStop = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var textSize = TextRenderer.MeasureText(e.Graphics, Text, Font, Size.Empty, TextFormatFlags.NoPadding);
        var y = Height / 2;
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(5, 0, Width - 10, Height), ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        using var line = new Pen(StorageHubTheme.Border);
        e.Graphics.DrawLine(line, Math.Min(Width - 8, textSize.Width + 18), y, Width - 8, y);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Font.Dispose();
        }

        base.Dispose(disposing);
    }
}

internal sealed class ConnectionSidebarGroup : Panel
{
    private readonly FlowLayoutPanel _body;
    private readonly Button _header;
    private readonly string _label;
    private readonly int _count;
    private bool _expanded;
    private bool _arranging;

    internal ConnectionSidebarGroup(string key, string label, int count, int depth, bool expanded)
    {
        Name = key;
        _label = label;
        _count = count;
        _expanded = expanded;
        AutoSize = false;
        Padding = new Padding(8, 7, 8, 9);
        Margin = new Padding(depth * 8, 2, 0, 10);
        BackColor = StorageHubTheme.SurfaceMuted;
        DoubleBuffered = true;
        _header = new Button
        {
            Height = 30,
            Dock = DockStyle.Top,
            FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = StorageHubTheme.CreateSectionFont(),
            ForeColor = StorageHubTheme.Text,
            BackColor = StorageHubTheme.SurfaceMuted,
            TabStop = true,
            AccessibleName = $"{label} connection group"
        };
        _header.FlatAppearance.BorderSize = 0;
        _header.Click += (_, _) => Expanded = !Expanded;
        _body = new FlowLayoutPanel
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(0, 3, 0, 0),
            Margin = Padding.Empty,
            BackColor = StorageHubTheme.SurfaceMuted,
            Visible = expanded
        };
        Controls.Add(_body);
        Controls.Add(_header);
        UpdateHeader();
        ArrangeChildren();
    }

    internal event EventHandler<bool>? ExpandedChanged;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool Expanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value)
            {
                return;
            }

            _expanded = value;
            _body.Visible = value;
            UpdateHeader();
            ArrangeChildren();
            ExpandedChanged?.Invoke(this, value);
        }
    }

    internal void AddChild(Control child)
    {
        _body.Controls.Add(child);
        child.SizeChanged += ChildSizeChanged;
        ArrangeChildren();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        ArrangeChildren();
    }

    private void ChildSizeChanged(object? sender, EventArgs e) => ArrangeChildren();

    private void ArrangeChildren()
    {
        if (_arranging || IsDisposed)
        {
            return;
        }

        _arranging = true;
        try
        {
            var innerWidth = Math.Max(80, ClientSize.Width - Padding.Horizontal);
            _header.Width = innerWidth;
            _body.Width = innerWidth;
            foreach (Control child in _body.Controls)
            {
                child.Width = Math.Max(72, innerWidth - child.Margin.Horizontal);
            }

            var bodyHeight = _body.Padding.Vertical + _body.Controls
                .Cast<Control>()
                .Where(static child => child.Visible)
                .Sum(static child => child.Height + child.Margin.Vertical);
            _body.Height = bodyHeight;
            Height = Padding.Top + _header.Height + (_expanded ? bodyHeight : 0) + Padding.Bottom;
            _body.Visible = _expanded;
            PerformLayout();

            using var path = RoundedPath(
                new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1)),
                12);
            var previousRegion = Region;
            Region = new Region(path);
            previousRegion?.Dispose();
        }
        finally
        {
            _arranging = false;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var path = RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 12);
        using var border = new Pen(StorageHubTheme.Border);
        e.Graphics.DrawPath(border, path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _header.Font.Dispose();
        }

        base.Dispose(disposing);
    }

    private void UpdateHeader() => _header.Text = $"{(_expanded ? "▾" : "▸")}  {_label}  ·  {_count}";

    private static System.Drawing.Drawing2D.GraphicsPath RoundedPath(Rectangle bounds, int radius)
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
}

internal sealed class ConnectionSidebarItem : Control
{
    private const int ActionSize = 22;
    private const int ActionGap = 4;
    private const int ActionInset = 8;

    private bool _selected;
    private bool _hovered;
    private ConnectionRowAction? _hotAction;

    internal ConnectionSidebarItem(ConnectionCardModel connection)
    {
        Connection = connection;
        Height = 58;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleName = connection.Name;
        AccessibleDescription =
            $"{connection.Descriptor.DisplayName} saved connection. {connection.State}. " +
            "Press Enter to open, F2 to edit, Delete to remove, Shift+F10 for more.";
        AccessibleRole = AccessibleRole.ListItem;
        DoubleBuffered = true;
        // Off by default on a raw Control, so without this the row never sees a double click.
        SetStyle(ControlStyles.StandardDoubleClick, true);
    }

    /// <summary>Raised when the row is opened: double click, or Enter.</summary>
    internal event EventHandler? Activated;

    internal event EventHandler<ConnectionRowAction>? ActionInvoked;

    internal event EventHandler<Point>? MenuRequested;

    internal ConnectionCardModel Connection { get; }

    /// <summary>Whether to draw the inline edit and delete affordances.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool ShowActions { get; init; }

    /// <summary>Borrowed from the list, which keeps them in step with the palette.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Image? EditIcon { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Image? DeleteIcon { get; set; }

    /// <summary>Internal so the hit targets can be driven directly from tests.</summary>
    internal Rectangle EditBounds => new(
        Width - (ActionSize * 2) - ActionGap - ActionInset, (Height - ActionSize) / 2, ActionSize, ActionSize);

    internal Rectangle DeleteBounds => new(
        Width - ActionSize - ActionInset, (Height - ActionSize) / 2, ActionSize, ActionSize);

    /// <summary>
    /// Reserved whether or not the icons are currently painted, so the name does not reflow under
    /// the pointer as the row is hovered.
    /// </summary>
    private int ActionStripWidth => ShowActions ? (ActionSize * 2) + ActionGap + ActionInset + 4 : 0;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            Invalidate();
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        _hotAction = null;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hot = HitAction(e.Location);
        if (hot != _hotAction)
        {
            _hotAction = hot;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right)
        {
            Focus();
            MenuRequested?.Invoke(this, PointToScreen(e.Location));
            return;
        }

        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        Focus();

        // The second click of a double click lands here too; letting it re-fire an action would
        // turn an over-eager open into a delete prompt.
        if (e.Clicks == 1 && HitAction(e.Location) is { } action)
        {
            ActionInvoked?.Invoke(this, action);
            return;
        }

        OnClick(EventArgs.Empty);
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        base.OnDoubleClick(e);
        Activated?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Enter:
                e.Handled = true;
                OnClick(EventArgs.Empty);
                Activated?.Invoke(this, EventArgs.Empty);
                break;
            case Keys.Space:
                e.Handled = true;
                OnClick(EventArgs.Empty);
                break;
            case Keys.F2 when ShowActions:
                e.Handled = true;
                ActionInvoked?.Invoke(this, ConnectionRowAction.Edit);
                break;
            case Keys.Delete when ShowActions:
                e.Handled = true;
                ActionInvoked?.Invoke(this, ConnectionRowAction.Delete);
                break;
            case Keys.Apps:
            case Keys.F10 when e.Shift:
                e.Handled = true;
                MenuRequested?.Invoke(this, PointToScreen(new Point(Width / 2, Height / 2)));
                break;
        }
    }

    private ConnectionRowAction? HitAction(Point location)
    {
        if (!ShowActions)
        {
            return null;
        }

        if (EditBounds.Contains(location)) return ConnectionRowAction.Edit;
        return DeleteBounds.Contains(location) ? ConnectionRowAction.Delete : null;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var bounds = new Rectangle(1, 1, Width - 3, Height - 3);
        using var path = CreatePath(bounds, 9);
        using var fill = new SolidBrush(_selected ? StorageHubTheme.CurrentPalette.Selection : StorageHubTheme.Surface);
        using var outline = new Pen(_selected ? StorageHubTheme.ParseAccent(Connection.AccentHex) : StorageHubTheme.Border,
            _selected ? 1.8F : 1F);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(outline, path);
        var accent = StorageHubTheme.ParseAccent(Connection.AccentHex);
        using var badge = new SolidBrush(accent);
        using (var badgePath = CreatePath(new Rectangle(9, 10, 36, 36), 8))
        {
            e.Graphics.FillPath(badge, badgePath);
        }

        using var badgeFont = new Font("Segoe UI Semibold", 7.5F, FontStyle.Bold);
        TextRenderer.DrawText(e.Graphics, Connection.Descriptor.ShortName, badgeFont, new Rectangle(9, 10, 36, 36), StorageHubTheme.ContrastText(accent),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        var textWidth = Math.Max(20, Width - 65 - ActionStripWidth);
        TextRenderer.DrawText(e.Graphics, Connection.Name, Font, new Rectangle(55, 7, textWidth, 22),
            StorageHubTheme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        var detail = Connection.IsEnabled
            ? $"{Connection.Endpoint} · {Connection.State}"
            : $"{Connection.Endpoint} · Disabled";
        TextRenderer.DrawText(e.Graphics, detail, Font, new Rectangle(55, 29, textWidth, 20),
            Connection.IsEnabled ? StorageHubTheme.TextMuted : StorageHubTheme.Warning,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        // Revealed on hover, selection or focus: drawing them on every row at rest turns a long
        // list into a wall of icons, but a keyboard user never hovers.
        if (ShowActions && (_hovered || _selected || Focused))
        {
            DrawAction(e.Graphics, EditIcon, EditBounds, _hotAction == ConnectionRowAction.Edit, danger: false);
            DrawAction(e.Graphics, DeleteIcon, DeleteBounds, _hotAction == ConnectionRowAction.Delete, danger: true);
        }

        if (Focused)
        {
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, -4, -4));
        }
    }

    private static void DrawAction(Graphics graphics, Image? icon, Rectangle bounds, bool hot, bool danger)
    {
        if (hot)
        {
            using var path = CreatePath(bounds, 6);
            using var fill = new SolidBrush(danger
                ? StorageHubTheme.CurrentPalette.DangerTint
                : StorageHubTheme.CurrentPalette.Elevated);
            graphics.FillPath(fill, path);
        }

        if (icon is null)
        {
            return;
        }

        graphics.DrawImage(
            icon,
            new Rectangle(
                bounds.X + ((bounds.Width - 16) / 2),
                bounds.Y + ((bounds.Height - 16) / 2),
                16,
                16));
    }

    /// <summary>
    /// The row is one owner-drawn control, so the inline icons have no windows of their own and are
    /// invisible to assistive technology. Publishing them as accessible children is what makes them
    /// reachable; the key bindings in <see cref="OnKeyDown"/> are what makes them operable.
    /// </summary>
    protected override AccessibleObject CreateAccessibilityInstance() => new RowAccessibleObject(this);

    private sealed class RowAccessibleObject(ConnectionSidebarItem owner)
        : Control.ControlAccessibleObject(owner)
    {
        public override int GetChildCount() => owner.ShowActions ? 2 : 0;

        public override AccessibleObject? GetChild(int index) => (owner.ShowActions, index) switch
        {
            (true, 0) => new RowActionAccessibleObject(owner, ConnectionRowAction.Edit),
            (true, 1) => new RowActionAccessibleObject(owner, ConnectionRowAction.Delete),
            _ => null
        };
    }

    private sealed class RowActionAccessibleObject(ConnectionSidebarItem owner, ConnectionRowAction action)
        : AccessibleObject
    {
        public override string Name => action == ConnectionRowAction.Edit
            ? $"Edit connection {owner.Connection.Name}"
            : $"Delete connection {owner.Connection.Name}";

        public override AccessibleRole Role => AccessibleRole.PushButton;

        public override AccessibleObject Parent => owner.AccessibilityObject;

        public override Rectangle Bounds => owner.RectangleToScreen(
            action == ConnectionRowAction.Edit ? owner.EditBounds : owner.DeleteBounds);

        public override string DefaultAction => action == ConnectionRowAction.Edit ? "Edit" : "Delete";

        public override void DoDefaultAction() => owner.ActionInvoked?.Invoke(owner, action);
    }

    private static System.Drawing.Drawing2D.GraphicsPath CreatePath(Rectangle bounds, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = radius * 2;
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
}
