using System.Reflection;

namespace StorageHub.Desktop.Tests;

public sealed class ShortcutSettingsTests
{
    [Fact]
    public void DefaultsCoverBasicFileActionsWithoutConflicts()
    {
        var defaults = ShortcutSettings.Resolve(null);
        Assert.Null(ShortcutSettings.Validate(defaults));
        Assert.Equal(Keys.Control | Keys.C, defaults[Command("Copy").Id]);
        Assert.Equal(Keys.Control | Keys.V, defaults[Command("Paste").Id]);
        Assert.Equal(Keys.F6, defaults[Command("Next Pane").Id]);
        Assert.DoesNotContain(ShortcutSettings.Commands, command => command.Label == "Quick Connect...");
    }

    [Fact]
    public void DuplicateAndUnsafeBindingsAreRejectedAndCorruptSettingsFallBack()
    {
        var bindings = ShortcutSettings.Resolve(null);
        bindings[Command("Paste").Id] = Keys.Control | Keys.C;
        Assert.Contains("already assigned", ShortcutSettings.Validate(bindings), StringComparison.Ordinal);
        Assert.Equal(Keys.Control | Keys.V, ShortcutSettings.Resolve(bindings)[Command("Paste").Id]);
        foreach (var invalid in new[] { Keys.C, Keys.ControlKey, Keys.Alt | Keys.F4, Keys.Control | Keys.Alt | Keys.Delete })
            Assert.False(ShortcutSettings.IsValid(invalid));
    }

    [Fact]
    public void ReassignedAndDisabledShortcutsSurviveRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"storagehub-shortcuts-{Guid.NewGuid():N}.json");
        try
        {
            var store = new DesktopUpdatePreferencesStore(path);
            var bindings = ShortcutSettings.Resolve(null);
            bindings[Command("Copy").Id] = Keys.Control | Keys.Shift | Keys.C;
            bindings[Command("Paste").Id] = Keys.None;
            store.Save(DesktopUpdatePreferences.Defaults with { Shortcuts = bindings });
            var restored = ShortcutSettings.Resolve(new DesktopUpdatePreferencesStore(path).Load().Shortcuts);
            Assert.Equal(bindings.OrderBy(pair => pair.Key), restored.OrderBy(pair => pair.Key));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("Copy")]
    [InlineData("Paste")]
    [InlineData("Delete")]
    [InlineData("Close Workspace")]
    public void ShortcutsLeaveSshAndTextInputAlone(string label)
    {
        var command = Command(label);
        Assert.False(ShortcutSettings.CanDispatch(command, sshFocused: true, textFocused: false, hasPane: true));
        Assert.False(ShortcutSettings.CanDispatch(command, sshFocused: false, textFocused: true, hasPane: true));
        Assert.True(ShortcutSettings.CanDispatch(command, sshFocused: false, textFocused: false, hasPane: true));
    }

    [Fact]
    public void ShellUsesRemappedShortcutAndIgnoresOldBinding()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"storagehub-shortcuts-{Guid.NewGuid():N}.json");
            try
            {
                var store = new DesktopUpdatePreferencesStore(path);
                var bindings = ShortcutSettings.Resolve(null);
                bindings[Command("Next Pane").Id] = Keys.Control | Keys.F6;
                store.Save(DesktopUpdatePreferences.Defaults with { Shortcuts = bindings });
                using var main = new MainForm(store);
                var tabs = (TabControl)typeof(MainForm).GetField("_workspaceTabs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
                _ = tabs.Handle;
                var page = main.AddWorkspace(2);
                var workspace = Assert.Single(page.Controls.OfType<WorkspaceControl>());
                var first = workspace.ActivePaneId;
                Assert.False(main.TryDispatchShortcut(Keys.F6));
                Assert.Equal(first, workspace.ActivePaneId);
                Assert.True(main.TryDispatchShortcut(Keys.Control | Keys.F6));
                Assert.NotEqual(first, workspace.ActivePaneId);
                Assert.Equal(ShortcutSettings.Format(Keys.Control | Keys.F6), main.ShortcutDisplay(Command("Next Pane").Id));
                var menu = Assert.Single(main.Controls.OfType<MenuStrip>());
                Assert.All(menu.Items.OfType<ToolStripMenuItem>().SelectMany(root => root.DropDownItems.OfType<ToolStripMenuItem>()),
                    item => Assert.Equal(Keys.None, item.ShortcutKeys));
            }
            finally { File.Delete(path); }
        });
    }

    [Fact]
    public void SettingsExposeShortcutEditorAndCancelDoesNotPersist()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"storagehub-shortcuts-{Guid.NewGuid():N}.json");
            using var settings = new SettingsForm(new DesktopUpdatePreferencesStore(path), saved: null);
            var tree = (TreeView)typeof(SettingsForm).GetField("_categories", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(settings)!;
            Assert.Contains(tree.Nodes.Cast<TreeNode>(), node => node.Name == "Shortcuts");
            Assert.False(File.Exists(path));
        });
    }

    private static UiCommandDefinition Command(string label) => ShortcutSettings.Commands.Single(command => command.Label == label);

    [Fact]
    public void ShortcutEditorAppliesChangesAndRejectsConflictsWithoutLosingAssignments()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"storagehub-shortcuts-{Guid.NewGuid():N}.json");
            try
            {
                var store = new DesktopUpdatePreferencesStore(path);
                using var settings = new SettingsForm(store, saved: null);
                var editor = (ShortcutSettingsControl)typeof(SettingsForm).GetField("_shortcuts", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(settings)!;
                var grid = editor.Controls.OfType<DataGridView>().Single();
                var copy = grid.Rows.Cast<DataGridViewRow>().Single(row => Equals(row.Tag, Command("Copy").Id));
                grid.CurrentCell = copy.Cells[0];
                var assign = typeof(ShortcutSettingsControl).GetMethod("SetSelected", BindingFlags.NonPublic | BindingFlags.Instance)!;
                assign.Invoke(editor, [Keys.Control | Keys.V]);
                Assert.Equal(Keys.Control | Keys.C, editor.ReadShortcuts()[Command("Copy").Id]);
                assign.Invoke(editor, [Keys.Control | Keys.Shift | Keys.C]);
                var save = typeof(SettingsForm).GetMethod("TrySave", BindingFlags.NonPublic | BindingFlags.Instance)!;
                Assert.True((bool)save.Invoke(settings, null)!);
                Assert.Equal(Keys.Control | Keys.Shift | Keys.C, store.Load().Shortcuts![Command("Copy").Id]);
                assign.Invoke(editor, [Keys.None]);
                Assert.Equal(Keys.None, editor.ReadShortcuts()[Command("Copy").Id]);
                Assert.Equal(Keys.Control | Keys.Shift | Keys.C, store.Load().Shortcuts![Command("Copy").Id]);
            }
            finally { File.Delete(path); }
        });
    }
}
