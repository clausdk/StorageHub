namespace StorageHub.Desktop;

/// <summary>
/// A tab control that paints its own strip.
///
/// The stock control leaves the strip — everything beside and beneath the tab headers — to
/// comctl32, which paints it from the Windows theme rather than from the control's colours. Owner
/// drawing only covers the individual headers, so in dark mode the app showed a bright band across
/// the width of every tabbed surface. Taking over <see cref="ControlStyles.UserPaint"/> moves the
/// whole strip into managed painting while still raising <see cref="TabControl.DrawItem"/>, so the
/// existing per-tab renderers keep working unchanged.
/// </summary>
public class ThemedTabControl : TabControl
{
    private int _hoverIndex = -1;

    public ThemedTabControl()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        DrawMode = TabDrawMode.OwnerDrawFixed;
    }

    /// <summary>The tab under the pointer, or -1. Renderers use it to draw a hover state.</summary>
    public int HoverIndex => _hoverIndex;

    protected override void OnPaint(PaintEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var graphics = e.Graphics;
        using (var strip = new SolidBrush(StorageHubTheme.Canvas))
        {
            graphics.FillRectangle(strip, ClientRectangle);
        }

        // The selected page covers the display rectangle, but an empty control and the sliver
        // around a page's border both show through, so they get the page surface rather than the
        // strip colour.
        var display = DisplayRectangle;
        using (var surface = new SolidBrush(StorageHubTheme.Surface))
        {
            graphics.FillRectangle(surface, Rectangle.Inflate(display, 2, 2));
        }

        var count = TabCount;
        for (var index = 0; index < count; index++)
        {
            Rectangle bounds;
            try
            {
                bounds = GetTabRect(index);
            }
            catch (ArgumentOutOfRangeException)
            {
                // The native control has not laid the strip out yet; the next paint will.
                continue;
            }

            if (!e.ClipRectangle.IntersectsWith(bounds))
            {
                continue;
            }

            var state = DrawItemState.None;
            if (index == SelectedIndex)
            {
                state |= DrawItemState.Selected;
            }

            if (index == _hoverIndex)
            {
                state |= DrawItemState.HotLight;
            }

            OnDrawItem(new DrawItemEventArgs(graphics, Font, bounds, index, state));
        }

        using var separator = new Pen(StorageHubTheme.Border);
        var line = display.Top - 1;
        graphics.DrawLine(separator, ClientRectangle.Left, line, ClientRectangle.Right, line);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseMove(e);
        var hovered = -1;
        for (var index = 0; index < TabCount; index++)
        {
            if (GetTabRect(index).Contains(e.Location))
            {
                hovered = index;
                break;
            }
        }

        if (hovered != _hoverIndex)
        {
            _hoverIndex = hovered;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex != -1)
        {
            _hoverIndex = -1;
            Invalidate();
        }
    }

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        base.OnSelectedIndexChanged(e);
        Invalidate();
    }

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        Invalidate();
    }

    protected override void OnControlRemoved(ControlEventArgs e)
    {
        base.OnControlRemoved(e);
        Invalidate();
    }
}
