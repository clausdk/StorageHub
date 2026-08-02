using Krypton.Toolkit;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

/// <summary>Edits persisted V6 sync profiles and drives the real preview/review/dispatch workflow.</summary>
public sealed class SyncProfileEditorForm : KryptonForm
{
    private readonly ISyncManagementAgentClient _syncClient;
    private readonly IRemoteStorageAgentClient _storageClient;
    private readonly bool _ownsClients;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ComboBox _profileChoice;
    private readonly TextBox _name;
    private readonly CheckBox _enabled;
    private readonly ComboBox _leftConnection;
    private readonly TextBox _leftRoot;
    private readonly ComboBox _rightConnection;
    private readonly TextBox _rightRoot;
    private readonly ComboBox _direction;
    private readonly ComboBox _deletionMode;
    private readonly NumericUpDown _maximumDeletionCount;
    private readonly NumericUpDown _maximumDeletionPercentage;
    private readonly CheckBox _overwrite;
    private readonly NumericUpDown _bufferSize;
    private readonly Label _status;
    private readonly Button _save;
    private readonly Button _preview;
    private readonly SyncRunReviewControl _review;
    private readonly TabControl _tabs;
    private SyncProfileDocument? _currentProfile;
    private bool _initialLoadStarted;
    private bool _suppressProfileSelection;
    private bool _disposed;

    public SyncProfileEditorForm()
        : this(
            new NamedPipeSyncManagementAgentClient(),
            new NamedPipeRemoteStorageAgentClient(),
            ownsClients: true)
    {
    }

    public SyncProfileEditorForm(
        ISyncManagementAgentClient syncClient,
        IRemoteStorageAgentClient storageClient,
        bool ownsClients = false)
    {
        _syncClient = syncClient ?? throw new ArgumentNullException(nameof(syncClient));
        _storageClient = storageClient ?? throw new ArgumentNullException(nameof(storageClient));
        _ownsClients = ownsClients;
        Text = "Sync Profiles — StorageHub";
        AccessibleName = "Sync Profile Editor";
        AccessibleDescription = "Create and update a persisted preview-first synchronization profile.";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(920, 650);
        Size = new Size(1120, 780);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = StorageHubTheme.Canvas;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 84,
            ColumnCount = 3,
            Padding = new Padding(18, 11, 18, 9),
            BackColor = StorageHubTheme.Surface
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var title = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = Padding.Empty
        };
        title.Controls.Add(UiControlFactory.CreateSectionTitle("Preview-first synchronization"));
        title.Controls.Add(UiControlFactory.CreateDescription(
            "Save a bounded profile, inspect its immutable plan, then explicitly dispatch that exact revision."));
        header.Controls.Add(title, 0, 0);
        _profileChoice = new ComboBox
        {
            Name = "SyncProfileChoice",
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            AccessibleName = "Saved synchronization profile",
            Margin = new Padding(8, 12, 8, 0)
        };
        _profileChoice.SelectedIndexChanged += ProfileSelectionChanged;
        header.Controls.Add(_profileChoice, 1, 0);
        var newProfile = new Button { Text = "New profile", AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
        StorageHubTheme.StyleSecondaryButton(newProfile);
        newProfile.Click += (_, _) => BeginNewProfile();
        header.Controls.Add(newProfile, 2, 0);

        _name = new TextBox
        {
            Name = "SyncProfileName",
            Text = "New Sync Profile",
            MaxLength = SyncManagementIpcLimits.MaximumDisplayNameLength
        };
        _enabled = new CheckBox { Text = "Enabled", AutoSize = true };
        _leftConnection = CreateChoice("Left connection");
        _leftRoot = CreateRootTextBox("Left connection-relative root");
        _rightConnection = CreateChoice("Right connection");
        _rightRoot = CreateRootTextBox("Right connection-relative root");
        _direction = CreateEnumChoice<SyncIpcDirection>("Synchronization direction");
        _direction.SelectedItem = SyncIpcDirection.LeftToRight;
        _deletionMode = CreateEnumChoice<SyncIpcDeletionMode>("Deletion mode");
        _deletionMode.SelectedItem = SyncIpcDeletionMode.Disabled;
        _direction.SelectedIndexChanged += (_, _) => EnforceDirectionCompatibility();
        _maximumDeletionCount = CreateNumeric(
            SyncPresentationCatalog.DefaultMassDeleteItemLimit,
            1,
            SyncManagementIpcLimits.MaximumDeletionCount);
        _maximumDeletionPercentage = CreateNumeric(
            SyncPresentationCatalog.DefaultMassDeletePercentageLimit,
            0.01m,
            100m,
            decimalPlaces: 2,
            increment: 0.25m);
        _overwrite = new CheckBox { Text = "Allow overwrite after preview", AutoSize = true };
        _bufferSize = CreateNumeric(64 * 1024, 1, SyncManagementIpcLimits.MaximumTransferBufferSize);

        _tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(14, 5),
            HotTrack = true,
            AccessibleName = "Synchronization profile workflow"
        };
        _tabs.TabPages.Add(BuildProfilePage());
        _review = new SyncRunReviewControl(_syncClient);
        var previewPage = new TabPage("Preview & approve")
        {
            BackColor = StorageHubTheme.Surface,
            Padding = new Padding(5)
        };
        previewPage.Controls.Add(_review);
        _tabs.TabPages.Add(previewPage);

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
            Name = "SyncProfileStatus",
            Text = "The editor connects to the background agent when shown.",
            AutoSize = true,
            ForeColor = StorageHubTheme.TextMuted,
            Anchor = AnchorStyles.Left,
            AccessibleName = "Synchronization profile status"
        };
        footer.Controls.Add(_status, 0, 0);
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel };
        StorageHubTheme.StyleSecondaryButton(close);
        _save = new Button { Name = "SaveSyncProfile", Text = "Save profile", AutoSize = true };
        StorageHubTheme.StyleSecondaryButton(_save);
        _save.Click += SaveClicked;
        _preview = new Button { Name = "GenerateSyncPreview", Text = "Generate preview", AutoSize = true };
        StorageHubTheme.StylePrimaryButton(_preview);
        _preview.Click += PreviewClicked;
        actions.Controls.Add(close);
        actions.Controls.Add(_save);
        actions.Controls.Add(_preview);
        footer.Controls.Add(actions, 1, 0);
        CancelButton = close;

        Controls.Add(_tabs);
        Controls.Add(footer);
        Controls.Add(header);
    }

    public SyncProfileDocument? CurrentProfile => _currentProfile;

    public SyncRunReviewControl Review => _review;

    public string StatusText => _status.Text;

    /// <summary>Loads saved profiles and endpoints. Construction itself remains IPC-inert.</summary>
    public async Task LoadProfilesAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        _status.Text = "Loading saved profiles and connections…";
        _status.ForeColor = StorageHubTheme.TextMuted;
        var profileTask = _syncClient.ListProfilesAsync(new SyncProfileListRequest(
            SyncManagementIpcContract.CurrentVersion,
            IncludeDisabled: true,
            MaximumCount: SyncManagementIpcLimits.MaximumProfileResults), linked.Token);
        var connectionTask = _storageClient.ListConnectionsAsync(new ConnectionListRequest(
            StorageIpcContract.CurrentVersion,
            IncludeDisabled: true,
            Limit: StorageIpcLimits.MaximumConnectionResults), linked.Token);
        await Task.WhenAll(profileTask, connectionTask).ConfigureAwait(true);
        var profiles = await profileTask.ConfigureAwait(true);
        var connections = await connectionTask.ConfigureAwait(true);
        ThrowIfFailure(profiles.Failure);
        ThrowIfFailure(connections.Failure);
        PopulateConnections(connections.Connections);
        PopulateProfiles(profiles.Profiles);
        _status.Text = profiles.Profiles.Length == 0
            ? "No saved profile yet. Configure two saved connections, save, then generate a preview."
            : $"Loaded {profiles.Profiles.Length} saved profile(s).";
        _status.ForeColor = StorageHubTheme.Success;
    }

    public async Task SelectProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("A sync profile ID is required.", nameof(profileId));
        }

        var response = await _syncClient.GetProfileAsync(new SyncProfileGetRequest(
            SyncManagementIpcContract.CurrentVersion,
            profileId), cancellationToken).ConfigureAwait(true);
        ThrowIfFailure(response.Failure);
        ApplyProfile(response.Profile ?? throw new InvalidDataException("The agent returned an incomplete profile."));
    }

    public async Task<SyncProfileDocument> SaveCurrentProfileAsync(
        CancellationToken cancellationToken = default)
    {
        var draft = BuildDraft();
        if (!draft.HasValidBounds)
        {
            throw new InvalidOperationException(
                "Complete two different connections and use bounded profile values. Mirror applies only to one-way sync; propagate applies only to two-way sync.");
        }

        SyncProfileMutationResponse response;
        if (_currentProfile is null)
        {
            var profileId = Guid.NewGuid();
            response = await _syncClient.CreateProfileAsync(new SyncProfileCreateRequest(
                SyncManagementIpcContract.CurrentVersion,
                profileId,
                draft), cancellationToken).ConfigureAwait(true);
        }
        else
        {
            response = await _syncClient.UpdateProfileAsync(new SyncProfileUpdateRequest(
                SyncManagementIpcContract.CurrentVersion,
                _currentProfile.ProfileId,
                _currentProfile.Revision,
                draft), cancellationToken).ConfigureAwait(true);
        }

        if (response.Outcome is not (SyncProfileMutationOutcome.Succeeded or SyncProfileMutationOutcome.AlreadyApplied))
        {
            throw new InvalidOperationException(response.Failure?.Message ?? "The sync profile could not be saved.");
        }

        var saved = response.Profile ?? throw new InvalidDataException("The agent did not return the saved profile revision.");
        ApplyProfile(saved);
        UpsertProfileChoice(saved);
        _status.Text = $"Profile saved at revision {saved.Revision}. No provider changes were requested.";
        _status.ForeColor = StorageHubTheme.Success;
        return saved;
    }

    public async Task<SyncRunSummary> GeneratePreviewAsync(CancellationToken cancellationToken = default)
    {
        var profile = await SaveCurrentProfileAsync(cancellationToken).ConfigureAwait(true);
        _status.Text = "Scanning both endpoints and generating an immutable preview…";
        _status.ForeColor = StorageHubTheme.TextMuted;
        var response = await _syncClient.GeneratePreviewAsync(new SyncPreviewGenerateRequest(
            SyncManagementIpcContract.CurrentVersion,
            profile.ProfileId,
            Guid.NewGuid()), cancellationToken).ConfigureAwait(true);
        ThrowIfFailure(response.Failure);
        var run = response.Run ?? throw new InvalidDataException("The agent did not return the generated preview run.");
        var plan = response.Plan ?? throw new InvalidDataException("The agent did not return the immutable plan summary.");
        if (plan.SyncRunId != run.SyncRunId ||
            plan.PlanId != run.PlanId ||
            !string.Equals(plan.PlanSha256, run.PlanSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The generated plan did not match its run summary.");
        }

        await _review.ShowPreviewAsync(run, cancellationToken).ConfigureAwait(true);
        _tabs.SelectedIndex = 1;
        _status.Text = "Preview generated. Review the immutable operations before explicit approval.";
        _status.ForeColor = StorageHubTheme.Success;
        return run;
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
            await LoadProfilesAsync(_lifetime.Token).ConfigureAwait(true);
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
            _profileChoice.SelectedIndexChanged -= ProfileSelectionChanged;
            _lifetime.Cancel();
            _lifetime.Dispose();
            if (_ownsClients)
            {
                _syncClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _storageClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        base.Dispose(disposing);
    }

    private TabPage BuildProfilePage()
    {
        var page = new TabPage("Profile") { BackColor = StorageHubTheme.Surface, Padding = new Padding(10) };
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
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var heading = UiControlFactory.CreateSectionTitle("Profile and endpoint policy");
        var description = UiControlFactory.CreateDescription(
            "All paths are connection-relative. Credentials, trust, and client certificates stay with each saved connection.");
        table.Controls.Add(heading, 0, 0);
        table.SetColumnSpan(heading, 2);
        table.Controls.Add(description, 0, 1);
        table.SetColumnSpan(description, 2);
        table.RowCount = 2;
        UiControlFactory.AddLabeledRow(table, "Name", _name, "A user-visible workflow name; never place secrets here.");
        UiControlFactory.AddLabeledRow(table, "Enabled", _enabled, "Disabled profiles can still be saved and previewed manually.");
        UiControlFactory.AddLabeledRow(table, "Left connection", _leftConnection, "A saved CL.Storage connection and its per-connection security settings.");
        UiControlFactory.AddLabeledRow(table, "Left root", _leftRoot, "Empty means the saved connection root.");
        UiControlFactory.AddLabeledRow(table, "Right connection", _rightConnection, "Must differ from the left connection.");
        UiControlFactory.AddLabeledRow(table, "Right root", _rightRoot, "Use normalized forward-slash connection-relative paths.");
        UiControlFactory.AddLabeledRow(table, "Direction", _direction, "One-way or baseline-aware two-way planning.");
        UiControlFactory.AddLabeledRow(table, "Deletion mode", _deletionMode, "Disabled is the safe default; every delete remains visible in preview.");
        UiControlFactory.AddLabeledRow(table, "Maximum deletes", _maximumDeletionCount, "The run blocks when this guard is exceeded.");
        UiControlFactory.AddLabeledRow(table, "Maximum baseline %", _maximumDeletionPercentage, "The run blocks when this percentage guard is exceeded.");
        UiControlFactory.AddLabeledRow(table, "Overwrite", _overwrite, "Applied only after exact-plan approval and durable dispatch.");
        UiControlFactory.AddLabeledRow(table, "Transfer buffer (bytes)", _bufferSize, "Bounded from 1 byte through 1 MiB.");
        panel.Controls.Add(table);
        page.Controls.Add(panel);
        return page;
    }

    private SyncProfileDraftDocument BuildDraft()
    {
        var left = (_leftConnection.SelectedItem as ConnectionChoice)?.Connection.ConnectionId ?? Guid.Empty;
        var right = (_rightConnection.SelectedItem as ConnectionChoice)?.Connection.ConnectionId ?? Guid.Empty;
        return new SyncProfileDraftDocument(
            _name.Text.Trim(),
            left,
            _leftRoot.Text.Trim(),
            right,
            _rightRoot.Text.Trim(),
            SelectedEnum<SyncIpcDirection>(_direction),
            SelectedEnum<SyncIpcDeletionMode>(_deletionMode),
            SyncIpcConflictPolicy.Block,
            decimal.ToInt32(_maximumDeletionCount.Value),
            _maximumDeletionPercentage.Value,
            _overwrite.Checked,
            decimal.ToInt32(_bufferSize.Value),
            _enabled.Checked);
    }

    private void ApplyProfile(SyncProfileDocument profile)
    {
        _currentProfile = profile;
        _name.Text = profile.Draft.DisplayName;
        _enabled.Checked = profile.Draft.Enabled;
        SelectConnection(_leftConnection, profile.Draft.LeftConnectionId);
        _leftRoot.Text = profile.Draft.LeftRoot;
        SelectConnection(_rightConnection, profile.Draft.RightConnectionId);
        _rightRoot.Text = profile.Draft.RightRoot;
        _direction.SelectedItem = profile.Draft.Direction;
        _deletionMode.SelectedItem = profile.Draft.DeletionMode;
        _maximumDeletionCount.Value = profile.Draft.MaximumDeletionCount;
        _maximumDeletionPercentage.Value = profile.Draft.MaximumDeletionPercentage;
        _overwrite.Checked = profile.Draft.Overwrite;
        _bufferSize.Value = profile.Draft.TransferBufferSize;
        SelectProfileChoice(profile.ProfileId);
        _status.Text = $"Loaded profile revision {profile.Revision}.";
        _status.ForeColor = StorageHubTheme.TextMuted;
    }

    private void BeginNewProfile()
    {
        _currentProfile = null;
        _suppressProfileSelection = true;
        try
        {
            _profileChoice.SelectedIndex = _profileChoice.Items.Count == 0 ? -1 : 0;
        }
        finally
        {
            _suppressProfileSelection = false;
        }

        _name.Text = "New Sync Profile";
        _enabled.Checked = false;
        _leftRoot.Clear();
        _rightRoot.Clear();
        _direction.SelectedItem = SyncIpcDirection.LeftToRight;
        _deletionMode.SelectedItem = SyncIpcDeletionMode.Disabled;
        _maximumDeletionCount.Value = SyncPresentationCatalog.DefaultMassDeleteItemLimit;
        _maximumDeletionPercentage.Value = SyncPresentationCatalog.DefaultMassDeletePercentageLimit;
        _overwrite.Checked = false;
        _bufferSize.Value = 64 * 1024;
        if (_leftConnection.Items.Count > 0)
        {
            _leftConnection.SelectedIndex = 0;
        }

        if (_rightConnection.Items.Count > 1)
        {
            _rightConnection.SelectedIndex = 1;
        }
        else if (_rightConnection.Items.Count > 0)
        {
            _rightConnection.SelectedIndex = 0;
        }

        _tabs.SelectedIndex = 0;
        _status.Text = "New profile draft. Saving does not change either provider.";
        _status.ForeColor = StorageHubTheme.TextMuted;
    }

    private void PopulateProfiles(IEnumerable<SyncProfileSummary> profiles)
    {
        var selected = _currentProfile?.ProfileId;
        _suppressProfileSelection = true;
        try
        {
            _profileChoice.Items.Clear();
            _profileChoice.Items.Add(ProfileChoice.New);
            foreach (var profile in profiles.OrderBy(static profile => profile.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                _profileChoice.Items.Add(new ProfileChoice(profile.ProfileId, profile.DisplayName, profile.Enabled));
            }

            _profileChoice.SelectedIndex = 0;
            if (selected is { } profileId)
            {
                SelectProfileChoice(profileId);
            }
        }
        finally
        {
            _suppressProfileSelection = false;
        }
    }

    private void PopulateConnections(IEnumerable<ConnectionSummary> connections)
    {
        var choices = connections
            .OrderByDescending(static connection => connection.IsFavorite)
            .ThenBy(static connection => connection.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(static connection => new ConnectionChoice(connection))
            .ToArray();
        _leftConnection.Items.Clear();
        _rightConnection.Items.Clear();
        _leftConnection.Items.AddRange(choices.Cast<object>().ToArray());
        _rightConnection.Items.AddRange(choices.Cast<object>().ToArray());
        if (choices.Length > 0)
        {
            _leftConnection.SelectedIndex = 0;
            _rightConnection.SelectedIndex = choices.Length > 1 ? 1 : 0;
        }
    }

    private void UpsertProfileChoice(SyncProfileDocument profile)
    {
        _suppressProfileSelection = true;
        try
        {
            for (var index = 1; index < _profileChoice.Items.Count; index++)
            {
                if (_profileChoice.Items[index] is ProfileChoice choice && choice.ProfileId == profile.ProfileId)
                {
                    _profileChoice.Items[index] = new ProfileChoice(
                        profile.ProfileId,
                        profile.Draft.DisplayName,
                        profile.Draft.Enabled);
                    _profileChoice.SelectedIndex = index;
                    return;
                }
            }

            _profileChoice.Items.Add(new ProfileChoice(
                profile.ProfileId,
                profile.Draft.DisplayName,
                profile.Draft.Enabled));
            _profileChoice.SelectedIndex = _profileChoice.Items.Count - 1;
        }
        finally
        {
            _suppressProfileSelection = false;
        }
    }

    private void SelectProfileChoice(Guid profileId)
    {
        for (var index = 1; index < _profileChoice.Items.Count; index++)
        {
            if (_profileChoice.Items[index] is ProfileChoice choice && choice.ProfileId == profileId)
            {
                _profileChoice.SelectedIndex = index;
                return;
            }
        }
    }

    private static void SelectConnection(ComboBox combo, Guid connectionId)
    {
        for (var index = 0; index < combo.Items.Count; index++)
        {
            if (combo.Items[index] is ConnectionChoice choice && choice.Connection.ConnectionId == connectionId)
            {
                combo.SelectedIndex = index;
                return;
            }
        }

        throw new InvalidOperationException("A connection referenced by this profile is not available in the connection manager.");
    }

    private void EnforceDirectionCompatibility()
    {
        if (_direction.SelectedItem is not SyncIpcDirection direction ||
            _deletionMode.SelectedItem is not SyncIpcDeletionMode deletion)
        {
            return;
        }

        if (direction == SyncIpcDirection.TwoWay && deletion == SyncIpcDeletionMode.Mirror ||
            direction != SyncIpcDirection.TwoWay && deletion == SyncIpcDeletionMode.Propagate)
        {
            _deletionMode.SelectedItem = SyncIpcDeletionMode.Disabled;
        }
    }

    private async void ProfileSelectionChanged(object? sender, EventArgs e)
    {
        if (_suppressProfileSelection)
        {
            return;
        }

        if (_profileChoice.SelectedItem is not ProfileChoice choice || choice.ProfileId == Guid.Empty)
        {
            BeginNewProfile();
            return;
        }

        try
        {
            await SelectProfileAsync(choice.ProfileId, _lifetime.Token).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            ShowError(error);
        }
    }

    private async void SaveClicked(object? sender, EventArgs e)
    {
        await RunBusyAsync(
            token => SaveCurrentProfileAsync(token),
            "Saving profile…").ConfigureAwait(true);
    }

    private async void PreviewClicked(object? sender, EventArgs e)
    {
        await RunBusyAsync(
            token => GeneratePreviewAsync(token),
            "Generating immutable preview…").ConfigureAwait(true);
    }

    private async Task RunBusyAsync<T>(Func<CancellationToken, Task<T>> action, string status)
    {
        _save.Enabled = false;
        _preview.Enabled = false;
        _status.Text = status;
        _status.ForeColor = StorageHubTheme.TextMuted;
        try
        {
            _ = await action(_lifetime.Token).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            ShowError(error);
        }
        finally
        {
            _save.Enabled = true;
            _preview.Enabled = true;
        }
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

    private static ComboBox CreateChoice(string accessibleName) => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        AccessibleName = accessibleName
    };

    private static ComboBox CreateEnumChoice<T>(string accessibleName) where T : struct, Enum
    {
        var combo = CreateChoice(accessibleName);
        combo.Items.AddRange(Enum.GetValues<T>().Cast<object>().ToArray());
        if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }

        return combo;
    }

    private static TextBox CreateRootTextBox(string accessibleName) => new()
    {
        MaxLength = SyncManagementIpcLimits.MaximumRelativeRootLength,
        PlaceholderText = "Connection-relative path (empty = root)",
        AccessibleName = accessibleName
    };

    private static NumericUpDown CreateNumeric(
        decimal value,
        decimal minimum,
        decimal maximum,
        int decimalPlaces = 0,
        decimal increment = 1) => new()
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = Math.Clamp(value, minimum, maximum),
            DecimalPlaces = decimalPlaces,
            Increment = increment,
            ThousandsSeparator = true,
            Width = 180
        };

    private static T SelectedEnum<T>(ComboBox combo) where T : struct, Enum =>
        combo.SelectedItem is T value
            ? value
            : throw new InvalidOperationException($"Select a valid {typeof(T).Name} value.");

    private sealed record ProfileChoice(Guid ProfileId, string DisplayName, bool Enabled)
    {
        public static ProfileChoice New { get; } = new(Guid.Empty, "Create a new profile", false);

        public override string ToString() => ProfileId == Guid.Empty
            ? DisplayName
            : Enabled ? DisplayName : $"{DisplayName} (disabled)";
    }

    private sealed record ConnectionChoice(ConnectionSummary Connection)
    {
        public override string ToString() =>
            $"[{Connection.Provider}] {Connection.DisplayName}{(Connection.IsEnabled ? string.Empty : " (disabled)")}";
    }
}
