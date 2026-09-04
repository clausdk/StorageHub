namespace StorageHub.Desktop.Tests;

public sealed class SyncRunsControlTests
{
    [Fact]
    public void Sync_tasks_hosts_run_history_and_review_as_an_internal_view()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var form = new Form { ClientSize = new Size(1_400, 760) };
            using var control = new SyncTasksOverviewControl(new FakeSyncManagementClient());
            form.Controls.Add(control);
            form.Show();
            System.Windows.Forms.Application.DoEvents();
            var views = Assert.Single(control.Controls.OfType<TabControl>());
            Assert.Equal(["Tasks", "Run history and review"],
                views.TabPages.Cast<TabPage>().Select(page => page.Text));
            Assert.Same(
                control.RunReview,
                Assert.Single(views.TabPages[1].Controls.OfType<SyncRunsControl>()));
            var content = Assert.Single(views.TabPages[0].Controls.OfType<TableLayoutPanel>());
            var metrics = Assert.Single(
                content.Controls.OfType<TableLayoutPanel>(),
                candidate => candidate.ColumnCount == 3);
            Assert.True(metrics.Height >= 108);
            Assert.All(metrics.Controls.Cast<Control>(), card =>
                Assert.True(card.Bottom <= metrics.ClientSize.Height, "A sync summary card was clipped."));

            var requested = 0;
            control.ReviewRunRequested += (_, _) => requested++;
            control.ShowRunReview();

            Assert.Equal(1, views.SelectedIndex);
            Assert.Equal(1, requested);

            var screenshotDirectory = Environment.GetEnvironmentVariable("STORAGEHUB_SYNC_SCREENSHOT_DIR");
            if (!string.IsNullOrWhiteSpace(screenshotDirectory))
            {
                Directory.CreateDirectory(screenshotDirectory);
                using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
                form.DrawToBitmap(bitmap, form.ClientRectangle);
                bitmap.Save(Path.Combine(screenshotDirectory, "sync-run-history.png"));
                views.SelectedIndex = 0;
                System.Windows.Forms.Application.DoEvents();
                form.DrawToBitmap(bitmap, form.ClientRectangle);
                bitmap.Save(Path.Combine(screenshotDirectory, "sync-tasks.png"));
            }
        });
    }

    [Fact]
    public void Runs_surface_browses_history_and_loads_a_selected_run()
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
            control.RefreshHistoryAsync().GetAwaiter().GetResult();
            Assert.Equal(1, client.RunListCount);
            Assert.Equal(1, control.DisplayedRunCount);
            control.LoadRunAsync(client.Run.SyncRunId).GetAwaiter().GetResult();

            Assert.Equal(1, client.RunStatusCount);
            Assert.Equal(1, client.PlanPageCount);
            Assert.Equal(1, client.ConflictPageCount);
            Assert.Equal(client.Run.SyncRunId, control.Review.CurrentRun?.SyncRunId);
        });
    }
}
