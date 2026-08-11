using StorageHub.Contracts.Ipc;

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

            var browseA = Assert.IsType<Button>(form.Controls.Find("BrowseLocationA", true).Single());
            var browseB = Assert.IsType<Button>(form.Controls.Find("BrowseLocationB", true).Single());
            var folderA = Assert.IsType<TextBox>(form.Controls.Find("LocationAFolder", true).Single());
            var folderB = Assert.IsType<TextBox>(form.Controls.Find("LocationBFolder", true).Single());

            Assert.Equal(0, sync.ListProfilesCount);
            Assert.Equal(0, storage.ListCount);
            Assert.False(browseA.Enabled);
            Assert.False(browseB.Enabled);
            Assert.False(folderA.Enabled);
            Assert.False(folderB.Enabled);

            form.LoadProfilesAsync().GetAwaiter().GetResult();
            Assert.True(browseA.Enabled);
            Assert.True(browseB.Enabled);
            Assert.True(folderA.Enabled);
            Assert.True(folderB.Enabled);
            Assert.Contains("Connection root", folderA.PlaceholderText, StringComparison.Ordinal);
            Assert.Contains("Connection root", folderB.PlaceholderText, StringComparison.Ordinal);
            var compatibility = Assert.IsType<CheckBox>(
                form.Controls.Find("AllowNonAtomicDestinationWrites", true).Single());
            Assert.False(compatibility.Checked);
            compatibility.Checked = true;
            var saved = form.SaveCurrentProfileAsync().GetAwaiter().GetResult();
            var run = form.GeneratePreviewAsync().GetAwaiter().GetResult();

            Assert.Equal(1, sync.ListProfilesCount);
            Assert.Equal(1, storage.ListCount);
            Assert.Equal(1, sync.CreateProfileCount);
            Assert.Equal(1, sync.UpdateProfileCount);
            Assert.True(saved.Draft.AllowNonAtomicDestinationWrites);
            Assert.Equal(saved.ProfileId, run.ProfileId);
            Assert.Equal(1, sync.PreviewCount);
            Assert.Equal(1, form.Review.LoadedOperationCount);
            Assert.True(form.Review.ApproveAndDispatchAsync().GetAwaiter().GetResult());
            Assert.Contains("completed", form.Review.StatusText, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Connections_remain_selectable_when_sync_profile_loading_fails()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var sync = new FakeSyncManagementClient
            {
                ListProfilesError = new InvalidDataException("The local agent rejected the sync request.")
            };
            var storage = new FakeRemoteStorageClient(
            [
                FakeRemoteStorageClient.CreateConnection("Local files"),
                FakeRemoteStorageClient.CreateConnection("S3 archive")
            ]);
            using var form = new SyncProfileEditorForm(sync, storage);
            _ = form.Handle;

            form.LoadProfilesAsync().GetAwaiter().GetResult();

            var browseA = Assert.IsType<Button>(form.Controls.Find("BrowseLocationA", true).Single());
            var browseB = Assert.IsType<Button>(form.Controls.Find("BrowseLocationB", true).Single());
            Assert.True(browseA.Enabled);
            Assert.True(browseB.Enabled);
            Assert.Contains("Connections loaded", form.StatusText, StringComparison.Ordinal);
            Assert.Contains("sync profiles are unavailable", form.StatusText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Descriptive_behavior_menu_exposes_and_persists_every_preset()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var sync = new FakeSyncManagementClient();
            var storage = new FakeRemoteStorageClient(
            [
                FakeRemoteStorageClient.CreateConnection("Location A"),
                FakeRemoteStorageClient.CreateConnection("Location B")
            ]);
            using var form = new SyncProfileEditorForm(sync, storage);
            _ = form.Handle;
            form.LoadProfilesAsync().GetAwaiter().GetResult();

            var picker = Assert.IsType<SyncBehaviorPickerControl>(
                form.Controls.Find("SyncBehaviorPicker", true).Single());
            var choices = Enum.GetValues<SyncIpcBehavior>();
            Assert.Equal(SyncIpcBehavior.UpdateAToB, picker.SelectedBehavior);
            foreach (var behavior in choices)
            {
                var button = Assert.IsAssignableFrom<Button>(
                    picker.Controls.Find($"Behavior{behavior}", true).Single());
                Assert.False(string.IsNullOrWhiteSpace(button.AccessibleDescription));
            }

            picker.SelectedBehavior = SyncIpcBehavior.MirrorBToA;
            var saved = form.SaveCurrentProfileAsync().GetAwaiter().GetResult();

            Assert.Equal(SyncIpcBehavior.MirrorBToA, saved.Draft.Behavior);
        });
    }
}
