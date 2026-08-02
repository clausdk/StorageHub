using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

public sealed class BrowserPaneControlTests
{
    [Fact]
    public void LocalDefaultPaneKeepsThisPcAndCanBecomeASavedConnectionPane()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var connectionId = Guid.NewGuid();
            var client = new FakeRemoteStorageClient(connectionId);
            using var pane = new BrowserPaneControl(
                "Source",
                showLocalDefault: true,
                new RemoteBrowserController(client));
            var selector = Assert.Single(Descendants<ComboBox>(pane));

            Assert.Equal("This PC", Assert.IsType<ConnectionCardModel>(selector.SelectedItem).Name);

            selector.SelectedIndex = 1;

            Assert.Equal(1, client.ConnectionListCount);
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
}
