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
    public void TheChooserOffersEveryPresetAndNoneIsPreselected()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var chooser = new NewWorkspaceForm(WorkspaceLayout.TopAndBottom);
            var buttons = chooser.Controls.OfType<TableLayoutPanel>()
                .Single().Controls.OfType<Button>().ToArray();

            Assert.Equal(WorkspaceLayoutModel.MaximumPanes, buttons.Length);
            // Nothing is chosen until a tile is clicked, so a cancelled dialog creates nothing.
            Assert.Equal(0, chooser.PaneCount);
            Assert.Contains("Top / bottom", buttons[1].Text, StringComparison.Ordinal);
        });
    }

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
    public void SettingsExposeThePaneCountAndCanRestoreAsking()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var fixture = new SettingsFixture();
            fixture.Store.Save(DesktopUpdatePreferences.Defaults with { DefaultWorkspacePaneCount = 2 });
            using var settings = new SettingsForm(fixture.Store, saved: null);
            var combo = Combo(settings);

            Assert.Equal(2, combo.SelectedItem);

            combo.SelectedItem = 0;
            Assert.True((bool)typeof(SettingsForm)
                .GetMethod("TrySave", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(settings, null)!);
            Assert.Null(fixture.Store.Load().DefaultWorkspacePaneCount);
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
            Combo(settings).SelectedItem = 3;
            Assert.True((bool)typeof(SettingsForm)
                .GetMethod("TrySave", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(settings, null)!);

            var saved = fixture.Store.Load();
            Assert.Equal(3, saved.DefaultWorkspacePaneCount);
            Assert.Equal(pinned, Assert.Single(saved.PinnedWorkspaces!));
            Assert.Equal(pinned, Assert.Single(saved.RecentWorkspaces!));
        });
    }

    private static ComboBox Combo(SettingsForm settings) => (ComboBox)typeof(SettingsForm)
        .GetField("_defaultWorkspacePaneCount", BindingFlags.Instance | BindingFlags.NonPublic)!
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
