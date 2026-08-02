namespace StorageHub.Desktop.Tests;

public sealed class SyncRunsControlTests
{
    [Fact]
    public void Runs_surface_is_inert_until_a_known_run_is_explicitly_loaded()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var client = new FakeSyncManagementClient();
            using var form = new Form();
            using var control = new SyncRunsControl(client);
            form.Controls.Add(control);
            _ = form.Handle;
            _ = control.Handle;
            System.Windows.Forms.Application.DoEvents();

            Assert.Equal(0, client.RunStatusCount);
            control.LoadRunAsync(client.Run.SyncRunId).GetAwaiter().GetResult();

            Assert.Equal(1, client.RunStatusCount);
            Assert.Equal(1, client.PlanPageCount);
            Assert.Equal(1, client.ConflictPageCount);
            Assert.Equal(client.Run.SyncRunId, control.Review.CurrentRun?.SyncRunId);
        });
    }
}
