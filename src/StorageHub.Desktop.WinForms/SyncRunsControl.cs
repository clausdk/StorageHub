using System.ComponentModel;
using System.Globalization;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

/// <summary>
/// Browses durable sync history, loads one immutable run for review, and polls active runs.
/// </summary>
public sealed class SyncRunsControl : UserControl
{
    private readonly ISyncManagementAgentClient _client;
    private readonly bool _ownsClient;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextBox _runId;
    private readonly DataGridView _history;
    private readonly Button _nextPage;
    private readonly Label _status;
    private readonly SyncRunReviewControl _review;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private int _polling;
    private int _loadingHistory;
    private string? _historyCursor;
    private string? _nextHistoryCursor;
    private bool _historyLoaded;
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
        AccessibleDescription = "Load, review, and durably dispatch a known synchronization run.";

        var toolbar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 58,
            ColumnCount = 6,
            Padding = new Padding(10, 9, 10, 7),
            BackColor = StorageHubTheme.Surface
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
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
        var refresh = new Button { Text = "Refresh history", AutoSize = true };
        StorageHubTheme.StyleSecondaryButton(refresh);
        refresh.Click += RefreshHistoryClicked;
        toolbar.Controls.Add(refresh, 3, 0);
        _nextPage = new Button { Text = "Next page", AutoSize = true, Enabled = false };
        StorageHubTheme.StyleSecondaryButton(_nextPage);
        _nextPage.Click += NextHistoryPageClicked;
        toolbar.Controls.Add(_nextPage, 4, 0);
        _status = new Label
        {
            Text = "Enter a synchronization run ID.",
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            ForeColor = StorageHubTheme.TextMuted,
            AccessibleDescription = "Current synchronization run history and review status"
        };
        toolbar.Controls.Add(_status, 5, 0);

        _review = new SyncRunReviewControl(_client) { Dock = DockStyle.Fill };
        _history = CreateHistoryGrid();
        _history.SelectionChanged += HistorySelectionChanged;
        _history.CellDoubleClick += HistoryCellDoubleClick;
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            BackColor = StorageHubTheme.Border,
            AccessibleName = "Synchronization history and run review"
        };
        split.SizeChanged += (_, _) =>
        {
            if (split.Width >= 800)
            {
                split.SplitterDistance = Math.Max(300, (int)(split.Width * 0.35));
            }
        };
        split.Panel1.Controls.Add(_history);
        split.Panel2.Controls.Add(_review);
        Controls.Add(split);
        Controls.Add(toolbar);

        _pollTimer = new System.Windows.Forms.Timer { Interval = 5_000 };
        _pollTimer.Tick += PollTimerTick;
    }

    public SyncRunReviewControl Review => _review;

    public int DisplayedRunCount => _history.Rows.Count;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int PollIntervalMilliseconds
    {
        get => _pollTimer.Interval;
        set => _pollTimer.Interval = value is >= 1_000 and <= 60_000
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public void SetRunId(Guid syncRunId) => _runId.Text = syncRunId == Guid.Empty ? string.Empty : syncRunId.ToString("D");

    public Task RefreshHistoryAsync(CancellationToken cancellationToken = default) =>
        LoadHistoryAsync(resetPage: true, cancellationToken);

    public async Task LoadRunAsync(Guid syncRunId, CancellationToken cancellationToken = default)
    {
        if (syncRunId == Guid.Empty)
        {
            throw new ArgumentException("A sync run ID is required.", nameof(syncRunId));
        }

        _runId.Text = syncRunId.ToString("D");
        _status.Text = "Loading immutable synchronization plan…";
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
            _history.SelectionChanged -= HistorySelectionChanged;
            _history.CellDoubleClick -= HistoryCellDoubleClick;
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

        if (!_disposed && IsHandleCreated && Visible && FindForm()?.Visible == true && !_historyLoaded)
        {
            _ = LoadHistoryAsync(resetPage: true, _lifetime.Token);
        }
    }

    private static DataGridView CreateHistoryGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowHeadersVisible = false,
            BackgroundColor = StorageHubTheme.Surface,
            BorderStyle = BorderStyle.None,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AccessibleName = "Durable synchronization run history"
        };
        grid.Columns.Add("Updated", "Updated");
        grid.Columns.Add("Run", "Run");
        grid.Columns.Add("Phase", "Phase");
        grid.Columns.Add("Dispatch", "Dispatch");
        grid.Columns.Add("Conflicts", "Conflicts");
        grid.Columns[0].FillWeight = 70;
        grid.Columns[1].FillWeight = 75;
        grid.Columns[4].FillWeight = 45;
        return grid;
    }

    private async void RefreshHistoryClicked(object? sender, EventArgs e) =>
        await LoadHistoryAsync(resetPage: true, _lifetime.Token).ConfigureAwait(true);

    private async void NextHistoryPageClicked(object? sender, EventArgs e)
    {
        if (_nextHistoryCursor is null)
        {
            return;
        }

        _historyCursor = _nextHistoryCursor;
        await LoadHistoryAsync(resetPage: false, _lifetime.Token).ConfigureAwait(true);
    }

    private async Task LoadHistoryAsync(bool resetPage, CancellationToken cancellationToken)
    {
        if (_disposed || Interlocked.Exchange(ref _loadingHistory, 1) != 0)
        {
            return;
        }

        if (resetPage)
        {
            _historyCursor = null;
        }

        _status.Text = "Loading durable run history…";
        _status.ForeColor = StorageHubTheme.TextMuted;
        try
        {
            var response = await _client.ListRunsAsync(new SyncRunListRequest(
                PageSize: SyncManagementIpcLimits.MaximumPageSize,
                ContinuationToken: _historyCursor), cancellationToken).ConfigureAwait(true);
            if (response.Failure is not null)
            {
                throw new InvalidOperationException(response.Failure.Message);
            }

            _history.Rows.Clear();
            foreach (var run in response.Runs)
            {
                var index = _history.Rows.Add(
                    run.UpdatedUtc.LocalDateTime.ToString("g", CultureInfo.CurrentCulture),
                    run.SyncRunId.ToString("N")[..8],
                    run.Phase,
                    run.DispatchState,
                    run.ConflictCount);
                _history.Rows[index].Tag = run;
            }

            _history.ClearSelection();
            _nextHistoryCursor = response.ContinuationToken;
            _nextPage.Enabled = _nextHistoryCursor is not null;
            _historyLoaded = true;
            _status.Text = response.Runs.Length == 0
                ? "No synchronization runs yet."
                : $"Showing {response.Runs.Length} durable run(s).";
            _status.ForeColor = StorageHubTheme.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            _status.Text = error.Message;
            _status.ForeColor = StorageHubTheme.Danger;
        }
        finally
        {
            Interlocked.Exchange(ref _loadingHistory, 0);
        }
    }

    private void HistorySelectionChanged(object? sender, EventArgs e)
    {
        if (_history.SelectedRows.Cast<DataGridViewRow>().FirstOrDefault()?.Tag is SyncRunSummary run)
        {
            SetRunId(run.SyncRunId);
        }
    }

    private async void HistoryCellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex >= 0 && _history.Rows[e.RowIndex].Tag is SyncRunSummary run)
        {
            try
            {
                await LoadRunAsync(run.SyncRunId, _lifetime.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _status.Text = error.Message;
                _status.ForeColor = StorageHubTheme.Danger;
            }
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
        catch (OperationCanceledException)
        {
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
