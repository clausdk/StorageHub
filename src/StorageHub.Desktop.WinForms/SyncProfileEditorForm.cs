using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

/// <summary>Edits persisted V6 sync profiles and drives the real preview/review/dispatch workflow.</summary>
public sealed class SyncProfileEditorForm : Form
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
    private readonly Button _leftBrowse;
    private readonly ComboBox _rightConnection;
    private readonly TextBox _rightRoot;
    private readonly Button _rightBrowse;
    private readonly SyncBehaviorPickerControl _behavior;
    private readonly ComboBox _conflictPolicy;
    private readonly TextBox _includeGlobs;
    private readonly TextBox _excludeGlobs;
    private readonly CheckBox _includeHiddenFiles;
    private readonly NumericUpDown _maximumDeletionCount;
    private readonly NumericUpDown _maximumDeletionPercentage;
    private readonly NumericUpDown _bufferSize;
    private readonly CheckBox _allowNonAtomicDestinationWrites;
    private readonly Label _status;
    private readonly Button _save;
    private readonly Button _preview;
    private readonly SyncRunReviewControl _review;
    private readonly TabControl _tabs;
    private SyncProfileDocument? _currentProfile;
    private SyncRunSummary? _lastGeneratedRun;
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
        AccessibleDescription = "Create, review, and run a persisted synchronization profile.";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(1000, 700);
        Size = new Size(1240, 860);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = StorageHubTheme.Canvas;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        StorageHubTheme.Register(this);

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
        title.Controls.Add(new Label
        {
            Text = "Safe synchronization",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 14F),
            ForeColor = StorageHubTheme.Text,
            Margin = Padding.Empty
        });
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
        _leftConnection = CreateChoice("Location A connection");
        _leftRoot = CreateRootTextBox("Location A connection-relative folder");
        _leftRoot.Name = "LocationAFolder";
        _leftBrowse = CreateBrowseButton("BrowseLocationA", "Browse Location A folders");
        _rightConnection = CreateChoice("Location B connection");
        _rightRoot = CreateRootTextBox("Location B connection-relative folder");
        _rightRoot.Name = "LocationBFolder";
        _rightBrowse = CreateBrowseButton("BrowseLocationB", "Browse Location B folders");
        _leftConnection.SelectedIndexChanged += LocationConnectionChanged;
        _rightConnection.SelectedIndexChanged += LocationConnectionChanged;
        _leftBrowse.Click += BrowseLeftClicked;
        _rightBrowse.Click += BrowseRightClicked;
        UpdateLocationSelectorState(_leftConnection, _leftRoot, _leftBrowse);
        UpdateLocationSelectorState(_rightConnection, _rightRoot, _rightBrowse);
        _behavior = new SyncBehaviorPickerControl
        {
            SelectedBehavior = SyncIpcBehavior.UpdateAToB
        };
        _conflictPolicy = CreateEnumChoice<SyncIpcConflictPolicy>("Conflict policy");
        _conflictPolicy.Format += FormatConflictPolicy;
        _conflictPolicy.SelectedItem = SyncIpcConflictPolicy.Block;
        _includeGlobs = CreateGlobTextBox("Include glob filters");
        _excludeGlobs = CreateGlobTextBox("Exclude glob filters");
        _excludeGlobs.Lines = [".storagehub", ".storagehub/**", "**/.storagehub/**"];
        _includeHiddenFiles = new CheckBox { Text = "Include hidden files", Checked = true, AutoSize = true };
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
        _bufferSize = CreateNumeric(64 * 1024, 1, SyncManagementIpcLimits.MaximumTransferBufferSize);
        _allowNonAtomicDestinationWrites = new CheckBox
        {
            Name = "AllowNonAtomicDestinationWrites",
            Text = "Allow for FTP / SFTP compatibility",
            AutoSize = true,
            AccessibleName = "Allow non-atomic destination writes",
            ForeColor = StorageHubTheme.Warning
        };

        _tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            AccessibleName = "Synchronization profile workflow"
        };
        StorageHubTheme.ConfigureTabs(_tabs);
        _tabs.TabPages.Add(BuildProfilePage());
        _review = new SyncRunReviewControl(_syncClient);
        var previewPage = new TabPage("Plan & run")
        {
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
        _preview = new Button { Name = "GenerateSyncPreview", Text = "Review & run", AutoSize = true };
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

    public SyncRunSummary? LastGeneratedRun => _lastGeneratedRun;

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

        SyncProfileListResponse? profiles = null;
        ConnectionListResponse? connections = null;
        Exception? profileError = null;
        Exception? connectionError = null;
        try
        {
            connections = await connectionTask.ConfigureAwait(true);
            ThrowIfFailure(connections.Failure);
            PopulateConnections(connections.Connections);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            connectionError = error;
        }

        try
        {
            profiles = await profileTask.ConfigureAwait(true);
            ThrowIfFailure(profiles.Failure);
            PopulateProfiles(profiles.Profiles);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            profileError = error;
        }

        if (profileError is null && connectionError is null)
        {
            _status.Text = profiles!.Profiles.Length == 0
                ? "No saved profile yet. Configure two saved connections, then choose Review & run."
                : $"Loaded {profiles.Profiles.Length} saved profile(s).";
            _status.ForeColor = StorageHubTheme.Success;
            return;
        }

        _status.Text = (profileError, connectionError) switch
        {
            (not null, null) => $"Connections loaded, but sync profiles are unavailable: {profileError.Message}",
            (null, not null) => $"Sync profiles loaded, but connections are unavailable: {connectionError.Message}",
            _ => $"Profiles and connections are unavailable: {profileError!.Message} {connectionError!.Message}"
        };
        _status.ForeColor = StorageHubTheme.Danger;
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
                "Complete two non-overlapping locations and use bounded profile, filter, and safety values.");
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
        _status.Text = saved.Draft.AllowNonAtomicDestinationWrites
            ? $"Profile saved at revision {saved.Revision}. WARNING: non-atomic destination writes are enabled."
            : $"Profile saved at revision {saved.Revision}. No provider changes were requested.";
        _status.ForeColor = saved.Draft.AllowNonAtomicDestinationWrites
            ? StorageHubTheme.Warning
            : StorageHubTheme.Success;
        return saved;
    }

    public async Task<SyncRunSummary> GeneratePreviewAsync(CancellationToken cancellationToken = default)
    {
        var profile = await SaveCurrentProfileAsync(cancellationToken).ConfigureAwait(true);
        _status.Text = "Scanning both locations and preparing the sync plan…";
        _status.ForeColor = StorageHubTheme.TextMuted;
        var response = await _syncClient.GeneratePreviewAsync(new SyncPreviewGenerateRequest(
            SyncManagementIpcContract.CurrentVersion,
            profile.ProfileId,
            Guid.NewGuid()), cancellationToken).ConfigureAwait(true);
        ThrowIfFailure(response.Failure);
        var run = response.Run ?? throw new InvalidDataException("The agent did not return the generated synchronization run.");
        _lastGeneratedRun = run;
        var plan = response.Plan ?? throw new InvalidDataException("The agent did not return the immutable plan summary.");
        if (plan.SyncRunId != run.SyncRunId ||
            plan.PlanId != run.PlanId ||
            !string.Equals(plan.PlanSha256, run.PlanSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The generated plan did not match its run summary.");
        }

        await _review.ShowPreviewAsync(run, cancellationToken).ConfigureAwait(true);
        _tabs.SelectedIndex = 1;
        _status.Text = profile.Draft.AllowNonAtomicDestinationWrites
            ? "Sync plan ready. WARNING: this approved plan permits non-atomic destination writes."
            : "Sync plan ready. Review any approval-gated operations, then run it.";
        _status.ForeColor = profile.Draft.AllowNonAtomicDestinationWrites
            ? StorageHubTheme.Warning
            : StorageHubTheme.Success;
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
        var page = new TabPage("Profile") { Padding = new Padding(0) };
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = StorageHubTheme.Canvas,
            Padding = new Padding(18)
        };
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            Padding = Padding.Empty
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var intro = CreateSectionHeader(
            "Design your synchronization",
            "Choose two saved locations, then select exactly how StorageHub should compare and converge them.");
        content.Controls.Add(intro);

        var identity = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        identity.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
        identity.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        identity.Controls.Add(CreateField("Profile name", _name, "Shown in Sync tasks and run history."), 0, 0);
        identity.Controls.Add(CreateField("Profile state", _enabled, "Disabled profiles can still be reviewed manually."), 1, 0);
        content.Controls.Add(CreateCard(identity, new Padding(16), new Padding(0, 0, 0, 14)));

        content.Controls.Add(CreateSectionHeader(
            "1. Choose locations",
            "Each folder stays relative to its saved connection; credentials and trust never leave that connection."));
        var locationGrid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
        locationGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        locationGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        locationGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        locationGrid.Controls.Add(CreateLocationCard("A", "PRIMARY", _leftConnection, _leftRoot, _leftBrowse), 0, 0);
        var swap = new Button { Text = "Swap A / B", AutoSize = true, Anchor = AnchorStyles.None };
        StorageHubTheme.StyleSecondaryButton(swap);
        swap.Click += (_, _) => SwapLocations();
        locationGrid.Controls.Add(swap, 1, 0);
        locationGrid.Controls.Add(CreateLocationCard("B", "PEER", _rightConnection, _rightRoot, _rightBrowse), 2, 0);
        content.Controls.Add(locationGrid);

        content.Controls.Add(CreateSectionHeader(
            "2. Choose behavior",
            "Select a complete preset. Updates are identity-guarded; every deletion remains approval-gated."));
        content.Controls.Add(CreateCard(_behavior, new Padding(14), new Padding(0, 0, 0, 14)));

        content.Controls.Add(CreateSectionHeader(
            "3. Review safety and scope",
            "Filters are applied before planning. Excluded content is never changed, deleted, or added to the baseline."));
        var advanced = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        advanced.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        advanced.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var policy = CreateFormTable();
        UiControlFactory.AddLabeledRow(policy, "Conflicts", _conflictPolicy, "Block and review is safest. Keep both always requires approval.");
        UiControlFactory.AddLabeledRow(policy, "Maximum deletes", _maximumDeletionCount, "Stop when this item limit is exceeded.");
        UiControlFactory.AddLabeledRow(policy, "Maximum baseline %", _maximumDeletionPercentage, "Stop when this percentage limit is exceeded.");
        UiControlFactory.AddLabeledRow(policy, "Transfer buffer", _bufferSize, "Bounded from 1 byte through 1 MiB.");
        UiControlFactory.AddLabeledRow(
            policy,
            "Non-atomic writes",
            _allowNonAtomicDestinationWrites,
            "WARNING: permits direct create/replace when a server cannot publish atomically. A concurrent destination change may be overwritten.");
        var scope = CreateFormTable();
        UiControlFactory.AddLabeledRow(scope, "Include globs", _includeGlobs, "Optional; one forward-slash glob per line.");
        UiControlFactory.AddLabeledRow(scope, "Exclude globs", _excludeGlobs, "StorageHub staging paths are excluded by default.");
        UiControlFactory.AddLabeledRow(scope, "Hidden content", _includeHiddenFiles, "Clear to exclude dot-prefixed path segments.");
        advanced.Controls.Add(CreateCard(policy, new Padding(10), new Padding(0, 0, 7, 0)), 0, 0);
        advanced.Controls.Add(CreateCard(scope, new Padding(10), new Padding(7, 0, 0, 0)), 1, 0);
        content.Controls.Add(advanced);

        panel.Controls.Add(content);
        page.Controls.Add(panel);
        return page;
    }

    private static TableLayoutPanel CreateSectionHeader(string title, string description)
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            Margin = new Padding(0, 5, 0, 7)
        };
        header.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 12F),
            ForeColor = StorageHubTheme.Text,
            Margin = Padding.Empty
        });
        header.Controls.Add(new Label
        {
            Text = description,
            AutoSize = true,
            MaximumSize = new Size(940, 0),
            ForeColor = StorageHubTheme.TextMuted,
            Margin = new Padding(0, 2, 0, 0)
        });
        return header;
    }

    private static Panel CreateCard(Control content, Padding padding, Padding margin)
    {
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = StorageHubTheme.Surface,
            BorderStyle = BorderStyle.FixedSingle,
            Padding = padding,
            Margin = margin
        };
        content.Dock = DockStyle.Top;
        card.Controls.Add(content);
        return card;
    }

    private static TableLayoutPanel CreateField(string label, Control control, string help)
    {
        var field = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            Margin = new Padding(0, 0, 12, 0)
        };
        field.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Font = StorageHubTheme.CreateSectionFont(),
            ForeColor = StorageHubTheme.Text,
            Margin = new Padding(0, 0, 0, 5)
        });
        control.Dock = DockStyle.Top;
        control.Margin = Padding.Empty;
        field.Controls.Add(control);
        field.Controls.Add(new Label
        {
            Text = help,
            AutoSize = true,
            ForeColor = StorageHubTheme.TextMuted,
            Margin = new Padding(0, 5, 0, 0)
        });
        return field;
    }

    private static Panel CreateLocationCard(
        string location,
        string badge,
        ComboBox connection,
        TextBox root,
        Button browse)
    {
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            Margin = Padding.Empty
        };
        var heading = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.Controls.Add(new Label
        {
            Text = $"Location {location}",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 13F),
            ForeColor = StorageHubTheme.Text,
            Margin = Padding.Empty
        }, 0, 0);
        heading.Controls.Add(new Label
        {
            Text = badge,
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 7.5F),
            ForeColor = StorageHubTheme.Primary,
            BackColor = StorageHubTheme.SurfaceMuted,
            Padding = new Padding(7, 3, 7, 3),
            Margin = Padding.Empty
        }, 1, 0);
        body.Controls.Add(heading);
        body.Controls.Add(new Label
        {
            Text = "Saved connection",
            AutoSize = true,
            ForeColor = StorageHubTheme.TextMuted,
            Margin = new Padding(0, 14, 0, 4)
        });
        connection.Dock = DockStyle.Top;
        connection.Margin = Padding.Empty;
        body.Controls.Add(connection);
        body.Controls.Add(new Label
        {
            Text = "Folder inside connection",
            AutoSize = true,
            ForeColor = StorageHubTheme.TextMuted,
            Margin = new Padding(0, 12, 0, 4)
        });
        body.Controls.Add(CreateFolderSelector(root, browse));
        body.Controls.Add(new Label
        {
            Text = "Empty means the saved connection root.",
            AutoSize = true,
            ForeColor = StorageHubTheme.TextMuted,
            Margin = new Padding(0, 5, 0, 0)
        });
        return CreateCard(body, new Padding(16), new Padding(0));
    }

    private static TableLayoutPanel CreateFormTable()
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Margin = Padding.Empty
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }

    private static void FormatConflictPolicy(object? sender, ListControlConvertEventArgs e)
    {
        e.Value = e.ListItem switch
        {
            SyncIpcConflictPolicy.Block => "Block and review",
            SyncIpcConflictPolicy.KeepBoth => "Keep both (manual approval)",
            _ => e.ListItem?.ToString() ?? string.Empty
        };
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
            _behavior.SelectedBehavior,
            SelectedEnum<SyncIpcConflictPolicy>(_conflictPolicy),
            ParseGlobs(_includeGlobs),
            ParseGlobs(_excludeGlobs),
            _includeHiddenFiles.Checked,
            decimal.ToInt32(_maximumDeletionCount.Value),
            _maximumDeletionPercentage.Value,
            decimal.ToInt32(_bufferSize.Value),
            _enabled.Checked)
        {
            AllowNonAtomicDestinationWrites = _allowNonAtomicDestinationWrites.Checked
        };
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
        _behavior.SelectedBehavior = profile.Draft.Behavior;
        _conflictPolicy.SelectedItem = profile.Draft.ConflictPolicy;
        _includeGlobs.Lines = profile.Draft.IncludeGlobs;
        _excludeGlobs.Lines = profile.Draft.ExcludeGlobs;
        _includeHiddenFiles.Checked = profile.Draft.IncludeHiddenFiles;
        _maximumDeletionCount.Value = profile.Draft.MaximumDeletionCount;
        _maximumDeletionPercentage.Value = profile.Draft.MaximumDeletionPercentage;
        _bufferSize.Value = profile.Draft.TransferBufferSize;
        _allowNonAtomicDestinationWrites.Checked = profile.Draft.AllowNonAtomicDestinationWrites;
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
        _behavior.SelectedBehavior = SyncIpcBehavior.UpdateAToB;
        _conflictPolicy.SelectedItem = SyncIpcConflictPolicy.Block;
        _includeGlobs.Clear();
        _excludeGlobs.Lines = [".storagehub", ".storagehub/**", "**/.storagehub/**"];
        _includeHiddenFiles.Checked = true;
        _maximumDeletionCount.Value = SyncPresentationCatalog.DefaultMassDeleteItemLimit;
        _maximumDeletionPercentage.Value = SyncPresentationCatalog.DefaultMassDeletePercentageLimit;
        _bufferSize.Value = 64 * 1024;
        _allowNonAtomicDestinationWrites.Checked = false;
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
            .Where(static connection => connection.Type == ConnectionProfileType.Storage)
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

    private void SwapLocations()
    {
        var connection = _leftConnection.SelectedItem;
        var root = _leftRoot.Text;
        _leftConnection.SelectedItem = _rightConnection.SelectedItem;
        _leftRoot.Text = _rightRoot.Text;
        _rightConnection.SelectedItem = connection;
        _rightRoot.Text = root;
    }

    private void LocationConnectionChanged(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _leftConnection))
        {
            _leftRoot.Clear();
        }
        else if (ReferenceEquals(sender, _rightConnection))
        {
            _rightRoot.Clear();
        }

        UpdateLocationSelectorState(_leftConnection, _leftRoot, _leftBrowse);
        UpdateLocationSelectorState(_rightConnection, _rightRoot, _rightBrowse);
    }

    private void BrowseLeftClicked(object? sender, EventArgs e) =>
        BrowseLocation(_leftConnection, _leftRoot, "Location A");

    private void BrowseRightClicked(object? sender, EventArgs e) =>
        BrowseLocation(_rightConnection, _rightRoot, "Location B");

    private void BrowseLocation(ComboBox connectionChoice, TextBox root, string locationName)
    {
        if (connectionChoice.SelectedItem is not ConnectionChoice choice)
        {
            _status.Text = $"Select a saved connection for {locationName} first.";
            _status.ForeColor = StorageHubTheme.Danger;
            return;
        }

        using var picker = new SyncLocationPickerForm(
            _storageClient,
            choice.Connection,
            root.Text.Trim(),
            locationName);
        if (picker.ShowDialog(this) == DialogResult.OK)
        {
            root.Text = picker.SelectedRelativePath;
            _status.Text = picker.SelectedRelativePath.Length == 0
                ? $"{locationName} uses the root of {choice.Connection.DisplayName}."
                : $"{locationName} folder selected: {picker.SelectedRelativePath}";
            _status.ForeColor = StorageHubTheme.Success;
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
            "Preparing sync plan…").ConfigureAwait(true);
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

    private static Button CreateBrowseButton(string name, string accessibleName)
    {
        var button = new Button
        {
            Name = name,
            Text = "Browse...",
            AutoSize = true,
            Enabled = false,
            AccessibleName = accessibleName,
            AccessibleDescription = "Browse folders inside the selected saved connection."
        };
        StorageHubTheme.StyleSecondaryButton(button);
        return button;
    }

    private static TableLayoutPanel CreateFolderSelector(TextBox root, Button browse)
    {
        var selector = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            Margin = Padding.Empty
        };
        selector.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        selector.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.Dock = DockStyle.Fill;
        root.Margin = new Padding(0, 0, 8, 0);
        browse.Margin = Padding.Empty;
        selector.Controls.Add(root, 0, 0);
        selector.Controls.Add(browse, 1, 0);
        return selector;
    }

    private static void UpdateLocationSelectorState(ComboBox connection, TextBox root, Button browse)
    {
        var choice = connection.SelectedItem as ConnectionChoice;
        var canBrowse = choice?.Connection.IsEnabled == true;
        root.Enabled = canBrowse;
        browse.Enabled = canBrowse;
        root.PlaceholderText = choice switch
        {
            null => "Select a connection first",
            { Connection.IsEnabled: false } => "This saved connection is disabled",
            { Connection.FolderPath.Length: > 0 } => $"Connection root: {choice.Connection.FolderPath}",
            _ => "Connection root (choose Browse for a subfolder)"
        };
    }

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

    private static TextBox CreateGlobTextBox(string accessibleName) => new()
    {
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        Height = 58,
        MaxLength = SyncManagementIpcLimits.MaximumFilterCount * SyncManagementIpcLimits.MaximumGlobLength,
        AccessibleName = accessibleName
    };

    private static string[] ParseGlobs(TextBox textBox) => textBox.Lines
        .Select(static line => line.Trim())
        .Where(static line => line.Length > 0)
        .ToArray();

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
