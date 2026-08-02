namespace StorageHub.Desktop.Tests;

public sealed class SyncRunReviewControlTests
{
    [Fact]
    public void Review_loads_immutable_pages_and_approves_the_exact_revision_and_digest()
    {
        RunOnSta(() =>
        {
            var client = new FakeSyncManagementClient();
            using var control = new SyncRunReviewControl(client);

            Assert.Equal(0, client.PlanPageCount);
            control.ShowPreviewAsync(client.Run).GetAwaiter().GetResult();

            Assert.Equal(1, control.LoadedOperationCount);
            Assert.Equal(0, control.LoadedConflictCount);
            Assert.Contains("no provider changes have been requested", control.StatusText, StringComparison.OrdinalIgnoreCase);
            var reviewed = control.CurrentRun!;

            Assert.True(control.ApproveAndDispatchAsync().GetAwaiter().GetResult());

            Assert.Equal(reviewed.SyncRunId, client.LastApproval?.SyncRunId);
            Assert.Equal(reviewed.Revision, client.LastApproval?.ExpectedRevision);
            Assert.Equal(reviewed.ApprovalSha256, client.LastApproval?.ApprovalSha256);
            Assert.Contains("durably dispatched", control.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not reported complete", control.StatusText, StringComparison.OrdinalIgnoreCase);
        });
    }

    internal static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The sync UI check timed out.");
        Assert.Null(failure);
    }
}
