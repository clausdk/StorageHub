namespace StorageHub.Desktop.Tests;

public sealed class SyncRunReviewControlTests
{
    private static readonly object StaExecutionGate = new();

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
        ArgumentNullException.ThrowIfNull(action);

        // WinForms and Krypton both maintain process-wide UI state. xUnit runs test
        // classes concurrently, so starting one STA per class allowed multiple forms
        // to initialize that shared state at once. Serialize the UI checks, and start
        // the timeout only after this test owns the gate so queued tests do not spend
        // their execution budget waiting for another STA check.
        lock (StaExecutionGate)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception error)
                {
                    failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error);
                }
            })
            {
                IsBackground = true,
                Name = "StorageHub Desktop test STA"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            Assert.True(
                thread.Join(TimeSpan.FromMinutes(1)),
                "The serialized WinForms UI check timed out after one minute.");
            failure?.Throw();
        }
    }
}
