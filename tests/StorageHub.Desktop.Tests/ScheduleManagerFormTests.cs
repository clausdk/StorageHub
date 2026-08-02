using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

public sealed class ScheduleManagerFormTests
{
    [Fact]
    public void Manager_is_inert_then_really_creates_enables_updates_and_deletes_preview_schedules()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var schedules = new FakeScheduleManagementClient();
            var sync = new FakeSyncManagementClient();
            var profileId = Guid.NewGuid();
            sync.SeedProfile(new SyncProfileDocument(
                profileId,
                new SyncProfileDraftDocument(
                    "Documents sync",
                    Guid.NewGuid(),
                    "documents",
                    Guid.NewGuid(),
                    "backup",
                    SyncIpcDirection.LeftToRight,
                    SyncIpcDeletionMode.Disabled,
                    SyncIpcConflictPolicy.Block,
                    MaximumDeletionCount: 100,
                    MaximumDeletionPercentage: 10,
                    Overwrite: false,
                    TransferBufferSize: 65_536,
                    Enabled: true),
                Revision: 1,
                FakeSyncManagementClient.Now,
                FakeSyncManagementClient.Now));
            using var form = new ScheduleManagerForm(schedules, sync);
            _ = form.Handle;

            Assert.Equal(0, schedules.ListCount);
            Assert.Equal(0, sync.ListProfilesCount);

            form.LoadSchedulesAsync().GetAwaiter().GetResult();
            var created = form.SaveCurrentScheduleAsync().GetAwaiter().GetResult();
            var enabled = form.SetCurrentEnabledAsync(enabled: true).GetAwaiter().GetResult();
            var updated = form.SaveCurrentScheduleAsync().GetAwaiter().GetResult();
            form.DeleteCurrentScheduleAsync().GetAwaiter().GetResult();

            Assert.Equal(profileId, created.ProfileId);
            Assert.Equal(ScheduleIpcExecutionMode.PreviewOnly, created.ExecutionMode);
            Assert.False(created.Enabled);
            Assert.True(enabled.Enabled);
            Assert.True(updated.Revision > enabled.Revision);
            Assert.Equal(1, schedules.CreateCount);
            Assert.Equal(1, schedules.SetEnabledCount);
            Assert.Equal(1, schedules.UpdateCount);
            Assert.Equal(1, schedules.DeleteCount);
            Assert.Equal(0, form.DisplayedScheduleCount);
            Assert.Contains("preview", form.StatusText, StringComparison.OrdinalIgnoreCase);
        });
    }
}
