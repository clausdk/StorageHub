namespace StorageHub.Desktop.Tests;

public sealed class SyncProfileEditorFormTests
{
    [Fact]
    public void Editor_is_inert_until_loaded_then_really_saves_previews_and_dispatches()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var sync = new FakeSyncManagementClient();
            var storage = new FakeRemoteStorageClient(
            [
                FakeRemoteStorageClient.CreateConnection("Source"),
                FakeRemoteStorageClient.CreateConnection("Remote")
            ]);
            using var form = new SyncProfileEditorForm(sync, storage);
            _ = form.Handle;

            Assert.Equal(0, sync.ListProfilesCount);
            Assert.Equal(0, storage.ListCount);

            form.LoadProfilesAsync().GetAwaiter().GetResult();
            var saved = form.SaveCurrentProfileAsync().GetAwaiter().GetResult();
            var run = form.GeneratePreviewAsync().GetAwaiter().GetResult();

            Assert.Equal(1, sync.ListProfilesCount);
            Assert.Equal(1, storage.ListCount);
            Assert.Equal(1, sync.CreateProfileCount);
            Assert.Equal(1, sync.UpdateProfileCount);
            Assert.Equal(saved.ProfileId, run.ProfileId);
            Assert.Equal(1, sync.PreviewCount);
            Assert.Equal(1, form.Review.LoadedOperationCount);
            Assert.True(form.Review.ApproveAndDispatchAsync().GetAwaiter().GetResult());
            Assert.Contains("not reported complete", form.Review.StatusText, StringComparison.OrdinalIgnoreCase);
        });
    }
}
