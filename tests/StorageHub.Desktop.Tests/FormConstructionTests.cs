namespace StorageHub.Desktop.Tests;

public sealed class FormConstructionTests
{
    [Fact]
    public void PrimaryWindowsConstructAndDisposeOnStaWithoutBeingShown()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var main = new MainForm();
                using var manager = new ConnectionManagerForm();
                using var quickConnect = new ConnectionManagerForm(
                    StorageProviderKind.Sftp,
                    quickConnectMode: true,
                    "sftp.example.com");
                using var settings = new SettingsForm();
                using var sync = new SyncProfileEditorForm();
                using var schedules = new ScheduleManagerForm();
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The WinForms construction check timed out.");
        Assert.Null(failure);
    }
}
