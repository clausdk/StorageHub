namespace StorageHub.Desktop.Tests;

public sealed class FormConstructionTests
{
    [Fact]
    public void PrimaryWindowsConstructAndDisposeOnStaWithoutBeingShown()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var main = new MainForm();
            using var manager = new ConnectionManagerForm();
            using var editor = new ConnectionManagerForm(connectionId: Guid.NewGuid());
            using var quickConnect = new ConnectionManagerForm(
                initialProvider: StorageProviderKind.Sftp,
                quickConnectMode: true,
                initialEndpoint: "sftp.example.com");
            using var settings = new SettingsForm();
            using var sync = new SyncProfileEditorForm();
            using var schedules = new ScheduleManagerForm();
        });
    }
}
