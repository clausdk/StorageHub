using System.Reflection;

namespace StorageHub.Desktop.Tests;

/// <summary>
/// The connections panel is docked beside the workspace area and can live on either side. A
/// SplitContainer's SplitterDistance is always Panel1's width, so moving the panel has to flip
/// which panel holds what, which one is fixed, both minimums, and which one the hide toggle
/// collapses — all together. These cover that they do.
/// </summary>
public sealed class ConnectionsPanelShellTests
{
    [Fact]
    public void TheShellWrapsTheWorkspaceLayoutInAConnectionsSplit()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var main = new MainForm();
            main.CreateControl();

            var shellSplit = GetField<SplitContainer>(main, "_shellSplit");
            Assert.Equal(Orientation.Vertical, shellSplit.Orientation);
            Assert.Same(shellSplit, Assert.Single(main.Controls.OfType<SplitContainer>()));

            // Panel1 is the connections panel by default; Panel2 keeps the whole existing
            // workspace-over-queue layout intact.
            Assert.Single(shellSplit.Panel1.Controls.OfType<ConnectionsPanelControl>());
            var workspaceSplit = Assert.Single(shellSplit.Panel2.Controls.OfType<SplitContainer>());
            Assert.Equal(Orientation.Horizontal, workspaceSplit.Orientation);
            Assert.Single(workspaceSplit.Panel1.Controls.OfType<TabControl>());
        });
    }

    [Fact]
    public void MovingThePanelAcrossSwapsEverySideDependentPropertyTogether()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var main = new MainForm();
            main.CreateControl();
            var shellSplit = GetField<SplitContainer>(main, "_shellSplit");
            var leftMinimum = shellSplit.Panel1MinSize;
            var rightMinimum = shellSplit.Panel2MinSize;
            var width = main.CurrentConnectionsPanelWidth();

            main.ToggleConnectionsPanelSide();

            Assert.Single(shellSplit.Panel2.Controls.OfType<ConnectionsPanelControl>());
            Assert.Single(shellSplit.Panel1.Controls.OfType<SplitContainer>());
            Assert.Equal(FixedPanel.Panel2, shellSplit.FixedPanel);
            Assert.Equal(rightMinimum, shellSplit.Panel1MinSize);
            Assert.Equal(leftMinimum, shellSplit.Panel2MinSize);

            // Carried across as a width, not a splitter position, so the panel is the same size
            // rather than the mirror of wherever the splitter happened to be.
            Assert.Equal(width, main.CurrentConnectionsPanelWidth());

            main.ToggleConnectionsPanelSide();

            Assert.Single(shellSplit.Panel1.Controls.OfType<ConnectionsPanelControl>());
            Assert.Equal(FixedPanel.Panel1, shellSplit.FixedPanel);
            Assert.Equal(leftMinimum, shellSplit.Panel1MinSize);
            Assert.Equal(rightMinimum, shellSplit.Panel2MinSize);
            Assert.Equal(width, main.CurrentConnectionsPanelWidth());
        });
    }

    [Fact]
    public void HidingCollapsesWhicheverPanelTheConnectionsListIsCurrentlyIn()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var main = new MainForm();
            main.CreateControl();
            var shellSplit = GetField<SplitContainer>(main, "_shellSplit");

            main.SetConnectionsPanelVisible(false);
            Assert.True(shellSplit.Panel1Collapsed);
            Assert.False(shellSplit.Panel2Collapsed);
            Assert.False(main.IsConnectionsPanelVisible);

            main.SetConnectionsPanelVisible(true);
            main.ToggleConnectionsPanelSide();
            main.SetConnectionsPanelVisible(false);

            // On the right it is Panel2 that has to collapse; collapsing Panel1 would hide the
            // workspaces instead.
            Assert.False(shellSplit.Panel1Collapsed);
            Assert.True(shellSplit.Panel2Collapsed);
            Assert.False(main.IsConnectionsPanelVisible);
        });
    }

    [Fact]
    public void AStoredWidthTooLargeForTheWindowIsClampedRatherThanThrown()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var main = new MainForm();
            main.CreateControl();
            var shellSplit = GetField<SplitContainer>(main, "_shellSplit");

            // SplitterDistance throws when it violates either minimum, which is why every write
            // goes through one clamped helper.
            SetWidth(main, DesktopUpdatePreferences.MaximumConnectionsPanelWidth * 4);

            Assert.InRange(
                shellSplit.SplitterDistance,
                shellSplit.Panel1MinSize,
                shellSplit.Width - shellSplit.SplitterWidth - shellSplit.Panel2MinSize);
        });
    }

    [Fact]
    public void TheViewCommandTogglesThePanelAndKeepsItsCheckmarkInStep()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var main = new MainForm();
            main.CreateControl();
            var menu = Assert.Single(main.Controls.OfType<MenuStrip>());
            var item = menu.Items
                .OfType<ToolStripMenuItem>()
                .SelectMany(root => root.DropDownItems.OfType<ToolStripMenuItem>())
                .Single(candidate => candidate.Text == "Connections Panel");

            item.PerformClick();

            Assert.False(main.IsConnectionsPanelVisible);
            Assert.False(item.Checked);

            item.PerformClick();

            Assert.True(main.IsConnectionsPanelVisible);
            Assert.True(item.Checked);
        });
    }

    [Fact]
    public void OpeningAConnectionCreatesAWorkspaceWhenOnlyTheFixedTabsAreOpen()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var main = new MainForm();
            main.CreateControl();
            var tabs = GetField<TabControl>(main, "_workspaceTabs");
            Assert.Equal(3, tabs.TabPages.Count);

            // Welcome and Sync tasks are not workspaces, so there is nothing to open into yet.
            InvokeOpenConnection(
                main,
                ConnectionUiTestFakes.Summary("Archive", Contracts.Ipc.StorageConnectionProvider.S3));

            // The workspace is created before the pane navigates, which is the half of this that
            // does not need a live agent behind the pipe.
            Assert.Equal(4, tabs.TabPages.Count);
            Assert.Single(tabs.TabPages[2].Controls.OfType<WorkspaceControl>());
        });
    }

    /// <summary>
    /// Starts the open and deliberately does not await it: navigating the pane reaches for the
    /// background agent over a named pipe, which is not running under test and would simply block
    /// until the harness gave up.
    /// </summary>
    private static void InvokeOpenConnection(MainForm main, Contracts.Ipc.ConnectionSummary connection)
    {
        var method = typeof(MainForm).GetMethod(
            "OpenConnectionInWorkspaceAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        _ = Assert.IsAssignableFrom<Task>(method.Invoke(main, [connection, false]));
    }

    private static void SetWidth(MainForm main, int width)
    {
        var method = typeof(MainForm).GetMethod(
            "SetConnectionsPanelWidth", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        _ = method.Invoke(main, [width]);
    }

    private static T GetField<T>(object instance, string name)
        where T : class
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsAssignableFrom<T>(field.GetValue(instance));
    }
}
