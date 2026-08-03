using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

public sealed class ScheduleManagerFormTests
{
    [Fact]
    public void Friendly_builder_creates_weekly_cron_and_keeps_the_real_time_zone_id()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var schedules = new FakeScheduleManagementClient();
            var sync = new FakeSyncManagementClient();
            var profileId = Guid.NewGuid();
            sync.SeedProfile(CreateProfile(profileId));
            using var form = new ScheduleManagerForm(schedules, sync);
            _ = form.Handle;
            form.LoadSchedulesAsync().GetAwaiter().GetResult();

            var frequency = Find<ComboBox>(form, "ScheduleFrequency");
            frequency.SelectedIndex = frequency.Items.Cast<object>()
                .Select((item, index) => (item, index))
                .Single(entry => entry.item.ToString() == "Every week")
                .index;
            Find<ComboBox>(form, "ScheduleWeekDay").SelectedItem = DayOfWeek.Tuesday;
            Find<DateTimePicker>(form, "ScheduleTime").Value = DateTime.Today.AddHours(14).AddMinutes(30);

            var created = form.SaveCurrentScheduleAsync().GetAwaiter().GetResult();
            var timeZone = Find<ComboBox>(form, "ScheduleTimeZone");

            Assert.Equal("30 14 * * 2", created.CronExpression);
            Assert.Equal(TimeZoneInfo.Local.Id, created.TimeZoneId);
            Assert.StartsWith("(UTC", timeZone.SelectedItem?.ToString(), StringComparison.Ordinal);
            Assert.Contains("Tuesday", Find<Label>(form, "ScheduleSummary").Text, StringComparison.Ordinal);
            Assert.False(Find<TextBox>(form, "ScheduleCron").Visible);
        });
    }

    [Fact]
    public void Manager_is_inert_then_really_creates_enables_updates_and_deletes_safe_automatic_schedules()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var schedules = new FakeScheduleManagementClient();
            var sync = new FakeSyncManagementClient();
            var profileId = Guid.NewGuid();
            sync.SeedProfile(CreateProfile(profileId));
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
            Assert.Equal(ScheduleIpcExecutionMode.SafeAutomatic, created.ExecutionMode);
            Assert.False(created.Enabled);
            Assert.True(enabled.Enabled);
            Assert.True(updated.Revision > enabled.Revision);
            Assert.Equal(1, schedules.CreateCount);
            Assert.Equal(1, schedules.SetEnabledCount);
            Assert.Equal(1, schedules.UpdateCount);
            Assert.Equal(1, schedules.DeleteCount);
            Assert.Equal(0, form.DisplayedScheduleCount);
            Assert.Contains("run history", form.StatusText, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static SyncProfileDocument CreateProfile(Guid profileId) => new(
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
        FakeSyncManagementClient.Now);

    private static T Find<T>(Control root, string name)
        where T : Control
    {
        if (root is T match && root.Name == name)
        {
            return match;
        }

        foreach (Control child in root.Controls)
        {
            try
            {
                return Find<T>(child, name);
            }
            catch (InvalidOperationException)
            {
                // Search the next branch.
            }
        }

        throw new InvalidOperationException($"Control '{name}' was not found.");
    }
}
