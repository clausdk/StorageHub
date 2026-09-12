using System.Drawing.Drawing2D;

namespace StorageHub.Desktop;

/// <summary>
/// The vector glyphs StorageHub draws for menus, toolbars, cards, and list rows. Every glyph is
/// authored on a 24x24 grid and stroked at render time, so one enum entry serves every size and
/// DPI without shipping bitmaps.
/// </summary>
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
    File,
    Save,
    Delete,
    Test,
    Terminal,
    Lock,
    Warning,
    More,
    Home,
    Settings,
    Info,
    Cut,
    Copy,
    Paste,
    Rename,
    SelectAll,
    Invert,
    Properties,
    Exit,
    Tree,
    Queue,
    Log,
    Hidden,
    Theme,
    Connect,
    Disconnect,
    Stop,
    Speed,
    Profiles,
    Schedule,
    History,
    Favorite,
    Key,
    Shield,
    Cloud,
    Server,
    Download,
    Upload,
    Keyboard,
    Documentation,
    Bug,
    Checksum,
    Diagnostics,
    Close,
    Link,
    Layers
}

/// <summary>
/// Which palette role an icon takes. Tracked icons resolve the tone again after an appearance
/// change, which is what keeps a glyph legible when the palette flips underneath it.
/// </summary>
public enum UiIconTone
{
    Text,
    Muted,
    Primary,
    OnPrimary,
    Success,
    Warning,
    Danger
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

        // Small icons lose their shape when a heavy stroke closes up the counters, so the weight
        // tapers with the rendered size rather than staying at one authored value.
        var weight = pixelSize <= 16 ? 1.65F : pixelSize <= 20 ? 1.8F : 1.95F;
        using var pen = new Pen(color, weight)
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
                // Two interlocking links. The previous arc pair collapsed into an unreadable
                // stroke below 20px, and this glyph carries the whole Connections surface.
                DrawRoundedRectangle(graphics, pen, new RectangleF(2.5F, 8.5F, 11, 7), 3.5F);
                DrawRoundedRectangle(graphics, pen, new RectangleF(10.5F, 8.5F, 11, 7), 3.5F);
                graphics.DrawLine(pen, 9, 12, 15, 12);
                break;
            case UiGlyph.Back:
                graphics.DrawLines(pen, [new PointF(10, 6), new PointF(4, 12), new PointF(10, 18)]);
                graphics.DrawLine(pen, 4, 12, 19, 12);
                break;
            case UiGlyph.Forward:
                graphics.DrawLines(pen, [new PointF(14, 6), new PointF(20, 12), new PointF(14, 18)]);
                graphics.DrawLine(pen, 5, 12, 20, 12);
                break;
            case UiGlyph.Up:
                graphics.DrawLines(pen, [new PointF(6, 11), new PointF(12, 5), new PointF(18, 11)]);
                graphics.DrawLine(pen, 12, 5, 12, 19);
                break;
            case UiGlyph.Refresh:
                graphics.DrawArc(pen, 4, 4, 16, 16, -35, 285);
                graphics.DrawLines(pen, [new PointF(16.5F, 3.5F), new PointF(20.5F, 4.5F), new PointF(19.5F, 8.5F)]);
                break;
            case UiGlyph.Compare:
                graphics.DrawLine(pen, 4, 8, 18, 8);
                graphics.DrawLines(pen, [new PointF(15, 5), new PointF(18, 8), new PointF(15, 11)]);
                graphics.DrawLine(pen, 20, 16, 6, 16);
                graphics.DrawLines(pen, [new PointF(9, 13), new PointF(6, 16), new PointF(9, 19)]);
                break;
            case UiGlyph.Run:
                graphics.FillPolygon(brush, [new PointF(7, 4.5F), new PointF(19, 12), new PointF(7, 19.5F)]);
                break;
            case UiGlyph.Pause:
                graphics.FillRectangle(brush, 6.5F, 5, 3.6F, 14);
                graphics.FillRectangle(brush, 13.9F, 5, 3.6F, 14);
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
            case UiGlyph.File:
                DrawPage(graphics, pen);
                break;
            case UiGlyph.Save:
                graphics.DrawRectangle(pen, 4.5F, 4.5F, 15, 15);
                graphics.DrawRectangle(pen, 8, 4.5F, 8, 4.5F);
                graphics.DrawRectangle(pen, 8, 13.5F, 8, 6);
                break;
            case UiGlyph.Delete:
                graphics.DrawLine(pen, 4.5F, 7, 19.5F, 7);
                graphics.DrawLine(pen, 9, 4, 15, 4);
                graphics.DrawLines(pen, [new PointF(6.5F, 7), new PointF(7.5F, 20), new PointF(16.5F, 20), new PointF(17.5F, 7)]);
                graphics.DrawLine(pen, 10.5F, 10.5F, 10.5F, 16.5F);
                graphics.DrawLine(pen, 13.5F, 10.5F, 13.5F, 16.5F);
                break;
            case UiGlyph.Test:
                graphics.DrawEllipse(pen, 4, 4, 16, 16);
                graphics.DrawLines(pen, [new PointF(8, 12), new PointF(11, 15), new PointF(16.5F, 8.5F)]);
                break;
            case UiGlyph.Terminal:
                DrawRoundedRectangle(graphics, pen, new RectangleF(2.5F, 4.5F, 19, 15), 2.5F);
                graphics.DrawLines(pen, [new PointF(6.5F, 9.5F), new PointF(9.5F, 12), new PointF(6.5F, 14.5F)]);
                graphics.DrawLine(pen, 12, 15, 17, 15);
                break;
            case UiGlyph.Lock:
                DrawRoundedRectangle(graphics, pen, new RectangleF(5, 10, 14, 10), 2F);
                graphics.DrawArc(pen, 8, 4, 8, 12, 180, 180);
                break;
            case UiGlyph.Warning:
                using (var path = new GraphicsPath())
                {
                    path.AddLines([new PointF(12, 3.5F), new PointF(21, 19.5F), new PointF(3, 19.5F)]);
                    path.CloseFigure();
                    graphics.DrawPath(pen, path);
                }
                graphics.DrawLine(pen, 12, 9, 12, 14);
                graphics.FillEllipse(brush, 11, 16, 2, 2);
                break;
            case UiGlyph.More:
                graphics.FillEllipse(brush, 4, 10.5F, 3, 3);
                graphics.FillEllipse(brush, 10.5F, 10.5F, 3, 3);
                graphics.FillEllipse(brush, 17, 10.5F, 3, 3);
                break;
            case UiGlyph.Home:
                graphics.DrawLines(pen, [new PointF(3, 11), new PointF(12, 4), new PointF(21, 11)]);
                graphics.DrawLines(pen, [new PointF(6, 10), new PointF(6, 20), new PointF(18, 20), new PointF(18, 10)]);
                graphics.DrawRectangle(pen, 10, 14, 4, 6);
                break;
            case UiGlyph.Settings:
                DrawGear(graphics, pen);
                break;
            case UiGlyph.Info:
                graphics.DrawEllipse(pen, 4, 4, 16, 16);
                graphics.DrawLine(pen, 12, 11, 12, 17);
                graphics.FillEllipse(brush, 11, 6.5F, 2, 2);
                break;
            case UiGlyph.Cut:
                graphics.DrawEllipse(pen, 4, 15, 5, 5);
                graphics.DrawEllipse(pen, 15, 15, 5, 5);
                graphics.DrawLine(pen, 8, 15.5F, 17.5F, 3.5F);
                graphics.DrawLine(pen, 16, 15.5F, 6.5F, 3.5F);
                break;
            case UiGlyph.Copy:
                DrawRoundedRectangle(graphics, pen, new RectangleF(8.5F, 3.5F, 12, 13), 2.5F);
                graphics.DrawLines(pen,
                [
                    new PointF(15.5F, 20.5F),
                    new PointF(3.5F, 20.5F),
                    new PointF(3.5F, 7.5F)
                ]);
                break;
            case UiGlyph.Paste:
                DrawRoundedRectangle(graphics, pen, new RectangleF(4.5F, 5.5F, 15, 15), 2.5F);
                DrawRoundedRectangle(graphics, pen, new RectangleF(8.5F, 2.5F, 7, 4.5F), 1.5F);
                graphics.DrawLine(pen, 8.5F, 12, 15.5F, 12);
                graphics.DrawLine(pen, 8.5F, 16, 13.5F, 16);
                break;
            case UiGlyph.Rename:
                graphics.DrawLines(pen, [new PointF(4, 20), new PointF(4.5F, 16), new PointF(15.5F, 5), new PointF(19, 8.5F), new PointF(8, 19.5F)]);
                graphics.DrawLine(pen, 13, 7.5F, 16.5F, 11);
                break;
            case UiGlyph.SelectAll:
                DrawDashedFrame(graphics, pen);
                graphics.DrawLines(pen, [new PointF(8.5F, 12), new PointF(11, 14.5F), new PointF(15.5F, 9)]);
                break;
            case UiGlyph.Invert:
                graphics.DrawRectangle(pen, 3.5F, 3.5F, 12, 12);
                using (var fill = new GraphicsPath())
                {
                    fill.AddRectangle(new RectangleF(8.5F, 8.5F, 12, 12));
                    graphics.FillPath(brush, fill);
                }
                break;
            case UiGlyph.Properties:
                DrawRoundedRectangle(graphics, pen, new RectangleF(3.5F, 3.5F, 17, 17), 2.5F);
                graphics.DrawLine(pen, 7.5F, 9, 16.5F, 9);
                graphics.DrawLine(pen, 7.5F, 12.5F, 16.5F, 12.5F);
                graphics.DrawLine(pen, 7.5F, 16, 13, 16);
                break;
            case UiGlyph.Exit:
                graphics.DrawLines(pen, [new PointF(13, 4), new PointF(4.5F, 4), new PointF(4.5F, 20), new PointF(13, 20)]);
                graphics.DrawLine(pen, 10, 12, 20, 12);
                graphics.DrawLines(pen, [new PointF(16.5F, 8.5F), new PointF(20, 12), new PointF(16.5F, 15.5F)]);
                break;
            case UiGlyph.Tree:
                graphics.DrawLine(pen, 5, 4, 5, 18);
                graphics.DrawLine(pen, 5, 8, 10, 8);
                graphics.DrawLine(pen, 5, 13, 10, 13);
                graphics.DrawLine(pen, 5, 18, 10, 18);
                graphics.DrawRectangle(pen, 11, 5.5F, 8, 5);
                graphics.DrawLine(pen, 11, 13, 19, 13);
                graphics.DrawLine(pen, 11, 18, 19, 18);
                break;
            case UiGlyph.Queue:
                graphics.DrawLine(pen, 3.5F, 6, 20.5F, 6);
                graphics.DrawLine(pen, 3.5F, 12, 20.5F, 12);
                graphics.DrawLine(pen, 3.5F, 18, 20.5F, 18);
                graphics.FillEllipse(brush, 5.5F, 9.5F, 5, 5);
                break;
            case UiGlyph.Log:
                DrawPage(graphics, pen);
                graphics.DrawLine(pen, 9, 11, 16, 11);
                graphics.DrawLine(pen, 9, 14.5F, 16, 14.5F);
                graphics.DrawLine(pen, 9, 18, 13, 18);
                break;
            case UiGlyph.Hidden:
                graphics.DrawArc(pen, 2.5F, 6, 19, 12, 175, 190);
                graphics.DrawArc(pen, 2.5F, 6, 19, 12, 5, 170);
                graphics.DrawEllipse(pen, 9.5F, 9.5F, 5, 5);
                graphics.DrawLine(pen, 4, 20, 20, 4);
                break;
            case UiGlyph.Theme:
                graphics.DrawEllipse(pen, 4, 4, 16, 16);
                using (var half = new GraphicsPath())
                {
                    half.AddArc(4, 4, 16, 16, 90, 180);
                    half.CloseFigure();
                    graphics.FillPath(brush, half);
                }
                break;
            case UiGlyph.Connect:
                DrawPlug(graphics, pen, connected: true);
                break;
            case UiGlyph.Disconnect:
                DrawPlug(graphics, pen, connected: false);
                break;
            case UiGlyph.Stop:
            case UiGlyph.Close:
                if (glyph == UiGlyph.Stop)
                {
                    graphics.DrawEllipse(pen, 4, 4, 16, 16);
                    graphics.DrawLine(pen, 8.5F, 8.5F, 15.5F, 15.5F);
                    graphics.DrawLine(pen, 15.5F, 8.5F, 8.5F, 15.5F);
                }
                else
                {
                    graphics.DrawLine(pen, 5.5F, 5.5F, 18.5F, 18.5F);
                    graphics.DrawLine(pen, 18.5F, 5.5F, 5.5F, 18.5F);
                }
                break;
            case UiGlyph.Speed:
                graphics.DrawArc(pen, 3, 5, 18, 18, 180, 180);
                graphics.DrawLine(pen, 12, 14, 17, 9);
                graphics.FillEllipse(brush, 10.8F, 12.8F, 2.4F, 2.4F);
                graphics.DrawLine(pen, 3, 14, 5, 14);
                graphics.DrawLine(pen, 19, 14, 21, 14);
                break;
            case UiGlyph.Profiles:
                graphics.DrawLine(pen, 4, 7, 20, 7);
                graphics.DrawLine(pen, 4, 12, 20, 12);
                graphics.DrawLine(pen, 4, 17, 20, 17);
                graphics.FillEllipse(brush, 13.5F, 4.5F, 5, 5);
                graphics.FillEllipse(brush, 6.5F, 9.5F, 5, 5);
                graphics.FillEllipse(brush, 11.5F, 14.5F, 5, 5);
                break;
            case UiGlyph.Schedule:
                DrawRoundedRectangle(graphics, pen, new RectangleF(3.5F, 5.5F, 17, 15), 2.5F);
                graphics.DrawLine(pen, 3.5F, 10.5F, 20.5F, 10.5F);
                graphics.DrawLine(pen, 8, 3, 8, 7.5F);
                graphics.DrawLine(pen, 16, 3, 16, 7.5F);
                graphics.FillEllipse(brush, 7, 13.5F, 2.6F, 2.6F);
                graphics.FillEllipse(brush, 14.4F, 13.5F, 2.6F, 2.6F);
                break;
            case UiGlyph.History:
                graphics.DrawArc(pen, 4, 4, 16, 16, 60, 285);
                graphics.DrawLines(pen, [new PointF(4, 4), new PointF(4, 9), new PointF(9, 9)]);
                graphics.DrawLines(pen, [new PointF(12, 8), new PointF(12, 12.5F), new PointF(15.5F, 14.5F)]);
                break;
            case UiGlyph.Favorite:
                graphics.FillPolygon(brush, StarPoints());
                break;
            case UiGlyph.Key:
                graphics.DrawEllipse(pen, 3.5F, 8, 8, 8);
                graphics.DrawLine(pen, 11.5F, 12, 20.5F, 12);
                graphics.DrawLine(pen, 17, 12, 17, 16);
                graphics.DrawLine(pen, 20, 12, 20, 15);
                break;
            case UiGlyph.Shield:
                using (var path = new GraphicsPath())
                {
                    path.AddLines(
                    [
                        new PointF(12, 3), new PointF(20, 6), new PointF(20, 12),
                        new PointF(12, 21), new PointF(4, 12), new PointF(4, 6)
                    ]);
                    path.CloseFigure();
                    graphics.DrawPath(pen, path);
                }
                graphics.DrawLines(pen, [new PointF(8.5F, 11.5F), new PointF(11, 14), new PointF(15.5F, 8.5F)]);
                break;
            case UiGlyph.Cloud:
                graphics.DrawArc(pen, 5, 5, 11, 11, 160, 220);
                graphics.DrawArc(pen, 12, 10, 9, 9, 250, 190);
                graphics.DrawLine(pen, 6.5F, 17.5F, 16.5F, 17.5F);
                graphics.DrawArc(pen, 2.5F, 10, 9, 9, 100, 190);
                break;
            case UiGlyph.Server:
                DrawRoundedRectangle(graphics, pen, new RectangleF(3.5F, 4, 17, 6.5F), 1.8F);
                DrawRoundedRectangle(graphics, pen, new RectangleF(3.5F, 13.5F, 17, 6.5F), 1.8F);
                graphics.FillEllipse(brush, 6.5F, 6.2F, 2.2F, 2.2F);
                graphics.FillEllipse(brush, 6.5F, 15.7F, 2.2F, 2.2F);
                break;
            case UiGlyph.Download:
                graphics.DrawLine(pen, 12, 3.5F, 12, 15);
                graphics.DrawLines(pen, [new PointF(7, 10), new PointF(12, 15), new PointF(17, 10)]);
                graphics.DrawLines(pen, [new PointF(4, 16.5F), new PointF(4, 20.5F), new PointF(20, 20.5F), new PointF(20, 16.5F)]);
                break;
            case UiGlyph.Upload:
                graphics.DrawLine(pen, 12, 15, 12, 3.5F);
                graphics.DrawLines(pen, [new PointF(7, 8.5F), new PointF(12, 3.5F), new PointF(17, 8.5F)]);
                graphics.DrawLines(pen, [new PointF(4, 16.5F), new PointF(4, 20.5F), new PointF(20, 20.5F), new PointF(20, 16.5F)]);
                break;
            case UiGlyph.Keyboard:
                DrawRoundedRectangle(graphics, pen, new RectangleF(2.5F, 6.5F, 19, 11), 2.5F);
                graphics.DrawLine(pen, 6, 10, 6.6F, 10);
                graphics.DrawLine(pen, 9.5F, 10, 10.1F, 10);
                graphics.DrawLine(pen, 13, 10, 13.6F, 10);
                graphics.DrawLine(pen, 16.5F, 10, 17.1F, 10);
                graphics.DrawLine(pen, 8, 14, 16, 14);
                break;
            case UiGlyph.Documentation:
                graphics.DrawLines(pen, [new PointF(12, 6.5F), new PointF(12, 20)]);
                graphics.DrawLines(pen, [new PointF(12, 6.5F), new PointF(7, 4), new PointF(3.5F, 4), new PointF(3.5F, 17.5F), new PointF(7, 17.5F), new PointF(12, 20)]);
                graphics.DrawLines(pen, [new PointF(12, 6.5F), new PointF(17, 4), new PointF(20.5F, 4), new PointF(20.5F, 17.5F), new PointF(17, 17.5F), new PointF(12, 20)]);
                break;
            case UiGlyph.Bug:
                DrawRoundedRectangle(graphics, pen, new RectangleF(7.5F, 7, 9, 12), 4.5F);
                graphics.DrawLine(pen, 3.5F, 10, 7.5F, 11.5F);
                graphics.DrawLine(pen, 3.5F, 18, 7.5F, 16);
                graphics.DrawLine(pen, 20.5F, 10, 16.5F, 11.5F);
                graphics.DrawLine(pen, 20.5F, 18, 16.5F, 16);
                graphics.DrawLine(pen, 3.5F, 14, 7.5F, 14);
                graphics.DrawLine(pen, 20.5F, 14, 16.5F, 14);
                graphics.DrawLine(pen, 9, 5, 10.5F, 7.5F);
                graphics.DrawLine(pen, 15, 5, 13.5F, 7.5F);
                break;
            case UiGlyph.Checksum:
                graphics.DrawLine(pen, 8.5F, 3.5F, 6.5F, 20.5F);
                graphics.DrawLine(pen, 17.5F, 3.5F, 15.5F, 20.5F);
                graphics.DrawLine(pen, 3.5F, 9, 20, 9);
                graphics.DrawLine(pen, 3.5F, 15, 20, 15);
                break;
            case UiGlyph.Diagnostics:
                graphics.DrawLines(pen,
                [
                    new PointF(2.5F, 12), new PointF(7, 12), new PointF(9.5F, 5.5F),
                    new PointF(13.5F, 18.5F), new PointF(16, 12), new PointF(21.5F, 12)
                ]);
                break;
            case UiGlyph.Link:
                graphics.DrawArc(pen, 2.5F, 8.5F, 10, 7, 90, 180);
                graphics.DrawArc(pen, 11.5F, 8.5F, 10, 7, 270, 180);
                graphics.DrawLine(pen, 8, 12, 16, 12);
                break;
            case UiGlyph.Layers:
                graphics.DrawPolygon(pen, [new PointF(12, 3.5F), new PointF(21, 8), new PointF(12, 12.5F), new PointF(3, 8)]);
                graphics.DrawLines(pen, [new PointF(3, 12), new PointF(12, 16.5F), new PointF(21, 12)]);
                graphics.DrawLines(pen, [new PointF(3, 16), new PointF(12, 20.5F), new PointF(21, 16)]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(glyph), glyph, "Unknown UI glyph.");
        }
    }

    private static PointF[] StarPoints()
    {
        var points = new PointF[10];
        for (var index = 0; index < 10; index++)
        {
            var radius = index % 2 == 0 ? 9F : 3.9F;
            var angle = (-Math.PI / 2) + (index * Math.PI / 5);
            points[index] = new PointF(
                12F + (float)(radius * Math.Cos(angle)),
                12F + (float)(radius * Math.Sin(angle)));
        }

        return points;
    }

    private static void DrawPage(Graphics graphics, Pen pen)
    {
        graphics.DrawLines(pen,
        [
            new PointF(6, 3.5F), new PointF(14.5F, 3.5F), new PointF(19, 8),
            new PointF(19, 20.5F), new PointF(6, 20.5F), new PointF(6, 3.5F)
        ]);
        graphics.DrawLines(pen, [new PointF(14.5F, 3.5F), new PointF(14.5F, 8), new PointF(19, 8)]);
    }

    private static void DrawPlug(Graphics graphics, Pen pen, bool connected)
    {
        graphics.DrawLine(pen, 3.5F, 20.5F, 8.5F, 15.5F);
        graphics.DrawLine(pen, 20.5F, 3.5F, 15.5F, 8.5F);
        if (connected)
        {
            DrawRotatedSocket(graphics, pen, 8F, 16F, 45F);
            DrawRotatedSocket(graphics, pen, 16F, 8F, 225F);
            return;
        }

        DrawRotatedSocket(graphics, pen, 6.5F, 17.5F, 45F);
        DrawRotatedSocket(graphics, pen, 17.5F, 6.5F, 225F);
        graphics.DrawLine(pen, 9.5F, 13, 11, 11.5F);
        graphics.DrawLine(pen, 13, 14.5F, 14.5F, 13);
    }

    private static void DrawRotatedSocket(Graphics graphics, Pen pen, float centerX, float centerY, float degrees)
    {
        var state = graphics.Save();
        graphics.TranslateTransform(centerX, centerY);
        graphics.RotateTransform(degrees);
        DrawRoundedRectangle(graphics, pen, new RectangleF(-3.6F, -2.6F, 7.2F, 5.2F), 1.6F);
        graphics.DrawLine(pen, -1.2F, -2.6F, -1.2F, -5.2F);
        graphics.DrawLine(pen, 1.2F, -2.6F, 1.2F, -5.2F);
        graphics.Restore(state);
    }

    private static void DrawGear(Graphics graphics, Pen pen)
    {
        graphics.DrawEllipse(pen, 8.5F, 8.5F, 7, 7);
        for (var tooth = 0; tooth < 8; tooth++)
        {
            var angle = tooth * Math.PI / 4;
            var cos = (float)Math.Cos(angle);
            var sin = (float)Math.Sin(angle);
            graphics.DrawLine(
                pen,
                12F + (cos * 7.4F),
                12F + (sin * 7.4F),
                12F + (cos * 10F),
                12F + (sin * 10F));
        }
    }

    private static void DrawDashedFrame(Graphics graphics, Pen pen)
    {
        // Pen.DashPattern only reads back while the style is already Custom, so the dashed pass
        // borrows a clone rather than mutating and restoring the shared stroke.
        using var dashed = (Pen)pen.Clone();
        dashed.DashStyle = DashStyle.Custom;
        dashed.DashPattern = [2F, 1.6F];
        graphics.DrawRectangle(dashed, 3.5F, 3.5F, 17, 17);
    }

    private static void DrawRoundedRectangle(Graphics graphics, Pen pen, RectangleF bounds, float radius)
    {
        using var path = UiShapes.RoundedRectangle(bounds, radius);
        graphics.DrawPath(pen, path);
    }
}

/// <summary>Shared geometry so cards, pills, and icons all round their corners the same way.</summary>
internal static class UiShapes
{
    public static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var limit = Math.Min(bounds.Width, bounds.Height) / 2F;
        var corner = Math.Max(0.1F, Math.Min(radius, limit));
        var diameter = corner * 2F;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
