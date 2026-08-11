using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

public sealed class ExternalEditorControllerTests
{
    [Fact]
    public void Unsupported_protected_download_can_continue_unprotected_and_remember_the_choice()
    {
        var root = Path.Combine(Path.GetTempPath(), $"storagehub-editor-warning-{Guid.NewGuid():N}");
        try
        {
            var store = new DesktopUpdatePreferencesStore(Path.Combine(root, "settings.json"));
            var client = new FakeInspectorClient();
            var warningCalls = 0;
            SyncRunReviewControlTests.RunOnSta(() =>
            {
                using var owner = new Form();
                var controller = new ExternalEditorController(
                    store,
                    client,
                    (_, _) =>
                    {
                        warningCalls++;
                        return new UnsafeExternalEditDecision(Continue: true, DontShowAgain: true);
                    });
                try
                {
                    var address = new ObjectInspectorAddress(
                        Guid.NewGuid(),
                        "bucket-root",
                        "folder/file.txt",
                        NativeItemId: "native-id",
                        VersionId: "version-1",
                        EntityTag: "etag-1");
                    var response = controller.DownloadForEditingAsync(
                        owner,
                        address,
                        "file.txt",
                        1024,
                        DesktopUpdatePreferences.Defaults,
                        CancellationToken.None).GetAwaiter().GetResult();

                    Assert.NotNull(response);
                    Assert.Equal("contents", System.Text.Encoding.UTF8.GetString(response.Content));
                    Assert.Equal(1, warningCalls);
                    Assert.Equal(2, client.Requests.Count);
                    Assert.Equal("etag-1", client.Requests[0].Address.EntityTag);
                    Assert.Null(client.Requests[1].Address.EntityTag);
                    Assert.Null(client.Requests[1].Address.VersionId);
                    Assert.False(store.Load().WarnBeforeUnsafeExternalEdit);

                    var secondResponse = controller.DownloadForEditingAsync(
                        owner,
                        address,
                        "file.txt",
                        1024,
                        store.Load(),
                        CancellationToken.None).GetAwaiter().GetResult();
                    Assert.NotNull(secondResponse);
                    Assert.Equal(1, warningCalls);
                    Assert.Equal(4, client.Requests.Count);
                    Assert.Null(client.Requests[3].Address.EntityTag);
                }
                finally
                {
                    controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeInspectorClient : IObjectInspectorAgentClient
    {
        internal List<EditableFileDownloadRequest> Requests { get; } = [];

        public Task<EditableFileDownloadResponse> DownloadEditableFileAsync(
            EditableFileDownloadRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(request.Address.EntityTag is not null || request.Address.VersionId is not null
                ? new EditableFileDownloadResponse(
                    request.ContractVersion,
                    request.Address,
                    [],
                    Failure: new StorageIpcFailure(
                        "storage.inspector.unsupported",
                        StorageIpcFailureCategory.Unsupported,
                        "The provider does not support this inspection safely.",
                        IsTransient: false))
                : new EditableFileDownloadResponse(
                    request.ContractVersion,
                    request.Address,
                    System.Text.Encoding.UTF8.GetBytes("contents"),
                    "text/plain"));
        }

        public Task<ObjectVersionListResponse> ListVersionsAsync(ObjectVersionListRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ObjectMetadataGetResponse> GetMetadataAsync(ObjectMetadataGetRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ObjectTagsGetResponse> GetTagsAsync(ObjectTagsGetRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
