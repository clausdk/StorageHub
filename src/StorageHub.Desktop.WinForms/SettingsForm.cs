using Krypton.Toolkit;

namespace StorageHub.Desktop;

public sealed class SettingsForm : KryptonForm
{
    private static readonly string[] CategoryNames =
    [
        "General",
        "Connections & trust",
        "Updates",
        "About"
    ];

    private readonly DesktopUpdatePreferencesStore _store;
    private readonly Action<DesktopUpdatePreferences>? _saved;
    private readonly ListBox _categories;
    private readonly Dictionary<string, Control> _pages = new(StringComparer.Ordinal);
    private readonly CheckBox _checkAutomatically;
    private readonly CheckBox _downloadAutomatically;
    private readonly CheckBox _restartAutomatically;
    private readonly CheckBox _includePrereleases;
    private readonly ComboBox _sshDiscovery;
    private readonly Label _sshDiscoveryDescription;
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

        _categories = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            IntegralHeight = false,
            ItemHeight = 38,
            Font = StorageHubTheme.CreateSectionFont(),
            BackColor = StorageHubTheme.SurfaceMuted,
            AccessibleName = "Settings categories"
        };
        _categories.Items.AddRange(CategoryNames);

        var pageHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = StorageHubTheme.Surface,
            Padding = new Padding(28, 24, 28, 18)
        };
        AddPage(pageHost, "General", BuildGeneralPage());
        AddPage(pageHost, "Connections & trust", BuildConnectionsPage());
        AddPage(pageHost, "Updates", BuildUpdatesPage());
        AddPage(pageHost, "About", BuildAboutPage());

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

        _categories.SelectedIndexChanged += CategorySelected;
        _checkAutomatically.CheckedChanged += UpdateDependencies;
        _downloadAutomatically.CheckedChanged += UpdateDependencies;
        _sshDiscovery.SelectedIndexChanged += DiscoverySelectionChanged;
        foreach (var option in UpdateOptions())
        {
            option.CheckedChanged += MarkDirty;
        }

        _categories.SelectedIndex = 0;
        UpdateDependencies(this, EventArgs.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _categories.SelectedIndexChanged -= CategorySelected;
            _checkAutomatically.CheckedChanged -= UpdateDependencies;
            _downloadAutomatically.CheckedChanged -= UpdateDependencies;
            _sshDiscovery.SelectedIndexChanged -= DiscoverySelectionChanged;
            foreach (var option in UpdateOptions())
            {
                option.CheckedChanged -= MarkDirty;
            }

            _categories.Font.Dispose();
        }

        base.Dispose(disposing);
    }

    private static FlowLayoutPanel BuildGeneralPage()
    {
        var page = CreatePage(
            "General",
            "StorageHub keeps the desktop intentionally predictable. All currently configurable behavior is grouped in this window.");
        page.Controls.Add(CreateInformationCard(
            "Workspace",
            "StorageHub opens with a dual-pane workspace. Workspace navigation, queues, schedules, and saved connections use the background agent and remain scoped to the signed-in Windows user."));
        page.Controls.Add(CreateInformationCard(
            "Settings storage",
            "Desktop preferences are schema-versioned, size-bounded, and atomically saved under your local application-data folder. Credentials and private keys are never stored here."));
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

    private FlowLayoutPanel BuildUpdatesPage()
    {
        var page = CreatePage(
            "Updates",
            "StorageHub checks the fixed official GitHub release feed and uses the same package lifecycle as the verified installer pipeline.");
        var options = new FlowLayoutPanel
        {
            AutoSize = true,
            Width = 650,
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

    private static FlowLayoutPanel BuildAboutPage()
    {
        var page = CreatePage(
            "About StorageHub",
            $"StorageHub {DesktopApplicationVersion.Current}\nOpen-source secure storage manager\nPowered by CodeLogic and CL.Storage");
        page.Controls.Add(CreateInformationCard(
            "Security model",
            "Remote identities are never trusted merely because they were fetched. Compare a discovered SHA-256 fingerprint with one obtained through a separate trusted channel before saving it."));
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
        heading.Height = 32;
        var summary = UiControlFactory.CreateDescription(description);
        summary.Width = 650;
        summary.Padding = new Padding(0, 0, 0, 12);
        page.Controls.Add(heading);
        page.Controls.Add(summary);
        return page;
    }

    private static Panel CreateInformationCard(string title, string text)
    {
        var card = new Panel
        {
            AutoSize = true,
            Width = 650,
            BackColor = StorageHubTheme.SurfaceMuted,
            Padding = new Padding(14),
            Margin = new Padding(0, 10, 0, 0)
        };
        var heading = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Text = title,
            Font = StorageHubTheme.CreateSectionFont(),
            ForeColor = StorageHubTheme.Text
        };
        var description = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            MaximumSize = new Size(620, 0),
            Text = text,
            ForeColor = StorageHubTheme.TextMuted,
            Padding = new Padding(0, 6, 0, 0)
        };
        card.Controls.Add(description);
        card.Controls.Add(heading);
        return card;
    }

    private static Panel CreateSecurityNotice()
    {
        var notice = new Panel
        {
            AutoSize = true,
            BackColor = Color.FromArgb(255, 242, 222),
            Width = 650,
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

    private void CategorySelected(object? sender, EventArgs e)
    {
        var selected = _categories.SelectedItem as string;
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
            var preferences = new DesktopUpdatePreferences(
                _checkAutomatically.Checked,
                _downloadAutomatically.Checked,
                _restartAutomatically.Checked,
                _includePrereleases.Checked,
                discovery);
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
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
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
