using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

public sealed class ObjectInspectorControllerTests
{
    [Fact]
    public async Task ControllerRefreshesAllSectionsAndAppendsBoundedVersionPages()
    {
        var address = CreateAddress();
        var client = new FakeInspectorClient();
        await using var controller = new ObjectInspectorController(client, address);

        var first = await controller.RefreshAsync();
        var second = await controller.LoadMoreVersionsAsync();

        Assert.Single(first.Versions);
        Assert.True(first.CanLoadMoreVersions);
        Assert.Equal(2, second.Versions.Count);
        Assert.False(second.CanLoadMoreVersions);
        Assert.Single(second.Metadata);
        Assert.Single(second.Tags);
        Assert.Equal([null, "page-2"], client.VersionTokens);
    }

    [Fact]
    public void FormConstructsInertlyLoadsStateAndBoundsWindowTitle()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var address = CreateAddress() with
            {
                RelativePath = new string('a', 180) + "/item.bin"
            };
            var client = new FakeInspectorClient();
            using var form = new ObjectInspectorForm(client, address);
            _ = form.Handle;

            Assert.Empty(client.VersionTokens);
            form.LoadInspectorAsync().GetAwaiter().GetResult();

            Assert.Equal(1, form.DisplayedVersionCount);
            Assert.Equal(1, form.DisplayedMetadataCount);
            Assert.Equal(1, form.DisplayedTagCount);
            Assert.True(form.CanLoadMoreVersions);
            Assert.True(form.Text.Length < 130);
            Assert.Contains("Loaded", form.StatusText, StringComparison.Ordinal);
        });
    }

    private static ObjectInspectorAddress CreateAddress() => new(
        Guid.NewGuid(),
        "root-1",
        "folder/item.bin");

    private sealed class FakeInspectorClient : IObjectInspectorAgentClient
    {
        public List<string?> VersionTokens { get; } = [];

        public Task<ObjectVersionListResponse> ListVersionsAsync(
            ObjectVersionListRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VersionTokens.Add(request.ContinuationToken);
            var secondPage = request.ContinuationToken is not null;
            return Task.FromResult(new ObjectVersionListResponse(
                ObjectInspectorIpcContract.CurrentVersion,
                request.Address,
                [new ObjectVersionSummary(
                    secondPage ? "version-1" : "version-2",
                    null,
                    secondPage ? 1 : 2,
                    new DateTimeOffset(2026, 8, secondPage ? 1 : 2, 10, 0, 0, TimeSpan.Zero),
                    IsLatest: !secondPage,
                    IsDeleteMarker: false)],
                secondPage ? null : "page-2"));
        }

        public Task<ObjectMetadataGetResponse> GetMetadataAsync(
            ObjectMetadataGetRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ObjectMetadataGetResponse(
                ObjectInspectorIpcContract.CurrentVersion,
                request.Address,
                [new ObjectMetadataEntry("owner", "storage-team")]));
        }

        public Task<ObjectTagsGetResponse> GetTagsAsync(
            ObjectTagsGetRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ObjectTagsGetResponse(
                ObjectInspectorIpcContract.CurrentVersion,
                request.Address,
                [new ObjectTagEntry("tier", "archive")]));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
