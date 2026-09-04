namespace StorageHub.Desktop.Tests;

public sealed class ThemeTests
{
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
