using System.ComponentModel;
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
    public static Color Elevated => CurrentPalette.Elevated;
    public static Color Border => CurrentPalette.Border;
    public static Color Text => CurrentPalette.Text;
    public static Color TextMuted => CurrentPalette.TextMuted;
    public static Color Primary => CurrentPalette.Primary;
    public static Color Success => CurrentPalette.Success;
    public static Color Warning => CurrentPalette.Warning;
    public static Color Danger => CurrentPalette.Danger;

    /// <summary>
    /// Low-saturation backgrounds for status callouts. A notice that hard-codes a pastel fill is
    /// unreadable once the palette flips, so every tinted surface resolves through the palette.
    /// </summary>
    public static Color SuccessTint => CurrentPalette.SuccessTint;
    public static Color WarningTint => CurrentPalette.WarningTint;
    public static Color DangerTint => CurrentPalette.DangerTint;
    public static Color Selection => CurrentPalette.Selection;

    static StorageHubTheme()
    {
        DesktopAppearanceService.AppearanceChanged += (_, _) => RefreshTrackedIcons();
    }

    public static void SetAppearance(DesktopAppearance appearance) => DesktopAppearanceService.SetAppearance(appearance);

    public static Font CreateSectionFont() => new("Segoe UI Semibold", 10F, FontStyle.Regular, GraphicsUnit.Point);

    /// <summary>
    /// Turns on the double buffering WinForms leaves off for DataGridView. Without it a grid that
    /// repaints on a timer visibly tears, which reads as the contents blinking even when the rows
    /// themselves are unchanged. The property is protected, so it is set through its descriptor.
    /// </summary>
    public static void ReduceFlicker(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (SystemInformation.TerminalServerSession)
        {
            // Double buffering is a pessimisation over a remote desktop connection.
            return;
        }

        var property = control.GetType().GetProperty(
            "DoubleBuffered",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        property?.SetValue(control, true, null);
    }

    public static void ConfigureList(ListView list)
    {
        ArgumentNullException.ThrowIfNull(list);
        list.BackColor = Surface;
        list.ForeColor = Text;
        list.OwnerDraw = true;
        list.BorderStyle = BorderStyle.None;
        list.DrawColumnHeader -= DrawListColumnHeader;
        list.DrawColumnHeader += DrawListColumnHeader;
        list.DrawItem -= DrawListItem;
        list.DrawItem += DrawListItem;
        list.DrawSubItem -= DrawListSubItem;
        list.DrawSubItem += DrawListSubItem;
        list.Resize -= ListResized;
        list.Resize += ListResized;
        // Column sizing can bring a scrollbar into existence, and a window that does not exist
        // yet cannot be themed, so the fill runs before the theming rather than after it.
        FillListHeader(list);
        ApplyNativeChrome(list);
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
            // Same order as setup: resize the columns, then theme whatever that produced. The
            // header and the scrollbars are created lazily, so both need the later pass.
            FillListHeader(list);
            ApplyNativeChrome(list);
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
            TextMuted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    /// <summary>
    /// Re-runs the trailing-column fill after a caller has set its own column widths.
    ///
    /// <see cref="ConfigureList"/> fills once, but a caller that then assigns fixed widths to
    /// every column undoes it, and the fill only ran again on resize. A list whose declared
    /// widths exceed a narrow window then keeps a horizontal scrollbar, which is the one piece
    /// of chrome the dark theme does not reach.
    /// </summary>
    public static void FitTrailingColumn(ListView list)
    {
        ArgumentNullException.ThrowIfNull(list);
        FillListHeader(list);
    }

    private static void FillListHeader(ListView list)
    {
        if (list.View != View.Details || list.Columns.Count == 0 || list.ClientSize.Width == 0)
        {
            return;
        }

        var trailing = list.Columns[^1];
        var preceding = list.Columns.Cast<ColumnHeader>().Take(list.Columns.Count - 1).Sum(column => column.Width);

        // Two pixels short of the client edge. ClientSize already accounts for a visible vertical
        // scrollbar and is unaffected by a horizontal one, so it is the width to divide up -- but
        // the control raises a horizontal scrollbar as soon as the column total merely *equals*
        // the client width, so filling exactly to the edge scrolls at every size. Reserving a
        // whole scrollbar's width instead would leave a strip of unpainted native header, which
        // is the bright block this fill exists to remove.
        var width = Math.Max(80, list.ClientSize.Width - preceding - 2);

        // Assigned even when the value is unchanged, and nudged first when it is. The control
        // re-evaluates its scroll range on a column assignment and at no other time, so a
        // horizontal scrollbar raised by an earlier, narrower layout survives any pass that
        // skips the write -- and that leftover scrollbar is the one piece of chrome the dark
        // theme does not reach.
        if (trailing.Width == width)
        {
            trailing.Width = width + 1;
        }

        trailing.Width = width;
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

    /// <summary>
    /// A secondary button that sits on the same row as an input field.
    ///
    /// The standalone minimum of 34 is set for dialog buttons that stand on their own; beside a
    /// single-line text box or spinner, which cannot grow past their font height, it left the
    /// button towering over the field it belongs to. This variant matches the field instead.
    /// </summary>
    public static void StyleInlineButton(Button button)
    {
        ArgumentNullException.ThrowIfNull(button);
        StyleSecondaryButton(button);
        button.Padding = new Padding(10, 2, 10, 2);
        button.AutoSize = false;
        // The font and DPI are not final until the button has a parent, and the secondary style
        // leaves AutoSize on, so the height is fixed once the button joins its row and again if
        // it moves to a display with different scaling.
        ResizeInlineButton(button);
        button.ParentChanged -= InlineButtonMetricsChanged;
        button.ParentChanged += InlineButtonMetricsChanged;
        button.DpiChangedAfterParent -= InlineButtonMetricsChanged;
        button.DpiChangedAfterParent += InlineButtonMetricsChanged;
        button.FontChanged -= InlineButtonMetricsChanged;
        button.FontChanged += InlineButtonMetricsChanged;
    }

    private static void InlineButtonMetricsChanged(object? sender, EventArgs e)
    {
        if (sender is Button button)
        {
            ResizeInlineButton(button);
        }
    }

    private static void ResizeInlineButton(Button button)
    {
        var height = MeasureInputHeight(button);
        var width = Math.Max(
            button.MinimumSize.Width,
            TextRenderer.MeasureText(button.Text, button.Font).Width + button.Padding.Horizontal + 10);
        button.MinimumSize = new Size(0, height);
        button.MaximumSize = new Size(0, height);
        button.Size = new Size(width, height);
    }

    /// <summary>
    /// The height a single-line input settles at beside this control: WinForms sizes a text box
    /// or spinner from the font plus a fixed border, so measuring the font is what keeps an
    /// inline button aligned at every DPI rather than at the one it was designed on.
    /// </summary>
    private static int MeasureInputHeight(Control control) =>
        control.Font.Height + (int)Math.Round(6 * control.DeviceDpi / 96D);

    /// <summary>A secondary button whose action destroys data, so it reads as dangerous at rest.</summary>
    public static void StyleDangerButton(Button button)
    {
        ArgumentNullException.ThrowIfNull(button);
        StyleSecondaryButton(button);
        button.EnabledChanged -= SecondaryButtonEnabledChanged;
        button.EnabledChanged -= DangerButtonEnabledChanged;
        button.EnabledChanged += DangerButtonEnabledChanged;
        ApplyDangerButtonState(button);
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

    private static void DangerButtonEnabledChanged(object? sender, EventArgs e)
    {
        if (sender is Button button)
        {
            ApplyDangerButtonState(button);
        }
    }

    private static void ApplyPrimaryButtonState(Button button)
    {
        var palette = CurrentPalette;
        button.BackColor = button.Enabled ? palette.Primary : palette.SurfaceMuted;
        button.ForeColor = button.Enabled ? Color.White : palette.DisabledText;
        button.FlatAppearance.MouseOverBackColor = palette.PrimaryHover;
        button.FlatAppearance.MouseDownBackColor = palette.PrimaryPressed;
        button.Cursor = button.Enabled ? Cursors.Hand : Cursors.Default;
    }

    private static void ApplySecondaryButtonState(Button button)
    {
        var palette = CurrentPalette;
        button.BackColor = button.Enabled ? palette.Surface : palette.SurfaceMuted;
        button.ForeColor = button.Enabled ? palette.Text : palette.DisabledText;
        button.FlatAppearance.BorderColor = palette.Border;
        button.FlatAppearance.MouseOverBackColor = palette.SurfaceMuted;
        button.FlatAppearance.MouseDownBackColor = palette.Elevated;
        button.Cursor = button.Enabled ? Cursors.Hand : Cursors.Default;
    }

    private static void ApplyDangerButtonState(Button button)
    {
        var palette = CurrentPalette;
        button.BackColor = button.Enabled ? palette.DangerTint : palette.SurfaceMuted;
        button.ForeColor = button.Enabled ? palette.Danger : palette.DisabledText;
        button.FlatAppearance.BorderColor = button.Enabled ? palette.Danger : palette.Border;
        button.FlatAppearance.MouseOverBackColor = button.Enabled ? palette.Danger : palette.SurfaceMuted;
        button.FlatAppearance.MouseDownBackColor = palette.Danger;
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
        tabs.Padding = isWorkspaceTabs ? new Point(39, 5) : new Point(16, 6);
        if (tabs is ThemedTabControl themed)
        {
            themed.Invalidate(true);
        }
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
                ApplyNativeChrome(control);
                if (control is NumericUpDown spinner && spinner.Controls.Count > 0)
                {
                    // The spin buttons are a separate child control that keeps painting on the
                    // stock window background, which is a bright block on a dark input.
                    spinner.Controls[0].BackColor = current.Input;
                    spinner.Controls[0].ForeColor = current.Text;
                }
                break;
            case TreeView tree:
                tree.BackColor = current.Surface;
                tree.ForeColor = current.Text;
                tree.LineColor = current.Border;
                ApplyNativeChrome(tree);
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
                    button.FlatAppearance.MouseOverBackColor = current.PrimaryHover;
                    button.FlatAppearance.MouseDownBackColor = current.PrimaryPressed;
                }
                break;
            case ToolStrip strip:
                strip.BackColor = current.Surface;
                strip.ForeColor = current.Text;
                strip.Renderer = DesktopAppearanceService.MenuRenderer;
                ApplyToolStripItems(strip.Items, current);
                break;
            case ScrollableControl scrollable when scrollable is not TabPage:
                ApplyNativeChrome(scrollable);
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
        grid.BorderStyle = BorderStyle.None;
        grid.BackgroundColor = palette.Surface;
        grid.GridColor = palette.Border;
        grid.DefaultCellStyle.BackColor = palette.Surface;
        grid.DefaultCellStyle.ForeColor = palette.Text;
        grid.DefaultCellStyle.SelectionBackColor = palette.Selection;
        grid.DefaultCellStyle.SelectionForeColor = palette.Text;
        grid.AlternatingRowsDefaultCellStyle.BackColor = palette.SurfaceMuted;
        grid.AlternatingRowsDefaultCellStyle.ForeColor = palette.Text;
        grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = palette.Selection;
        grid.AlternatingRowsDefaultCellStyle.SelectionForeColor = palette.Text;
        grid.ColumnHeadersDefaultCellStyle.BackColor = palette.SurfaceMuted;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = palette.TextMuted;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = palette.SurfaceMuted;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = palette.TextMuted;
        grid.RowHeadersDefaultCellStyle.BackColor = palette.SurfaceMuted;
        grid.RowHeadersDefaultCellStyle.ForeColor = palette.Text;
        ApplyNativeChrome(grid);
    }

    private static void ApplyToolStripItems(ToolStripItemCollection items, StorageHubPalette palette)
    {
        foreach (ToolStripItem item in items)
        {
            item.BackColor = palette.Surface;
            item.ForeColor = item.Enabled ? palette.Text : palette.DisabledText;
            item.EnabledChanged -= ToolStripItemEnabledChanged;
            item.EnabledChanged += ToolStripItemEnabledChanged;
            if (item is ToolStripDropDownItem dropDown)
            {
                ApplyToolStripItems(dropDown.DropDownItems, palette);
            }
        }
    }

    private static void ToolStripItemEnabledChanged(object? sender, EventArgs e)
    {
        if (sender is ToolStripItem item)
        {
            item.ForeColor = item.Enabled ? CurrentPalette.Text : CurrentPalette.DisabledText;
        }
    }

    private static Color MapColor(Color value, StorageHubPalette previous, StorageHubPalette current)
    {
        if (value == previous.Canvas) return current.Canvas;
        if (value == previous.Surface) return current.Surface;
        if (value == previous.SurfaceMuted) return current.SurfaceMuted;
        if (value == previous.Elevated) return current.Elevated;
        if (value == previous.Border) return current.Border;
        if (value == previous.Text) return current.Text;
        if (value == previous.TextMuted) return current.TextMuted;
        if (value == previous.DisabledText) return current.DisabledText;
        if (value == previous.Primary) return current.Primary;
        if (value == previous.Success) return current.Success;
        if (value == previous.Warning) return current.Warning;
        if (value == previous.Danger) return current.Danger;
        if (value == previous.SuccessTint) return current.SuccessTint;
        if (value == previous.WarningTint) return current.WarningTint;
        if (value == previous.DangerTint) return current.DangerTint;
        if (value == previous.Selection) return current.Selection;
        if (value == previous.Input) return current.Input;
        return value;
    }

    private static void DrawTab(object? sender, DrawItemEventArgs e)
    {
        if (sender is not TabControl tabs || e.Index < 0 || e.Index >= tabs.TabPages.Count)
        {
            return;
        }

        var palette = CurrentPalette;
        var selected = e.Index == tabs.SelectedIndex;
        var bounds = e.Bounds;
        using var background = new SolidBrush(selected ? palette.Surface : palette.SurfaceMuted);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var shape = UiShapes.RoundedRectangle(
                   new RectangleF(bounds.Left, bounds.Top, bounds.Width - 1, bounds.Height + 6),
                   5F))
        {
            e.Graphics.FillPath(background, shape);
            using var outline = new Pen(selected ? palette.Border : palette.SurfaceMuted);
            e.Graphics.DrawPath(outline, shape);
        }

        if (selected)
        {
            // A 2px accent cap is the only cue that survives at every DPI once the tab and the
            // page below it share one surface colour.
            using var accent = new SolidBrush(palette.Primary);
            e.Graphics.FillRectangle(accent, bounds.Left + 3, bounds.Top + 1, bounds.Width - 7, 2);
        }

        e.Graphics.SmoothingMode = SmoothingMode.Default;
        var page = tabs.TabPages[e.Index];
        var image = ResolveTabImage(tabs, page);
        var textSize = TextRenderer.MeasureText(
            page.Text,
            tabs.Font,
            Size.Empty,
            TextFormatFlags.NoPadding);
        var gap = image is null ? 0 : 7;
        var contentWidth = textSize.Width + gap + (image?.Width ?? 0);
        var contentLeft = bounds.Left + Math.Max(8, (bounds.Width - contentWidth) / 2);
        if (image is not null)
        {
            e.Graphics.DrawImage(
                image,
                contentLeft,
                bounds.Top + (bounds.Height - image.Height) / 2,
                image.Width,
                image.Height);
            contentLeft += image.Width + gap;
        }
        TextRenderer.DrawText(
            e.Graphics,
            page.Text,
            tabs.Font,
            new Rectangle(contentLeft, bounds.Top, Math.Max(1, bounds.Right - contentLeft - 6), bounds.Height),
            selected ? palette.Text : palette.TextMuted,
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
        appearance == DesktopAppearance.Dark ? DarkPalette : LightPalette;

    private static readonly StorageHubPalette DarkPalette = new(
        Canvas: Color.FromArgb(22, 24, 29),
        Surface: Color.FromArgb(30, 33, 39),
        SurfaceMuted: Color.FromArgb(39, 43, 51),
        Elevated: Color.FromArgb(46, 50, 59),
        Border: Color.FromArgb(58, 63, 74),
        Text: Color.FromArgb(232, 234, 240),
        TextMuted: Color.FromArgb(160, 167, 180),
        Primary: Color.FromArgb(76, 139, 245),
        PrimaryHover: Color.FromArgb(107, 160, 248),
        PrimaryPressed: Color.FromArgb(56, 114, 214),
        Selection: Color.FromArgb(43, 74, 122),
        SelectionPressed: Color.FromArgb(35, 57, 92),
        Input: Color.FromArgb(20, 22, 26),
        DisabledText: Color.FromArgb(107, 114, 128),
        Success: Color.FromArgb(74, 190, 132),
        Warning: Color.FromArgb(240, 176, 69),
        Danger: Color.FromArgb(244, 105, 111),
        SuccessTint: Color.FromArgb(27, 47, 39),
        WarningTint: Color.FromArgb(51, 41, 26),
        DangerTint: Color.FromArgb(51, 32, 31));

    private static readonly StorageHubPalette LightPalette = new(
        Canvas: Color.FromArgb(244, 246, 250),
        Surface: Color.White,
        SurfaceMuted: Color.FromArgb(237, 241, 247),
        Elevated: Color.FromArgb(249, 251, 253),
        Border: Color.FromArgb(211, 218, 228),
        Text: Color.FromArgb(30, 36, 45),
        TextMuted: Color.FromArgb(92, 102, 116),
        Primary: Color.FromArgb(24, 103, 192),
        PrimaryHover: Color.FromArgb(41, 123, 214),
        PrimaryPressed: Color.FromArgb(18, 82, 154),
        Selection: Color.FromArgb(218, 232, 250),
        SelectionPressed: Color.FromArgb(195, 218, 247),
        Input: Color.FromArgb(253, 254, 255),
        DisabledText: Color.FromArgb(146, 154, 165),
        Success: Color.FromArgb(17, 135, 86),
        Warning: Color.FromArgb(176, 94, 0),
        Danger: Color.FromArgb(190, 45, 55),
        SuccessTint: Color.FromArgb(230, 246, 238),
        WarningTint: Color.FromArgb(255, 244, 224),
        DangerTint: Color.FromArgb(253, 236, 236));

    /// <summary>
    /// Paints a card: a rounded, filled panel with a hairline border, and optionally a coloured
    /// rail down its leading edge. Callers pass the full client rectangle.
    /// </summary>
    public static void PaintCard(Graphics graphics, Rectangle bounds, Color? accent = null)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        if (bounds.Width <= 2 || bounds.Height <= 2)
        {
            return;
        }

        var palette = CurrentPalette;
        var previousMode = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var shape = new RectangleF(bounds.Left + 0.5F, bounds.Top + 0.5F, bounds.Width - 1.5F, bounds.Height - 1.5F);
        using (var path = UiShapes.RoundedRectangle(shape, 7F))
        {
            using var fill = new SolidBrush(palette.Surface);
            using var outline = new Pen(palette.Border);
            graphics.FillPath(fill, path);
            if (accent is { } rail)
            {
                var clip = graphics.Clip;
                graphics.SetClip(path);
                using var railBrush = new SolidBrush(rail);
                graphics.FillRectangle(railBrush, bounds.Left, bounds.Top, 3, bounds.Height);
                graphics.Clip = clip;
            }

            graphics.DrawPath(outline, path);
        }

        graphics.SmoothingMode = previousMode;
    }

    public static Color ToneColor(UiIconTone tone)
    {
        var palette = CurrentPalette;
        return tone switch
        {
            UiIconTone.Muted => palette.TextMuted,
            UiIconTone.Primary => palette.Primary,
            UiIconTone.OnPrimary => Color.White,
            UiIconTone.Success => palette.Success,
            UiIconTone.Warning => palette.Warning,
            UiIconTone.Danger => palette.Danger,
            _ => palette.Text
        };
    }

    /// <summary>
    /// Creates an icon and keeps repainting it in the palette's colours. Icons are rasterised once
    /// with a baked-in colour, so without this a menu that was built in one appearance keeps dark
    /// glyphs after a switch to dark mode and becomes invisible.
    /// </summary>
    public static Bitmap TrackIcon(
        ToolStripItem item,
        UiGlyph glyph,
        int size = 16,
        UiIconTone tone = UiIconTone.Text,
        float dpiScale = 1F)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Track(item, image => item.Image = image, glyph, size, tone, dpiScale);
    }

    public static Bitmap TrackIcon(
        ButtonBase button,
        UiGlyph glyph,
        int size = 18,
        UiIconTone tone = UiIconTone.Text,
        float dpiScale = 1F)
    {
        ArgumentNullException.ThrowIfNull(button);
        return Track(button, image => button.Image = image, glyph, size, tone, dpiScale);
    }

    public static Bitmap TrackIcon(
        PictureBox picture,
        UiGlyph glyph,
        int size = 20,
        UiIconTone tone = UiIconTone.Text,
        float dpiScale = 1F)
    {
        ArgumentNullException.ThrowIfNull(picture);
        return Track(picture, image => picture.Image = image, glyph, size, tone, dpiScale);
    }

    /// <summary>
    /// Tracks an icon that is drawn by hand rather than assigned to a control property. The owner
    /// decides when the icon dies; tracking stops once it is disposed or collected.
    /// </summary>
    public static Bitmap TrackIcon(
        Component owner,
        Action<Image> apply,
        UiGlyph glyph,
        int size,
        UiIconTone tone = UiIconTone.Text,
        float dpiScale = 1F)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(apply);
        return Track(owner, apply, glyph, size, tone, dpiScale);
    }

    private static readonly List<TrackedIcon> TrackedIcons = [];
    private static readonly Lock TrackedIconsLock = new();

    private static Bitmap Track(
        object owner,
        Action<Image> apply,
        UiGlyph glyph,
        int size,
        UiIconTone tone,
        float dpiScale)
    {
        var image = UiIconFactory.Create(glyph, ToneColor(tone), size, dpiScale);
        apply(image);
        lock (TrackedIconsLock)
        {
            TrackedIcons.Add(new TrackedIcon(new WeakReference<object>(owner), apply, glyph, size, tone, dpiScale)
            {
                Current = image
            });
        }

        return image;
    }

    private static void RefreshTrackedIcons()
    {
        TrackedIcon[] pending;
        lock (TrackedIconsLock)
        {
            TrackedIcons.RemoveAll(static tracked => !tracked.IsAlive);
            pending = [.. TrackedIcons];
        }

        foreach (var tracked in pending)
        {
            var replacement = UiIconFactory.Create(
                tracked.Glyph,
                ToneColor(tracked.Tone),
                tracked.Size,
                tracked.DpiScale);
            var previous = tracked.Current;
            tracked.Apply(replacement);
            tracked.Current = replacement;
            previous?.Dispose();
        }
    }

    private sealed class TrackedIcon(
        WeakReference<object> owner,
        Action<Image> apply,
        UiGlyph glyph,
        int size,
        UiIconTone tone,
        float dpiScale)
    {
        public Action<Image> Apply { get; } = apply;
        public UiGlyph Glyph { get; } = glyph;
        public int Size { get; } = size;
        public UiIconTone Tone { get; } = tone;
        public float DpiScale { get; } = dpiScale;
        public Image? Current { get; set; }

        public bool IsAlive => owner.TryGetTarget(out var target) && target switch
        {
            Control control => !control.IsDisposed,
            ToolStripItem item => !item.IsDisposed,
            _ => true
        };
    }

    /// <summary>
    /// Asks the shell to paint a native control's scrollbars, headers, and borders in the dark
    /// palette. WinForms leaves these to comctl32, which otherwise draws a light scrollbar track
    /// and a bright header gutter inside an otherwise dark window.
    /// </summary>
    internal static void ApplyNativeChrome(Control control)
    {
        if (!OperatingSystem.IsWindows() || !control.IsHandleCreated)
        {
            control.HandleCreated -= NativeChromeHandleCreated;
            control.HandleCreated += NativeChromeHandleCreated;
            return;
        }

        var dark = EffectiveAppearance == DesktopAppearance.Dark;
        var theme = control switch
        {
            TextBoxBase or ComboBox or NumericUpDown or DateTimePicker => dark ? "DarkMode_CFD" : "Explorer",
            _ => dark ? "DarkMode_Explorer" : "Explorer"
        };

        try
        {
            _ = SetWindowTheme(control.Handle, theme, null);
            foreach (Control child in control.Controls)
            {
                if (child.IsHandleCreated)
                {
                    _ = SetWindowTheme(child.Handle, theme, null);
                }
            }

            if (control is ListView)
            {
                // The column header is a comctl32 window rather than a WinForms child, so it is
                // not reached by walking Controls. Owner drawing covers the header cells but not
                // the gap past the last column, which stayed a bright block in dark mode.
                var header = SendMessage(control.Handle, LvmGetHeader, IntPtr.Zero, IntPtr.Zero);
                if (header != IntPtr.Zero)
                {
                    _ = SetWindowTheme(header, theme, null);
                }
            }
        }
        catch (EntryPointNotFoundException)
        {
            // An older shell without the dark-mode theme classes keeps the light chrome.
        }
    }

    private static void NativeChromeHandleCreated(object? sender, EventArgs e)
    {
        if (sender is Control control)
        {
            ApplyNativeChrome(control);
        }
    }

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

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    private const int LvmGetHeader = 0x1000 + 31;



    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    public static Color ParseAccent(string accentHex)
    {
        if (string.IsNullOrWhiteSpace(accentHex))
        {
            return Primary;
        }

        return ColorTranslator.FromHtml(accentHex);
    }

    /// <summary>
    /// Blends an accent towards the current surface. Hard-coding an alpha over an unknown
    /// background produced badge fills that vanished in one appearance and glared in the other.
    /// </summary>
    public static Color Tint(Color accent, double strength)
    {
        var clamped = Math.Clamp(strength, 0D, 1D);
        var surface = Surface;
        return Color.FromArgb(
            (int)Math.Round((accent.R * clamped) + (surface.R * (1 - clamped))),
            (int)Math.Round((accent.G * clamped) + (surface.G * (1 - clamped))),
            (int)Math.Round((accent.B * clamped) + (surface.B * (1 - clamped))));
    }

    /// <summary>
    /// Picks black or white text for an arbitrary accent fill, so provider badges stay readable
    /// whatever colour a profile chose.
    /// </summary>
    public static Color ContrastText(Color background)
    {
        static double Channel(int value)
        {
            var normalized = value / 255D;
            return normalized <= 0.03928D
                ? normalized / 12.92D
                : Math.Pow((normalized + 0.055D) / 1.055D, 2.4D);
        }

        var luminance = (0.2126D * Channel(background.R))
            + (0.7152D * Channel(background.G))
            + (0.0722D * Channel(background.B));
        // 0.179 is where black and white reach the same contrast ratio against a background, so
        // it is the crossover that keeps a mid-tone accent such as amber readable.
        return luminance > 0.179D ? Color.FromArgb(24, 26, 30) : Color.White;
    }
}

internal readonly record struct StorageHubPalette(
    Color Canvas,
    Color Surface,
    Color SurfaceMuted,
    Color Elevated,
    Color Border,
    Color Text,
    Color TextMuted,
    Color Primary,
    Color PrimaryHover,
    Color PrimaryPressed,
    Color Selection,
    Color SelectionPressed,
    Color Input,
    Color DisabledText,
    Color Success,
    Color Warning,
    Color Danger,
    Color SuccessTint,
    Color WarningTint,
    Color DangerTint);

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
