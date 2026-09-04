using System.Globalization;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

/// <summary>
/// Reviews one immutable preview and can durably dispatch its exact revision and approval digest.
/// Dispatch is never presented as provider execution completion.
/// </summary>
public sealed class SyncRunReviewControl : UserControl
{
    private const int CompletionPollLimit = 20;
    private static readonly TimeSpan CompletionPollInterval = TimeSpan.FromMilliseconds(500);
    private readonly ISyncManagementAgentClient _client;
    private readonly bool _ownsClient;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly Label _status;
    private readonly Label _summary;
    private readonly DataGridView _planGrid;
    private readonly DataGridView _conflictGrid;
    private readonly Button _approveButton;
    private readonly Button _nextPlanButton;
    private readonly Button _nextConflictButton;
    private string? _planContinuation;
    private string? _conflictContinuation;
    private bool _disposed;

    public SyncRunReviewControl()
        : this(new NamedPipeSyncManagementAgentClient(), ownsClient: true)
    {
    }

    public SyncRunReviewControl(ISyncManagementAgentClient client, bool ownsClient = false)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ownsClient = ownsClient;
        Dock = DockStyle.Fill;
        BackColor = StorageHubTheme.Surface;
        AccessibleName = "Synchronization run review";
        AccessibleDescription = "Immutable sync operations, conflicts, approval, and live run status.";

        var heading = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 94,
            ColumnCount = 2,
            Padding = new Padding(12, 8, 12, 6),
            BackColor = StorageHubTheme.Surface
        };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var text = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = Padding.Empty
        };
        _summary = UiControlFactory.CreateSectionTitle("No sync plan loaded");
        _status = UiControlFactory.CreateDescription(
            "Choose Review & run from a sync profile, or load an existing run.");
        _status.Name = "SyncRunStatus";
        text.Controls.Add(_summary);
        text.Controls.Add(_status);
        heading.Controls.Add(text, 0, 0);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(8, 8, 0, 0)
        };
        var refresh = new Button { Text = "Refresh status", AutoSize = true };
        StorageHubTheme.StyleSecondaryButton(refresh);
        refresh.Click += RefreshClicked;
        _approveButton = new Button
        {
            Name = "ApproveAndDispatch",
            Text = "Approve & dispatch",
            AutoSize = true,
            Enabled = false,
            AccessibleDescription = "Durably enqueue the exact reviewed plan. This does not report provider execution complete."
        };
        StorageHubTheme.StylePrimaryButton(_approveButton);
        _approveButton.Click += ApproveClicked;
        actions.Controls.Add(refresh);
        actions.Controls.Add(_approveButton);
        heading.Controls.Add(actions, 1, 0);

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            AccessibleName = "Sync plan details"
        };
        StorageHubTheme.ConfigureTabs(tabs);
        _planGrid = CreateGrid("Immutable plan operations");
        _planGrid.Columns.Add("Sequence", "#");
        _planGrid.Columns.Add("Action", "Action");
        _planGrid.Columns.Add("FromLocation", "From location");
        _planGrid.Columns.Add("ToLocation", "To location");
        _planGrid.Columns.Add("Bytes", "Expected bytes");
        _planGrid.Columns.Add("Safety", "Safety");
        _nextPlanButton = CreateNextButton("Load next operations", NextPlanClicked);
        tabs.TabPages.Add(CreatePagedTab("Plan", _planGrid, _nextPlanButton));

        _conflictGrid = CreateGrid("Synchronization conflicts");
        _conflictGrid.Columns.Add("Path", "Path");
        _conflictGrid.Columns.Add("Kind", "Kind");
        _conflictGrid.Columns.Add("State", "State");
        _conflictGrid.Columns.Add("Reason", "Safe reason");
        _nextConflictButton = CreateNextButton("Load next conflicts", NextConflictClicked);
        tabs.TabPages.Add(CreatePagedTab("Conflicts", _conflictGrid, _nextConflictButton));

        Controls.Add(tabs);
        Controls.Add(heading);
    }

    public SyncRunSummary? CurrentRun { get; private set; }

    public string StatusText => _status.Text;

    public int LoadedOperationCount => _planGrid.Rows.Count;

    public int LoadedConflictCount => _conflictGrid.Rows.Count;

    public async Task LoadRunAsync(Guid syncRunId, CancellationToken cancellationToken = default)
    {
        if (syncRunId == Guid.Empty)
        {
            throw new ArgumentException("A sync run ID is required.", nameof(syncRunId));
        }

        await ExecuteSerializedAsync(async token =>
        {
            var response = await _client.GetRunStatusAsync(new SyncRunStatusRequest(
                SyncManagementIpcContract.CurrentVersion,
                syncRunId), token).ConfigureAwait(true);
            var run = Require(response.Run, response.Failure);
            SetRun(run, resetPages: true);
            await LoadPlanPageCoreAsync(reset: true, token).ConfigureAwait(true);
            await LoadConflictPageCoreAsync(reset: true, token).ConfigureAwait(true);
        }, cancellationToken).ConfigureAwait(true);
    }

    public async Task ShowPreviewAsync(
        SyncRunSummary run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await ExecuteSerializedAsync(async token =>
        {
            SetRun(run, resetPages: true);
            await LoadPlanPageCoreAsync(reset: true, token).ConfigureAwait(true);
            await LoadConflictPageCoreAsync(reset: true, token).ConfigureAwait(true);
        }, cancellationToken).ConfigureAwait(true);
    }

    public async Task RefreshStatusAsync(CancellationToken cancellationToken = default)
    {
        var runId = CurrentRun?.SyncRunId ?? throw new InvalidOperationException("No sync run is loaded.");
        await ExecuteSerializedAsync(async token =>
        {
            var response = await _client.GetRunStatusAsync(new SyncRunStatusRequest(
                SyncManagementIpcContract.CurrentVersion,
                runId), token).ConfigureAwait(true);
            SetRun(Require(response.Run, response.Failure), resetPages: false);
        }, cancellationToken).ConfigureAwait(true);
    }

    /// <returns><see langword="true"/> only when the exact apply request was durably dispatched.</returns>
    public async Task<bool> ApproveAndDispatchAsync(CancellationToken cancellationToken = default)
    {
        var run = CurrentRun ?? throw new InvalidOperationException("No sync run is loaded.");
        if (run.DispatchState == SyncIpcDispatchState.DurablyDispatched)
        {
            SetRun(run, resetPages: false);
            return true;
        }

        if (run.Phase != SyncIpcRunPhase.AwaitingApproval)
        {
            throw new InvalidOperationException("This run is not awaiting approval.");
        }

        return await ExecuteSerializedAsync(async token =>
        {
            // These values come from the same immutable summary shown to the reviewer.
            var response = await _client.ApproveAndDispatchAsync(new SyncApproveDispatchRequest(
                SyncManagementIpcContract.CurrentVersion,
                run.SyncRunId,
                run.Revision,
                run.ApprovalSha256), token).ConfigureAwait(true);
            var approved = Require(response.Run, response.Failure);
            if (!response.DurablyDispatched ||
                approved.SyncRunId != run.SyncRunId ||
                approved.DispatchState != SyncIpcDispatchState.DurablyDispatched)
            {
                throw new InvalidDataException("The agent did not confirm durable sync dispatch.");
            }

            SetRun(approved, resetPages: false);
            await PollForCompletionAsync(approved.SyncRunId, token).ConfigureAwait(true);
            return true;
        }, cancellationToken).ConfigureAwait(true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
            _operationGate.Dispose();
            if (_ownsClient)
            {
                _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        base.Dispose(disposing);
    }

    private async Task LoadPlanPageCoreAsync(bool reset, CancellationToken cancellationToken)
    {
        var run = CurrentRun ?? throw new InvalidOperationException("No sync run is loaded.");
        var continuation = reset ? null : _planContinuation;
        if (!reset && continuation is null)
        {
            return;
        }

        var response = await _client.GetPlanPageAsync(new SyncPlanPageRequest(
            SyncManagementIpcContract.CurrentVersion,
            run.SyncRunId,
            PageSize: SyncManagementIpcLimits.MaximumPageSize,
            continuation), cancellationToken).ConfigureAwait(true);
        ThrowIfFailure(response.Failure);
        if (response.PlanId != run.PlanId ||
            !string.Equals(response.PlanSha256, run.PlanSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The plan page no longer matches the reviewed immutable plan.");
        }

        if (reset)
        {
            _planGrid.Rows.Clear();
        }

        foreach (var operation in response.Operations)
        {
            _planGrid.Rows.Add(
                operation.Sequence,
                operation.Kind,
                FormatEndpoint(operation.SourceConnectionId, operation.SourcePath),
                operation.DestinationConnectionId is { } destinationId
                    ? FormatEndpoint(destinationId, operation.DestinationPath ?? string.Empty)
                    : string.Empty,
                operation.ExpectedLength?.ToString("N0", CultureInfo.CurrentCulture) ?? "—",
                operation.IsDestructive ? "Destructive — approval required" : "Guarded");
        }

        _planContinuation = response.ContinuationToken;
        _nextPlanButton.Enabled = _planContinuation is not null;
    }

    private async Task LoadConflictPageCoreAsync(bool reset, CancellationToken cancellationToken)
    {
        var run = CurrentRun ?? throw new InvalidOperationException("No sync run is loaded.");
        var continuation = reset ? null : _conflictContinuation;
        if (!reset && continuation is null)
        {
            return;
        }

        var response = await _client.GetConflictPageAsync(new SyncConflictPageRequest(
            SyncManagementIpcContract.CurrentVersion,
            run.SyncRunId,
            State: null,
            PageSize: SyncManagementIpcLimits.MaximumPageSize,
            continuation), cancellationToken).ConfigureAwait(true);
        ThrowIfFailure(response.Failure);
        if (reset)
        {
            _conflictGrid.Rows.Clear();
        }

        foreach (var conflict in response.Conflicts)
        {
            _conflictGrid.Rows.Add(
                conflict.RelativePath,
                conflict.ConflictKind,
                conflict.State,
                conflict.SafeReason);
        }

        _conflictContinuation = response.ContinuationToken;
        _nextConflictButton.Enabled = _conflictContinuation is not null;
    }

    private void SetRun(SyncRunSummary run, bool resetPages)
    {
        CurrentRun = run;
        if (resetPages)
        {
            _planContinuation = null;
            _conflictContinuation = null;
            _planGrid.Rows.Clear();
            _conflictGrid.Rows.Clear();
            _nextPlanButton.Enabled = false;
            _nextConflictButton.Enabled = false;
        }

        _summary.Text = $"Run {run.SyncRunId:D} · {run.Phase} · revision {run.Revision}";
        if (run.DispatchState == SyncIpcDispatchState.DurablyDispatched)
        {
            (_status.Text, _status.ForeColor) = run.Phase switch
            {
                SyncIpcRunPhase.Completed => ("Synchronization completed and both locations were verified.", StorageHubTheme.Success),
                SyncIpcRunPhase.Failed => ("Synchronization failed. Review the run status before retrying.", StorageHubTheme.Danger),
                SyncIpcRunPhase.NeedsReconciliation => ("Synchronization stopped with uncertain provider state and requires reconciliation.", StorageHubTheme.Warning),
                SyncIpcRunPhase.Cancelled => ("Synchronization was cancelled before completion.", StorageHubTheme.Warning),
                SyncIpcRunPhase.Executing => ("Synchronizing provider content…", StorageHubTheme.Primary),
                SyncIpcRunPhase.Verifying => ("Provider changes finished; verifying both locations…", StorageHubTheme.Primary),
                SyncIpcRunPhase.CommittingBaseline => ("Locations verified; committing the new baseline…", StorageHubTheme.Primary),
                _ => ("Synchronization is queued in the background agent.", StorageHubTheme.Primary)
            };
        }
        else if (run.Phase == SyncIpcRunPhase.AwaitingApproval)
        {
            _status.Text = "Awaiting approval. No provider changes have started.";
            _status.ForeColor = StorageHubTheme.Warning;
        }
        else
        {
            _status.Text = $"Run phase: {run.Phase}. No provider execution completion is inferred from this status.";
            _status.ForeColor = StorageHubTheme.TextMuted;
        }

        _approveButton.Enabled =
            run.Phase == SyncIpcRunPhase.AwaitingApproval &&
            run.DispatchState == SyncIpcDispatchState.NotDispatched;
    }

    private async Task PollForCompletionAsync(Guid runId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < CompletionPollLimit; attempt++)
        {
            var response = await _client.GetRunStatusAsync(new SyncRunStatusRequest(
                SyncManagementIpcContract.CurrentVersion,
                runId), cancellationToken).ConfigureAwait(true);
            var current = Require(response.Run, response.Failure);
            SetRun(current, resetPages: false);
            if (IsTerminal(current.Phase))
            {
                return;
            }

            await Task.Delay(CompletionPollInterval, cancellationToken).ConfigureAwait(true);
        }

        _status.Text = "Synchronization is still running in the background. Use Refresh status for the latest result.";
        _status.ForeColor = StorageHubTheme.Primary;
    }

    private static bool IsTerminal(SyncIpcRunPhase phase) => phase is
        SyncIpcRunPhase.Completed or
        SyncIpcRunPhase.Failed or
        SyncIpcRunPhase.Cancelled or
        SyncIpcRunPhase.Interrupted or
        SyncIpcRunPhase.NeedsReconciliation or
        SyncIpcRunPhase.BlockedConflict or
        SyncIpcRunPhase.BlockedDeletionGuard or
        SyncIpcRunPhase.BlockedEndpoint or
        SyncIpcRunPhase.BlockedCredential or
        SyncIpcRunPhase.BlockedTrust;

    private async Task ExecuteSerializedAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _operationGate.WaitAsync(linked.Token).ConfigureAwait(true);
        try
        {
            await action(linked.Token).ConfigureAwait(true);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<T> ExecuteSerializedAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _operationGate.WaitAsync(linked.Token).ConfigureAwait(true);
        try
        {
            return await action(linked.Token).ConfigureAwait(true);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async void RefreshClicked(object? sender, EventArgs e)
    {
        try
        {
            await RefreshStatusAsync(_lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            ShowError(error);
        }
    }

    private async void ApproveClicked(object? sender, EventArgs e)
    {
        var confirmation = MessageBox.Show(
            this,
            "Approve this exact immutable plan and durably dispatch its apply request? This does not mean provider execution has completed.",
            "Approve and dispatch sync",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.OK)
        {
            return;
        }

        try
        {
            await ApproveAndDispatchAsync(_lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            ShowError(error);
        }
    }

    private async void NextPlanClicked(object? sender, EventArgs e)
    {
        try
        {
            await ExecuteSerializedAsync(
                token => LoadPlanPageCoreAsync(reset: false, token),
                _lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            ShowError(error);
        }
    }

    private async void NextConflictClicked(object? sender, EventArgs e)
    {
        try
        {
            await ExecuteSerializedAsync(
                token => LoadConflictPageCoreAsync(reset: false, token),
                _lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            ShowError(error);
        }
    }

    private void ShowError(Exception error)
    {
        _status.Text = error.Message;
        _status.ForeColor = StorageHubTheme.Danger;
    }

    private static T Require<T>(T? value, StorageIpcFailure? failure) where T : class
    {
        ThrowIfFailure(failure);
        return value ?? throw new InvalidDataException("The agent returned an incomplete sync response.");
    }

    private static void ThrowIfFailure(StorageIpcFailure? failure)
    {
        if (failure is not null)
        {
            throw new InvalidOperationException(failure.Message);
        }
    }

    private static string FormatEndpoint(Guid connectionId, string path) =>
        $"{connectionId:D} · {(path.Length == 0 ? "<root>" : path)}";

    private static DataGridView CreateGrid(string accessibleName)
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
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            BackgroundColor = StorageHubTheme.Surface,
            BorderStyle = BorderStyle.None,
            GridColor = StorageHubTheme.Border,
            AccessibleName = accessibleName
        };
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = StorageHubTheme.SurfaceMuted;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = StorageHubTheme.Text;
        return grid;
    }

    private static Button CreateNextButton(string text, EventHandler click)
    {
        var button = new Button
        {
            Text = text,
            Dock = DockStyle.Right,
            Width = 170,
            Enabled = false
        };
        StorageHubTheme.StyleSecondaryButton(button);
        button.Click += click;
        return button;
    }

    private static TabPage CreatePagedTab(string name, Control content, Button next)
    {
        var page = new TabPage(name) { Padding = new Padding(4) };
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(4) };
        footer.Controls.Add(next);
        page.Controls.Add(content);
        page.Controls.Add(footer);
        return page;
    }
}
