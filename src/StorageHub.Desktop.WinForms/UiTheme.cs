using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace StorageHub.Desktop;

public static class StorageHubTheme
{
    public static DesktopAppearance Appearance => DesktopAppearanceService.Appearance;
    public static DesktopAppearance EffectiveAppearance => DesktopAppearanceService.EffectiveAppearance;
    internal static StorageHubPalette CurrentPalette => PaletteFor(EffectiveAppearance);
    public static Color Canvas => CurrentPalette.Canvas;
    public static Color Surface => CurrentPalette.Surface;
    public static Color SurfaceMuted => CurrentPalette.SurfaceMuted;
    public static Color Border => CurrentPalette.Border;
    public static Color Text => CurrentPalette.Text;
    public static Color TextMuted => CurrentPalette.TextMuted;
    public static Color Primary => CurrentPalette.Primary;
    public static Color Success => CurrentPalette.Success;
    public static Color Warning => CurrentPalette.Warning;
    public static Color Danger => CurrentPalette.Danger;

    public static void SetAppearance(DesktopAppearance appearance) => DesktopAppearanceService.SetAppearance(appearance);

    public static Font CreateSectionFont() => new("Segoe UI Semibold", 10F, FontStyle.Regular, GraphicsUnit.Point);

    public static void ConfigureList(ListView list)
    {
        ArgumentNullException.ThrowIfNull(list);
        list.BackColor = Surface;
        list.ForeColor = Text;
        list.OwnerDraw = true;
        list.DrawColumnHeader -= DrawListColumnHeader;
        list.DrawColumnHeader += DrawListColumnHeader;
        list.DrawItem -= DrawListItem;
        list.DrawItem += DrawListItem;
        list.DrawSubItem -= DrawListSubItem;
        list.DrawSubItem += DrawListSubItem;
        list.Resize -= ListResized;
        list.Resize += ListResized;
        FillListHeader(list);
    }

    private static void DrawListItem(object? sender, DrawListViewItemEventArgs e)
    {
        // In Details view each cell is painted by DrawSubItem. Asking the native control to
        // paint the complete row here as well can abort the first paint pass for virtual lists,
        // leaving rows blank until selection invalidates them individually.
        if (e.Item.ListView?.View != View.Details)
        {
            e.DrawDefault = true;
        }
    }

    private static void DrawListSubItem(object? sender, DrawListViewSubItemEventArgs e) => e.DrawDefault = true;

    private static void ListResized(object? sender, EventArgs e)
    {
        if (sender is ListView list)
        {
            FillListHeader(list);
        }
    }

    private static void DrawListColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using var background = new SolidBrush(SurfaceMuted);
        using var border = new Pen(Border);
        e.Graphics.FillRectangle(background, e.Bounds);
        e.Graphics.DrawLine(border, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        TextRenderer.DrawText(
            e.Graphics,
            e.Header?.Text ?? string.Empty,
            e.Font,
            Rectangle.Inflate(e.Bounds, -6, 0),
            Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private static void FillListHeader(ListView list)
    {
        if (list.View != View.Details || list.Columns.Count == 0 || list.ClientSize.Width == 0)
        {
            return;
        }

        var trailing = list.Columns[^1];
        var preceding = list.Columns.Cast<ColumnHeader>().Take(list.Columns.Count - 1).Sum(column => column.Width);
        trailing.Width = Math.Max(80, list.ClientSize.Width - preceding - SystemInformation.VerticalScrollBarWidth - 4);
    }

    public static void StylePrimaryButton(Button button)
    {
        ArgumentNullException.ThrowIfNull(button);
        button.AutoSize = true;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.Padding = new Padding(12, 5, 12, 5);
        button.MinimumSize = new Size(90, 34);
        button.EnabledChanged -= PrimaryButtonEnabledChanged;
        button.EnabledChanged += PrimaryButtonEnabledChanged;
        ApplyPrimaryButtonState(button);
    }

    public static void StyleSecondaryButton(Button button)
    {
        ArgumentNullException.ThrowIfNull(button);
        button.AutoSize = true;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Border;
        button.Padding = new Padding(10, 4, 10, 4);
        button.MinimumSize = new Size(84, 34);
        button.EnabledChanged -= SecondaryButtonEnabledChanged;
        button.EnabledChanged += SecondaryButtonEnabledChanged;
        ApplySecondaryButtonState(button);
    }

    private static void PrimaryButtonEnabledChanged(object? sender, EventArgs e)
    {
        if (sender is Button button)
        {
            ApplyPrimaryButtonState(button);
        }
    }

    private static void SecondaryButtonEnabledChanged(object? sender, EventArgs e)
    {
        if (sender is Button button)
        {
            ApplySecondaryButtonState(button);
        }
    }

    private static void ApplyPrimaryButtonState(Button button)
    {
        button.BackColor = button.Enabled ? Primary : SurfaceMuted;
        button.ForeColor = button.Enabled ? Color.White : CurrentPalette.DisabledText;
        button.Cursor = button.Enabled ? Cursors.Hand : Cursors.Default;
    }

    private static void ApplySecondaryButtonState(Button button)
    {
        button.BackColor = button.Enabled ? Surface : SurfaceMuted;
        button.ForeColor = button.Enabled ? Text : CurrentPalette.DisabledText;
        button.Cursor = button.Enabled ? Cursors.Hand : Cursors.Default;
    }

    public static void ConfigureTabs(TabControl tabs)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        var isWorkspaceTabs = string.Equals(tabs.AccessibleName, "Workspace tabs", StringComparison.Ordinal);
        tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        tabs.DrawItem -= DrawTab;
        if (!isWorkspaceTabs)
        {
            tabs.DrawItem += DrawTab;
        }
        // Workspace headers include a 16px icon and, for browser workspaces, a
        // 16px close target. Native sizing only measures the text, so reserve
        // enough horizontal padding for those renderer-owned elements.
        tabs.Padding = isWorkspaceTabs ? new Point(39, 5) : new Point(14, 4);
    }

    public static void Register(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        DesktopAppearanceService.RegisterWindow(form);
        form.HandleCreated -= FormHandleCreated;
        form.HandleCreated += FormHandleCreated;
    }

    public static void Apply(Control root, DesktopAppearance? previousAppearance = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        var current = CurrentPalette;
        var previous = PaletteFor(previousAppearance ?? EffectiveAppearance);
        ApplyControl(root, previous, current);
        root.Invalidate(true);
    }

    private static void ApplyControl(Control control, StorageHubPalette previous, StorageHubPalette current)
    {
        control.BackColor = MapColor(control.BackColor, previous, current);
        control.ForeColor = MapColor(control.ForeColor, previous, current);

        switch (control)
        {
            case Form form:
                form.BackColor = current.Canvas;
                form.HandleCreated -= FormHandleCreated;
                form.HandleCreated += FormHandleCreated;
                ApplyDarkTitleBar(form, EffectiveAppearance == DesktopAppearance.Dark);
                break;
            case TextBoxBase or ComboBox or NumericUpDown or DateTimePicker:
                control.BackColor = current.Input;
                control.ForeColor = current.Text;
                break;
            case TreeView tree:
                tree.BackColor = current.Surface;
                tree.ForeColor = current.Text;
                tree.LineColor = current.Border;
                break;
            case ListView list:
                ConfigureList(list);
                break;
            case DataGridView grid:
                ConfigureGrid(grid, current);
                break;
            case TabControl tabs:
                ConfigureTabs(tabs);
                break;
            case TabPage page:
                page.BackColor = current.Surface;
                page.ForeColor = current.Text;
                break;
            case Button button when button.FlatStyle == FlatStyle.Flat:
                button.FlatAppearance.BorderColor = current.Border;
                if (button.BackColor == previous.Primary)
                {
                    button.BackColor = current.Primary;
                    button.ForeColor = Color.White;
                }
                break;
            case ToolStrip strip:
                strip.BackColor = current.Surface;
                strip.ForeColor = current.Text;
                strip.Renderer = DesktopAppearanceService.MenuRenderer;
                ApplyToolStripItems(strip.Items, current);
                break;
        }

        if (control.ContextMenuStrip is { } contextMenu)
        {
            contextMenu.BackColor = current.Surface;
            contextMenu.ForeColor = current.Text;
            contextMenu.Renderer = DesktopAppearanceService.MenuRenderer;
            ApplyToolStripItems(contextMenu.Items, current);
        }

        if (!control.Enabled)
        {
            control.ForeColor = current.DisabledText;
        }

        foreach (Control child in control.Controls)
        {
            ApplyControl(child, previous, current);
        }
    }

    private static void ConfigureGrid(DataGridView grid, StorageHubPalette palette)
    {
        grid.EnableHeadersVisualStyles = false;
        grid.BackgroundColor = palette.Surface;
        grid.GridColor = palette.Border;
        grid.DefaultCellStyle.BackColor = palette.Surface;
        grid.DefaultCellStyle.ForeColor = palette.Text;
        grid.DefaultCellStyle.SelectionBackColor = palette.Selection;
        grid.DefaultCellStyle.SelectionForeColor = palette.Text;
        grid.AlternatingRowsDefaultCellStyle.BackColor = palette.SurfaceMuted;
        grid.AlternatingRowsDefaultCellStyle.ForeColor = palette.Text;
        grid.ColumnHeadersDefaultCellStyle.BackColor = palette.SurfaceMuted;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = palette.Text;
        grid.RowHeadersDefaultCellStyle.BackColor = palette.SurfaceMuted;
        grid.RowHeadersDefaultCellStyle.ForeColor = palette.Text;
    }

    private static void ApplyToolStripItems(ToolStripItemCollection items, StorageHubPalette palette)
    {
        foreach (ToolStripItem item in items)
        {
            item.BackColor = palette.Surface;
            item.ForeColor = item.Enabled ? palette.Text : palette.DisabledText;
            if (item is ToolStripDropDownItem dropDown)
            {
                ApplyToolStripItems(dropDown.DropDownItems, palette);
            }
        }
    }

    private static Color MapColor(Color value, StorageHubPalette previous, StorageHubPalette current)
    {
        if (value == previous.Canvas) return current.Canvas;
        if (value == previous.Surface) return current.Surface;
        if (value == previous.SurfaceMuted) return current.SurfaceMuted;
        if (value == previous.Border) return current.Border;
        if (value == previous.Text) return current.Text;
        if (value == previous.TextMuted) return current.TextMuted;
        if (value == previous.Primary) return current.Primary;
        if (value == previous.Success) return current.Success;
        if (value == previous.Warning) return current.Warning;
        if (value == previous.Danger) return current.Danger;
        return value;
    }

    private static void DrawTab(object? sender, DrawItemEventArgs e)
    {
        if (sender is not TabControl tabs || e.Index < 0 || e.Index >= tabs.TabPages.Count)
        {
            return;
        }

        var selected = e.Index == tabs.SelectedIndex;
        using var background = new SolidBrush(selected ? Surface : SurfaceMuted);
        using var border = new Pen(Border);
        e.Graphics.FillRectangle(background, e.Bounds);
        e.Graphics.DrawRectangle(border, Rectangle.Inflate(e.Bounds, -1, -1));
        var page = tabs.TabPages[e.Index];
        var image = ResolveTabImage(tabs, page);
        var textSize = TextRenderer.MeasureText(
            page.Text,
            tabs.Font,
            Size.Empty,
            TextFormatFlags.NoPadding);
        var gap = image is null ? 0 : 7;
        var contentWidth = textSize.Width + gap + (image?.Width ?? 0);
        var contentLeft = e.Bounds.Left + Math.Max(8, (e.Bounds.Width - contentWidth) / 2);
        if (image is not null)
        {
            e.Graphics.DrawImage(
                image,
                contentLeft,
                e.Bounds.Top + (e.Bounds.Height - image.Height) / 2,
                image.Width,
                image.Height);
            contentLeft += image.Width + gap;
        }
        TextRenderer.DrawText(
            e.Graphics,
            page.Text,
            tabs.Font,
            new Rectangle(contentLeft, e.Bounds.Top, Math.Max(1, e.Bounds.Right - contentLeft - 6), e.Bounds.Height),
            Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private static Image? ResolveTabImage(TabControl tabs, TabPage page)
    {
        if (tabs.ImageList is not { } images)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(page.ImageKey))
        {
            var index = images.Images.IndexOfKey(page.ImageKey);
            return index >= 0 ? images.Images[index] : null;
        }

        return page.ImageIndex >= 0 && page.ImageIndex < images.Images.Count
            ? images.Images[page.ImageIndex]
            : null;
    }

    private static StorageHubPalette PaletteFor(DesktopAppearance appearance) =>
        appearance == DesktopAppearance.Dark
            ? new StorageHubPalette(
                Color.FromArgb(28, 29, 32), Color.FromArgb(37, 39, 43), Color.FromArgb(48, 51, 56),
                Color.FromArgb(75, 78, 85), Color.FromArgb(238, 239, 242), Color.FromArgb(178, 182, 190),
                Color.FromArgb(76, 139, 245), Color.FromArgb(54, 102, 166), Color.FromArgb(43, 77, 120),
                Color.FromArgb(33, 35, 39), Color.FromArgb(111, 115, 122),
                Color.FromArgb(74, 190, 132), Color.FromArgb(245, 180, 72), Color.FromArgb(244, 105, 112))
            : new StorageHubPalette(
                Color.FromArgb(243, 246, 250), Color.White, Color.FromArgb(238, 242, 247),
                Color.FromArgb(203, 210, 220), Color.FromArgb(32, 37, 45), Color.FromArgb(95, 104, 116),
                Color.FromArgb(24, 103, 192), Color.FromArgb(218, 232, 250), Color.FromArgb(195, 218, 247),
                Color.White, Color.FromArgb(145, 151, 160),
                Color.FromArgb(17, 135, 86), Color.FromArgb(176, 94, 0), Color.FromArgb(190, 45, 55));

    private static void ApplyDarkTitleBar(Form form, bool enabled)
    {
        if (!OperatingSystem.IsWindows() || !form.IsHandleCreated)
        {
            return;
        }

        var value = enabled ? 1 : 0;
        if (DwmSetWindowAttribute(form.Handle, 20, ref value, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(form.Handle, 19, ref value, sizeof(int));
        }
    }

    private static void FormHandleCreated(object? sender, EventArgs e)
    {
        if (sender is Form form)
        {
            Apply(form);
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    public static Color ParseAccent(string accentHex)
    {
        if (string.IsNullOrWhiteSpace(accentHex))
        {
            return Primary;
        }

        return ColorTranslator.FromHtml(accentHex);
    }

}

internal readonly record struct StorageHubPalette(
    Color Canvas,
    Color Surface,
    Color SurfaceMuted,
    Color Border,
    Color Text,
    Color TextMuted,
    Color Primary,
    Color Selection,
    Color SelectionPressed,
    Color Input,
    Color DisabledText,
    Color Success,
    Color Warning,
    Color Danger);

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
    Info
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
            case UiGlyph.File:
                graphics.DrawLines(pen,
                [
                    new PointF(6, 3),
                    new PointF(15, 3),
                    new PointF(19, 7),
                    new PointF(19, 21),
                    new PointF(6, 21),
                    new PointF(6, 3)
                ]);
                graphics.DrawLines(pen,
                [
                    new PointF(15, 3),
                    new PointF(15, 7),
                    new PointF(19, 7)
                ]);
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
            case UiGlyph.Terminal:
                graphics.DrawRectangle(pen, 3, 5, 18, 14);
                graphics.DrawLines(pen, [new PointF(7, 9), new PointF(10, 12), new PointF(7, 15)]);
                graphics.DrawLine(pen, 12, 15, 17, 15);
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
            case UiGlyph.Home:
                graphics.DrawLines(pen, [new PointF(3, 11), new PointF(12, 4), new PointF(21, 11)]);
                graphics.DrawLines(pen, [new PointF(6, 10), new PointF(6, 20), new PointF(18, 20), new PointF(18, 10)]);
                graphics.DrawRectangle(pen, 10, 14, 4, 6);
                break;
            case UiGlyph.Settings:
                graphics.DrawEllipse(pen, 8, 8, 8, 8);
                graphics.DrawEllipse(pen, 4, 4, 16, 16);
                graphics.DrawLine(pen, 12, 2, 12, 5);
                graphics.DrawLine(pen, 12, 19, 12, 22);
                graphics.DrawLine(pen, 2, 12, 5, 12);
                graphics.DrawLine(pen, 19, 12, 22, 12);
                break;
            case UiGlyph.Info:
                graphics.DrawEllipse(pen, 4, 4, 16, 16);
                graphics.DrawLine(pen, 12, 10, 12, 17);
                graphics.FillEllipse(brush, 11, 7, 2, 2);
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
