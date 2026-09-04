using System.Security.Cryptography;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

public sealed class SettingsForm : Form
{
    private const int ContentWidth = 700;
    private const int NavigationWidth = 260;
    private readonly DesktopUpdatePreferencesStore _store;
    private readonly Action<DesktopUpdatePreferences>? _saved;
    private readonly IRemoteSecretVaultClient _secretClient;
    private readonly bool _ownsSecretClient;
    private readonly TreeView _categories;
    private readonly Font _categoryItemFont;
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
    private readonly CheckBox _confirmBeforeClearingTransferHistory;
    private readonly NumericUpDown _minimumConcurrency;
    private readonly NumericUpDown _maximumTransferConcurrency;
    private readonly NumericUpDown _perConnectionConcurrency;
    private readonly NumericUpDown _maximumSyncConcurrency;
    private readonly ComboBox _appearance;
    private readonly ComboBox _defaultWorkspaceLayout;
    private readonly CheckBox _reconnectRemotePanes;
    private readonly ComboBox _sshTerminalName;
    private readonly TextBox _sshStartupCommand;
    private readonly NumericUpDown _sshKeepAliveSeconds;
    private readonly ComboBox _sshFontFamily;
    private readonly NumericUpDown _sshFontSize;
    private readonly NumericUpDown _sshScrollbackLines;
    private readonly NumericUpDown _sshRefreshInterval;
    private readonly CheckBox _sshRenderBoldText;
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
        MinimumSize = new Size(1080, 720);
        Size = new Size(1160, 780);
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
        _confirmBeforeClearingTransferHistory = CreateOption(
            "Warn before clearing all transfer history",
            "Shows a confirmation before permanently removing completed, cancelled, and failed transfer records.",
            preferences.ConfirmBeforeClearingTransferHistory);
        _minimumConcurrency = CreateConcurrencyInput(1, 8, preferences.MinimumConcurrency, "Starting concurrency");
        _maximumTransferConcurrency = CreateConcurrencyInput(1, 32, preferences.MaximumTransferConcurrency, "Maximum concurrent transfers");
        _perConnectionConcurrency = CreateConcurrencyInput(1, 16, preferences.PerConnectionConcurrency, "Maximum transfers per connection");
        _maximumSyncConcurrency = CreateConcurrencyInput(1, 8, preferences.MaximumSyncConcurrency, "Maximum concurrent synchronizations");
        _appearance = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 260,
            FormattingEnabled = true,
            AccessibleName = "Application appearance"
        };
        _appearance.Items.AddRange([DesktopAppearance.Light, DesktopAppearance.Dark, DesktopAppearance.System]);
        _appearance.SelectedItem = preferences.Appearance;
        _defaultWorkspaceLayout = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 260,
            FormattingEnabled = true,
            AccessibleName = "Default workspace layout"
        };
        _defaultWorkspaceLayout.Items.AddRange([WorkspaceLayout.SideBySide, WorkspaceLayout.TopAndBottom]);
        _defaultWorkspaceLayout.Format += (_, args) => args.Value = args.ListItem switch
        {
            WorkspaceLayout.TopAndBottom => "Top and bottom",
            _ => "Side by side"
        };
        _defaultWorkspaceLayout.SelectedItem = preferences.DefaultWorkspaceLayout;
        _reconnectRemotePanes = CreateOption(
            "Reconnect remote panes automatically when opening workspace files",
            "Uses saved profiles to create fresh storage and SSH sessions. Workspace files never contain credentials or terminal contents.",
            preferences.ReconnectRemotePanesAutomatically);

        var terminalPreferences = SshTerminalPreferences.Resolve(preferences.SshTerminal);
        _sshTerminalName = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDown,
            Width = 360,
            MaxLength = SshTerminalIpcContract.MaximumTerminalNameLength,
            Text = terminalPreferences.TerminalName,
            AccessibleName = "SSH terminal type"
        };
        _sshTerminalName.Items.AddRange([
            "xterm-256color", "xterm", "screen-256color", "tmux-256color", "linux", "vt220", "vt100"
        ]);
        _sshStartupCommand = new TextBox
        {
            Width = 390,
            MaxLength = SshTerminalPreferences.MaximumStartupCommandLength,
            Text = terminalPreferences.StartupCommand ?? string.Empty,
            PlaceholderText = "Server default (examples: bash -l, zsh -l, pwsh -NoLogo)",
            AccessibleName = "SSH startup shell or command"
        };
        _sshKeepAliveSeconds = CreateProviderNumberDefault(
            terminalPreferences.KeepAliveSeconds,
            0,
            3_600);
        _sshKeepAliveSeconds.AccessibleName = "SSH keepalive interval in seconds";
        _sshFontFamily = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDown,
            Width = 360,
            MaxLength = 128,
            Text = terminalPreferences.FontFamily,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.ListItems,
            AccessibleName = "SSH terminal font family"
        };
        _sshFontFamily.Items.AddRange(FontFamily.Families
            .Select(family => family.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Cast<object>()
            .ToArray());
        _sshFontSize = new NumericUpDown
        {
            Minimum = 6,
            Maximum = 32,
            DecimalPlaces = 1,
            Increment = 0.5M,
            Value = (decimal)terminalPreferences.FontSize,
            Width = 150,
            AccessibleName = "SSH terminal font size"
        };
        _sshScrollbackLines = CreateProviderNumberDefault(
            terminalPreferences.ScrollbackLines,
            100,
            20_000);
        _sshScrollbackLines.AccessibleName = "SSH terminal scrollback lines";
        _sshRefreshInterval = CreateProviderNumberDefault(
            terminalPreferences.RefreshIntervalMilliseconds,
            16,
            500);
        _sshRefreshInterval.AccessibleName = "SSH terminal output refresh interval in milliseconds";
        _sshRenderBoldText = CreateOption(
            "Render ANSI bold text",
            "Uses a bold terminal font for server output which requests the ANSI bold attribute.",
            terminalPreferences.RenderBoldText);

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
            MaximumSize = new Size(ContentWidth - 40, 0),
            ForeColor = StorageHubTheme.TextMuted,
            Padding = new Padding(0, 8, 0, 0),
            AccessibleName = "SSH host-key discovery description"
        };
        UpdateDiscoveryDescription();

        _categories = new TreeView
        {
            Dock = DockStyle.Fill,
            ItemHeight = 32,
            Indent = 18,
            FullRowSelect = true,
            HideSelection = false,
            ShowLines = false,
            ShowPlusMinus = true,
            ShowRootLines = false,
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold, GraphicsUnit.Point),
            BackColor = StorageHubTheme.SurfaceMuted,
            AccessibleName = "Settings categories"
        };
        var work = new TreeNode("Transfers & sync") { Name = "Performance" };
        _categories.Nodes.Add(work);
        _categories.Nodes.Add(new TreeNode("Editing") { Name = "Editing" });
        _categories.Nodes.Add(new TreeNode("Appearance") { Name = "Appearance" });
        _categories.Nodes.Add(new TreeNode("Workspace") { Name = "Workspace" });
        var connections = new TreeNode("Connections & trust") { Name = "Connections & trust" };
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
        _categoryItemFont = new Font(_categories.Font, FontStyle.Regular);
        foreach (TreeNode rootNode in _categories.Nodes)
        {
            ApplyCategoryItemFont(rootNode.Nodes, _categoryItemFont);
        }

        var pageHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = StorageHubTheme.Surface,
            Padding = new Padding(32, 26, 32, 20)
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
            Padding = new Padding(16, 22, 12, 14)
        };
        var navigationTitle = UiControlFactory.CreateSectionTitle("Settings");
        navigationTitle.Dock = DockStyle.Top;
        navigationTitle.Height = 42;
        navigation.Controls.Add(_categories);
        navigation.Controls.Add(navigationTitle);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Size = new Size(1060, 650),
            FixedPanel = FixedPanel.Panel1,
            SplitterDistance = NavigationWidth,
            Panel1MinSize = NavigationWidth,
            Panel2MinSize = ContentWidth + 64,
            IsSplitterFixed = true,
            BackColor = StorageHubTheme.Border
        };
        split.Panel1.Controls.Add(navigation);
        split.Panel2.Controls.Add(pageHost);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 64,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(16, 12, 16, 10),
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
        _confirmBeforeClearingTransferHistory.CheckedChanged += MarkDirty;
        _minimumConcurrency.ValueChanged += ConcurrencyChanged;
        _maximumTransferConcurrency.ValueChanged += ConcurrencyChanged;
        _perConnectionConcurrency.ValueChanged += ConcurrencyChanged;
        _maximumSyncConcurrency.ValueChanged += ConcurrencyChanged;
        _appearance.SelectedIndexChanged += AppearanceSelectionChanged;
        _defaultWorkspaceLayout.SelectedIndexChanged += MarkDirty;
        _reconnectRemotePanes.CheckedChanged += MarkDirty;
        _sshTerminalName.TextChanged += MarkDirty;
        _sshStartupCommand.TextChanged += MarkDirty;
        _sshFontFamily.TextChanged += MarkDirty;
        _sshFontSize.ValueChanged += MarkDirty;
        _sshRenderBoldText.CheckedChanged += MarkDirty;
        foreach (var option in UpdateOptions())
        {
            option.CheckedChanged += MarkDirty;
        }

        _categories.SelectedNode = work;
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
            _confirmBeforeClearingTransferHistory.CheckedChanged -= MarkDirty;
            _minimumConcurrency.ValueChanged -= ConcurrencyChanged;
            _maximumTransferConcurrency.ValueChanged -= ConcurrencyChanged;
            _perConnectionConcurrency.ValueChanged -= ConcurrencyChanged;
            _maximumSyncConcurrency.ValueChanged -= ConcurrencyChanged;
            _appearance.SelectedIndexChanged -= AppearanceSelectionChanged;
            _defaultWorkspaceLayout.SelectedIndexChanged -= MarkDirty;
            _reconnectRemotePanes.CheckedChanged -= MarkDirty;
            _sshTerminalName.TextChanged -= MarkDirty;
            _sshStartupCommand.TextChanged -= MarkDirty;
            _sshFontFamily.TextChanged -= MarkDirty;
            _sshFontSize.ValueChanged -= MarkDirty;
            _sshRenderBoldText.CheckedChanged -= MarkDirty;
            foreach (var option in UpdateOptions())
            {
                option.CheckedChanged -= MarkDirty;
            }

            if (_ownsSecretClient)
            {
                _secretClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            _categories.Font.Dispose();
            _categoryItemFont.Dispose();
        }

        base.Dispose(disposing);
    }

    private FlowLayoutPanel BuildPerformancePage()
    {
        var page = CreatePage(
            "Concurrency",
            "Control how many transfers and synchronization jobs run at once.");
        page.Controls.Add(_adaptiveConcurrency);
        page.Controls.Add(_confirmBeforeClearingTransferHistory);
        var table = new TableLayoutPanel
        {
            AutoSize = true,
            Width = ContentWidth,
            ColumnCount = 2,
            Padding = new Padding(0, 12, 0, 0)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        StyleSettingsSection(table);
        AddConcurrencyRow(table, "Start with", _minimumConcurrency,
            "The adaptive controller begins conservatively at this many jobs.");
        AddConcurrencyRow(table, "Maximum transfers", _maximumTransferConcurrency,
            "Global ceiling shared by transfers across every supported provider.");
        AddConcurrencyRow(table, "Per saved connection", _perConnectionConcurrency,
            "Prevents one server, bucket, or local connection from consuming every worker.");
        AddConcurrencyRow(table, "Maximum synchronizations", _maximumSyncConcurrency,
            "Separate ceiling for scheduled and manually approved synchronization runs.");
        table.AutoSize = false;
        table.Height = 300;
        page.Controls.Add(table);
        return page;
    }

    private FlowLayoutPanel BuildConnectionsPage()
    {
        var page = CreatePage(
            "Connections & trust",
            "Choose how SSH host keys are discovered before you verify and trust them.");
        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            Width = ContentWidth,
            MinimumSize = new Size(ContentWidth, 0),
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
        StyleSettingsSection(layout);
        page.Controls.Add(layout);
        return page;
    }

    private FlowLayoutPanel BuildEditingPage()
    {
        var page = CreatePage(
            "External editing",
            "Choose how remote files open in an external editor.");
        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            Width = ContentWidth,
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
            Text = "Leave blank to use the Windows default app.",
            AutoSize = true,
            MaximumSize = new Size(ContentWidth - 44, 0),
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
            Text = "Maximum: 1,024 KiB (1 MiB).",
            AutoSize = true,
            MaximumSize = new Size(ContentWidth - 44, 0),
            ForeColor = StorageHubTheme.Warning,
            Padding = new Padding(0, 5, 0, 0)
        }, 0, 5);
        layout.SetColumnSpan(layout.Controls[^1], 2);
        _warnBeforeUnsafeExternalEdit.Padding = new Padding(0, 12, 0, 0);
        layout.Controls.Add(_warnBeforeUnsafeExternalEdit, 0, 6);
        layout.SetColumnSpan(_warnBeforeUnsafeExternalEdit, 2);
        StyleSettingsSection(layout);
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
            "Choose how StorageHub checks for and installs updates.");
        var options = new FlowLayoutPanel
        {
            AutoSize = true,
            Width = ContentWidth,
            MinimumSize = new Size(ContentWidth, 0),
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
        StyleSettingsSection(options);
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
        heading.Width = ContentWidth;
        heading.MinimumSize = new Size(ContentWidth, 0);
        heading.Font = new Font("Segoe UI Semibold", 16F, FontStyle.Bold, GraphicsUnit.Point);
        heading.Height = 40;
        var summary = UiControlFactory.CreateDescription(description);
        summary.Width = ContentWidth;
        summary.MinimumSize = new Size(ContentWidth, 0);
        summary.MaximumSize = new Size(ContentWidth, 0);
        summary.Padding = new Padding(0, 0, 0, 14);
        page.Controls.Add(heading);
        page.Controls.Add(summary);
        page.Layout += FitSettingsPageContent;
        return page;
    }

    private static void FitSettingsPageContent(object? sender, LayoutEventArgs e)
    {
        if (sender is FlowLayoutPanel page)
        {
            FitSettingsPageContent(page);
        }
    }

    private static void FitSettingsPageContent(FlowLayoutPanel page)
    {
        if (!page.Visible || !page.IsHandleCreated ||
            page.ClientSize.Width <= SystemInformation.VerticalScrollBarWidth + 1)
        {
            return;
        }

        var scrollbarWidth = page.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0;
        var availableWidth = Math.Max(1, page.ClientSize.Width - scrollbarWidth - 1);
        foreach (Control control in page.Controls)
        {
            if (control is Button or CheckBox || control.Width == availableWidth)
            {
                continue;
            }

            control.MinimumSize = new Size(0, control.MinimumSize.Height);
            if (control is Label && control.MaximumSize.Width > 0)
            {
                control.MaximumSize = new Size(availableWidth, control.MaximumSize.Height);
            }
            control.Width = availableWidth;
        }
    }

    private static void ApplyCategoryItemFont(TreeNodeCollection nodes, Font itemFont)
    {
        foreach (TreeNode node in nodes)
        {
            node.NodeFont = itemFont;
            ApplyCategoryItemFont(node.Nodes, itemFont);
        }
    }

    private static void StyleSettingsSection(Control section)
    {
        section.Width = ContentWidth;
        section.MinimumSize = new Size(ContentWidth, 0);
        section.BackColor = StorageHubTheme.SurfaceMuted;
        section.Padding = new Padding(18);
        section.Margin = new Padding(0, 4, 0, 12);
        section.Paint += DrawSettingsSectionBorder;
        if (section is TableLayoutPanel table)
        {
            table.AutoSize = true;
            table.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            table.GrowStyle = TableLayoutPanelGrowStyle.AddRows;
        }
        var preferredHeight = section.GetPreferredSize(new Size(ContentWidth, 0)).Height;
        section.AutoSize = false;
        section.Height = preferredHeight;
        if (string.IsNullOrEmpty(section.Name))
        {
            section.Name = "SettingsSection";
        }
    }

    private static void DrawSettingsSectionBorder(object? sender, PaintEventArgs e)
    {
        if (sender is not Control section || section.ClientRectangle.Width <= 1 || section.ClientRectangle.Height <= 1)
        {
            return;
        }

        var bounds = Rectangle.Inflate(section.ClientRectangle, -1, -1);
        using var pen = new Pen(StorageHubTheme.Border);
        e.Graphics.DrawRectangle(pen, bounds);
    }

    private static TableLayoutPanel CreateInformationCard(string title, string text)
    {
        var card = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Width = ContentWidth,
            MinimumSize = new Size(ContentWidth, 0),
            ColumnCount = 1,
            RowCount = 2,
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize,
            BackColor = StorageHubTheme.SurfaceMuted,
            Padding = new Padding(14),
            Margin = new Padding(0, 4, 0, 12),
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
            MaximumSize = new Size(ContentWidth - 40, 0),
            Text = text,
            ForeColor = StorageHubTheme.TextMuted,
            Padding = new Padding(0, 6, 0, 0)
        };
        card.Controls.Add(heading, 0, 0);
        card.Controls.Add(description, 0, 1);
        card.Height = card.GetPreferredSize(new Size(ContentWidth, 0)).Height;
        card.AutoSize = false;
        card.Paint += DrawSettingsSectionBorder;
        return card;
    }

    private static Panel CreateSecurityNotice()
    {
        var notice = new Panel
        {
            AutoSize = true,
            BackColor = StorageHubTheme.SurfaceMuted,
            Width = ContentWidth - 36,
            MinimumSize = new Size(ContentWidth - 36, 0),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
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
            MaximumSize = new Size(ContentWidth, 0),
            Margin = new Padding(0, 8, 0, 8),
            ForeColor = StorageHubTheme.Text,
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
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
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
            MaximumSize = new Size(ContentWidth - 40, 0),
            ForeColor = StorageHubTheme.TextMuted,
            Padding = new Padding(0, 2, 0, 10)
        };
        table.Controls.Add(help, 0, row + 1);
        table.SetColumnSpan(help, 2);
        table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    }

    private FlowLayoutPanel BuildAppearancePage()
    {
        var page = CreatePage("Appearance", "Choose the application color theme.");
        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            Width = ContentWidth,
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
            "System follows Windows. Changes preview immediately."));
        StyleSettingsSection(layout);
        page.Controls.Add(layout);
        return page;
    }

    private FlowLayoutPanel BuildWorkspacePage()
    {
        var page = CreatePage(
            "Workspace",
            "Choose the default arrangement for new workspaces.");
        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            Width = ContentWidth,
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
            "Choose the orientation used by two- and three-pane presets."));
        layout.Controls.Add(_reconnectRemotePanes);
        StyleSettingsSection(layout);
        page.Controls.Add(layout);
        return page;
    }

    private static FlowLayoutPanel BuildConnectionTypePage(ConnectionProfileType type)
    {
        var isStorage = type == ConnectionProfileType.Storage;
        var page = CreatePage(
            isStorage ? "Storage connections" : "Client connections",
            isStorage
                ? "Providers available for browsing, transfers, and synchronization."
                : "Interactive remote client providers.");
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
            provider.Kind == StorageProviderKind.Ssh
                ? "Defaults for new SSH profiles and terminal sessions."
                : $"Defaults for new {provider.DisplayName} profiles.");
        var defaults = ConnectionDefaultSettings.Get(provider.Kind, preferences.ConnectionDefaults);
        var editor = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Width = ContentWidth,
            MinimumSize = new Size(ContentWidth, 0),
            ColumnCount = 2,
            Padding = new Padding(0, 8, 0, 6),
            Name = $"ProviderSettings:{provider.Kind}",
            AccessibleName = $"{provider.DisplayName} new-connection defaults"
        };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddProviderSectionTitle(editor, "Basic defaults");
        var editableFields = ConnectionDefaultSettings.EditableFields(provider);
        if (editableFields.Count == 0)
        {
            AddProviderSectionNote(
                editor,
                "This provider has no reusable basic defaults. Its root path remains specific to each connection.");
        }
        foreach (var field in editableFields)
        {
            AddProviderDefaultRow(
                editor,
                provider.Kind,
                field.Key,
                ProviderDefaultLabel(field),
                CreateProviderFieldDefault(field, defaults.FieldValues[field.Key]),
                ProviderFieldDescription(field));
        }

        AddProviderSectionTitle(editor, "Advanced connection behavior");
        var connectionTimeout = CreateProviderNumberDefault(defaults.ConnectTimeoutSeconds, 1, 600);
        AddProviderDefaultRow(
            editor,
            provider.Kind,
            ConnectionDefaultSettings.ConnectTimeoutKey,
            "Connection timeout (seconds)",
            connectionTimeout,
            description: null);
        var operationTimeout = CreateProviderNumberDefault(defaults.OperationTimeoutSeconds, 1, 86_400);
        operationTimeout.Enabled = provider.Kind == StorageProviderKind.Local;
        if (provider.Kind != StorageProviderKind.Local)
        {
            connectionTimeout.ValueChanged += (_, _) => operationTimeout.Value = connectionTimeout.Value;
        }
        AddProviderDefaultRow(
            editor,
            provider.Kind,
            ConnectionDefaultSettings.OperationTimeoutKey,
            "Operation timeout (seconds)",
            operationTimeout,
            provider.Kind == StorageProviderKind.Local
                ? null
                : "Uses the connection timeout for this provider.");
        var retries = CreateProviderNumberDefault(defaults.MaximumRetryAttempts, 0, 20);
        var retriesSupported = ConnectionDefaultSettings.SupportsConfigurableRetries(provider.Kind);
        retries.Enabled = retriesSupported;
        AddProviderDefaultRow(
            editor,
            provider.Kind,
            ConnectionDefaultSettings.RetryAttemptsKey,
            "Retry attempts",
            retries,
            retriesSupported
                ? null
                : "Automatic retries are not supported for this provider.");

        StyleSettingsSection(editor);
        page.Controls.Add(editor);
        if (provider.Kind == StorageProviderKind.Ssh)
        {
            page.Controls.Add(BuildSshTerminalSettingsEditor());
        }
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

    private TableLayoutPanel BuildSshTerminalSettingsEditor()
    {
        var editor = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Width = ContentWidth,
            MinimumSize = new Size(ContentWidth, 0),
            ColumnCount = 2,
            Padding = new Padding(0, 18, 0, 8),
            Name = "SshTerminalSettings",
            AccessibleName = "SSH terminal and shell preferences"
        };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var title = UiControlFactory.CreateSectionTitle("Terminal & shell");
        title.Margin = new Padding(0, 0, 0, 8);
        editor.Controls.Add(title, 0, editor.RowCount);
        editor.SetColumnSpan(title, 2);
        editor.RowCount++;
        AddSshTerminalRow(editor, "Terminal type (TERM)", _sshTerminalName,
            "Advertised to the server, for example xterm-256color or vt220.");
        AddSshTerminalRow(editor, "Startup shell / command", _sshStartupCommand);
        AddSshTerminalRow(editor, "Keepalive interval (seconds)", _sshKeepAliveSeconds);
        AddSshTerminalRow(editor, "Font family", _sshFontFamily,
            "A monospaced font is recommended.");
        AddSshTerminalRow(editor, "Font size (points)", _sshFontSize);
        AddSshTerminalRow(editor, "Scrollback lines", _sshScrollbackLines);
        AddSshTerminalRow(editor, "Output refresh (milliseconds)", _sshRefreshInterval,
            "Lower is smoother; higher uses fewer resources.");
        AddSshTerminalRow(editor, "ANSI text", _sshRenderBoldText);
        StyleSettingsSection(editor);
        return editor;
    }

    private static void AddSshTerminalRow(
        TableLayoutPanel table,
        string label,
        Control control,
        string? description = null)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            ForeColor = StorageHubTheme.Text,
            Padding = new Padding(0, 7, 10, 4)
        }, 0, row);
        var host = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = Padding.Empty
        };
        control.Margin = Padding.Empty;
        host.Controls.Add(control);
        if (!string.IsNullOrWhiteSpace(description))
        {
            host.Controls.Add(UiControlFactory.CreateDescription(description));
        }
        table.Controls.Add(host, 1, row);
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
        Control control,
        string? description = null)
    {
        var key = ConnectionDefaultSettings.Key(provider, setting);
        control.AccessibleName = label;
        _connectionDefaultControls.Add(key, control);
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            ForeColor = StorageHubTheme.Text,
            Padding = new Padding(0, 7, 10, 4)
        }, 0, row);
        if (string.IsNullOrWhiteSpace(description))
        {
            table.Controls.Add(control, 1, row);
            return;
        }

        var host = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = Padding.Empty
        };
        control.Margin = Padding.Empty;
        host.Controls.Add(control);
        host.Controls.Add(UiControlFactory.CreateDescription(description));
        table.Controls.Add(host, 1, row);
    }

    private static void AddProviderSectionTitle(TableLayoutPanel table, string text)
    {
        var title = UiControlFactory.CreateSectionTitle(text);
        title.Margin = new Padding(0, table.RowCount == 0 ? 0 : 14, 0, 6);
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(title, 0, row);
        table.SetColumnSpan(title, 2);
    }

    private static void AddProviderSectionNote(TableLayoutPanel table, string text)
    {
        var note = UiControlFactory.CreateDescription(text);
        note.MaximumSize = new Size(ContentWidth - 40, 0);
        note.Margin = new Padding(0, 0, 0, 6);
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(note, 0, row);
        table.SetColumnSpan(note, 2);
    }

    private static string? ProviderFieldDescription(ConnectionFieldDescriptor field)
    {
        return field.Key switch
        {
            "authenticationMode" => "Selects the authentication fields shown for new profiles.",
            "privateKeyReference" => "Stored securely in the encrypted vault.",
            "tlsMode" or "trustMode" => field.HelpText,
            _ => null
        };
    }

    private static string ProviderDefaultLabel(ConnectionFieldDescriptor field) => field.Key switch
    {
        "privateKeyReference" => "Default private key",
        _ => $"Default {field.Label.ToLowerInvariant()}"
    };

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
                if (page.Value is FlowLayoutPanel settingsPage)
                {
                    settingsPage.PerformLayout();
                    FitSettingsPageContent(settingsPage);
                }
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

    private SshTerminalPreferences ReadSshTerminalPreferences() =>
        SshTerminalPreferences.Resolve(new SshTerminalPreferences(
            _sshTerminalName.Text,
            _sshStartupCommand.Text,
            decimal.ToInt32(_sshKeepAliveSeconds.Value),
            _sshFontFamily.Text,
            decimal.ToSingle(_sshFontSize.Value),
            decimal.ToInt32(_sshScrollbackLines.Value),
            decimal.ToInt32(_sshRefreshInterval.Value),
            _sshRenderBoldText.Checked));

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
                    : WorkspaceLayout.SideBySide,
                ReadSshTerminalPreferences(),
                _reconnectRemotePanes.Checked,
                _confirmBeforeClearingTransferHistory.Checked);
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
