using System.Reflection;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

public sealed class ShellWiringTests
{
    [Fact]
    public void WorkspaceTabsExposeAWorkingCloseTargetIncludingForTheLastWorkspace()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var main = new MainForm();
            main.CreateControl();
            main.PerformLayout();
            var tabs = GetField<TabControl>(main, "_workspaceTabs");
            tabs.CreateControl();
            tabs.PerformLayout();

            Assert.Equal(TabDrawMode.OwnerDrawFixed, tabs.DrawMode);
            Assert.Equal(2, tabs.TabPages.Count);

            var firstTab = tabs.GetTabRect(0);
            RaiseMouseDown(tabs, new Point(firstTab.Right - 14, firstTab.Top + (firstTab.Height / 2)));

            Assert.Single(tabs.TabPages);
            Assert.Equal("+", tabs.TabPages[0].Text);
            Assert.Equal(-1, tabs.SelectedIndex);

            tabs.SelectedIndex = 0;

            Assert.Equal(2, tabs.TabPages.Count);
            Assert.NotEqual("+", tabs.SelectedTab!.Text);
        });
    }

    [Fact]
    public void MainShellOnlyPresentsCommandsWithRealHandlers()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var main = new MainForm();
            var menu = Assert.Single(main.Controls.OfType<MenuStrip>());
            var labels = menu.Items
                .OfType<ToolStripMenuItem>()
                .SelectMany(root => root.DropDownItems.OfType<ToolStripMenuItem>())
                .Select(item => item.Text)
                .ToArray();

            Assert.Contains("Connection Manager...", labels);
            Assert.Contains("Refresh", labels);
            Assert.Contains("Select All", labels);
            Assert.Contains("Settings...", labels);
            Assert.Contains("Check for Updates...", labels);
            Assert.DoesNotContain("Quick Connect...", labels);
            Assert.DoesNotContain("Start Queue", labels);
            Assert.DoesNotContain("Pause All", labels);
            Assert.DoesNotContain("Compare Panes", labels);
            Assert.DoesNotContain("Run Sync", labels);

            var toolbar = Assert.Single(
                main.Controls.OfType<ToolStrip>(),
                candidate => candidate.AccessibleName == "Main toolbar");
            var toolbarActions = toolbar.Items.Cast<ToolStripItem>()
                .Select(item => item.AccessibleName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToArray();
            Assert.Equal(
                ["New tab", "Connection Manager", "Back", "Forward", "Up", "Refresh"],
                toolbarActions);
        });
    }

    [Fact]
    public void ConnectionManagerStartsWithSavedProfilesOnlyAndNoDeadToolbarActions()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var manager = new ConnectionManagerForm();
            var profiles = GetField<ListBox>(manager, "_profileCards");
            Assert.Empty(profiles.Items.Cast<object>());

            var toolbar = Assert.Single(
                manager.Controls.OfType<ToolStrip>(),
                candidate => candidate.AccessibleName == "Connection Manager commands");
            var actions = toolbar.Items.Cast<ToolStripItem>()
                .Select(item => item.AccessibleName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToArray();
            Assert.Equal(
                ["New connection", "Test connection", "Save profile", "Delete profile"],
                actions);
        });
    }

    [Fact]
    public void ReloadingConnectionManagerNeverAddsProviderExamplesAsProfiles()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var connectionId = Guid.NewGuid();
            using var manager = new ConnectionManagerForm(storageClient: new FakeStorageClient(connectionId));

            InvokeReloadProfiles(manager);

            var profiles = GetField<ListBox>(manager, "_profileCards");
            var card = Assert.IsType<ConnectionCardModel>(Assert.Single(profiles.Items.Cast<object>()));
            Assert.Equal(connectionId, card.ConnectionId);
            Assert.Equal("Saved archive", card.Name);
            Assert.Equal("S3 / Object Storage saved profile", card.Endpoint);
            Assert.DoesNotContain("example", card.Endpoint, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(profiles.Items.Cast<ConnectionCardModel>(), item => item.ConnectionId is null);
        });
    }

    private static void RaiseMouseDown(Control control, Point location)
    {
        var method = typeof(Control).GetMethod("OnMouseDown", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        _ = method.Invoke(control, [new MouseEventArgs(MouseButtons.Left, 1, location.X, location.Y, 0)]);
    }

    private static void InvokeReloadProfiles(ConnectionManagerForm manager)
    {
        var method = typeof(ConnectionManagerForm).GetMethod(
            "ReloadProfilesAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(manager, [CancellationToken.None]));
        task.GetAwaiter().GetResult();
    }

    private static T GetField<T>(object instance, string name)
        where T : class
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field.GetValue(instance));
    }

    private sealed class FakeStorageClient(Guid connectionId) : IRemoteStorageAgentClient
    {
        public Task<ConnectionListResponse> ListConnectionsAsync(
            ConnectionListRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConnectionListResponse(
                request.ContractVersion,
                [new ConnectionSummary(
                    connectionId,
                    "Saved archive",
                    StorageConnectionProvider.S3,
                    FolderPath: null,
                    Tags: [],
                    IsFavorite: false,
                    IsEnabled: true,
                    IconKey: "s3",
                    AccentColor: null,
                    Version: 1)]));

        public Task<ConnectionTestResponse> TestConnectionAsync(
            ConnectionTestRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StorageListPageResponse> ListStorageAsync(
            StorageListPageRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
