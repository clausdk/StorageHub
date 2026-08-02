using System.Drawing.Drawing2D;

namespace StorageHub.Desktop;

public static class StorageHubTheme
{
    public static Color Canvas { get; } = Color.FromArgb(246, 248, 252);

    public static Color Surface { get; } = Color.White;

    public static Color SurfaceMuted { get; } = Color.FromArgb(239, 243, 249);

    public static Color Border { get; } = Color.FromArgb(214, 221, 232);

    public static Color Text { get; } = Color.FromArgb(28, 37, 54);

    public static Color TextMuted { get; } = Color.FromArgb(91, 103, 124);

    public static Color Primary { get; } = Color.FromArgb(42, 104, 214);

    public static Color Success { get; } = Color.FromArgb(17, 135, 86);

    public static Color Warning { get; } = Color.FromArgb(190, 104, 0);

    public static Color Danger { get; } = Color.FromArgb(190, 45, 55);

    public static Font CreateSectionFont() => new("Segoe UI Semibold", 10F, FontStyle.Regular, GraphicsUnit.Point);

    public static ToolStripRenderer CreateToolStripRenderer() =>
        new ToolStripProfessionalRenderer(new StorageHubColorTable());

    public static void StylePrimaryButton(Button button)
    {
        ArgumentNullException.ThrowIfNull(button);
        button.AutoSize = true;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = Primary;
        button.ForeColor = Color.White;
        button.Padding = new Padding(12, 5, 12, 5);
        button.MinimumSize = new Size(90, 34);
        button.Cursor = Cursors.Hand;
    }

    public static void StyleSecondaryButton(Button button)
    {
        ArgumentNullException.ThrowIfNull(button);
        button.AutoSize = true;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Border;
        button.BackColor = Surface;
        button.ForeColor = Text;
        button.Padding = new Padding(10, 4, 10, 4);
        button.MinimumSize = new Size(84, 34);
        button.Cursor = Cursors.Hand;
    }

    public static Color ParseAccent(string accentHex)
    {
        if (string.IsNullOrWhiteSpace(accentHex))
        {
            return Primary;
        }

        return ColorTranslator.FromHtml(accentHex);
    }

    private sealed class StorageHubColorTable : ProfessionalColorTable
    {
        public override Color ToolStripGradientBegin => Surface;
        public override Color ToolStripGradientMiddle => Surface;
        public override Color ToolStripGradientEnd => Surface;
        public override Color MenuStripGradientBegin => Surface;
        public override Color MenuStripGradientEnd => Surface;
        public override Color ToolStripBorder => Border;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Color.FromArgb(151, 179, 226);
        public override Color MenuItemSelected => Color.FromArgb(230, 238, 252);
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(230, 238, 252);
        public override Color MenuItemSelectedGradientEnd => Color.FromArgb(230, 238, 252);
        public override Color ButtonSelectedHighlight => Color.FromArgb(230, 238, 252);
        public override Color ButtonPressedHighlight => Color.FromArgb(211, 226, 249);
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Surface;
        public override Color StatusStripGradientBegin => Surface;
        public override Color StatusStripGradientEnd => Surface;
    }
}

public enum UiGlyph
{
    Add,
    Connections,
    Back,
    Forward,
    Up,
    Refresh,
    Compare,
    Run,
    Pause,
    Search,
    Folder,
    Save,
    Delete,
    Test,
    Lock,
    Warning,
    More
}

public static class UiIconFactory
{
    public static Bitmap Create(UiGlyph glyph, Color color, int logicalSize = 20, float dpiScale = 1F)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(logicalSize, 12);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(dpiScale, 0F);
        var pixelSize = Math.Max(12, (int)Math.Round(logicalSize * dpiScale, MidpointRounding.AwayFromZero));
        var bitmap = new Bitmap(pixelSize, pixelSize);
        bitmap.SetResolution(96F * dpiScale, 96F * dpiScale);

        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.ScaleTransform(pixelSize / 24F, pixelSize / 24F);
        using var pen = new Pen(color, 1.9F)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var brush = new SolidBrush(color);
        DrawGlyph(graphics, pen, brush, glyph);
        return bitmap;
    }

    private static void DrawGlyph(Graphics graphics, Pen pen, Brush brush, UiGlyph glyph)
    {
        switch (glyph)
        {
            case UiGlyph.Add:
                graphics.DrawLine(pen, 12, 5, 12, 19);
                graphics.DrawLine(pen, 5, 12, 19, 12);
                break;
            case UiGlyph.Connections:
                graphics.DrawArc(pen, 3, 5, 8, 10, -75, 150);
                graphics.DrawArc(pen, 13, 9, 8, 10, 105, 150);
                graphics.DrawLine(pen, 9, 9, 15, 15);
                break;
            case UiGlyph.Back:
                DrawChevron(graphics, pen, false);
                graphics.DrawLine(pen, 6, 12, 19, 12);
                break;
            case UiGlyph.Forward:
                DrawChevron(graphics, pen, true);
                graphics.DrawLine(pen, 5, 12, 18, 12);
                break;
            case UiGlyph.Up:
                graphics.DrawLines(pen, [new PointF(6, 12), new PointF(12, 6), new PointF(18, 12)]);
                graphics.DrawLine(pen, 12, 6, 12, 19);
                break;
            case UiGlyph.Refresh:
                graphics.DrawArc(pen, 4, 4, 16, 16, -35, 285);
                graphics.DrawLines(pen, [new PointF(17, 4), new PointF(20, 4), new PointF(20, 7)]);
                break;
            case UiGlyph.Compare:
                graphics.DrawLine(pen, 4, 8, 18, 8);
                graphics.DrawLines(pen, [new PointF(15, 5), new PointF(18, 8), new PointF(15, 11)]);
                graphics.DrawLine(pen, 20, 16, 6, 16);
                graphics.DrawLines(pen, [new PointF(9, 13), new PointF(6, 16), new PointF(9, 19)]);
                break;
            case UiGlyph.Run:
                graphics.FillPolygon(brush, [new PointF(7, 4), new PointF(19, 12), new PointF(7, 20)]);
                break;
            case UiGlyph.Pause:
                graphics.FillRectangle(brush, 6, 5, 4, 14);
                graphics.FillRectangle(brush, 14, 5, 4, 14);
                break;
            case UiGlyph.Search:
                graphics.DrawEllipse(pen, 4, 4, 11, 11);
                graphics.DrawLine(pen, 14, 14, 20, 20);
                break;
            case UiGlyph.Folder:
                using (var path = new GraphicsPath())
                {
                    path.AddLines([new PointF(3, 7), new PointF(10, 7), new PointF(12, 9), new PointF(21, 9), new PointF(19, 19), new PointF(3, 19)]);
                    path.CloseFigure();
                    graphics.DrawPath(pen, path);
                }
                break;
            case UiGlyph.Save:
                graphics.DrawRectangle(pen, 5, 4, 14, 16);
                graphics.DrawRectangle(pen, 8, 4, 8, 5);
                graphics.DrawRectangle(pen, 8, 14, 8, 6);
                break;
            case UiGlyph.Delete:
                graphics.DrawLine(pen, 5, 7, 19, 7);
                graphics.DrawLine(pen, 9, 4, 15, 4);
                graphics.DrawRectangle(pen, 7, 7, 10, 13);
                graphics.DrawLine(pen, 10, 10, 10, 17);
                graphics.DrawLine(pen, 14, 10, 14, 17);
                break;
            case UiGlyph.Test:
                graphics.DrawEllipse(pen, 4, 4, 16, 16);
                graphics.DrawLines(pen, [new PointF(8, 12), new PointF(11, 15), new PointF(17, 8)]);
                break;
            case UiGlyph.Lock:
                graphics.DrawRectangle(pen, 5, 10, 14, 10);
                graphics.DrawArc(pen, 8, 3, 8, 12, 180, 180);
                break;
            case UiGlyph.Warning:
                graphics.DrawPolygon(pen, [new PointF(12, 3), new PointF(21, 20), new PointF(3, 20)]);
                graphics.DrawLine(pen, 12, 8, 12, 14);
                graphics.FillEllipse(brush, 11, 16, 2, 2);
                break;
            case UiGlyph.More:
                graphics.FillEllipse(brush, 4, 10, 3, 3);
                graphics.FillEllipse(brush, 10.5F, 10, 3, 3);
                graphics.FillEllipse(brush, 17, 10, 3, 3);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(glyph), glyph, "Unknown UI glyph.");
        }
    }

    private static void DrawChevron(Graphics graphics, Pen pen, bool pointsRight)
    {
        if (pointsRight)
        {
            graphics.DrawLines(pen, [new PointF(13, 6), new PointF(19, 12), new PointF(13, 18)]);
        }
        else
        {
            graphics.DrawLines(pen, [new PointF(11, 6), new PointF(5, 12), new PointF(11, 18)]);
        }
    }
}

internal static class UiControlFactory
{
    public static Label CreateSectionTitle(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = StorageHubTheme.CreateSectionFont(),
        ForeColor = StorageHubTheme.Text,
        Margin = new Padding(0, 4, 0, 2)
    };

    public static Label CreateDescription(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(760, 0),
        ForeColor = StorageHubTheme.TextMuted,
        Margin = new Padding(0, 0, 0, 10)
    };

    public static void AddLabeledRow(TableLayoutPanel table, string labelText, Control control, string? helpText = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(control);
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Dock = DockStyle.Top;
        control.Margin = new Padding(4, 6, 4, string.IsNullOrWhiteSpace(helpText) ? 8 : 1);
        control.AccessibleName = string.IsNullOrWhiteSpace(control.AccessibleName) ? labelText : control.AccessibleName;
        var label = new Label
        {
            Text = labelText,
            AutoSize = true,
            ForeColor = StorageHubTheme.Text,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(4, 10, 12, 3)
        };
        table.Controls.Add(label, 0, row);
        table.Controls.Add(control, 1, row);

        if (!string.IsNullOrWhiteSpace(helpText))
        {
            var helpRow = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(new Label
            {
                Text = helpText,
                AutoSize = true,
                MaximumSize = new Size(660, 0),
                ForeColor = StorageHubTheme.TextMuted,
                Margin = new Padding(4, 0, 4, 8)
            }, 1, helpRow);
        }
    }
}
