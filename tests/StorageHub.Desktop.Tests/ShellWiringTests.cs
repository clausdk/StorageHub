using System.Reflection;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

public sealed class ShellWiringTests
{
    [Fact]
    public void StartsOnWelcomeAndWorkspaceTabsExposeAWorkingCloseTarget()
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
            Assert.Equal(3, tabs.TabPages.Count);
            Assert.Equal("Welcome", tabs.TabPages[0].AccessibleName);
            Assert.Equal("Sync tasks", tabs.TabPages[1].AccessibleName);
            Assert.Equal("Welcome", tabs.TabPages[0].Text);
            Assert.Equal("Sync tasks", tabs.TabPages[1].Text);
            Assert.Equal("+", tabs.TabPages[2].Text);
            Assert.Equal(new Point(39, 5), tabs.Padding);

            var created = main.AddWorkspace(4);
            tabs.PerformLayout();
            Assert.Equal(4, tabs.TabPages.Count);
            Assert.Equal("Workspace 1 *", created.Text);
            Assert.Equal(4, Assert.Single(created.Controls.OfType<WorkspaceControl>()).Panes.Count);

            var workspaceTab = tabs.GetTabRect(2);
            RaiseMouseDown(tabs, new Point(
                workspaceTab.Right - 14,
                workspaceTab.Top + (workspaceTab.Height / 2)));

            Assert.Equal(3, tabs.TabPages.Count);
            Assert.Equal("+", tabs.TabPages[^1].Text);
            Assert.Equal(0, tabs.SelectedIndex);
            Assert.Equal("Welcome", tabs.SelectedTab!.AccessibleName);

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
            Assert.Contains("Connections Panel", labels);
            Assert.Contains("Move Connections Panel", labels);
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

            // Most toolbar buttons forward to a menu entry; the shell actions are wired
            // directly. Anything else on the strip would be a button with nothing behind it.
            string[] directActions = ["New workspace", "Connections panel", "New connection"];
            Assert.All(toolbarActions, action => Assert.True(
                directActions.Contains(action) || labels.Contains(action),
                $"Toolbar action '{action}' does not match a shell handler or a live menu command."));
            Assert.Contains("New workspace", toolbarActions);
            Assert.Contains("Connections panel", toolbarActions);
            Assert.Contains("New connection", toolbarActions);
            Assert.Contains("Refresh", toolbarActions);
            Assert.Contains("Delete", toolbarActions);

            // Commands the shell does not implement yet are absent from the menus, so their
            // toolbar buttons must not appear either.
            Assert.DoesNotContain("Compare Panes", toolbarActions);
            Assert.All(toolbar.Items.Cast<ToolStripItem>().OfType<ToolStripButton>(), button =>
                Assert.NotNull(button.Image));
        });
    }

    [Fact]
    public void WorkspacePresetHonorsDefaultOrientation()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var settingsPath = Path.Combine(
                Path.GetTempPath(),
                $"storagehub-layout-test-{Guid.NewGuid():N}.json");
            var store = new DesktopUpdatePreferencesStore(settingsPath);
            store.Save(DesktopUpdatePreferences.Defaults with { DefaultWorkspaceLayout = WorkspaceLayout.TopAndBottom });
            using var main = new MainForm(store);
            var page = main.AddWorkspace(2);
            var workspace = Assert.Single(page.Controls.OfType<WorkspaceControl>());
            var split = Assert.IsType<WorkspaceSplitNode>(workspace.LayoutModel.Root);
            Assert.Equal(WorkspaceSplitOrientation.Horizontal, split.Orientation);
        });
    }

    [Fact]
    public void TheConnectionManagerIsAPureEditorWithNoListOfItsOwn()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var manager = new ConnectionManagerForm();

            // The saved-connection list lives in the shell panel now. A second list here is what
            // used to let the two disagree about what was saved.
            Assert.Empty(Descendants<ConnectionSidebarControl>(manager));
            Assert.Empty(Descendants<TreeView>(manager));
            Assert.DoesNotContain(
                Descendants<TextBox>(manager),
                static box => box.AccessibleName == "Search connections");

            var toolbar = Assert.Single(
                manager.Controls.OfType<ToolStrip>(),
                candidate => candidate.AccessibleName == "Connection Manager commands");
            var actions = toolbar.Items.Cast<ToolStripItem>()
                .Select(item => item.AccessibleName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToArray();
            Assert.Equal(
                ["New connection", "Test connection", "Open client", "Save profile", "Delete profile"],
                actions);
        });
    }

    [Fact]
    public void OpeningTheEditorOnAConnectionLoadsThatProfile()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var connectionId = Guid.NewGuid();
            var profile = new ConnectionProfileDocument(
                connectionId,
                3,
                new ConnectionProfileDraft(
                    new ConnectionProfileMetadataDocument("Loaded archive", Tags: []),
                    new ConnectionEndpointDocument(StorageConnectionProvider.S3, Host: "s3.loaded.test"),
                    new ConnectionAuthenticationDocument(ConnectionAuthenticationKind.None),
                    new ConnectionOperationalOptionsDocument()),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
            using var manager = new ConnectionManagerForm(
                connectionId: connectionId,
                profileClient: new FakeProfileClient(profile));

            InvokeLoadProfile(manager, connectionId);

            var selected = GetField<ConnectionProfileDocument>(manager, "_selectedProfile");
            Assert.Equal(connectionId, selected.ConnectionId);
            Assert.Equal("Loaded archive", selected.Draft.Metadata.DisplayName);
        });
    }

    [Fact]
    public void TheEditorCannotBeAskedToQuickConnectToASavedConnection()
    {
        // Quick Connect always creates; pointing it at a saved connection is a caller mistake
        // rather than something to silently resolve one way or the other.
        Assert.Throws<ArgumentException>(() => new ConnectionManagerForm(
            connectionId: Guid.NewGuid(),
            quickConnectMode: true));
    }

    [Fact]
    public void SettingsExposeStructuredWorkingCategoriesAndSshDiscoveryChoices()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var settings = new SettingsForm();
            settings.Size = new Size(1080, 720);
            settings.Show();
            System.Windows.Forms.Application.DoEvents();
            var categories = GetField<TreeView>(settings, "_categories");
            Assert.Equal(
                ["Transfers & sync", "Editing", "Appearance", "Workspace", "Shortcuts", "Connections & trust", "Updates"],
                categories.Nodes.Cast<TreeNode>().Select(static node => node.Text));
            var transfers = categories.Nodes.Cast<TreeNode>().Single(static node => node.Text == "Transfers & sync");
            Assert.Empty(transfers.Nodes.Cast<TreeNode>());
            var pages = GetField<Dictionary<string, Control>>(settings, "_pages");
            Assert.Equal(9 + ConnectionProviderCatalog.All.Count, pages.Count);
            var pageNodes = categories.Nodes.Cast<TreeNode>()
                .SelectMany(FlattenTree)
                .Where(node => pages.ContainsKey(node.Name))
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
                initialProvider: StorageProviderKind.Sftp,
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

    [Fact]
    public void WorkspaceDisablesExplorerExportWhenTheShellBrokerIsUnavailable()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var settingsPath = Path.Combine(
                Path.GetTempPath(),
                $"storagehub-shell-test-{Guid.NewGuid():N}.json");
            using var main = new MainForm(
                new DesktopUpdatePreferencesStore(settingsPath),
                explorerDropBrokerAvailable: false);
            var workspace = Assert.Single(main.AddWorkspace(2).Controls.OfType<WorkspaceControl>());
            var panes = workspace.Panes;

            Assert.Equal(2, panes.Count);
            Assert.All(panes, pane =>
            {
                Assert.Null(pane.BeginExplorerDropAsync);
                Assert.Null(pane.CommitExplorerDropAsync);
                Assert.Contains("unavailable", pane.ExplorerDropUnavailableReason, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("repair", pane.ExplorerDropUnavailableReason, StringComparison.OrdinalIgnoreCase);
            });
        });
    }

    [Fact]
    public void SshMfaModeEnablesPasswordAndPrivateKeyVaultFieldsTogether()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var manager = new ConnectionManagerForm(initialProvider: StorageProviderKind.Ssh);
            var fields = GetField<Dictionary<string, Control>>(manager, "_editorFields");
            var mode = Assert.IsType<ComboBox>(fields["authenticationMode"]);

            mode.SelectedItem = "Private key + password (MFA)";

            Assert.True(fields["passwordReference"].Enabled);
            Assert.True(fields["privateKeyReference"].Enabled);
            Assert.True(fields["privateKeyPassphraseReference"].Enabled);
            Assert.Contains("public-key", mode.AccessibleDescription, StringComparison.OrdinalIgnoreCase);
        });
    }

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

    private static IEnumerable<T> Descendants<T>(Control root) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void RaiseMouseDown(Control control, Point location)
    {
        var method = typeof(Control).GetMethod("OnMouseDown", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        _ = method.Invoke(control, [new MouseEventArgs(MouseButtons.Left, 1, location.X, location.Y, 0)]);
    }

    private static void InvokeLoadProfile(ConnectionManagerForm manager, Guid connectionId)
    {
        var method = typeof(ConnectionManagerForm).GetMethod(
            "LoadProfileAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(manager, [connectionId, CancellationToken.None]));
        task.GetAwaiter().GetResult();
    }

    private static T GetField<T>(object instance, string name)
        where T : class
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        // Assignable rather than exact: the shell swaps in themed subclasses of the stock
        // controls, and this helper only cares that the field holds one.
        return Assert.IsAssignableFrom<T>(field.GetValue(instance));
    }
}
