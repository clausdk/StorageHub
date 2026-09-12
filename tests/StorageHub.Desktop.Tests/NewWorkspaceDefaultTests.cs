using System.Reflection;

namespace StorageHub.Desktop.Tests;

/// <summary>
/// The pane chooser can be told to stop asking, and Settings ▸ Workspace is the way back. These
/// cover the preference itself, the dialog's opt-in, and the shell honouring it.
/// </summary>
public sealed class NewWorkspaceDefaultTests
{
    [Fact]
    public void APaneCountRoundTripsAndAskEveryTimeIsTheDefault()
    {
        using var fixture = new SettingsFixture();

        Assert.Null(DesktopUpdatePreferences.Defaults.DefaultWorkspacePaneCount);

        fixture.Store.Save(DesktopUpdatePreferences.Defaults with { DefaultWorkspacePaneCount = 3 });
        Assert.Equal(3, new DesktopUpdatePreferencesStore(fixture.SettingsPath).Load().DefaultWorkspacePaneCount);

        fixture.Store.Save(DesktopUpdatePreferences.Defaults with { DefaultWorkspacePaneCount = null });
        Assert.Null(fixture.Store.Load().DefaultWorkspacePaneCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(WorkspaceLayoutModel.MaximumPanes + 1)]
    public void APaneCountNoWorkspaceCouldHaveIsRefused(int paneCount)
    {
        using var fixture = new SettingsFixture();

        Assert.Throws<ArgumentException>(() => fixture.Store.Save(
            DesktopUpdatePreferences.Defaults with { DefaultWorkspacePaneCount = paneCount }));
    }

    [Fact]
    public void SettingsWrittenBeforeTheChoiceExistedStillAskEveryTime()
    {
        using var fixture = new SettingsFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.SettingsPath)!);
        File.WriteAllText(
            fixture.SettingsPath,
            """{"schemaVersion":13,"appearance":2,"sshHostKeyDiscovery":2}""");

        var restored = fixture.Store.Load();

        Assert.Null(restored.DefaultWorkspacePaneCount);
        Assert.Equal(DesktopAppearance.Dark, restored.Appearance);
    }

    [Fact]
    public void TheChooserOnlyRemembersWhenAsked()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var chooser = new NewWorkspaceForm(WorkspaceLayout.SideBySide);

            // Unticked by default: the dialog must not quietly stop asking.
            Assert.False(chooser.RememberChoice);

            var remember = Assert.Single(chooser.Controls.OfType<CheckBox>());
            remember.Checked = true;
            Assert.True(chooser.RememberChoice);
        });
    }

    [Fact]
    public void TheChooserOffersEveryArrangementAndNoneIsPreselected()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var chooser = new NewWorkspaceForm(WorkspaceLayout.TopAndBottom);
            var buttons = chooser.Controls.OfType<TableLayoutPanel>()
                .Single().Controls.OfType<Button>().ToArray();

            // Both two-pane and both three-pane arrangements, not one of each filtered through a
            // setting the chooser never showed.
            Assert.Equal(WorkspacePreset.All.Count, buttons.Length);
            Assert.Contains(buttons, button => button.Text.Contains("Side by side", StringComparison.Ordinal));
            Assert.Contains(buttons, button => button.Text.Contains("Top and bottom", StringComparison.Ordinal));
            Assert.All(buttons, button => Assert.NotNull(button.Image));

            // Nothing is chosen until a tile is clicked, so a cancelled dialog creates nothing.
            Assert.Equal(0, chooser.PaneCount);
        });
    }

    [Fact]
    public void PickingAnArrangementReportsItsOrientation()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var chooser = new NewWorkspaceForm(WorkspaceLayout.SideBySide);
            var buttons = chooser.Controls.OfType<TableLayoutPanel>()
                .Single().Controls.OfType<Button>().ToArray();

            Click(Single(buttons, "2 panes", "Top and bottom"));

            Assert.Equal(2, chooser.PaneCount);
            Assert.Equal(WorkspaceLayout.TopAndBottom, chooser.PaneLayout);
        });
    }

    [Fact]
    public void AnArrangementThatIgnoresOrientationLeavesItAlone()
    {
        // A single pane has nothing to divide, so choosing it must not rewrite the stored
        // orientation that two- and three-pane workspaces still depend on.
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var chooser = new NewWorkspaceForm(WorkspaceLayout.TopAndBottom);
            var buttons = chooser.Controls.OfType<TableLayoutPanel>()
                .Single().Controls.OfType<Button>().ToArray();

            Click(Single(buttons, "1 pane", "Single"));

            Assert.Equal(1, chooser.PaneCount);
            Assert.Equal(WorkspaceLayout.TopAndBottom, chooser.PaneLayout);
        });
    }

    [Fact]
    public void EveryArrangementIsDistinctAndBuildable()
    {
        foreach (var preset in WorkspacePreset.All)
        {
            var model = WorkspaceLayoutModel.CreatePreset(preset.PaneCount, preset.Layout);
            Assert.Equal(preset.PaneCount, model.PaneIds.Count);
            Assert.Equal(preset, WorkspacePreset.Find(preset.PaneCount, preset.Layout));
        }

        // Distinct as arrangements, not merely as records: two presets that render the same tree
        // would be the same choice offered twice.
        var shapes = WorkspacePreset.All
            .Select(preset => Describe(WorkspaceLayoutModel.CreatePreset(preset.PaneCount, preset.Layout).Root))
            .ToArray();
        Assert.Equal(shapes.Length, shapes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void AnOrientationThatChangesNothingStillResolves()
    {
        // Four panes are always a grid, so a stored "4 panes, top and bottom" has no exact preset
        // and must fall back rather than losing the choice.
        Assert.Equal(4, WorkspacePreset.Find(4, WorkspaceLayout.TopAndBottom)!.PaneCount);
        Assert.Null(WorkspacePreset.Find(9, WorkspaceLayout.SideBySide));
    }

    private static string Describe(WorkspaceLayoutNode node) => node switch
    {
        WorkspaceSplitNode split => $"({Describe(split.First)}{split.Orientation}{Describe(split.Second)})",
        _ => "."
    };

    /// <summary>
    /// Raises Click directly. PerformClick does nothing on a form that was never shown, because an
    /// invisible button cannot be selected.
    /// </summary>
    private static void Click(Button button) => typeof(Control)
        .GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(button, [EventArgs.Empty]);

    private static Button Single(IEnumerable<Button> buttons, params string[] contains) =>
        Assert.Single(buttons, button => contains.All(
            text => button.Text.Contains(text, StringComparison.Ordinal)));

    [Fact]
    public void ARememberedPaneCountCreatesTheWorkspaceWithoutAsking()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var fixture = new SettingsFixture();
            fixture.Store.Save(DesktopUpdatePreferences.Defaults with { DefaultWorkspacePaneCount = 4 });
            using var main = new MainForm(fixture.Store);

            // Showing a dialog here would block forever, so reaching the assert is the proof.
            typeof(MainForm)
                .GetMethod("ChooseAndAddWorkspace", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(main, null);

            var tabs = (TabControl)typeof(MainForm)
                .GetField("_workspaceTabs", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(main)!;
            var workspace = Assert.Single(
                tabs.TabPages.Cast<TabPage>().SelectMany(page => page.Controls.OfType<WorkspaceControl>()));
            Assert.Equal(4, workspace.Panes.Count);
        });
    }

    [Fact]
    public void SettingsOfferTheSameArrangementsAsTheChooser()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var fixture = new SettingsFixture();
            fixture.Store.Save(DesktopUpdatePreferences.Defaults with
            {
                DefaultWorkspacePaneCount = 2,
                DefaultWorkspaceLayout = WorkspaceLayout.TopAndBottom
            });
            using var settings = new SettingsForm(fixture.Store, saved: null);
            var combo = Combo(settings);

            // Every arrangement, plus "Ask every time", which no arrangement can represent.
            Assert.Equal(WorkspacePreset.All.Count + 1, combo.Items.Count);
            var selected = Assert.IsType<WorkspacePreset>(combo.SelectedItem);
            Assert.Equal(2, selected.PaneCount);
            Assert.Equal(WorkspaceLayout.TopAndBottom, selected.Layout);
        });
    }

    [Fact]
    public void SettingsCanRestoreAsking()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var fixture = new SettingsFixture();
            fixture.Store.Save(DesktopUpdatePreferences.Defaults with { DefaultWorkspacePaneCount = 2 });
            using var settings = new SettingsForm(fixture.Store, saved: null);

            Combo(settings).SelectedIndex = 0;
            Assert.True(Save(settings));
            Assert.Null(fixture.Store.Load().DefaultWorkspacePaneCount);
        });
    }

    [Fact]
    public void TheArrangementAndTheOrientationCannotContradictEachOther()
    {
        // Two controls describing the same workspace differently would leave nothing on screen to
        // say which one wins, so choosing an arrangement moves the orientation with it.
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var fixture = new SettingsFixture();
            fixture.Store.Save(DesktopUpdatePreferences.Defaults with
            {
                DefaultWorkspaceLayout = WorkspaceLayout.SideBySide
            });
            using var settings = new SettingsForm(fixture.Store, saved: null);
            var combo = Combo(settings);

            combo.SelectedItem = WorkspacePreset.All.Single(
                preset => preset.PaneCount == 3 && preset.Layout == WorkspaceLayout.TopAndBottom);
            Assert.True(Save(settings));

            var saved = fixture.Store.Load();
            Assert.Equal(3, saved.DefaultWorkspacePaneCount);
            Assert.Equal(WorkspaceLayout.TopAndBottom, saved.DefaultWorkspaceLayout);
        });
    }

    [Fact]
    public void SavingSettingsKeepsWorkspacesTheShellRemembered()
    {
        // Settings rebuilds the whole preferences record from its controls, and it presents
        // neither workspace list. Without carrying them through, opening Settings and pressing
        // Save would silently drop every pin.
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var fixture = new SettingsFixture();
            var pinned = new WorkspaceShortcutEntry(@"C:\work\nightly.shw", "Nightly", DateTimeOffset.UnixEpoch);
            fixture.Store.Save(DesktopUpdatePreferences.Defaults with
            {
                PinnedWorkspaces = [pinned],
                RecentWorkspaces = [pinned]
            });

            using var settings = new SettingsForm(fixture.Store, saved: null);
            Combo(settings).SelectedItem = WorkspacePreset.All.Single(
                preset => preset.PaneCount == 3 && preset.Layout == WorkspaceLayout.SideBySide);
            Assert.True(Save(settings));

            var saved = fixture.Store.Load();
            Assert.Equal(3, saved.DefaultWorkspacePaneCount);
            Assert.Equal(pinned, Assert.Single(saved.PinnedWorkspaces!));
            Assert.Equal(pinned, Assert.Single(saved.RecentWorkspaces!));
        });
    }

    private static bool Save(SettingsForm settings) => (bool)typeof(SettingsForm)
        .GetMethod("TrySave", BindingFlags.NonPublic | BindingFlags.Instance)!
        .Invoke(settings, null)!;

    private static ComboBox Combo(SettingsForm settings) => (ComboBox)typeof(SettingsForm)
        .GetField("_defaultWorkspacePreset", BindingFlags.Instance | BindingFlags.NonPublic)!
        .GetValue(settings)!;

    private sealed class SettingsFixture : IDisposable
    {
        internal SettingsFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(), "StorageHub new workspace tests", Guid.NewGuid().ToString("N"));
            SettingsPath = Path.Combine(Root, "settings.json");
            Store = new DesktopUpdatePreferencesStore(SettingsPath);
        }

        internal string Root { get; }

        internal string SettingsPath { get; }

        internal DesktopUpdatePreferencesStore Store { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
