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
            Assert.Equal(4, tabs.TabPages.Count);
            Assert.Equal("Welcome", tabs.TabPages[0].AccessibleName);
            Assert.Equal("Sync tasks", tabs.TabPages[1].AccessibleName);

            var workspaceTab = tabs.GetTabRect(2);
            RaiseMouseDown(tabs, new Point(
                workspaceTab.Right - 14,
                workspaceTab.Top + (workspaceTab.Height / 2)));

            Assert.Equal(3, tabs.TabPages.Count);
            Assert.Equal("+", tabs.TabPages[^1].Text);
            Assert.Equal(0, tabs.SelectedIndex);
            Assert.Equal("Welcome", tabs.SelectedTab!.AccessibleName);

            tabs.SelectedIndex = tabs.TabPages.Count - 1;

            Assert.Equal(4, tabs.TabPages.Count);
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
                ["New tab", "Connection Manager"],
                toolbarActions);
        });
    }

    [Fact]
    public void ConnectionManagerStartsWithSavedProfilesOnlyAndNoDeadToolbarActions()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var manager = new ConnectionManagerForm();
            var profiles = GetField<TreeView>(manager, "_profileTree");
            Assert.Empty(profiles.Nodes.Cast<TreeNode>());

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

            var profiles = GetField<TreeView>(manager, "_profileTree");
            var profileNodes = profiles.Nodes
                .Cast<TreeNode>()
                .SelectMany(FlattenTree)
                .Where(node => node.Tag is ConnectionCardModel)
                .ToArray();
            var card = Assert.IsType<ConnectionCardModel>(Assert.Single(profileNodes).Tag);
            Assert.Equal(connectionId, card.ConnectionId);
            Assert.Equal("Saved archive", card.Name);
            Assert.Equal("S3 / Object Storage saved profile", card.Endpoint);
            Assert.Equal(["archive", "production"], card.DisplayTags);
            Assert.DoesNotContain("example", card.Endpoint, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(profileNodes, node => ((ConnectionCardModel)node.Tag!).ConnectionId is null);
            Assert.Equal("Providers", Assert.Single(profiles.Nodes.Cast<TreeNode>()).Text);
        });
    }

    [Fact]
    public void ConnectionManagerBuildsAndSearchesTheGroupedProfileTree()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var connections = new[]
            {
                Summary("Favorite", StorageConnectionProvider.S3, folder: "Team", tags: ["production"], favorite: true),
                Summary("Foldered", StorageConnectionProvider.Sftp, folder: "Team"),
                Summary("Provider only", StorageConnectionProvider.Ftp),
                Summary("Offline", StorageConnectionProvider.Ftps, folder: "Team", favorite: true, enabled: false)
            };
            using var manager = new ConnectionManagerForm(storageClient: new FakeStorageClient(connections));

            InvokeReloadProfiles(manager);

            var tree = GetField<TreeView>(manager, "_profileTree");
            Assert.Equal(
                ["Favorites", "Team", "Providers", "Disabled"],
                tree.Nodes.Cast<TreeNode>().Select(static node => node.Text));
            var folder = tree.Nodes.Cast<TreeNode>().Single(static node => node.Text == "Team");
            Assert.Equal(
                "Foldered",
                Assert.IsType<ConnectionCardModel>(Assert.Single(folder.Nodes.Cast<TreeNode>()).Tag).Name);
            Assert.DoesNotContain(
                tree.Nodes.Cast<TreeNode>(),
                static node => node.Text == "Folders");
            var cards = tree.Nodes
                .Cast<TreeNode>()
                .SelectMany(FlattenTree)
                .Where(static node => node.Tag is ConnectionCardModel)
                .Select(static node => (ConnectionCardModel)node.Tag!)
                .ToArray();
            Assert.Equal(4, cards.Length);
            Assert.Equal(4, cards.Select(static card => card.ConnectionId).Distinct().Count());

            GetField<TextBox>(manager, "_searchBox").Text = "production";

            var filteredRoot = Assert.Single(tree.Nodes.Cast<TreeNode>());
            Assert.Equal("Favorites", filteredRoot.Text);
            Assert.Equal(
                "Favorite",
                Assert.IsType<ConnectionCardModel>(Assert.Single(filteredRoot.Nodes.Cast<TreeNode>()).Tag).Name);
        });
    }

    [Fact]
    public void SettingsExposeStructuredWorkingCategoriesAndSshDiscoveryChoices()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var settings = new SettingsForm();
            settings.Show();
            System.Windows.Forms.Application.DoEvents();
            var categories = GetField<TreeView>(settings, "_categories");
            Assert.Equal(
                ["Transfers & sync", "Editing", "Connections & trust", "Updates"],
                categories.Nodes.Cast<TreeNode>().Select(static node => node.Text));
            var transfers = categories.Nodes.Cast<TreeNode>().Single(static node => node.Text == "Transfers & sync");
            Assert.Equal("Concurrency", Assert.Single(transfers.Nodes.Cast<TreeNode>()).Text);
            var pages = GetField<Dictionary<string, Control>>(settings, "_pages");
            Assert.Equal(4, pages.Count);
            var pageNodes = categories.Nodes.Cast<TreeNode>()
                .SelectMany(static node => node.Nodes.Count == 0 ? [node] : node.Nodes.Cast<TreeNode>())
                .ToArray();
            foreach (var node in pageNodes)
            {
                categories.SelectedNode = node;
                var selectedPage = pages[node.Name];
                Assert.Equal(0, selectedPage.Parent!.Controls.GetChildIndex(selectedPage));
            }

            var discovery = GetField<ComboBox>(settings, "_sshDiscovery");
            Assert.Equal(3, discovery.Items.Count);
            Assert.Contains(discovery.Items.Cast<object>(), choice =>
                choice.ToString()!.Contains("Manual", StringComparison.Ordinal));
            Assert.Contains(discovery.Items.Cast<object>(), choice =>
                choice.ToString()!.Contains("Ask", StringComparison.Ordinal));
            Assert.Contains(discovery.Items.Cast<object>(), choice =>
                choice.ToString()!.Contains("automatically", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void SftpTrustEditorExposesFetchFromHostAlongsideExplicitRejection()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var manager = new ConnectionManagerForm(
                StorageProviderKind.Sftp,
                sshHostKeyDiscoveryMode: SshHostKeyDiscoveryMode.Manual);
            var fields = GetField<Dictionary<string, Control>>(manager, "_editorFields");
            var fingerprint = fields["hostKeyFingerprint"];
            var actions = fingerprint.Controls
                .OfType<Button>()
                .Select(static button => button.Text)
                .ToArray();

            Assert.Contains("Fetch from host…", actions);
            Assert.Contains("Reject…", actions);
        });
    }

    private static ConnectionSummary Summary(
        string name,
        StorageConnectionProvider provider,
        string? folder = null,
        string[]? tags = null,
        bool favorite = false,
        bool enabled = true) => new(
            Guid.NewGuid(),
            name,
            provider,
            folder,
            tags ?? [],
            favorite,
            enabled,
            provider.ToString(),
            AccentColor: null,
            Version: 1);

    private static IEnumerable<TreeNode> FlattenTree(TreeNode node)
    {
        yield return node;
        foreach (TreeNode child in node.Nodes)
        {
            foreach (var descendant in FlattenTree(child))
            {
                yield return descendant;
            }
        }
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

    private sealed class FakeStorageClient : IRemoteStorageAgentClient
    {
        private readonly ConnectionSummary[] _connections;

        internal FakeStorageClient(Guid connectionId)
            : this(
                [new ConnectionSummary(
                    connectionId,
                    "Saved archive",
                    StorageConnectionProvider.S3,
                    FolderPath: null,
                    Tags: ["archive", "production"],
                    IsFavorite: false,
                    IsEnabled: true,
                    IconKey: "s3",
                    AccentColor: null,
                    Version: 1)])
        {
        }

        internal FakeStorageClient(ConnectionSummary[] connections)
        {
            _connections = connections;
        }

        public Task<ConnectionListResponse> ListConnectionsAsync(
            ConnectionListRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConnectionListResponse(
                request.ContractVersion,
                _connections));

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
