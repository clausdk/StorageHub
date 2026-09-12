using System.Reflection;

namespace StorageHub.Desktop.Tests;

/// <summary>
/// The pinned and recent entries are built imperatively, outside <see cref="UiCommandCatalog"/>,
/// and rebuilt every time the menu opens. These STA tests cover what that hand-rolled section can
/// get wrong: enable state, ordering, duplicate rendering, and accelerator escaping.
/// </summary>
public sealed class WorkspaceShortcutMenuTests
{
    [Fact]
    public void PinnedEntriesRenderBeforeRecentAndAPathInBothRendersOnce()
    {
        WithShell(
            preferences => preferences with
            {
                PinnedWorkspaces = [Entry("nightly", "Nightly")],
                RecentWorkspaces = [Entry("nightly", "Nightly"), Entry("scratch", "Scratch")]
            },
            (main, directory) =>
            {
                var labels = OpenWorkspaceMenu(main)
                    .Where(item => item.Enabled && item.Text is not (null or "Exit"))
                    .Select(item => item.Text!)
                    .ToArray();

                Assert.Equal(1, labels.Count(label => label == "Nightly"));
                Assert.True(
                    Array.IndexOf(labels, "Nightly") < Array.IndexOf(labels, "Scratch"),
                    "The pinned workspace should be listed before the recent one.");
            });
    }

    [Fact]
    public void RememberedWorkspacesStayClickableWhenNoWorkspaceIsOpen()
    {
        // The shell starts on the Welcome tab with no workspace, which is exactly when the recent
        // list is most useful. Save Workspace is correctly disabled there; the list must not be.
        WithShell(
            preferences => preferences with { RecentWorkspaces = [Entry("scratch", "Scratch")] },
            (main, directory) =>
            {
                var items = OpenWorkspaceMenu(main);

                Assert.False(Single(items, "Save Workspace").Enabled);
                Assert.True(Single(items, "Scratch").Enabled);
                Assert.True(Single(items, "Open Workspace...").Enabled);
            });
    }

    [Fact]
    public void AMissingWorkspaceIsMarkedAndDimmedButStillClickable()
    {
        // A disabled item cannot be clicked, and clicking is how a stale entry gets pruned.
        WithShell(
            preferences => preferences with
            {
                PinnedWorkspaces = [new WorkspaceShortcutEntry(
                    Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.shw"),
                    "Gone",
                    DateTimeOffset.UtcNow)]
            },
            (main, directory) =>
            {
                var item = Single(OpenWorkspaceMenu(main), "Gone (missing)");

                Assert.True(item.Enabled);
                Assert.Equal(StorageHubTheme.TextMuted, item.ForeColor);
                Assert.Contains("missing", item.AccessibleDescription!, StringComparison.OrdinalIgnoreCase);
            });
    }

    [Fact]
    public void AnAmpersandInAWorkspaceNameIsNotReadAsAnAccelerator()
    {
        WithShell(
            preferences => preferences with { RecentWorkspaces = [Entry("rd", "R&D")] },
            (main, directory) => Assert.NotNull(Single(OpenWorkspaceMenu(main), "R&&D")));
    }

    [Fact]
    public void WithNothingRememberedTheMenuGrowsNoEmptyHeadings()
    {
        WithShell(
            preferences => preferences,
            (main, directory) =>
            {
                var labels = OpenWorkspaceMenu(main).Select(item => item.Text).ToArray();

                Assert.DoesNotContain("Pinned", labels);
                Assert.DoesNotContain("Recent", labels);
                Assert.Contains("Pin Workspace", labels);
            });
    }

    [Fact]
    public void ThePinItemNeedsAWorkspaceAndFollowsWhetherItIsAlreadyPinned()
    {
        WithShell(
            preferences => preferences,
            (main, directory) =>
            {
                Assert.False(Single(OpenWorkspaceMenu(main), "Pin Workspace").Enabled);

                _ = main.AddWorkspace(2);
                Assert.True(Single(OpenWorkspaceMenu(main), "Pin Workspace").Enabled);
            });
    }

    [Fact]
    public void TheGoMenuOffersAWayForwardWhenNothingIsFavorited()
    {
        WithShell(
            preferences => preferences,
            (main, directory) =>
            {
                var items = OpenMenu(main, "Go");

                Assert.Contains(items, item => item.Text == "Favorites");
                Assert.False(Single(items, "No favorite connections").Enabled);
                Assert.True(Single(items, "Connection Manager...").Enabled);
            });
    }

    /// <summary>
    /// Builds a shell over a throwaway settings file and workspace directory, so the tests never
    /// touch the developer's own settings.
    /// </summary>
    private static void WithShell(
        Func<DesktopUpdatePreferences, DesktopUpdatePreferences> configure,
        Action<MainForm, string> assert)
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var directory = Path.Combine(
                Path.GetTempPath(), "StorageHub workspace menu tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            CurrentDirectory = directory;
            try
            {
                var store = new DesktopUpdatePreferencesStore(Path.Combine(directory, "settings.json"));
                store.Save(configure(DesktopUpdatePreferences.Defaults));
                using var main = new MainForm(store);
                assert(main, directory);
            }
            finally
            {
                CurrentDirectory = null;
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    /// <summary>Where <see cref="Entry"/> places the files it claims exist.</summary>
    private static string? CurrentDirectory;

    /// <summary>A remembered workspace whose file genuinely exists, so it renders as present.</summary>
    private static WorkspaceShortcutEntry Entry(string fileName, string name)
    {
        var path = Path.Combine(CurrentDirectory!, fileName + ".shw");
        if (!File.Exists(path)) File.WriteAllText(path, "{}");
        return new WorkspaceShortcutEntry(path, name, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Raises DropDownOpening, which is what rebuilds the dynamic entries, and returns what the
    /// user would see.
    /// </summary>
    private static IReadOnlyList<ToolStripItem> OpenMenu(MainForm main, string menuName)
    {
        var menu = Assert.Single(main.Controls.OfType<MenuStrip>());
        var root = Assert.Single(menu.Items.OfType<ToolStripMenuItem>(), item => item.Text == menuName);
        typeof(ToolStripDropDownItem)
            .GetMethod("OnDropDownShow", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(root, [EventArgs.Empty]);
        return [.. root.DropDownItems.Cast<ToolStripItem>()];
    }

    private static IReadOnlyList<ToolStripItem> OpenWorkspaceMenu(MainForm main)
    {
        var items = OpenMenu(main, "Workspace");
        // Enable state is refreshed on tab changes, not on open, so apply it the same way here.
        typeof(MainForm)
            .GetMethod("UpdateWorkspaceCommandState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(main, null);
        return items;
    }

    private static ToolStripItem Single(IEnumerable<ToolStripItem> items, string text) =>
        Assert.Single(items, item => item.Text == text);
}
