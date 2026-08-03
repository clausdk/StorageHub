using Krypton.Toolkit;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

public sealed class SettingsForm : KryptonForm
{
    private readonly DesktopUpdatePreferencesStore _store;
    private readonly Action<DesktopUpdatePreferences>? _saved;
    private readonly TreeView _categories;
    private readonly Dictionary<string, Control> _pages = new(StringComparer.Ordinal);
    private readonly CheckBox _checkAutomatically;
    private readonly CheckBox _downloadAutomatically;
    private readonly CheckBox _restartAutomatically;
    private readonly CheckBox _includePrereleases;
    private readonly ComboBox _sshDiscovery;
    private readonly Label _sshDiscoveryDescription;
    private readonly TextBox _externalEditor;
    private readonly NumericUpDown _maximumEditableKilobytes;
    private readonly CheckBox _adaptiveConcurrency;
    private readonly NumericUpDown _minimumConcurrency;
    private readonly NumericUpDown _maximumTransferConcurrency;
    private readonly NumericUpDown _perConnectionConcurrency;
    private readonly NumericUpDown _maximumSyncConcurrency;
    private readonly Button _apply;

    public SettingsForm()
        : this(DesktopUpdatePreferencesStore.CreateDefault(), saved: null)
    {
    }

    internal SettingsForm(
        DesktopUpdatePreferencesStore store,
        Action<DesktopUpdatePreferences>? saved)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _saved = saved;

        Text = "Settings — StorageHub";
        AccessibleName = "StorageHub Settings";
        AccessibleDescription = "Configure StorageHub behavior, connection trust, and updates.";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(840, 600);
        Size = new Size(980, 700);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = StorageHubTheme.Canvas;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        var preferences = _store.Load();
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
        _adaptiveConcurrency = CreateOption(
            "Automatically tune concurrency from observed speed",
            "Starts at the minimum, increases after sustained healthy throughput, and backs off on slowdown or provider errors.",
            preferences.AdaptiveConcurrency);
        _minimumConcurrency = CreateConcurrencyInput(1, 8, preferences.MinimumConcurrency, "Starting concurrency");
        _maximumTransferConcurrency = CreateConcurrencyInput(1, 32, preferences.MaximumTransferConcurrency, "Maximum concurrent transfers");
        _perConnectionConcurrency = CreateConcurrencyInput(1, 16, preferences.PerConnectionConcurrency, "Maximum transfers per connection");
        _maximumSyncConcurrency = CreateConcurrencyInput(1, 8, preferences.MaximumSyncConcurrency, "Maximum concurrent synchronizations");

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
            BorderStyle = BorderStyle.None,
            ItemHeight = 30,
            Indent = 20,
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
        _categories.Nodes.Add(new TreeNode("Connections & trust") { Name = "Connections & trust" });
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
        AddPage(pageHost, "Connections & trust", BuildConnectionsPage());
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
        _adaptiveConcurrency.CheckedChanged += ConcurrencyChanged;
        _minimumConcurrency.ValueChanged += ConcurrencyChanged;
        _maximumTransferConcurrency.ValueChanged += ConcurrencyChanged;
        _perConnectionConcurrency.ValueChanged += ConcurrencyChanged;
        _maximumSyncConcurrency.ValueChanged += ConcurrencyChanged;
        foreach (var option in UpdateOptions())
        {
            option.CheckedChanged += MarkDirty;
        }

        _categories.SelectedNode = work.Nodes[0];
        UpdateDependencies(this, EventArgs.Empty);
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
            _adaptiveConcurrency.CheckedChanged -= ConcurrencyChanged;
            _minimumConcurrency.ValueChanged -= ConcurrencyChanged;
            _maximumTransferConcurrency.ValueChanged -= ConcurrencyChanged;
            _perConnectionConcurrency.ValueChanged -= ConcurrencyChanged;
            _maximumSyncConcurrency.ValueChanged -= ConcurrencyChanged;
            foreach (var option in UpdateOptions())
            {
                option.CheckedChanged -= MarkDirty;
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
            BackColor = Color.FromArgb(255, 242, 222),
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
                (int)_maximumSyncConcurrency.Value);
            if (_saved is null)
            {
                _store.Save(preferences);
            }
            else
            {
                _saved(preferences);
            }

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
