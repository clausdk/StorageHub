using System.ComponentModel;

namespace StorageHub.Desktop;

/// <summary>
/// A panel that paints itself as a card: rounded corners, a hairline border, and an optional
/// accent rail down the leading edge.
///
/// <see cref="BorderStyle.FixedSingle"/> draws a square border in a colour the theme does not
/// control, which is why every grouped surface in the app used to read as a wireframe box in dark
/// mode. Painting the border keeps it on the palette and lets a card carry a status colour.
/// </summary>
public class UiCard : Panel
{
    private Color? _accent;

    public UiCard()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BorderStyle = BorderStyle.None;
        BackColor = StorageHubTheme.Surface;
    }

    /// <summary>The rail colour along the leading edge, or null for no rail.</summary>
    [DefaultValue(null)]
    public Color? Accent
    {
        get => _accent;
        set
        {
            if (_accent == value)
            {
                return;
            }

            _accent = value;
            Invalidate();
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        // The card's own corners are transparent, so whatever sits behind it has to show through
        // them. Layout rows are themselves transparent, so the search walks up to the first
        // ancestor that actually paints a colour.
        using (var behind = new SolidBrush(ResolveBackdrop()))
        {
            e.Graphics.FillRectangle(behind, e.ClipRectangle);
        }

        StorageHubTheme.PaintCard(e.Graphics, ClientRectangle, _accent);
    }

    private Color ResolveBackdrop()
    {
        for (var ancestor = Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor.BackColor.A == 255)
            {
                return ancestor.BackColor;
            }
        }

        return StorageHubTheme.Canvas;
    }
}
