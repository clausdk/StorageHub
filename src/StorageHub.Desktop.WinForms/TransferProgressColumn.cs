using System.Drawing.Drawing2D;

namespace StorageHub.Desktop;

/// <summary>
/// A grid column that paints a determinate progress bar behind its text. The completed fraction is
/// carried on the cell style's <see cref="DataGridViewCellStyle.Tag"/> so that the displayed text
/// stays the authoritative accessible value and a row with an unknown total degrades to text only.
/// </summary>
internal sealed class TransferProgressColumn : DataGridViewTextBoxColumn
{
    public TransferProgressColumn() => CellTemplate = new TransferProgressCell();
}

internal sealed class TransferProgressCell : DataGridViewTextBoxCell
{
    private const int TrackInset = 4;
    private const int TrackRadius = 3;

    protected override void Paint(
        Graphics graphics,
        Rectangle clipBounds,
        Rectangle cellBounds,
        int rowIndex,
        DataGridViewElementStates cellState,
        object? value,
        object? formattedValue,
        string? errorText,
        DataGridViewCellStyle cellStyle,
        DataGridViewAdvancedBorderStyle advancedBorderStyle,
        DataGridViewPaintParts paintParts)
    {
        base.Paint(
            graphics,
            clipBounds,
            cellBounds,
            rowIndex,
            cellState,
            value,
            formattedValue,
            errorText,
            cellStyle,
            advancedBorderStyle,
            paintParts & ~DataGridViewPaintParts.ContentForeground);

        var track = Rectangle.Inflate(cellBounds, -TrackInset, -TrackInset);
        if (track.Width <= 2 || track.Height <= 2)
        {
            return;
        }

        if (cellStyle.Tag is double fraction)
        {
            var selected = (cellState & DataGridViewElementStates.Selected) != 0;
            PaintBar(graphics, track, fraction, selected);
        }

        if ((paintParts & DataGridViewPaintParts.ContentForeground) == 0)
        {
            return;
        }

        var text = formattedValue?.ToString();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        TextRenderer.DrawText(
            graphics,
            text,
            cellStyle.Font,
            track,
            StorageHubTheme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
    }

    private static void PaintBar(Graphics graphics, Rectangle track, double fraction, bool selected)
    {
        var previousMode = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            using (var trackPath = CreateRoundedPath(track, TrackRadius))
            using (var trackBrush = new SolidBrush(selected
                ? Color.FromArgb(40, StorageHubTheme.Text)
                : StorageHubTheme.SurfaceMuted))
            {
                graphics.FillPath(trackBrush, trackPath);
            }

            var filledWidth = (int)Math.Round(track.Width * Math.Clamp(fraction, 0D, 1D));
            if (filledWidth <= 0)
            {
                return;
            }

            // A rounded fill narrower than its own corners degenerates into a wedge, so keep the
            // painted width at least one full diameter and clip it back to the real fraction.
            var fill = new Rectangle(track.X, track.Y, Math.Max(filledWidth, TrackRadius * 2), track.Height);
            using var fillPath = CreateRoundedPath(fill, TrackRadius);
            using var fillBrush = new SolidBrush(fraction >= 1D
                ? StorageHubTheme.Success
                : StorageHubTheme.Primary);
            var previousClip = graphics.Clip;
            try
            {
                graphics.SetClip(new Rectangle(track.X, track.Y, filledWidth, track.Height), CombineMode.Intersect);
                graphics.FillPath(fillBrush, fillPath);
            }
            finally
            {
                graphics.Clip = previousClip;
            }
        }
        finally
        {
            graphics.SmoothingMode = previousMode;
        }
    }

    private static GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
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
