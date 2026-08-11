using System.Security.Cryptography;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

public sealed class SettingsForm : Form
{
    private readonly DesktopUpdatePreferencesStore _store;
    private readonly Action<DesktopUpdatePreferences>? _saved;
    private readonly IRemoteSecretVaultClient _secretClient;
    private readonly bool _ownsSecretClient;
    private readonly TreeView _categories;
    private readonly Dictionary<string, Control> _pages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Control> _connectionDefaultControls = new(StringComparer.Ordinal);
    private readonly CheckBox _checkAutomatically;
    private readonly CheckBox _downloadAutomatically;
    private readonly CheckBox _restartAutomatically;
    private readonly CheckBox _includePrereleases;
    private readonly ComboBox _sshDiscovery;
    private readonly Label _sshDiscoveryDescription;
    private readonly TextBox _externalEditor;
    private readonly NumericUpDown _maximumEditableKilobytes;
    private readonly CheckBox _warnBeforeUnsafeExternalEdit;
    private readonly CheckBox _adaptiveConcurrency;
    private readonly NumericUpDown _minimumConcurrency;
    private readonly NumericUpDown _maximumTransferConcurrency;
    private readonly NumericUpDown _perConnectionConcurrency;
    private readonly NumericUpDown _maximumSyncConcurrency;
    private readonly ComboBox _appearance;
    private readonly ComboBox _defaultWorkspaceLayout;
    private readonly Button _apply;
    private DesktopAppearance _appliedAppearance;

    public SettingsForm()
        : this(DesktopUpdatePreferencesStore.CreateDefault(), saved: null, secretClient: null)
    {
    }

    internal SettingsForm(
        DesktopUpdatePreferencesStore store,
        Action<DesktopUpdatePreferences>? saved,
        IRemoteSecretVaultClient? secretClient = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _saved = saved;
        _ownsSecretClient = secretClient is null;
        _secretClient = secretClient ?? new NamedPipeRemoteSecretVaultClient();

        Text = "Settings — StorageHub";
        AccessibleName = "StorageHub Settings";
        AccessibleDescription = "Configure StorageHub behavior, connection trust, and updates.";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(840, 600);
        Size = new Size(980, 700);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = StorageHubTheme.Canvas;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        StorageHubTheme.Register(this);

        var preferences = _store.Load();
        _appliedAppearance = preferences.Appearance;
        _checkAutomatically = CreateOption(
            "Check GitHub for updates when StorageHub starts",
            "Uses the fixed official StorageHub repository. Manual checks remain available when disabled.",
            preferences.CheckAutomatically);
        _downloadAutomatically = CreateOption(
            "Download available updates automatically",
            "Downloads the matching integrity-checked Velopack package silently after an automatic check.",
            preferences.DownloadAutomatically);
        _restartAutomatically = CreateOption(
            "Install silently and restart automatically",
            "Closes StorageHub after download, applies the update, and reopens it. Disabled by default to avoid interrupting work.",
            preferences.RestartAutomatically);
        _includePrereleases = CreateOption(
            "Include engineering preview releases",
            "Keep enabled while using StorageHub preview builds. Disable it later to receive stable releases only.",
            preferences.IncludePrereleases);
        _externalEditor = new TextBox
        {
            Text = preferences.ExternalEditorPath ?? string.Empty,
            Width = 520,
            PlaceholderText = "Choose an editor executable, or leave blank for the Windows default",
            AccessibleName = "External editor executable"
        };
        _maximumEditableKilobytes = new NumericUpDown
        {
            Minimum = 1,
            Maximum = EditableFileIpcContract.MaximumContentBytes / 1024,
            Value = Math.Clamp(preferences.MaximumEditableFileBytes / 1024, 1, EditableFileIpcContract.MaximumContentBytes / 1024),
            Width = 120,
            ThousandsSeparator = true,
            AccessibleName = "Maximum externally editable file size in KiB"
        };
        _warnBeforeUnsafeExternalEdit = CreateOption(
            "Warn before editing without remote change protection",
            "When a provider cannot enforce version or ETag checks, ask before continuing with an edit that could overwrite newer remote changes.",
            preferences.WarnBeforeUnsafeExternalEdit);
        _adaptiveConcurrency = CreateOption(
            "Automatically tune concurrency from observed speed",
            "Starts at the minimum, increases after sustained healthy throughput, and backs off on slowdown or provider errors.",
            preferences.AdaptiveConcurrency);
        _minimumConcurrency = CreateConcurrencyInput(1, 8, preferences.MinimumConcurrency, "Starting concurrency");
        _maximumTransferConcurrency = CreateConcurrencyInput(1, 32, preferences.MaximumTransferConcurrency, "Maximum concurrent transfers");
        _perConnectionConcurrency = CreateConcurrencyInput(1, 16, preferences.PerConnectionConcurrency, "Maximum transfers per connection");
        _maximumSyncConcurrency = CreateConcurrencyInput(1, 8, preferences.MaximumSyncConcurrency, "Maximum concurrent synchronizations");
        _appearance = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 260,
            AccessibleName = "Application appearance"
        };
        _appearance.Items.AddRange([DesktopAppearance.Light, DesktopAppearance.Dark, DesktopAppearance.System]);
        _appearance.SelectedItem = preferences.Appearance;
        _defaultWorkspaceLayout = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 260,
            AccessibleName = "Default workspace layout"
        };
        _defaultWorkspaceLayout.Items.AddRange([WorkspaceLayout.SideBySide, WorkspaceLayout.TopAndBottom]);
        _defaultWorkspaceLayout.Format += (_, args) => args.Value = args.ListItem switch
        {
            WorkspaceLayout.TopAndBottom => "Top and bottom",
            _ => "Side by side"
        };
        _defaultWorkspaceLayout.SelectedItem = preferences.DefaultWorkspaceLayout;

        _sshDiscovery = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 310,
            AccessibleName = "SSH host-key discovery"
        };
        _sshDiscovery.Items.AddRange(new object[]
        {
            new DiscoveryChoice(
                SshHostKeyDiscoveryMode.Manual,
                "Manual — use Fetch from host",
                "StorageHub fetches a host key only when you press the button in Connection Manager."),
            new DiscoveryChoice(
                SshHostKeyDiscoveryMode.AskBeforeFetching,
                "Ask before fetching",
                "When an SFTP endpoint is ready and has no fingerprint, StorageHub asks before contacting it."),
            new DiscoveryChoice(
                SshHostKeyDiscoveryMode.Automatic,
                "Fetch automatically",
                "When an SFTP endpoint is ready and has no fingerprint, StorageHub retrieves and displays its key automatically.")
        });
        _sshDiscovery.SelectedItem = _sshDiscovery.Items
            .Cast<DiscoveryChoice>()
            .Single(choice => choice.Mode == preferences.SshHostKeyDiscovery);
        _sshDiscoveryDescription = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(610, 0),
            ForeColor = StorageHubTheme.TextMuted,
            Padding = new Padding(0, 8, 0, 0),
            AccessibleName = "SSH host-key discovery description"
        };
        UpdateDiscoveryDescription();

        _categories = new TreeView
        {
            Dock = DockStyle.Fill,
            ItemHeight = 30,
            FullRowSelect = true,
            HideSelection = false,
            ShowLines = true,
            ShowPlusMinus = true,
            ShowRootLines = false,
            Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point),
            BackColor = StorageHubTheme.SurfaceMuted,
            AccessibleName = "Settings categories"
        };
        var work = new TreeNode("Transfers & sync") { Name = "Performance" };
        work.Nodes.Add(new TreeNode("Concurrency") { Name = "Performance" });
        _categories.Nodes.Add(work);
        _categories.Nodes.Add(new TreeNode("Editing") { Name = "Editing" });
        _categories.Nodes.Add(new TreeNode("Appearance") { Name = "Appearance" });
        _categories.Nodes.Add(new TreeNode("Workspace") { Name = "Workspace" });
        var connections = new TreeNode("Connections") { Name = "Connections & trust" };
        connections.Nodes.Add(new TreeNode("Overview & trust") { Name = "Connections & trust" });
        foreach (var type in new[] { ConnectionProfileType.Storage, ConnectionProfileType.Client })
        {
            var typeName = type == ConnectionProfileType.Storage ? "Storage" : "Clients";
            var typeNode = new TreeNode(typeName) { Name = ConnectionTypePageKey(type) };
            foreach (var provider in ConnectionProviderCatalog.All.Where(provider => provider.Type == type))
            {
                typeNode.Nodes.Add(new TreeNode(provider.DisplayName) { Name = ProviderPageKey(provider.Kind) });
            }
            typeNode.Expand();
            connections.Nodes.Add(typeNode);
        }
        connections.Expand();
        _categories.Nodes.Add(connections);
        _categories.Nodes.Add(new TreeNode("Updates") { Name = "Updates" });
        work.Expand();

        var pageHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = StorageHubTheme.Surface,
            Padding = new Padding(28, 24, 28, 18)
        };
        AddPage(pageHost, "Performance", BuildPerformancePage());
        AddPage(pageHost, "Editing", BuildEditingPage());
        AddPage(pageHost, "Appearance", BuildAppearancePage());
        AddPage(pageHost, "Workspace", BuildWorkspacePage());
        AddPage(pageHost, "Connections & trust", BuildConnectionsPage());
        foreach (var type in new[] { ConnectionProfileType.Storage, ConnectionProfileType.Client })
        {
            AddPage(pageHost, ConnectionTypePageKey(type), BuildConnectionTypePage(type));
        }
        foreach (var provider in ConnectionProviderCatalog.All)
        {
            AddPage(pageHost, ProviderPageKey(provider.Kind), BuildProviderSettingsPage(provider, preferences));
        }
        AddPage(pageHost, "Updates", BuildUpdatesPage());

        var navigation = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = StorageHubTheme.SurfaceMuted,
            Padding = new Padding(12, 18, 8, 12)
        };
        var navigationTitle = UiControlFactory.CreateSectionTitle("Settings");
        navigationTitle.Dock = DockStyle.Top;
        navigationTitle.Height = 42;
        navigation.Controls.Add(_categories);
        navigation.Controls.Add(navigationTitle);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Size = new Size(900, 600),
            FixedPanel = FixedPanel.Panel1,
            SplitterDistance = 220,
            Panel1MinSize = 190,
            Panel2MinSize = 500,
            IsSplitterFixed = true,
            BackColor = StorageHubTheme.Border
        };
        split.Panel1.Controls.Add(navigation);
        split.Panel2.Controls.Add(pageHost);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(12, 9, 12, 8),
            BackColor = StorageHubTheme.Surface
        };
        var ok = new Button { Text = "OK" };
        StorageHubTheme.StylePrimaryButton(ok);
        ok.Click += SaveAndClose;
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        StorageHubTheme.StyleSecondaryButton(cancel);
        _apply = new Button { Text = "Apply", Enabled = false };
        StorageHubTheme.StyleSecondaryButton(_apply);
        _apply.Click += SaveWithoutClosing;
        footer.Controls.Add(ok);
        footer.Controls.Add(cancel);
        footer.Controls.Add(_apply);

        Controls.Add(split);
        Controls.Add(footer);
        AcceptButton = ok;
        CancelButton = cancel;

        _categories.AfterSelect += CategorySelected;
        _checkAutomatically.CheckedChanged += UpdateDependencies;
        _downloadAutomatically.CheckedChanged += UpdateDependencies;
        _sshDiscovery.SelectedIndexChanged += DiscoverySelectionChanged;
        _externalEditor.TextChanged += MarkDirty;
        _maximumEditableKilobytes.ValueChanged += MarkDirty;
        _warnBeforeUnsafeExternalEdit.CheckedChanged += MarkDirty;
        _adaptiveConcurrency.CheckedChanged += ConcurrencyChanged;
        _minimumConcurrency.ValueChanged += ConcurrencyChanged;
        _maximumTransferConcurrency.ValueChanged += ConcurrencyChanged;
        _perConnectionConcurrency.ValueChanged += ConcurrencyChanged;
        _maximumSyncConcurrency.ValueChanged += ConcurrencyChanged;
        _appearance.SelectedIndexChanged += AppearanceSelectionChanged;
        _defaultWorkspaceLayout.SelectedIndexChanged += MarkDirty;
        foreach (var option in UpdateOptions())
        {
            option.CheckedChanged += MarkDirty;
        }

        _categories.SelectedNode = work.Nodes[0];
        UpdateDependencies(this, EventArgs.Empty);
        StorageHubTheme.Apply(this);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (DialogResult != DialogResult.OK)
        {
            DesktopAppearanceService.SetAppearance(_appliedAppearance);
        }

        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _categories.AfterSelect -= CategorySelected;
            _checkAutomatically.CheckedChanged -= UpdateDependencies;
            _downloadAutomatically.CheckedChanged -= UpdateDependencies;
            _sshDiscovery.SelectedIndexChanged -= DiscoverySelectionChanged;
            _externalEditor.TextChanged -= MarkDirty;
            _maximumEditableKilobytes.ValueChanged -= MarkDirty;
            _warnBeforeUnsafeExternalEdit.CheckedChanged -= MarkDirty;
            _adaptiveConcurrency.CheckedChanged -= ConcurrencyChanged;
            _minimumConcurrency.ValueChanged -= ConcurrencyChanged;
            _maximumTransferConcurrency.ValueChanged -= ConcurrencyChanged;
            _perConnectionConcurrency.ValueChanged -= ConcurrencyChanged;
            _maximumSyncConcurrency.ValueChanged -= ConcurrencyChanged;
            _appearance.SelectedIndexChanged -= AppearanceSelectionChanged;
            _defaultWorkspaceLayout.SelectedIndexChanged -= MarkDirty;
            foreach (var option in UpdateOptions())
            {
                option.CheckedChanged -= MarkDirty;
            }

            if (_ownsSecretClient)
            {
                _secretClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            _categories.Font.Dispose();
        }

        base.Dispose(disposing);
    }

    private FlowLayoutPanel BuildPerformancePage()
    {
        var page = CreatePage(
            "Concurrency",
            "One provider-neutral policy controls Local, S3, FTP, FTPS, and SFTP transfers plus background synchronization work.");
        page.Controls.Add(_adaptiveConcurrency);
        var table = new TableLayoutPanel
        {
            AutoSize = true,
            Width = 650,
            ColumnCount = 2,
            Padding = new Padding(0, 12, 0, 0)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        AddConcurrencyRow(table, "Start with", _minimumConcurrency,
            "The adaptive controller begins conservatively at this many jobs.");
        AddConcurrencyRow(table, "Maximum transfers", _maximumTransferConcurrency,
            "Global ceiling shared by transfers across every supported provider.");
        AddConcurrencyRow(table, "Per saved connection", _perConnectionConcurrency,
            "Prevents one server, bucket, or local connection from consuming every worker.");
        AddConcurrencyRow(table, "Maximum synchronizations", _maximumSyncConcurrency,
            "Separate ceiling for scheduled and manually approved synchronization runs.");
        page.Controls.Add(table);
        page.Controls.Add(CreateInformationCard(
            "How automatic tuning works",
            "StorageHub measures completed work, raises concurrency only after sustained throughput, and lowers it immediately after significant slowdown or a provider failure. Changes apply when the background Agent safely restarts."));
        return page;
    }

    private FlowLayoutPanel BuildConnectionsPage()
    {
        var page = CreatePage(
            "Connections & trust",
            "Choose when StorageHub may contact a new SFTP endpoint to retrieve its presented SSH host key.");
        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            Width = 650,
            MinimumSize = new Size(650, 0),
            ColumnCount = 1,
            Padding = new Padding(0, 14, 0, 0)
        };
        layout.Controls.Add(new Label
        {
            Text = "SSH host-key discovery",
            AutoSize = true,
            Font = StorageHubTheme.CreateSectionFont(),
            ForeColor = StorageHubTheme.Text
        });
        layout.Controls.Add(_sshDiscovery);
        layout.Controls.Add(_sshDiscoveryDescription);
        layout.Controls.Add(CreateSecurityNotice());
        page.Controls.Add(layout);
        return page;
    }

    private FlowLayoutPanel BuildEditingPage()
    {
        var page = CreatePage(
            "External editing",
            "Download a bounded remote file into StorageHub's private temporary workspace, open it in your editor, and ask before uploading detected changes.");
        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            Width = 650,
            ColumnCount = 2,
            Padding = new Padding(0, 12, 0, 0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            Text = "Editor executable",
            AutoSize = true,
            Font = StorageHubTheme.CreateSectionFont(),
            ForeColor = StorageHubTheme.Text
        }, 0, 0);
        layout.SetColumnSpan(layout.Controls[^1], 2);
        layout.Controls.Add(_externalEditor, 0, 1);
        var browse = new Button { Text = "Browse...", AutoSize = true };
        StorageHubTheme.StyleSecondaryButton(browse);
        browse.Click += BrowseEditorClicked;
        layout.Controls.Add(browse, 1, 1);
        layout.Controls.Add(new Label
        {
            Text = "Leave blank to use the Windows file association. StorageHub passes only the temporary file path to the editor.",
            AutoSize = true,
            MaximumSize = new Size(620, 0),
            ForeColor = StorageHubTheme.TextMuted,
            Padding = new Padding(0, 4, 0, 12)
        }, 0, 2);
        layout.SetColumnSpan(layout.Controls[^1], 2);
        layout.Controls.Add(new Label
        {
            Text = "Maximum editable file size (KiB)",
            AutoSize = true,
            Font = StorageHubTheme.CreateSectionFont(),
            ForeColor = StorageHubTheme.Text
        }, 0, 3);
        layout.SetColumnSpan(layout.Controls[^1], 2);
        layout.Controls.Add(_maximumEditableKilobytes, 0, 4);
        layout.Controls.Add(new Label
        {
            Text = "Hard limit: 1,024 KiB (1 MiB). Larger files are rejected before download and before re-upload.",
            AutoSize = true,
            MaximumSize = new Size(620, 0),
            ForeColor = StorageHubTheme.Warning,
            Padding = new Padding(0, 5, 0, 0)
        }, 0, 5);
        layout.SetColumnSpan(layout.Controls[^1], 2);
        _warnBeforeUnsafeExternalEdit.Padding = new Padding(0, 12, 0, 0);
        layout.Controls.Add(_warnBeforeUnsafeExternalEdit, 0, 6);
        layout.SetColumnSpan(_warnBeforeUnsafeExternalEdit, 2);
        page.Controls.Add(layout);
        return page;
    }

    private void BrowseEditorClicked(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose external editor",
            Filter = "Applications (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _externalEditor.Text = dialog.FileName;
        }
    }

    private FlowLayoutPanel BuildUpdatesPage()
    {
        var page = CreatePage(
            "Updates",
            "StorageHub checks the fixed official GitHub release feed and uses the same package lifecycle as the verified installer pipeline.");
        var options = new FlowLayoutPanel
        {
            AutoSize = true,
            Width = 650,
            MinimumSize = new Size(650, 0),
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(0, 10, 0, 0)
        };
        foreach (var option in UpdateOptions())
        {
            options.Controls.Add(option);
        }

        options.Controls.Add(new Label
        {
            AutoSize = true,
            Text = $"Update source: {VelopackDesktopUpdateEngineFactory.TrustedRepositoryUrl}\nInstalled version: {DesktopApplicationVersion.Current}",
            ForeColor = StorageHubTheme.TextMuted,
            Padding = new Padding(0, 12, 0, 0),
            AccessibleName = "Update source and installed version"
        });
        page.Controls.Add(options);
        return page;
    }

    private static FlowLayoutPanel CreatePage(string title, string description)
    {
        var page = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = StorageHubTheme.Surface,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false
        };
        var heading = UiControlFactory.CreateSectionTitle(title);
        heading.Width = 650;
        heading.MinimumSize = new Size(650, 0);
        heading.Height = 32;
        var summary = UiControlFactory.CreateDescription(description);
        summary.Width = 650;
        summary.MinimumSize = new Size(650, 0);
        summary.Padding = new Padding(0, 0, 0, 12);
        page.Controls.Add(heading);
        page.Controls.Add(summary);
        return page;
    }

    private static TableLayoutPanel CreateInformationCard(string title, string text)
    {
        var card = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Width = 650,
            MinimumSize = new Size(650, 0),
            ColumnCount = 1,
            RowCount = 2,
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize,
            BackColor = StorageHubTheme.SurfaceMuted,
            Padding = new Padding(14),
            Margin = new Padding(0, 10, 0, 0),
            Name = "InformationCard",
            AccessibleName = $"{title} settings card"
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var heading = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Text = title,
            Font = StorageHubTheme.CreateSectionFont(),
            ForeColor = StorageHubTheme.Text
        };
        var description = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            MaximumSize = new Size(620, 0),
            Text = text,
            ForeColor = StorageHubTheme.TextMuted,
            Padding = new Padding(0, 6, 0, 0)
        };
        card.Controls.Add(heading, 0, 0);
        card.Controls.Add(description, 0, 1);
        return card;
    }

    private static Panel CreateSecurityNotice()
    {
        var notice = new Panel
        {
            AutoSize = true,
            BackColor = StorageHubTheme.SurfaceMuted,
            Width = 650,
            MinimumSize = new Size(650, 0),
            Padding = new Padding(12),
            Margin = new Padding(0, 18, 0, 0)
        };
        notice.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(600, 0),
            Text = "Fetching discovers what the contacted host presents; it does not prove that the host is genuine. Automatic discovery never records trust or bypasses fingerprint verification.",
            ForeColor = StorageHubTheme.Warning
        });
        return notice;
    }

    private void AddPage(Control host, string name, Control page)
    {
        page.Visible = false;
        _pages.Add(name, page);
        host.Controls.Add(page);
    }

    private IEnumerable<CheckBox> UpdateOptions()
    {
        yield return _checkAutomatically;
        yield return _downloadAutomatically;
        yield return _restartAutomatically;
        yield return _includePrereleases;
    }

    private static CheckBox CreateOption(string text, string description, bool isChecked) =>
        new()
        {
            Text = text,
            Checked = isChecked,
            AutoSize = true,
            MaximumSize = new Size(650, 0),
            Margin = new Padding(0, 8, 0, 8),
            AccessibleName = text,
            AccessibleDescription = description
        };

    private static NumericUpDown CreateConcurrencyInput(int minimum, int maximum, int value, string accessibleName) =>
        new()
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = Math.Clamp(value, minimum, maximum),
            Width = 110,
            AccessibleName = accessibleName
        };

    private static void AddConcurrencyRow(
        TableLayoutPanel table,
        string label,
        Control input,
        string description)
    {
        var row = table.RowCount++;
        var text = new Label
        {
            Text = label,
            AutoSize = true,
            Font = StorageHubTheme.CreateSectionFont(),
            ForeColor = StorageHubTheme.Text,
            Padding = new Padding(0, 7, 8, 0)
        };
        table.Controls.Add(text, 0, row);
        table.Controls.Add(input, 1, row);
        var help = new Label
        {
            Text = description,
            AutoSize = true,
            MaximumSize = new Size(610, 0),
            ForeColor = StorageHubTheme.TextMuted,
            Padding = new Padding(0, 2, 0, 10)
        };
        table.Controls.Add(help, 0, row + 1);
        table.SetColumnSpan(help, 2);
        table.RowCount++;
    }

    private FlowLayoutPanel BuildAppearancePage()
    {
        var page = CreatePage("Appearance", "Choose the color theme used throughout StorageHub.");
        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            Width = 650,
            ColumnCount = 1,
            Padding = new Padding(0, 12, 0, 0)
        };
        layout.Controls.Add(new Label
        {
            Text = "Theme",
            AutoSize = true,
            Font = StorageHubTheme.CreateSectionFont(),
            ForeColor = StorageHubTheme.Text
        });
        layout.Controls.Add(_appearance);
        layout.Controls.Add(UiControlFactory.CreateDescription(
            "System follows the Windows app theme. Choices preview immediately; Apply or OK remembers the choice, while Cancel restores the last applied theme."));
        page.Controls.Add(layout);
        return page;
    }

    private FlowLayoutPanel BuildWorkspacePage()
    {
        var page = CreatePage(
            "Workspace",
            "Choose how the two panes are arranged when a new workspace is created. Every workspace can still switch layouts from its own toolbar.");
        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            Width = 650,
            ColumnCount = 1,
            Padding = new Padding(0, 12, 0, 0)
        };
        layout.Controls.Add(new Label
        {
            Text = "Default pane layout",
            AutoSize = true,
            Font = StorageHubTheme.CreateSectionFont(),
            ForeColor = StorageHubTheme.Text
        });
        layout.Controls.Add(_defaultWorkspaceLayout);
        layout.Controls.Add(UiControlFactory.CreateDescription(
            "Side by side is the classic left/right file-manager layout. Top and bottom works especially well with an SSH terminal above a storage browser."));
        page.Controls.Add(layout);
        return page;
    }

    private static FlowLayoutPanel BuildConnectionTypePage(ConnectionProfileType type)
    {
        var isStorage = type == ConnectionProfileType.Storage;
        var page = CreatePage(
            isStorage ? "Storage connections" : "Client connections",
            isStorage
                ? "Browsable endpoints used by file operations, transfers, synchronization, and schedules."
                : "Interactive remote clients. Client profiles share Connection Manager labels, folders, vault references, and trust records without appearing in storage pickers.");
        var providers = ConnectionProviderCatalog.All.Where(provider => provider.Type == type).ToArray();
        foreach (var provider in providers)
        {
            page.Controls.Add(CreateInformationCard(
                provider.DisplayName,
                $"{provider.Summary} Default port: {provider.DefaultPort?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "Not applicable"}."));
        }
        return page;
    }

    private FlowLayoutPanel BuildProviderSettingsPage(
        ConnectionProviderDescriptor provider,
        DesktopUpdatePreferences preferences)
    {
        var page = CreatePage(
            $"{provider.DisplayName} defaults",
            $"Defaults applied when you create a new {provider.DisplayName} profile. Existing saved profiles are never changed.");
        var defaults = ConnectionDefaultSettings.Get(provider.Kind, preferences.ConnectionDefaults);
        var editor = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Width = 650,
            MinimumSize = new Size(650, 0),
            ColumnCount = 2,
            Padding = new Padding(0, 8, 0, 6),
            Name = $"ProviderSettings:{provider.Kind}",
            AccessibleName = $"{provider.DisplayName} new-connection defaults"
        };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddProviderDefaultRow(
            editor,
            provider.Kind,
            ConnectionDefaultSettings.ConnectTimeoutKey,
            "Connection timeout (seconds)",
            CreateProviderNumberDefault(defaults.ConnectTimeoutSeconds, 1, 600));
        AddProviderDefaultRow(
            editor,
            provider.Kind,
            ConnectionDefaultSettings.OperationTimeoutKey,
            "Operation timeout (seconds)",
            CreateProviderNumberDefault(defaults.OperationTimeoutSeconds, 1, 86_400));
        AddProviderDefaultRow(
            editor,
            provider.Kind,
            ConnectionDefaultSettings.RetryAttemptsKey,
            "Retry attempts",
            CreateProviderNumberDefault(defaults.MaximumRetryAttempts, 0, 20));
        foreach (var field in ConnectionDefaultSettings.EditableFields(provider))
        {
            AddProviderDefaultRow(
                editor,
                provider.Kind,
                field.Key,
                $"Default {field.Label.ToLowerInvariant()}",
                CreateProviderFieldDefault(field, defaults.FieldValues[field.Key]));
        }

        page.Controls.Add(editor);
        page.Controls.Add(CreateInformationCard(
            "Profile-specific values stay in Connection Manager",
            "Names, folders, labels, hosts, buckets, usernames, passwords, passphrases, access keys, certificate pins, and SSH fingerprints stay per connection. An imported SSH private key is the one reusable credential exception: Settings stores only its encrypted-vault reference."));

        var open = new Button
        {
            Text = $"Create a {provider.DisplayName} connection...",
            AutoSize = true,
            Margin = new Padding(0, 16, 0, 8),
            AccessibleName = $"Configure {provider.DisplayName} connection"
        };
        StorageHubTheme.StylePrimaryButton(open);
        open.Click += (_, _) =>
        {
            using var manager = new ConnectionManagerForm(initialProvider: provider.Kind);
            _ = manager.ShowDialog(this);
        };
        page.Controls.Add(open);
        return page;
    }

    private NumericUpDown CreateProviderNumberDefault(int value, int minimum, int maximum)
    {
        var control = new NumericUpDown
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = Math.Clamp(value, minimum, maximum),
            Width = 150,
            ThousandsSeparator = true
        };
        control.ValueChanged += MarkDirty;
        return control;
    }

    private Control CreateProviderFieldDefault(ConnectionFieldDescriptor field, string value)
    {
        if (field.Kind == ConnectionFieldKind.SecretReference &&
            string.Equals(field.Key, "privateKeyReference", StringComparison.Ordinal))
        {
            return CreateDefaultSshPrivateKeyPicker(value);
        }

        if (field.Kind == ConnectionFieldKind.Number)
        {
            _ = int.TryParse(value, out var number);
            return CreateProviderNumberDefault(number, 1, 65_535);
        }

        if (field.Kind == ConnectionFieldKind.Choice)
        {
            var choice = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 360
            };
            choice.Items.AddRange((field.Choices ?? []).Cast<object>().ToArray());
            choice.SelectedItem = choice.Items.Cast<object>()
                .FirstOrDefault(item => string.Equals(item.ToString(), value, StringComparison.Ordinal));
            if (choice.SelectedIndex < 0 && choice.Items.Count > 0)
            {
                choice.SelectedIndex = 0;
            }
            choice.SelectedIndexChanged += MarkDirty;
            return choice;
        }

        var text = new TextBox
        {
            Text = value,
            Width = 390,
            MaxLength = 2_048
        };
        text.TextChanged += MarkDirty;
        return text;
    }

    private TableLayoutPanel CreateDefaultSshPrivateKeyPicker(string reference)
    {
        var panel = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 3,
            Dock = DockStyle.Top,
            Margin = Padding.Empty
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var value = new TextBox
        {
            ReadOnly = true,
            Text = reference,
            PlaceholderText = "No default SSH private key",
            Dock = DockStyle.Fill,
            AccessibleDescription = "Opaque encrypted-vault reference; the key file path and contents are not saved in settings."
        };
        value.TextChanged += MarkDirty;
        var import = new Button { Text = "Import key…", AutoSize = true };
        var clear = new Button { Text = "Clear default", AutoSize = true };
        StorageHubTheme.StyleSecondaryButton(import);
        StorageHubTheme.StyleSecondaryButton(clear);
        import.Margin = new Padding(6, 0, 0, 0);
        clear.Margin = new Padding(6, 0, 0, 0);
        import.Click += async (_, _) => await ImportDefaultSshPrivateKeyAsync(value);
        clear.Click += (_, _) => value.Clear();
        panel.Controls.Add(value, 0, 0);
        panel.Controls.Add(import, 1, 0);
        panel.Controls.Add(clear, 2, 0);
        return panel;
    }

    private async Task ImportDefaultSshPrivateKeyAsync(TextBox referenceBox)
    {
        using var picker = new OpenFileDialog
        {
            Title = "Select an encrypted OpenSSH or PEM private key",
            Filter = "SSH private keys (*.key;*.pem)|*.key;*.pem|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        byte[]? material = null;
        try
        {
            var file = new FileInfo(picker.FileName);
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0 ||
                file.Length is <= 0 or > SecretVaultIpcContract.MaximumSecretBytes)
            {
                throw new IOException("The selected key is unavailable, redirected, empty, or too large.");
            }

            material = await File.ReadAllBytesAsync(picker.FileName);
            var response = await _secretClient.EnrollAsync(
                SecretMaterialPurpose.SshPrivateKey,
                material);
            if (!response.Succeeded || string.IsNullOrWhiteSpace(response.Reference))
            {
                _ = MessageBox.Show(
                    this,
                    response.Failure?.Message ?? "The SSH private key could not be imported into the vault.",
                    "Default SSH private key",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            referenceBox.Text = response.Reference;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
            InvalidOperationException or TimeoutException or System.Text.Json.JsonException)
        {
            _ = MessageBox.Show(
                this,
                "StorageHub could not import that private key into the encrypted vault.",
                "Default SSH private key",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            if (material is not null)
            {
                CryptographicOperations.ZeroMemory(material);
            }
        }
    }

    private void AddProviderDefaultRow(
        TableLayoutPanel table,
        StorageProviderKind provider,
        string setting,
        string label,
        Control control)
    {
        var key = ConnectionDefaultSettings.Key(provider, setting);
        control.AccessibleName = label;
        _connectionDefaultControls.Add(key, control);
        var row = table.RowCount++;
        table.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            ForeColor = StorageHubTheme.Text,
            Padding = new Padding(0, 7, 10, 4)
        }, 0, row);
        table.Controls.Add(control, 1, row);
    }

    private static void AddProviderFact(TableLayoutPanel table, string label, string value)
    {
        var row = table.RowCount++;
        table.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Font = StorageHubTheme.CreateSectionFont(),
            ForeColor = StorageHubTheme.Text,
            Padding = new Padding(0, 4, 8, 4)
        }, 0, row);
        table.Controls.Add(new Label
        {
            Text = value,
            AutoSize = true,
            MaximumSize = new Size(470, 0),
            ForeColor = StorageHubTheme.TextMuted,
            Padding = new Padding(0, 4, 0, 4)
        }, 1, row);
    }

    private static void AddProviderFieldGroup(
        FlowLayoutPanel page,
        string title,
        IReadOnlyList<ConnectionFieldDescriptor> fields)
    {
        var details = fields.Count == 0
            ? "No settings are required."
            : string.Join(Environment.NewLine, fields.Select(field =>
                $"• {field.Label}{(field.Required ? " (required)" : string.Empty)}" +
                (string.IsNullOrWhiteSpace(field.DefaultValue) ? string.Empty : $" — default: {field.DefaultValue}") +
                (string.IsNullOrWhiteSpace(field.HelpText) ? string.Empty : $" — {field.HelpText}")));
        page.Controls.Add(CreateInformationCard(title, details));
    }

    private static string ConnectionTypePageKey(ConnectionProfileType type) => $"ConnectionType:{type}";

    private static string ProviderPageKey(StorageProviderKind provider) => $"Provider:{provider}";

    private void CategorySelected(object? sender, EventArgs e)
    {
        var selected = _categories.SelectedNode?.Name;
        foreach (var page in _pages)
        {
            page.Value.Visible = string.Equals(page.Key, selected, StringComparison.Ordinal);
            if (page.Value.Visible)
            {
                page.Value.BringToFront();
            }
        }
    }

    private void UpdateDependencies(object? sender, EventArgs e)
    {
        _downloadAutomatically.Enabled = _checkAutomatically.Checked;
        _restartAutomatically.Enabled = _checkAutomatically.Checked && _downloadAutomatically.Checked;
        _minimumConcurrency.Enabled = _adaptiveConcurrency.Checked;
    }

    private void ConcurrencyChanged(object? sender, EventArgs e)
    {
        var minimum = (int)_minimumConcurrency.Value;
        if (_maximumTransferConcurrency.Value < minimum)
        {
            _maximumTransferConcurrency.Value = minimum;
        }

        if (_maximumSyncConcurrency.Value < minimum)
        {
            _maximumSyncConcurrency.Value = minimum;
        }

        UpdateDependencies(sender, e);
        MarkDirty(sender, e);
    }

    private void DiscoverySelectionChanged(object? sender, EventArgs e)
    {
        UpdateDiscoveryDescription();
        MarkDirty(sender, e);
    }

    private void AppearanceSelectionChanged(object? sender, EventArgs e)
    {
        if (_appearance.SelectedItem is DesktopAppearance appearance)
        {
            var previous = DesktopAppearanceService.EffectiveAppearance;
            DesktopAppearanceService.SetAppearance(appearance);
            StorageHubTheme.Apply(this, previous);
        }

        MarkDirty(sender, e);
    }

    private void UpdateDiscoveryDescription()
    {
        _sshDiscoveryDescription.Text = _sshDiscovery.SelectedItem is DiscoveryChoice choice
            ? choice.Description
            : string.Empty;
    }

    private void MarkDirty(object? sender, EventArgs e) => _apply.Enabled = true;

    private void SaveAndClose(object? sender, EventArgs e)
    {
        if (TrySave())
        {
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    private void SaveWithoutClosing(object? sender, EventArgs e) => _ = TrySave();

    private Dictionary<string, string> ReadConnectionDefaults()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in _connectionDefaultControls)
        {
            values[pair.Key] = pair.Value switch
            {
                NumericUpDown number => decimal.ToInt32(number.Value)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture),
                ComboBox choice => choice.SelectedItem?.ToString() ?? string.Empty,
                TextBox text => text.Text.Trim(),
                TableLayoutPanel panel => panel.Controls.OfType<TextBox>().Single().Text.Trim(),
                _ => throw new InvalidOperationException("Unknown connection-default control.")
            };
        }

        return ConnectionDefaultSettings.Normalize(values);
    }

    private bool TrySave()
    {
        try
        {
            var discovery = (_sshDiscovery.SelectedItem as DiscoveryChoice)?.Mode
                ?? SshHostKeyDiscoveryMode.AskBeforeFetching;
            var editorPath = string.IsNullOrWhiteSpace(_externalEditor.Text)
                ? null
                : Path.GetFullPath(_externalEditor.Text.Trim());
            if (editorPath is not null && (!File.Exists(editorPath) ||
                (File.GetAttributes(editorPath) & FileAttributes.ReparsePoint) != 0))
            {
                _ = MessageBox.Show(
                    this,
                    "Choose an existing editor executable that is not a symbolic link or reparse point.",
                    "External editor",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return false;
            }

            var preferences = new DesktopUpdatePreferences(
                _checkAutomatically.Checked,
                _downloadAutomatically.Checked,
                _restartAutomatically.Checked,
                _includePrereleases.Checked,
                discovery,
                editorPath,
                checked((int)_maximumEditableKilobytes.Value * 1024),
                _adaptiveConcurrency.Checked,
                (int)_minimumConcurrency.Value,
                (int)_maximumTransferConcurrency.Value,
                (int)_perConnectionConcurrency.Value,
                (int)_maximumSyncConcurrency.Value,
                _appearance.SelectedItem is DesktopAppearance appearance ? appearance : DesktopAppearance.System,
                _warnBeforeUnsafeExternalEdit.Checked,
                ReadConnectionDefaults(),
                _defaultWorkspaceLayout.SelectedItem is WorkspaceLayout layout
                    ? layout
                    : WorkspaceLayout.SideBySide);
            if (_saved is null)
            {
                _store.Save(preferences);
            }
            else
            {
                _saved(preferences);
            }

            DesktopAppearanceService.SetAppearance(preferences.Appearance);
            _appliedAppearance = preferences.Appearance;

            _apply.Enabled = false;
            return true;
        }
        catch (Exception error) when (error is
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
            _ = MessageBox.Show(
                this,
                "StorageHub could not save settings. Your previous settings are unchanged.",
                "Settings",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }
    }

    private sealed record DiscoveryChoice(
        SshHostKeyDiscoveryMode Mode,
        string Label,
        string Description)
    {
        public override string ToString() => Label;
    }
}
