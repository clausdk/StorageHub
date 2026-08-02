using System.ComponentModel;

namespace StorageHub.Desktop;

/// <summary>
/// Loads a durable sync run by ID. V6 orchestration has no run-list query, so the surface does not
/// fabricate history; it reviews known run IDs and polls only after one is explicitly loaded.
/// </summary>
public sealed class SyncRunsControl : UserControl
{
    private readonly ISyncManagementAgentClient _client;
    private readonly bool _ownsClient;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextBox _runId;
    private readonly Label _status;
    private readonly SyncRunReviewControl _review;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private int _polling;
    private bool _disposed;

    public SyncRunsControl()
        : this(new NamedPipeSyncManagementAgentClient(), ownsClient: true)
    {
    }

    public SyncRunsControl(ISyncManagementAgentClient client, bool ownsClient = false)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ownsClient = ownsClient;
        Dock = DockStyle.Fill;
        BackColor = StorageHubTheme.Surface;
        AccessibleName = "Synchronization runs";
        AccessibleDescription = "Load, review, and durably dispatch a known preview run.";

        var toolbar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 58,
            ColumnCount = 4,
            Padding = new Padding(10, 9, 10, 7),
            BackColor = StorageHubTheme.Surface
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.Controls.Add(new Label
        {
            Text = "Run ID",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = StorageHubTheme.Text,
            Margin = new Padding(0, 8, 8, 0)
        }, 0, 0);
        _runId = new TextBox
        {
            Name = "SyncRunId",
            Dock = DockStyle.Fill,
            PlaceholderText = "00000000-0000-0000-0000-000000000000",
            AccessibleName = "Synchronization run ID"
        };
        toolbar.Controls.Add(_runId, 1, 0);
        var load = new Button { Text = "Load run", AutoSize = true };
        StorageHubTheme.StylePrimaryButton(load);
        load.Click += LoadClicked;
        toolbar.Controls.Add(load, 2, 0);
        _status = new Label
        {
            Text = "Enter a run ID from a generated preview.",
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            ForeColor = StorageHubTheme.TextMuted,
            AccessibleName = "Sync runs load status"
        };
        toolbar.Controls.Add(_status, 3, 0);

        _review = new SyncRunReviewControl(_client) { Dock = DockStyle.Fill };
        Controls.Add(_review);
        Controls.Add(toolbar);

        _pollTimer = new System.Windows.Forms.Timer { Interval = 5_000 };
        _pollTimer.Tick += PollTimerTick;
    }

    public SyncRunReviewControl Review => _review;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int PollIntervalMilliseconds
    {
        get => _pollTimer.Interval;
        set => _pollTimer.Interval = value is >= 1_000 and <= 60_000
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public void SetRunId(Guid syncRunId) => _runId.Text = syncRunId == Guid.Empty ? string.Empty : syncRunId.ToString("D");

    public async Task LoadRunAsync(Guid syncRunId, CancellationToken cancellationToken = default)
    {
        if (syncRunId == Guid.Empty)
        {
            throw new ArgumentException("A sync run ID is required.", nameof(syncRunId));
        }

        _runId.Text = syncRunId.ToString("D");
        _status.Text = "Loading immutable sync preview…";
        _status.ForeColor = StorageHubTheme.TextMuted;
        try
        {
            await _review.LoadRunAsync(syncRunId, cancellationToken).ConfigureAwait(true);
            _status.Text = "Run loaded. Status polling is active while this view is visible.";
            _status.ForeColor = StorageHubTheme.Success;
            UpdatePollingState();
        }
        catch
        {
            _status.Text = "The sync run could not be loaded.";
            _status.ForeColor = StorageHubTheme.Danger;
            UpdatePollingState();
            throw;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdatePollingState();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _pollTimer.Stop();
        base.OnHandleDestroyed(e);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdatePollingState();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _pollTimer.Stop();
            _pollTimer.Tick -= PollTimerTick;
            _pollTimer.Dispose();
            _lifetime.Cancel();
            _lifetime.Dispose();
            if (_ownsClient)
            {
                _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        base.Dispose(disposing);
    }

    private void UpdatePollingState()
    {
        var shouldPoll = !_disposed && IsHandleCreated && Visible && FindForm()?.Visible == true &&
            _review.CurrentRun is not null;
        if (shouldPoll)
        {
            _pollTimer.Start();
        }
        else
        {
            _pollTimer.Stop();
        }
    }

    private async void LoadClicked(object? sender, EventArgs e)
    {
        if (!Guid.TryParse(_runId.Text, out var runId) || runId == Guid.Empty)
        {
            _status.Text = "Enter a valid non-empty run ID.";
            _status.ForeColor = StorageHubTheme.Warning;
            return;
        }

        try
        {
            await LoadRunAsync(runId, _lifetime.Token).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _status.Text = error.Message;
            _status.ForeColor = StorageHubTheme.Danger;
        }
    }

    private async void PollTimerTick(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _polling, 1) != 0)
        {
            return;
        }

        try
        {
            await _review.RefreshStatusAsync(_lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            _status.Text = error.Message;
            _status.ForeColor = StorageHubTheme.Danger;
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }
}
