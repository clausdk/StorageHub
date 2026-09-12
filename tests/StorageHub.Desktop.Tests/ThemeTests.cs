namespace StorageHub.Desktop.Tests;

public sealed class ThemeTests
{
    [Theory]
    [InlineData(DesktopAppearance.Light)]
    [InlineData(DesktopAppearance.Dark)]
    public void Workspace_menu_colors_follow_command_availability(DesktopAppearance appearance)
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var previous = DesktopAppearanceService.Appearance;
            try
            {
                DesktopAppearanceService.SetAppearance(appearance);
                using var main = new MainForm();
                main.CreateControl();
                var tabs = DescendantsAndSelf(main).OfType<TabControl>().Single(tab => tab.AccessibleName == "Workspace tabs");
                _ = tabs.Handle;
                StorageHubTheme.Apply(main);
                var menu = Assert.Single(main.Controls.OfType<MenuStrip>());
                var workspaceMenu = menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "Workspace");
                var save = workspaceMenu.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Text == "Save Workspace");
                Assert.False(save.Enabled);
                Assert.Equal(StorageHubTheme.CurrentPalette.DisabledText, save.ForeColor);

                main.AddWorkspace(2);

                Assert.True(save.Enabled);
                Assert.Equal(StorageHubTheme.Text, save.ForeColor);

                tabs.SelectedIndex = 0;
                Assert.False(save.Enabled);
                Assert.Equal(StorageHubTheme.CurrentPalette.DisabledText, save.ForeColor);
                tabs.SelectedIndex = 2;
                Assert.True(save.Enabled);
                Assert.Equal(StorageHubTheme.Text, save.ForeColor);
            }
            finally
            {
                DesktopAppearanceService.SetAppearance(previous);
            }
        });
    }

    private static readonly string[] MajorWindowNames =
        ["Main", "Connections", "Settings", "Sync profiles", "Schedules"];

    [Theory]
    [InlineData(DesktopAppearance.Light)]
    [InlineData(DesktopAppearance.Dark)]
    public void Disabled_primary_buttons_have_an_unambiguous_disabled_surface(DesktopAppearance appearance)
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var previous = DesktopAppearanceService.EffectiveAppearance;
            DesktopAppearanceService.SetAppearance(appearance);
            using var button = new Button { Enabled = false };

            StorageHubTheme.StylePrimaryButton(button);

            Assert.Equal(StorageHubTheme.CurrentPalette.SurfaceMuted, button.BackColor);
            Assert.Equal(StorageHubTheme.CurrentPalette.DisabledText, button.ForeColor);

            button.Enabled = true;
            Assert.Equal(StorageHubTheme.CurrentPalette.Primary, button.BackColor);
            Assert.Equal(Color.White, button.ForeColor);
            Assert.Equal(Cursors.Hand, button.Cursor);

            button.Enabled = false;
            Assert.Equal(StorageHubTheme.CurrentPalette.SurfaceMuted, button.BackColor);
            Assert.Equal(Cursors.Default, button.Cursor);
            DesktopAppearanceService.SetAppearance(previous);
        });
    }

    [Fact]
    public void Disabled_secondary_buttons_use_the_disabled_palette()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var button = new Button { Enabled = false };

            StorageHubTheme.StyleSecondaryButton(button);

            Assert.Equal(StorageHubTheme.CurrentPalette.SurfaceMuted, button.BackColor);
            Assert.Equal(StorageHubTheme.CurrentPalette.DisabledText, button.ForeColor);
            Assert.Equal(Cursors.Default, button.Cursor);
        });
    }

    [Theory]
    [InlineData(DesktopAppearance.Light)]
    [InlineData(DesktopAppearance.Dark)]
    public void Stock_controls_receive_the_resolved_semantic_palette(DesktopAppearance appearance)
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var previous = DesktopAppearanceService.EffectiveAppearance;
            DesktopAppearanceService.SetAppearance(appearance);
            using var main = new MainForm();
            main.AddWorkspace(2);
            main.CreateControl();
            StorageHubTheme.Apply(main, previous);

            var controls = DescendantsAndSelf(main).ToArray();
            var tabs = Assert.Single(controls.OfType<TabControl>(), tab => tab.AccessibleName == "Workspace tabs");
            var trees = controls.OfType<TreeView>().Where(candidate =>
                candidate.AccessibleName?.Contains("directory tree", StringComparison.Ordinal) == true).ToArray();
            var lists = controls.OfType<ListView>().Where(candidate =>
                candidate.AccessibleName?.Contains("file list", StringComparison.Ordinal) == true).ToArray();
            var grid = Assert.Single(controls.OfType<DataGridView>(), candidate =>
                candidate.AccessibleName == "Active transfer jobs");

            Assert.Equal(StorageHubTheme.Canvas, main.BackColor);
            Assert.Equal(TabDrawMode.OwnerDrawFixed, tabs.DrawMode);
            Assert.NotEmpty(trees);
            Assert.All(trees, tree =>
            {
                Assert.Equal(StorageHubTheme.Surface, tree.BackColor);
                Assert.Equal(StorageHubTheme.Text, tree.ForeColor);
            });
            Assert.NotEmpty(lists);
            Assert.All(lists, list =>
            {
                Assert.True(list.OwnerDraw);
                Assert.Equal(StorageHubTheme.Surface, list.BackColor);
            });
            Assert.False(grid.EnableHeadersVisualStyles);
            Assert.Equal(StorageHubTheme.SurfaceMuted, grid.ColumnHeadersDefaultCellStyle.BackColor);
            Assert.Equal(StorageHubTheme.CurrentPalette.Selection, grid.DefaultCellStyle.SelectionBackColor);

            DesktopAppearanceService.SetAppearance(DesktopAppearance.System);
        });
    }

    [Fact]
    public void Public_desktop_windows_derive_directly_from_stock_form()
    {
        var formTypes = typeof(MainForm).Assembly.GetTypes()
            .Where(type => type.IsPublic && !type.IsAbstract && typeof(Form).IsAssignableFrom(type))
            .ToArray();

        Assert.NotEmpty(formTypes);
        Assert.All(formTypes, type => Assert.Equal(typeof(Form), type.BaseType));
    }

    [Fact]
    public void System_preference_changes_restyle_open_windows()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var systemIsDark = false;
            DesktopAppearanceService.SetSystemDarkModeReaderForTests(() => systemIsDark);
            DesktopAppearanceService.SetAppearance(DesktopAppearance.System);
            using var form = new SettingsForm();
            form.Show();
            Assert.Equal(DesktopAppearance.Light, DesktopAppearanceService.EffectiveAppearance);
            Assert.Equal(StorageHubTheme.Canvas, form.BackColor);

            systemIsDark = true;
            DesktopAppearanceService.RefreshSystemAppearance();
            Assert.Equal(DesktopAppearance.Dark, DesktopAppearanceService.EffectiveAppearance);
            Assert.Equal(StorageHubTheme.Canvas, form.BackColor);

            form.Close();
            DesktopAppearanceService.SetSystemDarkModeReaderForTests(null);
            DesktopAppearanceService.SetAppearance(DesktopAppearance.System);
        });
    }

    [Theory]
    [InlineData(DesktopAppearance.Light, 1.0f)]
    [InlineData(DesktopAppearance.Light, 1.5f)]
    [InlineData(DesktopAppearance.Dark, 1.0f)]
    [InlineData(DesktopAppearance.Dark, 1.5f)]
    public void Major_windows_render_at_supported_themes_and_dpi_scales(
        DesktopAppearance appearance,
        float scale)
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var previous = DesktopAppearanceService.EffectiveAppearance;
            try
            {
                DesktopAppearanceService.SetAppearance(appearance);
                foreach (var windowName in MajorWindowNames)
                {
                    using Form form = windowName switch
                    {
                        "Main" => new MainForm(),
                        "Connections" => new ConnectionManagerForm(),
                        "Settings" => new SettingsForm(),
                        "Sync profiles" => new SyncProfileEditorForm(),
                        "Schedules" => new ScheduleManagerForm(),
                        _ => throw new InvalidOperationException()
                    };
                    form.CreateControl();
                    if (scale != 1.0f)
                    {
                        form.Scale(new SizeF(scale, scale));
                    }

                    StorageHubTheme.Apply(form, previous);
                    form.PerformLayout();
                    Assert.Equal(AutoScaleMode.Dpi, form.AutoScaleMode);
                    Assert.Equal(StorageHubTheme.Canvas, form.BackColor);
                    Assert.All(form.Controls.Cast<Control>().Where(static control => control.Visible), control =>
                        Assert.True(
                            form.ClientRectangle.Contains(control.Bounds),
                            $"{windowName}: {control.Name} ({control.GetType().Name}) is outside {form.ClientRectangle}: {control.Bounds}"));

                    using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
                    form.DrawToBitmap(bitmap, form.ClientRectangle);
                    Assert.Equal(form.ClientSize, bitmap.Size);
                }
            }
            finally
            {
                DesktopAppearanceService.SetAppearance(previous);
            }
        });
    }

    [Fact]
    public void Every_glyph_renders_at_the_sizes_the_shell_asks_for()
    {
        // One malformed stroke takes down the whole shell, because the menus rasterise every
        // glyph during construction.
        foreach (var glyph in Enum.GetValues<UiGlyph>())
        {
            foreach (var size in new[] { 12, 16, 18, 20, 24 })
            {
                using var bitmap = UiIconFactory.Create(glyph, Color.White, size, 1.5F);
                Assert.Equal((int)Math.Round(size * 1.5F), bitmap.Width);
                Assert.Equal(bitmap.Width, bitmap.Height);
                Assert.True(
                    HasVisiblePixels(bitmap),
                    $"{glyph} at {size} drew nothing.");
            }
        }
    }

    [Fact]
    public void Every_menu_command_carries_an_icon()
    {
        // A menu where only some rows are illustrated reads as unfinished, and the icon column is
        // what makes these menus scannable.
        Assert.All(UiCommandCatalog.Definitions, definition => Assert.NotNull(definition.Glyph));
    }

    [Fact]
    public void Icons_are_recoloured_when_the_appearance_changes()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var previous = DesktopAppearanceService.Appearance;
            try
            {
                DesktopAppearanceService.SetAppearance(DesktopAppearance.Light);
                using var item = new ToolStripMenuItem("Refresh");
                _ = StorageHubTheme.TrackIcon(item, UiGlyph.Refresh, 16);
                var light = Assert.IsType<Bitmap>(item.Image);
                AssertInk(StorageHubTheme.Text, StrongestColor(light));

                DesktopAppearanceService.SetAppearance(DesktopAppearance.Dark);

                var dark = Assert.IsType<Bitmap>(item.Image);
                Assert.NotSame(light, dark);
                AssertInk(StorageHubTheme.Text, StrongestColor(dark));
            }
            finally
            {
                DesktopAppearanceService.SetAppearance(previous);
            }
        });
    }

    [Theory]
    [InlineData(DesktopAppearance.Light)]
    [InlineData(DesktopAppearance.Dark)]
    public void Status_tints_stay_readable_against_their_own_status_colour(DesktopAppearance appearance)
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var previous = DesktopAppearanceService.Appearance;
            try
            {
                DesktopAppearanceService.SetAppearance(appearance);
                var pairs = new[]
                {
                    (Tint: StorageHubTheme.SuccessTint, Ink: StorageHubTheme.Success),
                    (Tint: StorageHubTheme.WarningTint, Ink: StorageHubTheme.Warning),
                    (Tint: StorageHubTheme.DangerTint, Ink: StorageHubTheme.Danger)
                };

                // A notice that hard-codes a pastel fill inverts in dark mode. Every tint has to
                // sit on the same side of the palette as the surface it replaces.
                Assert.All(pairs, pair =>
                {
                    var tintIsDark = Luminance(pair.Tint) < 0.5;
                    Assert.Equal(appearance == DesktopAppearance.Dark, tintIsDark);
                    Assert.True(
                        Math.Abs(Luminance(pair.Tint) - Luminance(pair.Ink)) > 0.15,
                        $"{appearance}: tint {pair.Tint} and ink {pair.Ink} are too close.");
                });
            }
            finally
            {
                DesktopAppearanceService.SetAppearance(previous);
            }
        });
    }

    [Theory]
    // A provider may pick any accent, so the badge picks its ink from the accent's luminance.
    [InlineData("#FFFFFF", 24)]
    [InlineData("#F59E0B", 24)]
    [InlineData("#000000", 255)]
    [InlineData("#1867C0", 255)]
    public void Badge_text_contrasts_with_the_accent_behind_it(string accent, int expectedChannel)
    {
        var ink = StorageHubTheme.ContrastText(ColorTranslator.FromHtml(accent));
        Assert.Equal(expectedChannel, ink.R);
    }

    private static bool HasVisiblePixels(Bitmap bitmap)
    {
        for (var x = 0; x < bitmap.Width; x++)
        {
            for (var y = 0; y < bitmap.Height; y++)
            {
                if (bitmap.GetPixel(x, y).A > 8)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Compares a rendered stroke against its intended colour. Nothing in a small anti-aliased
    /// glyph is guaranteed to reach full opacity, so the darkest pixel is a near miss by design.
    /// </summary>
    private static void AssertInk(Color expected, Color actual)
    {
        Assert.True(
            Math.Abs(expected.R - actual.R) <= 12 &&
            Math.Abs(expected.G - actual.G) <= 12 &&
            Math.Abs(expected.B - actual.B) <= 12,
            $"Expected ink near {expected}, drew {actual}.");
    }

    /// <summary>The colour of the most opaque pixel, which for a stroked glyph is its ink.</summary>
    private static Color StrongestColor(Bitmap bitmap)
    {
        var best = Color.Transparent;
        for (var x = 0; x < bitmap.Width; x++)
        {
            for (var y = 0; y < bitmap.Height; y++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.A > best.A)
                {
                    best = pixel;
                }
            }
        }

        return Color.FromArgb(best.R, best.G, best.B);
    }

    private static double Luminance(Color color) =>
        ((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) / 255D;

    private static IEnumerable<Control> DescendantsAndSelf(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls)
        {
            foreach (var descendant in DescendantsAndSelf(child))
            {
                yield return descendant;
            }
        }
    }
}
