using System.Reflection;
using StorageHub.Contracts.Ipc;
using static StorageHub.Desktop.Tests.ConnectionUiTestFakes;

namespace StorageHub.Desktop.Tests;

/// <summary>
/// The saved-connection list now lives in the shell panel rather than the Connection Manager
/// dialog, so the grouping, search, and selection cover that used to sit on the dialog lives here.
/// </summary>
public sealed class ConnectionsPanelControlTests
{
    [Fact]
    public void ThePanelListsDisabledConnectionsSoItCanShowThemGroupedApart()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var storage = new FakeStorageClient([
                Summary("Live", StorageConnectionProvider.S3),
                Summary("Offline", StorageConnectionProvider.Ftps, enabled: false)
            ]);
            using var panel = CreatePanel(storage);

            Refresh(panel);

            // The overview's cache lists enabled connections only, which is why the panel asks the
            // agent itself rather than reusing it.
            Assert.True(Assert.Single(storage.ListRequests).IncludeDisabled);
            Assert.Equal(
                ["Storage", "Disabled"],
                Descendants<ConnectionSidebarSectionHeader>(panel).Select(static header => header.Text));
        });
    }

    [Fact]
    public void ThePanelGroupsByFavouriteTypeAndFolderAndNeverInventsProfiles()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var storage = new FakeStorageClient([
                Summary(
                    "Favorite",
                    StorageConnectionProvider.S3,
                    folder: "Team",
                    tags: ["production"],
                    favorite: true,
                    health: new ConnectionHealthSnapshot(
                        ConnectionHealthState.Healthy, DateTimeOffset.UtcNow, 42, "Connection healthy")),
                Summary("Foldered", StorageConnectionProvider.Sftp, folder: "Team"),
                Summary("Provider only", StorageConnectionProvider.Ftp),
                Summary("Shell", StorageConnectionProvider.Ssh, folder: "Team", type: ConnectionProfileType.Client),
                Summary("Offline", StorageConnectionProvider.Ftps, folder: "Team", favorite: true, enabled: false)
            ]);
            using var panel = CreatePanel(storage);

            Refresh(panel);

            Assert.Equal(
                ["Favorites", "Storage", "Remote clients", "Disabled"],
                Descendants<ConnectionSidebarSectionHeader>(panel).Select(static header => header.Text));
            var groups = Descendants<ConnectionSidebarGroup>(panel).Select(static group => group.Name).ToArray();
            Assert.Contains("storage/Team", groups);
            Assert.Contains("storage/Unsorted", groups);
            Assert.Contains("clients/Team", groups);

            var cards = Descendants<ConnectionSidebarItem>(panel).Select(static item => item.Connection).ToArray();
            Assert.Equal(5, cards.Length);

            // Every row is a saved profile: the provider catalogue's examples are editor scaffolding
            // and must never reach the list.
            Assert.Equal(5, cards.Select(static card => card.ConnectionId).Distinct().Count());
            Assert.DoesNotContain(cards, static card => card.ConnectionId is null);
            Assert.Equal("Healthy · 42 ms", cards.Single(static card => card.Name == "Favorite").State);
            Assert.Equal(
                "FTP saved profile",
                cards.Single(static card => card.Name == "Provider only").Endpoint);
            Assert.DoesNotContain(cards, static card =>
                card.Endpoint.Contains("example", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void SearchNarrowsWithEveryTermRatherThanWidening()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var storage = new FakeStorageClient([
                Summary("Archive", StorageConnectionProvider.S3, tags: ["production"]),
                Summary("Archive", StorageConnectionProvider.Ftp, tags: ["staging"])
            ]);
            using var panel = CreatePanel(storage);
            Refresh(panel);

            SearchBox(panel).Text = "archive production";

            var match = Assert.Single(Descendants<ConnectionSidebarItem>(panel));
            Assert.Equal(StorageProviderKind.S3, match.Connection.Provider);
        });
    }

    [Fact]
    public void SelectingAConnectionFillsTheDetailSectionWithoutOpeningIt()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var storage = new FakeStorageClient([
                Summary("Archive", StorageConnectionProvider.S3, folder: "Team", tags: ["production"])
            ]);
            using var panel = CreatePanel(storage);
            var activations = 0;
            panel.ConnectionActivated += (_, _) => activations++;
            Refresh(panel);

            Click(Assert.Single(Descendants<ConnectionSidebarItem>(panel)));

            var values = Descendants<Label>(panel).Select(static label => label.Text).ToArray();
            Assert.Contains("Archive", values);
            Assert.Contains("S3 / Object Storage", values);
            Assert.Contains("Team", values);
            Assert.Contains("production", values);
            Assert.Equal(0, activations);
        });
    }

    [Fact]
    public void NothingSelectedShowsThePromptRatherThanStaleDetails()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var panel = CreatePanel(new FakeStorageClient([Summary("Archive", StorageConnectionProvider.S3)]));
            Refresh(panel);

            var prompt = Assert.Single(
                Descendants<Label>(panel),
                static label => label.Text == "Select a connection to see its details.");
            Assert.True(prompt.Visible);
        });
    }

    [Fact]
    public void DoubleClickingAConnectionAsksTheShellToOpenItWithTheAgentsSummary()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var id = Guid.NewGuid();
            var storage = new FakeStorageClient([
                Summary("Shell", StorageConnectionProvider.Ssh, type: ConnectionProfileType.Client, connectionId: id)
            ]);
            using var panel = CreatePanel(storage);
            ConnectionActivationEventArgs? activated = null;
            panel.ConnectionActivated += (_, args) => activated = args;
            Refresh(panel);

            typeof(Control).GetMethod("OnDoubleClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Assert.Single(Descendants<ConnectionSidebarItem>(panel)), [EventArgs.Empty]);

            Assert.NotNull(activated);
            Assert.Equal(id, activated.Connection.ConnectionId);
            Assert.False(activated.InNewPane);

            // The shell opens SSH profiles as a terminal rather than a file pane.
            Assert.Equal(PaneContentKind.SshClient, MainForm.StateFor(activated.Connection).ContentKind);
        });
    }

    [Fact]
    public void ThePencilAsksTheShellToEditThatConnection()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var id = Guid.NewGuid();
            using var panel = CreatePanel(new FakeStorageClient([
                Summary("Archive", StorageConnectionProvider.S3, connectionId: id)
            ]));
            ConnectionEditRequest? requested = null;
            panel.EditRequested += (_, request) => requested = request;
            Refresh(panel);

            var row = Assert.Single(Descendants<ConnectionSidebarItem>(panel));
            RaiseMouseUp(row, Centre(row.EditBounds));

            Assert.NotNull(requested);
            Assert.Equal(id, requested.ConnectionId);
        });
    }

    [Fact]
    public void DeletingUsesTheVersionThePanelActuallyListed()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var id = Guid.NewGuid();
            var profiles = new FakeProfileClient();
            using var panel = CreatePanel(
                new FakeStorageClient([Summary("Archive", StorageConnectionProvider.S3, connectionId: id, version: 7)]),
                profiles);
            Refresh(panel);

            InvokeDelete(panel, Assert.Single(Descendants<ConnectionSidebarItem>(panel)).Connection);

            // Pinned to the listed version, so a profile edited in between fails as a conflict
            // instead of being deleted at a revision nobody reviewed.
            var request = Assert.Single(profiles.DeleteRequests);
            Assert.Equal(id, request.ConnectionId);
            Assert.Equal(7, request.ExpectedVersion);
        });
    }

    [Fact]
    public void TheDetailSectionOffersARouteToWhateverNeedsAttention()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var panel = CreatePanel(new FakeStorageClient([
                Summary(
                    "Untrusted",
                    StorageConnectionProvider.Sftp,
                    health: new ConnectionHealthSnapshot(
                        ConnectionHealthState.NeedsAttention,
                        DateTimeOffset.UtcNow,
                        8,
                        "Host key changed",
                        RequiresTrustAction: true))
            ]));
            ConnectionEditRequest? requested = null;
            panel.EditRequested += (_, request) => requested = request;
            Refresh(panel);
            Click(Assert.Single(Descendants<ConnectionSidebarItem>(panel)));

            var attention = Assert.Single(
                Descendants<Button>(panel), static button => button.Text == "Review trust…");
            Assert.True(attention.Visible);
            attention.PerformClick();

            Assert.Equal(ConnectionEditorTab.Trust, requested?.Tab);
        });
    }

    [Fact]
    public void AHealthyConnectionOffersNoAttentionAction()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var panel = CreatePanel(new FakeStorageClient([
                Summary(
                    "Fine",
                    StorageConnectionProvider.S3,
                    health: new ConnectionHealthSnapshot(
                        ConnectionHealthState.Healthy, DateTimeOffset.UtcNow, 12, "Connection healthy"))
            ]));
            Refresh(panel);
            Click(Assert.Single(Descendants<ConnectionSidebarItem>(panel)));

            Assert.DoesNotContain(
                Descendants<Button>(panel),
                static button => button.Visible && button.Text.EndsWith('…') && button.Text.Contains("trust", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void TheDetailSectionGroupsTheProfileIntoKeyValueSections()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var id = Guid.NewGuid();
            var profile = new ConnectionProfileDocument(
                id,
                2,
                new ConnectionProfileDraft(
                    new ConnectionProfileMetadataDocument("Archive", FolderPath: "Team", Tags: ["production"]),
                    new ConnectionEndpointDocument(
                        StorageConnectionProvider.Sftp,
                        Host: "sftp.example.test",
                        Port: 2222,
                        SshHostKeyPolicy: ConnectionSshHostKeyPolicy.Pinned),
                    new ConnectionAuthenticationDocument(
                        ConnectionAuthenticationKind.UsernamePassword,
                        Username: "deploy",
                        PasswordReference: "shs_" + new string('a', 43)),
                    new ConnectionOperationalOptionsDocument(ConnectTimeoutSeconds: 45)),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
            using var panel = CreatePanel(
                new FakeStorageClient([
                    Summary("Archive", StorageConnectionProvider.Sftp, folder: "Team", tags: ["production"], connectionId: id)
                ]),
                new FakeProfileClient(profile));
            Refresh(panel);

            Click(Assert.Single(Descendants<ConnectionSidebarItem>(panel)));
            DrainDetailLoad(panel);

            var labels = Descendants<Label>(panel).Select(static label => label.Text).ToArray();
            Assert.Equal(
                ["Server", "Authentication", "Security", "Transfer", "Organisation", "Status"],
                labels.Where(static text => Sections.Contains(text)));

            // Host, port and the authentication method exist only on the profile, so their presence
            // is what proves the second pass landed.
            Assert.Contains("sftp.example.test", labels);
            Assert.Contains("2222", labels);
            Assert.Contains("Username and password", labels);
            Assert.Contains("deploy", labels);
            Assert.Contains("Pinned", labels);
            Assert.Contains("45s", labels);

            // The vault handle itself is never rendered; only that a credential is enrolled.
            Assert.Contains("Stored in vault", labels);
            Assert.DoesNotContain(labels, static text => text.StartsWith("shs_", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void EmptyProfileFieldsAreLeftOutRatherThanDrawnBlank()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var id = Guid.NewGuid();
            var profile = new ConnectionProfileDocument(
                id,
                1,
                new ConnectionProfileDraft(
                    new ConnectionProfileMetadataDocument("Scratch"),
                    new ConnectionEndpointDocument(StorageConnectionProvider.Local, RootPath: @"C:\scratch"),
                    new ConnectionAuthenticationDocument(ConnectionAuthenticationKind.None),
                    new ConnectionOperationalOptionsDocument()),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
            using var panel = CreatePanel(
                new FakeStorageClient([Summary("Scratch", StorageConnectionProvider.Local, connectionId: id)]),
                new FakeProfileClient(profile));
            Refresh(panel);

            Click(Assert.Single(Descendants<ConnectionSidebarItem>(panel)));
            DrainDetailLoad(panel);

            var keys = Descendants<Label>(panel).Select(static label => label.Text).ToArray();
            Assert.Contains("Anonymous", keys);

            // A local folder has no host, port, bucket or region, so those rows are absent rather
            // than present and empty.
            Assert.DoesNotContain("Host", keys);
            Assert.DoesNotContain("Port", keys);
            Assert.DoesNotContain("Bucket", keys);
            Assert.DoesNotContain("Region", keys);
        });
    }

    private static readonly string[] Sections =
        ["Server", "Authentication", "Security", "Transfer", "Organisation", "Status"];

    /// <summary>
    /// The second pass is started without being awaited so selection stays instant; the fake client
    /// completes synchronously, so pumping the message queue is enough to land it.
    /// </summary>
    private static void DrainDetailLoad(ConnectionsPanelControl panel)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            System.Windows.Forms.Application.DoEvents();
        }
    }

    private static ConnectionsPanelControl CreatePanel(
        FakeStorageClient storage,
        FakeProfileClient? profiles = null) =>
        new(storage, profiles ?? new FakeProfileClient(), new FakeSecretVaultClient())
        {
            Size = new Size(320, 760)
        };

    private static void Refresh(ConnectionsPanelControl panel)
    {
        panel.CreateControl();
        panel.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void InvokeDelete(ConnectionsPanelControl panel, ConnectionCardModel card)
    {
        // Goes straight at the delete rather than the trash icon: the icon path raises a modal
        // confirmation, which has no answer without a message pump.
        var method = typeof(ConnectionsPanelControl).GetMethod(
            "DeleteConfirmedAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.IsAssignableFrom<Task>(method.Invoke(panel, [card])).GetAwaiter().GetResult();
    }

    private static TextBox SearchBox(Control root) =>
        Assert.Single(Descendants<TextBox>(root), static box => box.AccessibleName == "Search connections");

    private static Point Centre(Rectangle bounds) =>
        new(bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2));

    private static void Click(Control control) =>
        typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(control, [EventArgs.Empty]);

    private static void RaiseMouseUp(Control control, Point location) =>
        typeof(Control).GetMethod("OnMouseUp", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(control, [new MouseEventArgs(MouseButtons.Left, 1, location.X, location.Y, 0)]);

    private static IEnumerable<T> Descendants<T>(Control root) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
