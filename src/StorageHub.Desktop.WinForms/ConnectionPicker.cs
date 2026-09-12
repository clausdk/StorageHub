using System.Drawing.Drawing2D;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

/// <summary>
/// One rendered row in the connection picker: either a group heading or a selectable connection.
/// Headings are rows rather than decoration so the list can be drawn and measured in one pass.
/// </summary>
internal sealed record ConnectionPickerRow(string? GroupLabel, ConnectionCardModel? Card)
{
    public bool IsHeader => Card is null;
}

/// <summary>
/// Filters and groups the connection list for the picker.
///
/// Kept as pure functions so the matching rules can be tested without a window. The fields searched
/// deliberately mirror Connection Manager, which already searches names, endpoints, folders,
/// providers, states, and tags: a query that finds a connection in one place should find it in the
/// other.
/// </summary>
internal static class ConnectionPickerFilter
{
    public static bool Matches(ConnectionCardModel card, string? query)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        // Every whitespace-separated term must match somewhere, so typing more narrows rather than
        // widens - "s3 archive" finds the S3 connection called Archive, not every S3 connection.
        foreach (var term in query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!MatchesTerm(card, term))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesTerm(ConnectionCardModel card, string term) =>
        Contains(card.Name, term) ||
        Contains(card.Endpoint, term) ||
        Contains(card.FolderPath, term) ||
        Contains(card.State, term) ||
        Contains(card.Provider.ToString(), term) ||
        Contains(card.Type.ToString(), term) ||
        card.DisplayTags.Any(tag => Contains(tag, term));

    private static bool Contains(string? value, string term) =>
        value is not null && value.Contains(term, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>
    /// Builds the visible rows, inserting a heading whenever the group changes. The incoming order
    /// is preserved: the caller has already sorted favourites, storage, and clients into the order
    /// the rest of the shell uses.
    /// </summary>
    public static IReadOnlyList<ConnectionPickerRow> BuildRows(
        IEnumerable<ConnectionCardModel> cards,
        string? query,
        Func<ConnectionCardModel, string> groupLabel)
    {
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentNullException.ThrowIfNull(groupLabel);

        var rows = new List<ConnectionPickerRow>();
        string? currentGroup = null;
        foreach (var card in cards)
        {
            if (!Matches(card, query))
            {
                continue;
            }

            var label = groupLabel(card);
            if (!string.Equals(label, currentGroup, StringComparison.OrdinalIgnoreCase))
            {
                rows.Add(new ConnectionPickerRow(label, null));
                currentGroup = label;
            }

            rows.Add(new ConnectionPickerRow(null, card));
        }

        return rows;
    }

    /// <summary>The first selectable row at or after an index, or -1 when none remains.</summary>
    public static int NextSelectable(IReadOnlyList<ConnectionPickerRow> rows, int start, int direction)
    {
        ArgumentNullException.ThrowIfNull(rows);
        for (var index = start; index >= 0 && index < rows.Count; index += direction == 0 ? 1 : direction)
        {
            if (!rows[index].IsHeader)
            {
                return index;
            }

            if (direction == 0)
            {
                break;
            }
        }

        return -1;
    }
}

/// <summary>
/// A searchable connection chooser shown under the pane's connection button.
///
/// A plain drop-down list stops scaling once a user has more than a handful of saved connections:
/// it can only be navigated by scrolling or by first-letter matching. This keeps the existing card
/// design and grouping, marks the active connection, and adds a search box that filters as you
/// type, with the keyboard driving the list from the search field.
/// </summary>
internal sealed class ConnectionPickerPopup : ToolStripDropDown
{
    private const int HeaderHeight = 24;
    private const int CardHeight = 46;

    private readonly ListBox _list;
    private readonly TextBox _search;
    private readonly Label _empty;
    private readonly IReadOnlyList<ConnectionCardModel> _cards;
    private readonly Func<ConnectionCardModel, string> _groupLabel;
    private readonly Guid? _activeConnectionId;
    private readonly string? _activeName;
    private IReadOnlyList<ConnectionPickerRow> _rows = [];

    public ConnectionPickerPopup(
        IReadOnlyList<ConnectionCardModel> cards,
        ConnectionCardModel? active,
        Func<ConnectionCardModel, string> groupLabel,
        int width)
    {
        _cards = cards ?? throw new ArgumentNullException(nameof(cards));
        _groupLabel = groupLabel ?? throw new ArgumentNullException(nameof(groupLabel));
        _activeConnectionId = active?.ConnectionId;
        _activeName = active?.Name;

        AutoClose = true;
        DropShadowEnabled = true;
        Padding = Padding.Empty;
        BackColor = StorageHubTheme.Surface;

        _search = new TextBox
        {
            Dock = DockStyle.Top,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = StorageHubTheme.SurfaceMuted,
            ForeColor = StorageHubTheme.Text,
            PlaceholderText = "Search connections",
            Margin = Padding.Empty,
            AccessibleName = "Search connections"
        };
        _search.TextChanged += (_, _) => Rebuild(preserveSelection: false);
        _search.KeyDown += SearchKeyDown;

        _list = new ListBox
        {
            Dock = DockStyle.Fill,
            DrawMode = DrawMode.OwnerDrawVariable,
            BorderStyle = BorderStyle.None,
            BackColor = StorageHubTheme.Surface,
            ForeColor = StorageHubTheme.Text,
            IntegralHeight = false,
            AccessibleName = "Connections"
        };
        _list.MeasureItem += MeasureRow;
        _list.DrawItem += DrawRow;
        _list.SelectedIndexChanged += SkipHeaders;
        _list.MouseMove += HighlightUnderCursor;
        _list.Click += (_, _) => CommitSelection();
        _list.KeyDown += ListKeyDown;

        _empty = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = StorageHubTheme.TextMuted,
            BackColor = StorageHubTheme.Surface,
            Text = "No connection matches that search.",
            Visible = false
        };

        var host = new Panel
        {
            BackColor = StorageHubTheme.Surface,
            Padding = new Padding(6),
            Size = new Size(Math.Max(280, width), 360)
        };
        host.Controls.Add(_empty);
        host.Controls.Add(_list);
        host.Controls.Add(_search);

        Items.Add(new ToolStripControlHost(host)
        {
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            AutoSize = false,
            Size = host.Size
        });

        Rebuild(preserveSelection: false);
    }

    /// <summary>Raised when a connection is chosen. The popup closes itself first.</summary>
    public event EventHandler<ConnectionCardModel>? ConnectionChosen;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Typing should filter immediately, without first having to click the search box.
        _search.Focus();
    }

    private void Rebuild(bool preserveSelection)
    {
        var previous = preserveSelection && _list.SelectedIndex >= 0 && _list.SelectedIndex < _rows.Count
            ? _rows[_list.SelectedIndex].Card
            : null;

        _rows = ConnectionPickerFilter.BuildRows(_cards, _search.Text, _groupLabel);
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var row in _rows)
            {
                _list.Items.Add(row);
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        _empty.Visible = _rows.Count == 0;
        _list.Visible = _rows.Count > 0;
        if (_rows.Count == 0)
        {
            return;
        }

        var target = previous is not null ? IndexOfCard(previous) : IndexOfActive();
        _list.SelectedIndex = target >= 0
            ? target
            : ConnectionPickerFilter.NextSelectable(_rows, 0, 1);
    }

    private int IndexOfActive()
    {
        for (var index = 0; index < _rows.Count; index++)
        {
            if (_rows[index].Card is { } card && IsActive(card))
            {
                return index;
            }
        }

        return -1;
    }

    private int IndexOfCard(ConnectionCardModel card)
    {
        for (var index = 0; index < _rows.Count; index++)
        {
            if (ReferenceEquals(_rows[index].Card, card))
            {
                return index;
            }
        }

        return -1;
    }

    private bool IsActive(ConnectionCardModel card) => _activeConnectionId is { } id
        ? card.ConnectionId == id
        : card.ConnectionId is null && string.Equals(card.Name, _activeName, StringComparison.Ordinal);

    private void SearchKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Down:
                MoveHighlight(1);
                e.Handled = true;
                e.SuppressKeyPress = true;
                break;
            case Keys.Up:
                MoveHighlight(-1);
                e.Handled = true;
                e.SuppressKeyPress = true;
                break;
            case Keys.Enter:
                CommitSelection();
                e.Handled = true;
                e.SuppressKeyPress = true;
                break;
            case Keys.Escape:
                Close(ToolStripDropDownCloseReason.Keyboard);
                e.Handled = true;
                break;
        }
    }

    private void ListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            CommitSelection();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void MoveHighlight(int direction)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        var start = _list.SelectedIndex < 0 ? (direction > 0 ? 0 : _rows.Count - 1) : _list.SelectedIndex + direction;
        var next = ConnectionPickerFilter.NextSelectable(_rows, start, direction);
        if (next >= 0)
        {
            _list.SelectedIndex = next;
        }
    }

    /// <summary>Keeps the highlight on a real connection when a group heading is landed on.</summary>
    private void SkipHeaders(object? sender, EventArgs e)
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _rows.Count || !_rows[_list.SelectedIndex].IsHeader)
        {
            return;
        }

        var next = ConnectionPickerFilter.NextSelectable(_rows, _list.SelectedIndex + 1, 1);
        if (next < 0)
        {
            next = ConnectionPickerFilter.NextSelectable(_rows, _list.SelectedIndex - 1, -1);
        }

        if (next >= 0)
        {
            _list.SelectedIndex = next;
        }
    }

    private void HighlightUnderCursor(object? sender, MouseEventArgs e)
    {
        var index = _list.IndexFromPoint(e.Location);
        if (index >= 0 && index < _rows.Count && !_rows[index].IsHeader && index != _list.SelectedIndex)
        {
            _list.SelectedIndex = index;
        }
    }

    private void CommitSelection()
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _rows.Count ||
            _rows[_list.SelectedIndex].Card is not { } card)
        {
            return;
        }

        Close(ToolStripDropDownCloseReason.ItemClicked);
        ConnectionChosen?.Invoke(this, card);
    }

    private void MeasureRow(object? sender, MeasureItemEventArgs e)
    {
        e.ItemHeight = e.Index >= 0 && e.Index < _rows.Count && _rows[e.Index].IsHeader
            ? HeaderHeight
            : CardHeight;
    }

    private void DrawRow(object? sender, DrawItemEventArgs e)
    {
        using var background = new SolidBrush(StorageHubTheme.Surface);
        e.Graphics.FillRectangle(background, e.Bounds);
        if (e.Index < 0 || e.Index >= _rows.Count)
        {
            return;
        }

        var row = _rows[e.Index];
        if (row.IsHeader)
        {
            using var groupFont = new Font("Segoe UI Semibold", 7.5F, FontStyle.Bold, GraphicsUnit.Point);
            TextRenderer.DrawText(
                e.Graphics,
                (row.GroupLabel ?? string.Empty).ToUpperInvariant(),
                groupFont,
                new Rectangle(e.Bounds.Left + 10, e.Bounds.Top + 2, e.Bounds.Width - 20, HeaderHeight - 4),
                StorageHubTheme.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            return;
        }

        var card = row.Card!;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var highlighted = (e.State & DrawItemState.Selected) != 0;
        var accent = StorageHubTheme.ParseAccent(card.AccentHex);
        var bounds = new Rectangle(e.Bounds.Left + 4, e.Bounds.Top + 2, e.Bounds.Width - 8, e.Bounds.Height - 5);

        using (var fill = new SolidBrush(highlighted
                   ? StorageHubTheme.CurrentPalette.Selection
                   : StorageHubTheme.SurfaceMuted))
        using (var border = new Pen(highlighted ? accent : StorageHubTheme.Border, highlighted ? 1.8F : 1F))
        using (var path = RoundedRectangle(bounds, 10))
        {
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(border, path);
        }

        using (var accentBrush = new SolidBrush(accent))
        {
            e.Graphics.FillEllipse(accentBrush, bounds.Left + 11, bounds.Top + (bounds.Height - 9) / 2, 9, 9);
        }

        var right = bounds.Right - 10;

        // The connection already open in this pane is marked, so the list answers "where am I?"
        // as well as "where do I want to go?".
        if (IsActive(card))
        {
            using var activeFont = new Font("Segoe UI Semibold", 7.5F, FontStyle.Bold, GraphicsUnit.Point);
            const string activeText = "ACTIVE";
            var activeWidth = TextRenderer.MeasureText(activeText, activeFont, Size.Empty, TextFormatFlags.NoPadding).Width + 12;
            var activeBounds = new Rectangle(right - activeWidth, bounds.Top + (bounds.Height - 20) / 2, activeWidth, 20);
            using (var activeFill = new SolidBrush(Color.FromArgb(46, StorageHubTheme.Success)))
            using (var activePath = RoundedRectangle(activeBounds, 9))
            {
                e.Graphics.FillPath(activeFill, activePath);
            }

            TextRenderer.DrawText(
                e.Graphics,
                activeText,
                activeFont,
                activeBounds,
                StorageHubTheme.Success,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            right = activeBounds.Left - 8;
        }

        var badgeText = card.Type == ConnectionProfileType.Client
            ? $"CLIENT · {card.Provider.ToString().ToUpperInvariant()}"
            : card.Provider == StorageProviderKind.Local
                ? "SYSTEM · LOCAL"
                : $"STORAGE · {card.Provider.ToString().ToUpperInvariant()}";
        using (var badgeFont = new Font("Segoe UI Semibold", 7.5F, FontStyle.Bold, GraphicsUnit.Point))
        {
            var badgeSize = TextRenderer.MeasureText(badgeText, badgeFont, Size.Empty, TextFormatFlags.NoPadding);
            var badgeBounds = new Rectangle(
                right - badgeSize.Width - 12,
                bounds.Top + (bounds.Height - 20) / 2,
                badgeSize.Width + 12,
                20);
            using (var badgeFill = new SolidBrush(Color.FromArgb(highlighted ? 48 : 28, accent)))
            using (var badgePath = RoundedRectangle(badgeBounds, 9))
            {
                e.Graphics.FillPath(badgeFill, badgePath);
            }

            TextRenderer.DrawText(
                e.Graphics,
                badgeText,
                badgeFont,
                badgeBounds,
                highlighted ? StorageHubTheme.Text : accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            right = badgeBounds.Left - 8;
        }

        var textLeft = bounds.Left + 28;
        var textRight = Math.Max(textLeft + 40, right);
        TextRenderer.DrawText(
            e.Graphics,
            card.Name,
            _list.Font,
            Rectangle.FromLTRB(textLeft, bounds.Top + 4, textRight, bounds.Top + 24),
            card.IsEnabled ? StorageHubTheme.Text : StorageHubTheme.TextMuted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(
            e.Graphics,
            card.Endpoint,
            _list.Font,
            Rectangle.FromLTRB(textLeft, bounds.Top + 22, textRight, bounds.Bottom - 3),
            StorageHubTheme.TextMuted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        if (diameter <= 1)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var arc = new Rectangle(bounds.X, bounds.Y, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.X;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}
