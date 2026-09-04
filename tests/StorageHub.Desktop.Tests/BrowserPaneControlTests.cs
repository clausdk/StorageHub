using StorageHub.Contracts.Ipc;
using System.Reflection;

namespace StorageHub.Desktop.Tests;

public sealed class BrowserPaneControlTests
{
    [Fact]
    public void ParentNavigationRowIsPinnedAboveFilesOnlyWhenUpIsAvailable()
    {
        var file = new BrowserListItem(
            "report.txt",
            "1 KiB",
            "File",
            string.Empty,
            string.Empty,
            "report.txt",
            Kind: StorageItemKind.File);

        var nested = BrowserPaneControl.ComposeVisibleItems([file], canNavigateUp: true);
        var root = BrowserPaneControl.ComposeVisibleItems([file], canNavigateUp: false);

        Assert.Collection(
            nested,
            parent =>
            {
                Assert.Equal("..", parent.Name);
                Assert.Equal("Parent folder", parent.Type);
                Assert.True(parent.IsContainer);
                Assert.True(parent.IsParentNavigation);
            },
            item => Assert.Same(file, item));
        Assert.Same(file, Assert.Single(root));
    }

    [Fact]
    public void FileCommandBarHasRightAlignedSelectionAwareOverflowMenu()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var pane = new BrowserPaneControl("Pane 1", showLocalDefault: true);
            var more = Assert.IsType<ToolStripDropDownButton>(
                typeof(BrowserPaneControl)
                    .GetField("_moreButton", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(pane));

            Assert.Equal(ToolStripItemAlignment.Right, more.Alignment);
            Assert.Equal(ToolStripItemDisplayStyle.Image, more.DisplayStyle);
            Assert.Equal("More file commands", more.ToolTipText);
            var commands = more.DropDownItems
                .OfType<ToolStripMenuItem>()
                .Select(item => item.Text)
                .ToArray();
            Assert.Contains("Copy", commands);
            Assert.Contains("Move", commands);
            Assert.Contains("Paste", commands);
            Assert.Contains("Delete", commands);
            Assert.DoesNotContain("Copy to other pane", commands);
            Assert.DoesNotContain("Move to other pane", commands);
            Assert.Contains("Refresh", commands);
            Assert.Contains("Select all", commands);
            Assert.Contains("Properties...", commands);
        });
    }

    [Fact]
    public void ReplacingVirtualRowsPrimesIconsAndImmediatelyReturnsEveryNewRow()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var pane = new BrowserPaneControl("Pane 1", showLocalDefault: true);
            using var host = new Form { Size = new Size(900, 600) };
            host.Controls.Add(pane);
            host.Show();
            System.Windows.Forms.Application.DoEvents();
            pane.SetItems(
            [
                new BrowserListItem("one.txt", "1 B", "File", "", "", "C:\\one.txt", Kind: StorageItemKind.File),
                new BrowserListItem("two.png", "2 B", "File", "", "", "C:\\two.png", Kind: StorageItemKind.File),
                new BrowserListItem("folder", "", "Folder", "", "", "C:\\folder", true, StorageItemKind.Directory)
            ]);
            var list = Assert.Single(Descendants<ListView>(pane));
            var imageCountAfterUpdate = list.SmallImageList!.Images.Count;

            Assert.Equal(3, list.VirtualListSize);
            Assert.Equal(["folder", "one.txt", "two.png"],
                Enumerable.Range(0, list.VirtualListSize).Select(index => list.Items[index].Text));
            Assert.Equal(imageCountAfterUpdate, list.SmallImageList.Images.Count);
        });
    }

    [Fact]
    public void ShellExportCacheKeyIsStableForSelectionOrderAndChangesWithRemoteIdentity()
    {
        var connectionId = Guid.NewGuid();
        var context = PaneTransferContext.Create(
            PaneTransferContextKind.SavedConnection, connectionId, "root", "folder").Value;
        var first = PaneTransferItem.Create("a.txt", "folder/a.txt", StorageItemKind.File, 1, versionId: "v1").Value;
        var second = PaneTransferItem.Create("b.txt", "folder/b.txt", StorageItemKind.File, 2, entityTag: "etag").Value;
        var forward = PaneSelectionSnapshot.Create(context, [first, second]).Value;
        var reverse = PaneSelectionSnapshot.Create(context, [second, first]).Value;
        var changed = PaneSelectionSnapshot.Create(
            context,
            [PaneTransferItem.Create("a.txt", "folder/a.txt", StorageItemKind.File, 1, versionId: "v2").Value, second]).Value;

        Assert.Equal(BrowserPaneControl.CreateShellExportKey(forward), BrowserPaneControl.CreateShellExportKey(reverse));
        Assert.NotEqual(BrowserPaneControl.CreateShellExportKey(forward), BrowserPaneControl.CreateShellExportKey(changed));
    }

    [Fact]
    public void LocalDefaultPaneKeepsThisPcAndCanBecomeASavedConnectionPane()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var connectionId = Guid.NewGuid();
            var client = new FakeRemoteStorageClient(connectionId);
            using var pane = new BrowserPaneControl(
                "Pane 1",
                showLocalDefault: true,
                new RemoteBrowserController(client));
            var selector = Assert.Single(Descendants<ComboBox>(pane));

            Assert.Equal("This PC", Assert.IsType<ConnectionCardModel>(selector.SelectedItem).Name);

            selector.SelectedIndex = 1;

            Assert.InRange(client.ConnectionListCount, 1, 2);
            Assert.Collection(
                selector.Items.Cast<ConnectionCardModel>(),
                item => Assert.Equal("This PC", item.Name),
                item => Assert.Equal("Connections Home", item.Name),
                item => Assert.Equal(connectionId, item.ConnectionId));
            Assert.Equal("Connections Home", Assert.IsType<ConnectionCardModel>(selector.SelectedItem).Name);

            selector.SelectedIndex = 2;

            var snapshot = pane.CaptureDestinationSnapshot();
            Assert.True(snapshot.IsSuccess);
            Assert.Equal(PaneTransferContextKind.SavedConnection, snapshot.Value.Context.Kind);
            Assert.Equal(connectionId, snapshot.Value.Context.ConnectionId);
            Assert.Equal("root-identity", snapshot.Value.Context.RootIdentity);
            var item = Assert.Single(snapshot.Value.Entries);
            Assert.Equal("version-1", item.VersionId);
            Assert.Equal("etag-1", item.EntityTag);
        });
    }

    [Fact]
    public void ActivatingSshClientFromDestinationConnectionsHomeEmbedsTerminalInPane()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var storageId = Guid.NewGuid();
            var sshId = Guid.NewGuid();
            var client = new FakeMixedConnectionClient(storageId, sshId);
            var terminalClient = new FakeSshTerminalClient();
            using var pane = new BrowserPaneControl(
                "Pane 2",
                showLocalDefault: false,
                new RemoteBrowserController(client),
                (connectionId, displayName) => new SshTerminalForm(
                    connectionId,
                    displayName,
                    terminalClient));
            using var host = new Form { Size = new Size(1000, 700) };
            host.Controls.Add(pane);
            host.Show();
            System.Windows.Forms.Application.DoEvents();

            var selector = Assert.Single(Descendants<ComboBox>(pane));
            Assert.Collection(
                selector.Items.Cast<ConnectionCardModel>(),
                item => Assert.Equal("This PC", item.Name),
                item => Assert.Equal("Connections Home", item.Name),
                item => Assert.Equal(storageId, item.ConnectionId),
                item => Assert.Equal(sshId, item.ConnectionId));

            var items = Assert.IsAssignableFrom<IReadOnlyList<BrowserListItem>>(
                typeof(BrowserPaneControl)
                    .GetField("_items", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(pane));
            var sshIndex = Assert.Single(
                items.Select((item, index) => (item, index)),
                candidate => candidate.item.Name == "SSH Client")
                .index;
            var list = Assert.Single(Descendants<ListView>(pane));
            list.SelectedIndices.Add(sshIndex);

            typeof(BrowserPaneControl)
                .GetMethod("OpenSelectedContainer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(pane, null);

            Assert.Equal(sshId, Assert.IsType<ConnectionCardModel>(selector.SelectedItem).ConnectionId);
            var embedded = Assert.Single(Descendants<SshTerminalForm>(pane));
            Assert.False(embedded.TopLevel);
            Assert.Equal(1, terminalClient.OpenCount);
            var destination = pane.CaptureDestinationSnapshot();
            Assert.True(destination.IsFailure);
            Assert.Equal("manual_transfer.pane.client_not_storage", destination.Error.Code);

            selector.SelectedIndex = 1;
            System.Windows.Forms.Application.DoEvents();

            Assert.Empty(Descendants<SshTerminalForm>(pane));
            Assert.Equal(1, terminalClient.CloseCount);
            Assert.True(pane.CaptureDestinationSnapshot().IsSuccess);
        });
    }

    private static IEnumerable<T> Descendants<T>(Control root)
        where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class FakeRemoteStorageClient(Guid connectionId) : IRemoteStorageAgentClient
    {
        public int ConnectionListCount { get; private set; }

        public Task<ConnectionListResponse> ListConnectionsAsync(
            ConnectionListRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectionListCount++;
            return Task.FromResult(new ConnectionListResponse(
                request.ContractVersion,
                [new ConnectionSummary(
                    connectionId,
                    "Saved S3",
                    StorageConnectionProvider.S3,
                    "bucket-a",
                    [],
                    IsFavorite: true,
                    IsEnabled: true,
                    IconKey: "s3",
                    AccentColor: null,
                    Version: 1)]));
        }

        public Task<ConnectionTestResponse> TestConnectionAsync(
            ConnectionTestRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ConnectionTestResponse(
                request.ContractVersion,
                request.ConnectionId,
                Succeeded: true,
                ElapsedMilliseconds: 1));
        }

        public Task<StorageListPageResponse> ListStorageAsync(
            StorageListPageRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new StorageListPageResponse(
                request.ContractVersion,
                request.ConnectionId,
                request.RelativePath,
                [new StorageListItem(
                    "remote.bin",
                    "remote.bin",
                    StorageItemKind.File,
                    Size: 3,
                    LastModifiedUtc: null,
                    ContentType: "application/octet-stream",
                    IsContainer: false,
                    NativeItemId: "native-1",
                    VersionId: "version-1",
                    EntityTag: "etag-1")],
                ContinuationToken: null,
                RootIdentity: "root-identity"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeMixedConnectionClient(Guid storageId, Guid sshId) : IRemoteStorageAgentClient
    {
        public Task<ConnectionListResponse> ListConnectionsAsync(
            ConnectionListRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(new ConnectionListResponse(
                request.ContractVersion,
                [
                    new ConnectionSummary(
                        sshId,
                        "SSH Client",
                        StorageConnectionProvider.Ssh,
                        null,
                        [],
                        IsFavorite: true,
                        IsEnabled: true,
                        IconKey: "ssh",
                        AccentColor: null,
                        Version: 1,
                        Type: ConnectionProfileType.Client),
                    new ConnectionSummary(
                        storageId,
                        "Saved S3",
                        StorageConnectionProvider.S3,
                        "bucket-a",
                        [],
                        IsFavorite: true,
                        IsEnabled: true,
                        IconKey: "s3",
                        AccentColor: null,
                        Version: 1)
                ]));

        public Task<ConnectionTestResponse> TestConnectionAsync(
            ConnectionTestRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(new ConnectionTestResponse(
                request.ContractVersion,
                request.ConnectionId,
                Succeeded: true,
                ElapsedMilliseconds: 1));

        public Task<StorageListPageResponse> ListStorageAsync(
            StorageListPageRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(new StorageListPageResponse(
                request.ContractVersion,
                request.ConnectionId,
                request.RelativePath,
                [],
                ContinuationToken: null,
                RootIdentity: "root-identity"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSshTerminalClient : ISshTerminalAgentClient
    {
        private readonly Guid _sessionId = Guid.NewGuid();

        public int OpenCount { get; private set; }

        public int CloseCount { get; private set; }

        public Task<SshTerminalOpenResponse> OpenAsync(
            SshTerminalOpenRequest request,
            CancellationToken cancellationToken = default)
        {
            OpenCount++;
            return Task.FromResult(new SshTerminalOpenResponse(
                request.ContractVersion,
                _sessionId,
                "SSH Client"));
        }

        public Task<SshTerminalWriteResponse> WriteAsync(
            SshTerminalWriteRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(new SshTerminalWriteResponse(
                request.ContractVersion,
                request.SessionId,
                request.Content.Length));

        public Task<SshTerminalReadResponse> ReadAsync(
            SshTerminalReadRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(new SshTerminalReadResponse(
                request.ContractVersion,
                request.SessionId,
                [],
                IsConnected: true));

        public Task<SshTerminalResizeResponse> ResizeAsync(
            SshTerminalResizeRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(new SshTerminalResizeResponse(
                request.ContractVersion,
                request.SessionId,
                Resized: true));

        public Task<SshTerminalCloseResponse> CloseAsync(
            SshTerminalCloseRequest request,
            CancellationToken cancellationToken = default)
        {
            CloseCount++;
            return Task.FromResult(new SshTerminalCloseResponse(
                request.ContractVersion,
                request.SessionId,
                Closed: true));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
