using System.Globalization;
using Krypton.Toolkit;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

/// <summary>Manages durable preview-only schedules through the local agent.</summary>
public sealed class ScheduleManagerForm : KryptonForm
{
    private readonly IScheduleManagementAgentClient _scheduleClient;
    private readonly ISyncManagementAgentClient _syncClient;
    private readonly bool _ownsClients;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DataGridView _grid;
    private readonly ComboBox _profile;
    private readonly TextBox _cron;
    private readonly ComboBox _timeZone;
    private readonly NumericUpDown _misfireGraceMinutes;
    private readonly CheckBox _queueOne;
    private readonly CheckBox _enabled;
    private readonly Label _nextOccurrence;
    private readonly Label _runState;
    private readonly Label _status;
    private readonly Button _save;
    private readonly Button _toggle;
    private readonly Button _delete;
    private ScheduleDocument? _current;
    private bool _initialLoadStarted;
    private bool _suppressSelection;
    private bool _disposed;

    public ScheduleManagerForm()
        : this(
            new NamedPipeScheduleManagementAgentClient(),
            new NamedPipeSyncManagementAgentClient(),
            ownsClients: true)
    {
    }

    public ScheduleManagerForm(
        IScheduleManagementAgentClient scheduleClient,
        ISyncManagementAgentClient syncClient,
        bool ownsClients = false)
    {
        _scheduleClient = scheduleClient ?? throw new ArgumentNullException(nameof(scheduleClient));
        _syncClient = syncClient ?? throw new ArgumentNullException(nameof(syncClient));
        _ownsClients = ownsClients;
        Text = "Synchronization Schedules — StorageHub";
        AccessibleName = "Synchronization schedule manager";
        AccessibleDescription = "Manage durable schedules that generate reviewable previews only.";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(980, 640);
        Size = new Size(1220, 760);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = StorageHubTheme.Canvas;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        var banner = new Label
        {
            Dock = DockStyle.Top,
            Height = 62,
            Text = "PREVIEW ONLY — Schedules generate immutable sync previews in the background. They do not approve plans or apply provider changes unattended.",
            ForeColor = StorageHubTheme.Warning,
            BackColor = Color.FromArgb(255, 244, 224),
            Padding = new Padding(18, 13, 18, 8),
            AccessibleName = "Preview-only schedule safety notice"
        };

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(12, 8, 12, 5),
            WrapContents = false,
            BackColor = StorageHubTheme.Surface
        };
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        StorageHubTheme.StyleSecondaryButton(refresh);
        refresh.Click += RefreshClicked;
        var newSchedule = new Button { Text = "New schedule", AutoSize = true };
        StorageHubTheme.StylePrimaryButton(newSchedule);
        newSchedule.Click += (_, _) => BeginNewSchedule();
        toolbar.Controls.Add(refresh);
        toolbar.Controls.Add(newSchedule);

        _grid = new DataGridView
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
            AccessibleName = "Saved synchronization schedules"
        };
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = StorageHubTheme.SurfaceMuted;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = StorageHubTheme.Text;
        _grid.Columns.Add("Profile", "Profile");
        _grid.Columns.Add("Cron", "Cron");
        _grid.Columns.Add("TimeZone", "Time zone");
        _grid.Columns.Add("Next", "Next preview");
        _grid.Columns.Add("Enabled", "Enabled");
        _grid.Columns.Add("State", "State");
        _grid.SelectionChanged += GridSelectionChanged;

        _profile = new ComboBox
        {
            Name = "ScheduleProfile",
            DropDownStyle = ComboBoxStyle.DropDownList,
            AccessibleName = "Scheduled sync profile"
        };
        _cron = new TextBox
        {
            Name = "ScheduleCron",
            Text = "0 2 * * *",
            MaxLength = ScheduleManagementIpcLimits.MaximumCronExpressionLength,
            AccessibleName = "Five-field cron expression"
        };
        _timeZone = new ComboBox
        {
            Name = "ScheduleTimeZone",
            DropDownStyle = ComboBoxStyle.DropDownList,
            AccessibleName = "Schedule time zone"
        };
        _timeZone.Items.AddRange(TimeZoneInfo.GetSystemTimeZones()
            .Select(static zone => zone.Id)
            .Cast<object>()
            .ToArray());
        SelectTimeZone("UTC");
        _misfireGraceMinutes = new NumericUpDown
        {
            Name = "ScheduleMisfireGrace",
            Minimum = 1,
            Maximum = 30 * 24 * 60,
            Value = 24 * 60,
            ThousandsSeparator = true,
            Width = 160,
            AccessibleName = "Misfire grace in minutes"
        };
        _queueOne = new CheckBox
        {
            Text = "Keep one coalesced preview while the profile is already running",
            Checked = true,
            AutoSize = true
        };
        _enabled = new CheckBox
        {
            Text = "Enabled",
            Checked = false,
            AutoSize = true
        };
        _nextOccurrence = UiControlFactory.CreateDescription("Not scheduled until saved and enabled.");
        _runState = UiControlFactory.CreateDescription("No active preview run.");

        var editor = BuildEditor();
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            Size = new Size(1180, 600),
            SplitterDistance = 610,
            Panel1MinSize = 430,
            Panel2MinSize = 400,
            BackColor = StorageHubTheme.Border,
            AccessibleName = "Schedule list and editor"
        };
        split.Panel1.Controls.Add(_grid);
        split.Panel1.Controls.Add(toolbar);
        split.Panel2.Controls.Add(editor);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 64,
            ColumnCount = 2,
            Padding = new Padding(14, 9, 14, 8),
            BackColor = StorageHubTheme.Surface
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _status = new Label
        {
            Name = "ScheduleStatus",
            Text = "The manager connects to the background agent when shown.",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = StorageHubTheme.TextMuted,
            AccessibleName = "Schedule management status"
        };
        footer.Controls.Add(_status, 0, 0);
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel };
        StorageHubTheme.StyleSecondaryButton(close);
        _delete = new Button { Name = "DeleteSchedule", Text = "Delete", AutoSize = true, Enabled = false };
        StorageHubTheme.StyleSecondaryButton(_delete);
        _delete.Click += DeleteClicked;
        _toggle = new Button { Name = "ToggleSchedule", Text = "Enable", AutoSize = true, Enabled = false };
        StorageHubTheme.StyleSecondaryButton(_toggle);
        _toggle.Click += ToggleClicked;
        _save = new Button { Name = "SaveSchedule", Text = "Save schedule", AutoSize = true };
        StorageHubTheme.StylePrimaryButton(_save);
        _save.Click += SaveClicked;
        actions.Controls.Add(close);
        actions.Controls.Add(_delete);
        actions.Controls.Add(_toggle);
        actions.Controls.Add(_save);
        footer.Controls.Add(actions, 1, 0);
        CancelButton = close;

        Controls.Add(split);
        Controls.Add(footer);
        Controls.Add(banner);
        BeginNewSchedule();
    }

    public ScheduleDocument? CurrentSchedule => _current;

    public string StatusText => _status.Text;

    public int DisplayedScheduleCount => _grid.Rows.Count;

    public async Task LoadSchedulesAsync(CancellationToken cancellationToken = default)
    {
        var selectedScheduleId = _current?.ScheduleId;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        _status.Text = "Loading schedules and sync profiles…";
        _status.ForeColor = StorageHubTheme.TextMuted;
        var schedulesTask = _scheduleClient.ListAsync(new ScheduleListRequest(
            ScheduleManagementIpcContract.CurrentVersion,
            IncludeDisabled: true,
            MaximumCount: ScheduleManagementIpcLimits.MaximumScheduleResults), linked.Token);
        var profilesTask = _syncClient.ListProfilesAsync(new SyncProfileListRequest(
            SyncManagementIpcContract.CurrentVersion,
            IncludeDisabled: true,
            MaximumCount: SyncManagementIpcLimits.MaximumProfileResults), linked.Token);
        await Task.WhenAll(schedulesTask, profilesTask).ConfigureAwait(true);
        var schedules = await schedulesTask.ConfigureAwait(true);
        var profiles = await profilesTask.ConfigureAwait(true);
        ThrowIfFailure(schedules.Failure);
        ThrowIfFailure(profiles.Failure);
        PopulateProfiles(profiles.Profiles);
        PopulateGrid(schedules.Schedules);
        if (selectedScheduleId is { } scheduleId)
        {
            var refreshed = schedules.Schedules.FirstOrDefault(schedule => schedule.ScheduleId == scheduleId);
            if (refreshed is null)
            {
                BeginNewSchedule();
            }
            else
            {
                ApplySchedule(refreshed);
                _suppressSelection = true;
                try
                {
                    SelectGridRow(refreshed.ScheduleId);
                }
                finally
                {
                    _suppressSelection = false;
                }
            }
        }

        _status.Text = schedules.Schedules.Length == 0
            ? "No schedules yet. New schedules are disabled by default and generate previews only."
            : $"Loaded {schedules.Schedules.Length} preview-only schedule(s).";
        _status.ForeColor = StorageHubTheme.Success;
    }

    public async Task SelectScheduleAsync(Guid scheduleId, CancellationToken cancellationToken = default)
    {
        if (scheduleId == Guid.Empty)
        {
            throw new ArgumentException("A schedule ID is required.", nameof(scheduleId));
        }

        var response = await _scheduleClient.GetAsync(new ScheduleGetRequest(
            ScheduleManagementIpcContract.CurrentVersion,
            scheduleId), cancellationToken).ConfigureAwait(true);
        ThrowIfFailure(response.Failure);
        ApplySchedule(response.Schedule ?? throw new InvalidDataException("The agent returned an incomplete schedule."));
    }

    public async Task<ScheduleDocument> SaveCurrentScheduleAsync(
        CancellationToken cancellationToken = default)
    {
        var draft = BuildDraft();
        if (!draft.HasValidBounds)
        {
            throw new InvalidOperationException("Select a profile and enter a bounded five-field cron schedule and time zone.");
        }

        ScheduleMutationResponse response;
        if (_current is null)
        {
            response = await _scheduleClient.CreateAsync(new ScheduleCreateRequest(
                ScheduleManagementIpcContract.CurrentVersion,
                Guid.NewGuid(),
                draft), cancellationToken).ConfigureAwait(true);
        }
        else
        {
            response = await _scheduleClient.UpdateAsync(new ScheduleUpdateRequest(
                ScheduleManagementIpcContract.CurrentVersion,
                _current.ScheduleId,
                _current.Revision,
                draft), cancellationToken).ConfigureAwait(true);
        }

        var saved = RequireSuccessfulSchedule(response);
        ApplySchedule(saved);
        UpsertGrid(saved);
        _status.Text = "Schedule saved. It will generate previews only; unattended apply is not enabled.";
        _status.ForeColor = StorageHubTheme.Success;
        return saved;
    }

    public async Task<ScheduleDocument> SetCurrentEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var current = _current ?? throw new InvalidOperationException("Select a saved schedule first.");
        var response = await _scheduleClient.SetEnabledAsync(new ScheduleSetEnabledRequest(
            ScheduleManagementIpcContract.CurrentVersion,
            current.ScheduleId,
            current.Revision,
            enabled), cancellationToken).ConfigureAwait(true);
        var updated = RequireSuccessfulSchedule(response);
        ApplySchedule(updated);
        UpsertGrid(updated);
        _status.Text = enabled
            ? "Schedule enabled for background preview generation only."
            : "Schedule disabled. No future preview occurrence is queued.";
        _status.ForeColor = StorageHubTheme.Success;
        return updated;
    }

    public async Task DeleteCurrentScheduleAsync(CancellationToken cancellationToken = default)
    {
        var current = _current ?? throw new InvalidOperationException("Select a saved schedule first.");
        var response = await _scheduleClient.DeleteAsync(new ScheduleDeleteRequest(
            ScheduleManagementIpcContract.CurrentVersion,
            current.ScheduleId,
            current.Revision), cancellationToken).ConfigureAwait(true);
        if (response.Outcome is not (ScheduleMutationOutcome.Succeeded or ScheduleMutationOutcome.AlreadyApplied) ||
            response.Failure is not null)
        {
            throw new InvalidOperationException(response.Failure?.Message ?? "The schedule could not be deleted.");
        }

        RemoveGridRow(current.ScheduleId);
        BeginNewSchedule();
        _status.Text = "Schedule deleted. Any previously generated preview remains reviewable by run ID.";
        _status.ForeColor = StorageHubTheme.Success;
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_initialLoadStarted)
        {
            return;
        }

        _initialLoadStarted = true;
        try
        {
            await LoadSchedulesAsync(_lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Closing the window cancels its in-flight IPC work.
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            ShowError(error);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _grid.SelectionChanged -= GridSelectionChanged;
            _lifetime.Cancel();
            _lifetime.Dispose();
            if (_ownsClients)
            {
                _scheduleClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _syncClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        base.Dispose(disposing);
    }

    private Panel BuildEditor()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = StorageHubTheme.Surface,
            Padding = new Padding(12)
        };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(8)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var heading = UiControlFactory.CreateSectionTitle("Preview schedule");
        var description = UiControlFactory.CreateDescription(
            "Cron uses five fields. Time-zone and daylight-saving rules are evaluated by the background agent.");
        table.Controls.Add(heading, 0, 0);
        table.SetColumnSpan(heading, 2);
        table.Controls.Add(description, 0, 1);
        table.SetColumnSpan(description, 2);
        table.RowCount = 2;
        UiControlFactory.AddLabeledRow(table, "Sync profile", _profile, "Only saved profiles can be scheduled.");
        UiControlFactory.AddLabeledRow(table, "Cron expression", _cron, "Example: 0 2 * * * generates a preview daily at 02:00.");
        UiControlFactory.AddLabeledRow(table, "Time zone", _timeZone, "DST gaps and overlaps use the selected system time-zone rules.");
        UiControlFactory.AddLabeledRow(table, "Misfire grace (min)", _misfireGraceMinutes, "Expired occurrences outside this window are skipped safely.");
        UiControlFactory.AddLabeledRow(table, "Overlap", _queueOne, "At most one coalesced preview occurrence is retained.");
        UiControlFactory.AddLabeledRow(table, "Enabled", _enabled, "New schedules remain disabled until explicitly enabled.");
        UiControlFactory.AddLabeledRow(
            table,
            "Execution mode",
            new Label
            {
                Text = "Preview only (fixed)",
                AutoSize = true,
                ForeColor = StorageHubTheme.Success,
                Padding = new Padding(0, 7, 0, 0)
            },
            "Approval and provider apply always require a separate reviewed action.");
        UiControlFactory.AddLabeledRow(table, "Next preview", _nextOccurrence, "Calculated by the agent after save or enable.");
        UiControlFactory.AddLabeledRow(table, "Current state", _runState, "Active runs block destructive management changes.");
        panel.Controls.Add(table);
        return panel;
    }

    private ScheduleDraftDocument BuildDraft() => new(
        (_profile.SelectedItem as ProfileChoice)?.ProfileId ?? Guid.Empty,
        _cron.Text.Trim(),
        _timeZone.SelectedItem?.ToString() ?? string.Empty,
        checked(decimal.ToInt32(_misfireGraceMinutes.Value) * 60),
        _queueOne.Checked,
        _enabled.Checked,
        ScheduleIpcExecutionMode.PreviewOnly);

    private void BeginNewSchedule()
    {
        _current = null;
        _cron.Text = "0 2 * * *";
        SelectTimeZone("UTC");
        _misfireGraceMinutes.Value = 24 * 60;
        _queueOne.Checked = true;
        _enabled.Checked = false;
        if (_profile.Items.Count > 0)
        {
            _profile.SelectedIndex = 0;
        }

        _nextOccurrence.Text = "Not scheduled until saved and enabled.";
        _runState.Text = "No active preview run.";
        _toggle.Text = "Enable";
        _toggle.Enabled = false;
        _delete.Enabled = false;
        _save.Enabled = true;
        _grid.ClearSelection();
        _status.Text = "New schedule draft. Preview-only is fixed; enabled is off by default.";
        _status.ForeColor = StorageHubTheme.TextMuted;
    }

    private void ApplySchedule(ScheduleDocument schedule)
    {
        _current = schedule;
        SelectProfile(schedule.ProfileId, schedule.ProfileDisplayName);
        _cron.Text = schedule.CronExpression;
        SelectTimeZone(schedule.TimeZoneId);
        _misfireGraceMinutes.Value = Math.Clamp(
            schedule.MisfireGraceSeconds / 60,
            _misfireGraceMinutes.Minimum,
            _misfireGraceMinutes.Maximum);
        _queueOne.Checked = schedule.QueueOneWhileRunning;
        _enabled.Checked = schedule.Enabled;
        _nextOccurrence.Text = schedule.NextOccurrenceUtc is { } next
            ? $"{next.ToLocalTime():g} ({next:O})"
            : "No future preview is scheduled.";
        _runState.Text = schedule.IsBusy
            ? "A preview occurrence is active; update, disable, and delete are blocked."
            : schedule.LastRunOutcome is { Length: > 0 } outcome
                ? $"Idle · last outcome: {outcome}{FormatErrorCode(schedule.LastErrorCode)}"
                : "Idle · no recorded outcome.";
        _toggle.Text = schedule.Enabled ? "Disable" : "Enable";
        _toggle.Enabled = !schedule.IsBusy;
        _delete.Enabled = !schedule.IsBusy;
        _save.Enabled = !schedule.IsBusy;
    }

    private void PopulateProfiles(IEnumerable<SyncProfileSummary> profiles)
    {
        var selected = (_profile.SelectedItem as ProfileChoice)?.ProfileId;
        _profile.Items.Clear();
        foreach (var profile in profiles.OrderBy(static profile => profile.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            _profile.Items.Add(new ProfileChoice(profile.ProfileId, profile.DisplayName, profile.Enabled));
        }

        if (selected is { } profileId)
        {
            SelectProfile(profileId, profileId.ToString("D"));
        }
        else if (_profile.Items.Count > 0)
        {
            _profile.SelectedIndex = 0;
        }
    }

    private void PopulateGrid(IEnumerable<ScheduleDocument> schedules)
    {
        _suppressSelection = true;
        try
        {
            _grid.Rows.Clear();
            foreach (var schedule in schedules)
            {
                AddGridRow(schedule);
            }

            _grid.ClearSelection();
        }
        finally
        {
            _suppressSelection = false;
        }
    }

    private void AddGridRow(ScheduleDocument schedule)
    {
        var index = _grid.Rows.Add(
            schedule.ProfileDisplayName,
            schedule.CronExpression,
            schedule.TimeZoneId,
            schedule.NextOccurrenceUtc is { } next ? next.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "—",
            schedule.Enabled ? "Yes" : "No",
            schedule.IsBusy ? "Preview active" : schedule.LastRunOutcome ?? "Idle");
        _grid.Rows[index].Tag = schedule;
    }

    private void UpsertGrid(ScheduleDocument schedule)
    {
        _suppressSelection = true;
        try
        {
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.Tag is ScheduleDocument existing && existing.ScheduleId == schedule.ScheduleId)
                {
                    var index = row.Index;
                    _grid.Rows.RemoveAt(index);
                    AddGridRow(schedule);
                    SelectGridRow(schedule.ScheduleId);
                    return;
                }
            }

            AddGridRow(schedule);
            SelectGridRow(schedule.ScheduleId);
        }
        finally
        {
            _suppressSelection = false;
        }
    }

    private void RemoveGridRow(Guid scheduleId)
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is ScheduleDocument schedule && schedule.ScheduleId == scheduleId)
            {
                _grid.Rows.Remove(row);
                return;
            }
        }
    }

    private void SelectGridRow(Guid scheduleId)
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is ScheduleDocument schedule && schedule.ScheduleId == scheduleId)
            {
                row.Selected = true;
                _grid.CurrentCell = row.Cells[0];
                return;
            }
        }
    }

    private void SelectProfile(Guid profileId, string fallbackName)
    {
        for (var index = 0; index < _profile.Items.Count; index++)
        {
            if (_profile.Items[index] is ProfileChoice choice && choice.ProfileId == profileId)
            {
                _profile.SelectedIndex = index;
                return;
            }
        }

        _profile.Items.Add(new ProfileChoice(profileId, fallbackName, Enabled: false));
        _profile.SelectedIndex = _profile.Items.Count - 1;
    }

    private void SelectTimeZone(string timeZoneId)
    {
        for (var index = 0; index < _timeZone.Items.Count; index++)
        {
            if (string.Equals(_timeZone.Items[index]?.ToString(), timeZoneId, StringComparison.Ordinal))
            {
                _timeZone.SelectedIndex = index;
                return;
            }
        }

        _timeZone.Items.Add(timeZoneId);
        _timeZone.SelectedIndex = _timeZone.Items.Count - 1;
    }

    private async void GridSelectionChanged(object? sender, EventArgs e)
    {
        if (_suppressSelection || _grid.SelectedRows.Count != 1 ||
            _grid.SelectedRows[0].Tag is not ScheduleDocument schedule)
        {
            return;
        }

        try
        {
            await SelectScheduleAsync(schedule.ScheduleId, _lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Closing the window cancels its in-flight IPC work.
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            ShowError(error);
        }
    }

    private async void RefreshClicked(object? sender, EventArgs e)
    {
        await RunBusyAsync(
            async token =>
            {
                await LoadSchedulesAsync(token).ConfigureAwait(true);
                return true;
            },
            "Refreshing schedules…").ConfigureAwait(true);
    }

    private async void SaveClicked(object? sender, EventArgs e)
    {
        await RunBusyAsync(
            token => SaveCurrentScheduleAsync(token),
            "Saving preview-only schedule…").ConfigureAwait(true);
    }

    private async void ToggleClicked(object? sender, EventArgs e)
    {
        var target = !(_current?.Enabled ?? false);
        await RunBusyAsync(
            token => SetCurrentEnabledAsync(target, token),
            target ? "Enabling preview schedule…" : "Disabling schedule…").ConfigureAwait(true);
    }

    private async void DeleteClicked(object? sender, EventArgs e)
    {
        if (MessageBox.Show(
                this,
                "Delete this preview-only schedule? Existing preview runs are not deleted.",
                "Delete schedule",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.OK)
        {
            return;
        }

        await RunBusyAsync(
            async token =>
            {
                await DeleteCurrentScheduleAsync(token).ConfigureAwait(true);
                return true;
            },
            "Deleting schedule…").ConfigureAwait(true);
    }

    private async Task RunBusyAsync<T>(Func<CancellationToken, Task<T>> action, string message)
    {
        _save.Enabled = false;
        _toggle.Enabled = false;
        _delete.Enabled = false;
        _status.Text = message;
        _status.ForeColor = StorageHubTheme.TextMuted;
        try
        {
            _ = await action(_lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Closing the window cancels its in-flight IPC work.
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            ShowError(error);
        }
        finally
        {
            if (!_disposed && !IsDisposed && !Disposing)
            {
                var busy = _current?.IsBusy == true;
                _save.Enabled = !busy;
                _toggle.Enabled = _current is not null && !busy;
                _delete.Enabled = _current is not null && !busy;
            }
        }
    }

    private static ScheduleDocument RequireSuccessfulSchedule(ScheduleMutationResponse response)
    {
        if (response.Outcome is not (ScheduleMutationOutcome.Succeeded or ScheduleMutationOutcome.AlreadyApplied) ||
            response.Failure is not null)
        {
            throw new InvalidOperationException(response.Failure?.Message ?? "The schedule could not be changed.");
        }

        return response.Schedule ?? throw new InvalidDataException("The agent did not return the saved schedule revision.");
    }

    private void ShowError(Exception error)
    {
        _status.Text = error.Message;
        _status.ForeColor = StorageHubTheme.Danger;
    }

    private static void ThrowIfFailure(StorageIpcFailure? failure)
    {
        if (failure is not null)
        {
            throw new InvalidOperationException(failure.Message);
        }
    }

    private static string FormatErrorCode(string? code) => code is { Length: > 0 } ? $" · {code}" : string.Empty;

    private sealed record ProfileChoice(Guid ProfileId, string DisplayName, bool Enabled)
    {
        public override string ToString() => Enabled ? DisplayName : $"{DisplayName} (disabled)";
    }
}
