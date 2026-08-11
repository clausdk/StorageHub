using System.Reflection;

namespace StorageHub.Desktop.Tests;

public sealed class SshTerminalFormTests
{
    [Fact]
    public void ClosingCallbackAfterDisposalIsIdempotent()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var terminal = new SshTerminalForm(Guid.NewGuid(), "SSH test");
            terminal.Dispose();

            var error = Record.Exception(() =>
                typeof(SshTerminalForm)
                    .GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(terminal, [new FormClosingEventArgs(CloseReason.FormOwnerClosing, false)]));

            Assert.Null(error);
        });
    }
}
