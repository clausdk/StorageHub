using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

internal sealed class ConnectionSidebarControl : UserControl
{
    private readonly FlowLayoutPanel _content;
    private readonly Dictionary<Guid, ConnectionSidebarItem> _items = [];
    private readonly HashSet<string> _collapsedGroups = new(StringComparer.OrdinalIgnoreCase);

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
    }

    internal event EventHandler<ConnectionCardModel>? ConnectionSelected;

    internal Guid? SelectedConnectionId { get; private set; }

    internal void SetConnections(
        IEnumerable<ConnectionCardModel> connections,
        string? searchText,
        Guid? selectedConnectionId)
    {
        ArgumentNullException.ThrowIfNull(connections);
        var query = searchText?.Trim() ?? string.Empty;
        var matching = connections
            .Where(card => Matches(card, query))
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
            AddSection("Storage", matching.Where(static card => card.Type == ConnectionProfileType.Storage));
            AddSection("Remote clients", matching.Where(static card => card.Type == ConnectionProfileType.Client));
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
            var item = new ConnectionSidebarItem(card);
            item.Click += (_, _) => SelectConnection(card.ConnectionId, raiseEvent: true);
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

    private static string[] SplitFolder(string? folder) => string.IsNullOrWhiteSpace(folder)
        ? []
        : folder.Split(['/', '\\'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static bool Matches(ConnectionCardModel card, string query) => query.Length == 0 ||
        card.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        card.Endpoint.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        card.State.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        (card.FolderPath?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
        card.Descriptor.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        card.DisplayTags.Any(tag => tag.Contains(query, StringComparison.CurrentCultureIgnoreCase));

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
    private bool _selected;

    internal ConnectionSidebarItem(ConnectionCardModel connection)
    {
        Connection = connection;
        Height = 58;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleName = connection.Name;
        AccessibleDescription = $"{connection.Descriptor.DisplayName} saved connection. {connection.State}";
        DoubleBuffered = true;
    }

    internal ConnectionCardModel Connection { get; }

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

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
        {
            OnClick(EventArgs.Empty);
            Focus();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            e.Handled = true;
            OnClick(EventArgs.Empty);
        }
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
        e.Graphics.FillRectangle(badge, 9, 10, 36, 36);
        using var badgeFont = new Font("Segoe UI Semibold", 7.5F, FontStyle.Bold);
        TextRenderer.DrawText(e.Graphics, Connection.Descriptor.ShortName, badgeFont, new Rectangle(9, 10, 36, 36), Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, Connection.Name, Font, new Rectangle(55, 7, Math.Max(20, Width - 65), 22),
            StorageHubTheme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        var detail = Connection.IsEnabled
            ? $"{Connection.Endpoint} · {Connection.State}"
            : $"{Connection.Endpoint} · Disabled";
        TextRenderer.DrawText(e.Graphics, detail, Font, new Rectangle(55, 29, Math.Max(20, Width - 65), 20),
            Connection.IsEnabled ? StorageHubTheme.TextMuted : StorageHubTheme.Warning,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (Focused)
        {
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, -4, -4));
        }
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
