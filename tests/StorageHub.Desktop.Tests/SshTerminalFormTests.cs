using System.Reflection;
using StorageHub.Contracts.Ipc;

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

    [Fact]
    public void OpeningSessionUsesConfiguredTerminalShellKeepaliveAndAppearance()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var client = new CapturingSshTerminalClient();
            var preferences = new SshTerminalPreferences(
                "screen-256color",
                "bash -l",
                90,
                "Consolas",
                12F,
                4_000,
                125,
                RenderBoldText: false);
            using var terminal = new SshTerminalForm(
                Guid.NewGuid(),
                "SSH test",
                client,
                preferences);

            terminal.StartSessionAsync().GetAwaiter().GetResult();

            var request = Assert.IsType<SshTerminalOpenRequest>(client.OpenRequest);
            Assert.Equal("screen-256color", request.TerminalName);
            Assert.Equal("bash -l", request.StartupCommand);
            Assert.Equal(90, request.KeepAliveSeconds);
            var output = Assert.IsType<RichTextBox>(typeof(SshTerminalForm)
                .GetField("_terminal", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(terminal));
            Assert.Equal("Consolas", output.Font.Name);
            Assert.Equal(12F, output.Font.Size);
            var pollTimer = Assert.IsType<System.Windows.Forms.Timer>(typeof(SshTerminalForm)
                .GetField("_pollTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(terminal));
            Assert.Equal(125, pollTimer.Interval);
        });
    }

    private sealed class CapturingSshTerminalClient : ISshTerminalAgentClient
    {
        private readonly Guid _sessionId = Guid.NewGuid();

        internal SshTerminalOpenRequest? OpenRequest { get; private set; }

        public Task<SshTerminalOpenResponse> OpenAsync(
            SshTerminalOpenRequest request,
            CancellationToken cancellationToken = default)
        {
            OpenRequest = request;
            return Task.FromResult(new SshTerminalOpenResponse(
                request.ContractVersion,
                _sessionId,
                "SSH test"));
        }

        public Task<SshTerminalWriteResponse> WriteAsync(
            SshTerminalWriteRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(
                new SshTerminalWriteResponse(request.ContractVersion, request.SessionId, request.Content.Length));

        public Task<SshTerminalReadResponse> ReadAsync(
            SshTerminalReadRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(
                new SshTerminalReadResponse(request.ContractVersion, request.SessionId, [], IsConnected: true));

        public Task<SshTerminalResizeResponse> ResizeAsync(
            SshTerminalResizeRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(
                new SshTerminalResizeResponse(request.ContractVersion, request.SessionId, Resized: true));

        public Task<SshTerminalCloseResponse> CloseAsync(
            SshTerminalCloseRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(
                new SshTerminalCloseResponse(request.ContractVersion, request.SessionId, Closed: true));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
