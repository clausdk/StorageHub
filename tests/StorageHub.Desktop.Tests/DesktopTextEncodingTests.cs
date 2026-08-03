namespace StorageHub.Desktop.Tests;

public sealed class DesktopTextEncodingTests
{
    [Fact]
    public void Sync_task_overview_has_no_mojibake_in_visible_text()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var overview = new SyncTasksOverviewControl(new FakeSyncManagementClient());
            _ = overview.Handle;

            var visibleText = DescendantsAndSelf(overview)
                .Select(static control => control.Text)
                .Where(static text => !string.IsNullOrEmpty(text))
                .ToArray();

            Assert.DoesNotContain(visibleText, static text =>
                text.Contains('â') ||
                text.Contains('Ã') ||
                text.Contains('\uFFFD'));
        });
    }

    private static IEnumerable<Control> DescendantsAndSelf(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls)
        {
            foreach (var descendant in DescendantsAndSelf(child))
            {
                yield return descendant;
            }
        }
    }
}
