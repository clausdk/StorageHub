using System.Text;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

public sealed class SshTerminalForm : Form
{
    private readonly Guid _connectionId;
    private readonly ISshTerminalAgentClient _client;
    private readonly bool _ownsClient;
    private readonly RichTextBox _terminal;
    private readonly ToolStripStatusLabel _status;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly System.Windows.Forms.Timer _resizeTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();
    private readonly VtTerminalBuffer _buffer = new(120, 30);
    private readonly Font _boldTerminalFont;
    private Guid _sessionId;
    private bool _polling;
    private bool _closingSession;
    private bool _shutdownStarted;
    private bool _openStarted;
    private Task _closeTask = Task.CompletedTask;

    public SshTerminalForm(
        Guid connectionId,
        string displayName,
        ISshTerminalAgentClient? client = null)
    {
        if (connectionId == Guid.Empty)
        {
            throw new ArgumentException("An SSH connection ID is required.", nameof(connectionId));
        }

        _connectionId = connectionId;
        _ownsClient = client is null;
        _client = client ?? new NamedPipeSshTerminalAgentClient();
        Text = $"{displayName} — SSH Terminal";
        AccessibleName = $"SSH terminal for {displayName}";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(640, 400);
        Size = new Size(1000, 680);
        BackColor = Color.FromArgb(12, 18, 28);
        KeyPreview = true;

        _terminal = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            DetectUrls = false,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(12, 18, 28),
            ForeColor = Color.FromArgb(226, 232, 240),
            Font = new Font("Cascadia Mono", 10F, FontStyle.Regular, GraphicsUnit.Point),
            WordWrap = false,
            ScrollBars = RichTextBoxScrollBars.ForcedVertical,
            HideSelection = false,
            AccessibleName = "Interactive SSH terminal output",
            AccessibleDescription = "Type to send input. Select text and press Control+C to copy."
        };
        _boldTerminalFont = new Font(_terminal.Font, FontStyle.Bold);
        _terminal.KeyDown += TerminalKeyDown;
        _terminal.KeyPress += TerminalKeyPress;
        _terminal.Resize += TerminalResize;

        var statusStrip = new StatusStrip
        {
            SizingGrip = false,
            BackColor = Color.FromArgb(20, 29, 43)
        };
        _status = new ToolStripStatusLabel("Connecting…")
        {
            ForeColor = Color.FromArgb(148, 163, 184),
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft
        };
        statusStrip.Items.Add(_status);
        Controls.Add(_terminal);
        Controls.Add(statusStrip);

        _pollTimer = new System.Windows.Forms.Timer { Interval = 60 };
        _pollTimer.Tick += PollTimerTick;
        _resizeTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _resizeTimer.Tick += ResizeTimerTick;
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await StartSessionAsync();
    }

    public async Task StartSessionAsync()
    {
        if (_shutdownStarted || _openStarted)
        {
            return;
        }

        _openStarted = true;
        await OpenSessionAsync(_lifetime.Token);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        BeginShutdown();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            BeginShutdown();
            _pollTimer.Tick -= PollTimerTick;
            _resizeTimer.Tick -= ResizeTimerTick;
            _terminal.KeyDown -= TerminalKeyDown;
            _terminal.KeyPress -= TerminalKeyPress;
            _terminal.Resize -= TerminalResize;
            _boldTerminalFont.Dispose();
            _pollTimer.Dispose();
            _resizeTimer.Dispose();
            _lifetime.Dispose();
            if (_ownsClient)
            {
                _closeTask.GetAwaiter().GetResult();
                _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        base.Dispose(disposing);
    }

    private void BeginShutdown()
    {
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        _pollTimer.Stop();
        _resizeTimer.Stop();
        _lifetime.Cancel();
        _closeTask = CloseSessionAsync();
    }

    private async Task OpenSessionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var (columns, rows) = GetTerminalSize();
            _buffer.Resize(columns, rows);
            var response = await _client.OpenAsync(new SshTerminalOpenRequest(
                SshTerminalIpcContract.CurrentVersion,
                _connectionId,
                columns,
                rows), cancellationToken);
            if (response.Failure is not null)
            {
                AppendSystemText($"\r\n[StorageHub] {response.Failure.Message}\r\n");
                _status.Text = "Connection failed";
                return;
            }

            _sessionId = response.SessionId;
            _status.Text = $"Connected · {response.DisplayName} · {columns}×{rows}";
            _pollTimer.Start();
            _terminal.Focus();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
            InvalidOperationException or TimeoutException or System.Text.Json.JsonException)
        {
            AppendSystemText("\r\n[StorageHub] The background agent could not open the SSH terminal.\r\n");
            _status.Text = "Agent unavailable";
        }
    }

    private async void PollTimerTick(object? sender, EventArgs e)
    {
        if (_polling || _sessionId == Guid.Empty || _lifetime.IsCancellationRequested)
        {
            return;
        }

        _polling = true;
        try
        {
            var response = await _client.ReadAsync(new SshTerminalReadRequest(
                SshTerminalIpcContract.CurrentVersion,
                _sessionId,
                SshTerminalIpcContract.MaximumChunkBytes), _lifetime.Token);
            if (response.Failure is not null)
            {
                AppendSystemText($"\r\n[StorageHub] {response.Failure.Message}\r\n");
                DisconnectUi();
                return;
            }
            if (response.Content.Length > 0)
            {
                FeedTerminalBytes(response.Content);
            }
            if (!response.IsConnected)
            {
                AppendSystemText("\r\n[StorageHub] SSH session closed.\r\n");
                DisconnectUi();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
            InvalidOperationException or TimeoutException or System.Text.Json.JsonException)
        {
            AppendSystemText("\r\n[StorageHub] Lost contact with the background agent.\r\n");
            DisconnectUi();
        }
        finally
        {
            _polling = false;
        }
    }

    private void TerminalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.C && _terminal.SelectionLength > 0)
        {
            _terminal.Copy();
            e.SuppressKeyPress = true;
            return;
        }

        byte[]? bytes = e.KeyCode switch
        {
            Keys.Enter => [13],
            Keys.Back => [127],
            Keys.Tab => [9],
            Keys.Up => Encoding.ASCII.GetBytes("\u001b[A"),
            Keys.Down => Encoding.ASCII.GetBytes("\u001b[B"),
            Keys.Right => Encoding.ASCII.GetBytes("\u001b[C"),
            Keys.Left => Encoding.ASCII.GetBytes("\u001b[D"),
            Keys.Home => Encoding.ASCII.GetBytes("\u001b[H"),
            Keys.End => Encoding.ASCII.GetBytes("\u001b[F"),
            Keys.Delete => Encoding.ASCII.GetBytes("\u001b[3~"),
            Keys.C when e.Control => [3],
            Keys.D when e.Control => [4],
            Keys.L when e.Control => [12],
            _ => null
        };
        if (bytes is not null)
        {
            e.SuppressKeyPress = true;
            _ = SendAsync(bytes);
        }
    }

    private void TerminalKeyPress(object? sender, KeyPressEventArgs e)
    {
        if (!char.IsControl(e.KeyChar))
        {
            e.Handled = true;
            _ = SendAsync(Encoding.UTF8.GetBytes(e.KeyChar.ToString()));
        }
    }

    private async Task SendAsync(byte[] bytes)
    {
        if (_sessionId == Guid.Empty || _lifetime.IsCancellationRequested)
        {
            return;
        }
        try
        {
            var response = await _client.WriteAsync(new SshTerminalWriteRequest(
                SshTerminalIpcContract.CurrentVersion,
                _sessionId,
                bytes), _lifetime.Token);
            if (response.Failure is not null)
            {
                AppendSystemText($"\r\n[StorageHub] {response.Failure.Message}\r\n");
                DisconnectUi();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private void TerminalResize(object? sender, EventArgs e)
    {
        if (_sessionId != Guid.Empty)
        {
            _resizeTimer.Stop();
            _resizeTimer.Start();
        }
    }

    private async void ResizeTimerTick(object? sender, EventArgs e)
    {
        _resizeTimer.Stop();
        if (_sessionId == Guid.Empty || _lifetime.IsCancellationRequested)
        {
            return;
        }
        var (columns, rows) = GetTerminalSize();
        _buffer.Resize(columns, rows);
        RenderTerminal();
        try
        {
            var response = await _client.ResizeAsync(new SshTerminalResizeRequest(
                SshTerminalIpcContract.CurrentVersion,
                _sessionId,
                columns,
                rows), _lifetime.Token);
            if (response.Resized)
            {
                _status.Text = $"Connected · {columns}×{rows}";
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task CloseSessionAsync()
    {
        if (_closingSession || _sessionId == Guid.Empty)
        {
            return;
        }
        _closingSession = true;
        var sessionId = _sessionId;
        _sessionId = Guid.Empty;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            _ = await _client.CloseAsync(new SshTerminalCloseRequest(
                SshTerminalIpcContract.CurrentVersion,
                sessionId), deadline.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
            InvalidOperationException or TimeoutException or OperationCanceledException or System.Text.Json.JsonException)
        {
        }
    }

    private (int Columns, int Rows) GetTerminalSize()
    {
        var characterSize = TextRenderer.MeasureText("M", _terminal.Font, Size.Empty, TextFormatFlags.NoPadding);
        var columns = Math.Clamp(
            _terminal.ClientSize.Width / Math.Max(1, characterSize.Width),
            SshTerminalIpcContract.MinimumColumns,
            SshTerminalIpcContract.MaximumColumns);
        var rows = Math.Clamp(
            _terminal.ClientSize.Height / Math.Max(1, characterSize.Height),
            SshTerminalIpcContract.MinimumRows,
            SshTerminalIpcContract.MaximumRows);
        return (columns, rows);
    }

    private void FeedTerminalBytes(byte[] content)
    {
        var characters = new char[Encoding.UTF8.GetMaxCharCount(content.Length)];
        _utf8Decoder.Convert(content, characters, flush: false, out _, out var used, out _);
        if (used > 0)
        {
            _buffer.Feed(characters.AsSpan(0, used));
            RenderTerminal();
        }
    }

    private void AppendSystemText(string text)
    {
        _buffer.Feed(text);
        RenderTerminal();
    }

    private void RenderTerminal()
    {
        var snapshot = _buffer.Snapshot();
        _terminal.SuspendLayout();
        try
        {
            _terminal.Text = snapshot.Text;
            foreach (var run in snapshot.Runs)
            {
                _terminal.Select(run.Start, run.Length);
                _terminal.SelectionColor = run.Foreground;
                _terminal.SelectionBackColor = run.Background;
                _terminal.SelectionFont = run.Bold ? _boldTerminalFont : _terminal.Font;
            }
            _terminal.Select(snapshot.CursorOffset, 0);
            _terminal.ScrollToCaret();
        }
        finally
        {
            _terminal.ResumeLayout();
        }
    }

    private void DisconnectUi()
    {
        _pollTimer.Stop();
        _status.Text = "Disconnected";
        _closeTask = CloseSessionAsync();
    }

}
