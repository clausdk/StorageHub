using Krypton.Toolkit;

namespace StorageHub.Desktop;

public sealed class SettingsForm : KryptonForm
{
    private readonly DesktopUpdatePreferencesStore _store;
    private readonly Action<DesktopUpdatePreferences>? _saved;
    private readonly CheckBox _checkAutomatically;
    private readonly CheckBox _downloadAutomatically;
    private readonly CheckBox _restartAutomatically;
    private readonly CheckBox _includePrereleases;
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
        AccessibleDescription = "Configure automatic StorageHub updates.";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(720, 520);
        Size = new Size(820, 590);
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
            "Downloads the matching Velopack package silently after an automatic check.",
            preferences.DownloadAutomatically);
        _restartAutomatically = CreateOption(
            "Install silently and restart automatically",
            "Closes StorageHub after an integrity-checked download, applies the update, and reopens it. Disabled by default to avoid interrupting work.",
            preferences.RestartAutomatically);
        _includePrereleases = CreateOption(
            "Include engineering preview releases",
            "Keep enabled while using StorageHub preview builds. Disable it later to receive stable releases only.",
            preferences.IncludePrereleases);

        _checkAutomatically.CheckedChanged += UpdateDependencies;
        _downloadAutomatically.CheckedChanged += UpdateDependencies;
        foreach (var option in new[]
                 {
                     _checkAutomatically,
                     _downloadAutomatically,
                     _restartAutomatically,
                     _includePrereleases
                 })
        {
            option.CheckedChanged += MarkDirty;
        }

        var heading = UiControlFactory.CreateSectionTitle("Updates");
        heading.Dock = DockStyle.Top;
        heading.Height = 36;
        var summary = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            Text = "StorageHub checks the official GitHub release feed and uses the same package and lifecycle hooks as the verified installer pipeline.",
            ForeColor = StorageHubTheme.TextMuted,
            Padding = new Padding(0, 0, 0, 12)
        };
        var source = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Text = $"Update source: {VelopackDesktopUpdateEngineFactory.TrustedRepositoryUrl}\nInstalled version: {DesktopApplicationVersion.Current}",
            ForeColor = StorageHubTheme.TextMuted,
            Padding = new Padding(0, 12, 0, 0),
            AccessibleName = "Update source and installed version"
        };

        var options = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = StorageHubTheme.Surface,
            Padding = new Padding(0, 8, 0, 0)
        };
        options.Controls.Add(_checkAutomatically);
        options.Controls.Add(_downloadAutomatically);
        options.Controls.Add(_restartAutomatically);
        options.Controls.Add(_includePrereleases);
        options.Controls.Add(source);

        var content = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = StorageHubTheme.Surface,
            Padding = new Padding(28, 24, 28, 18)
        };
        content.Controls.Add(options);
        content.Controls.Add(summary);
        content.Controls.Add(heading);

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

        Controls.Add(content);
        Controls.Add(footer);
        AcceptButton = ok;
        CancelButton = cancel;
        UpdateDependencies(this, EventArgs.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _checkAutomatically.CheckedChanged -= UpdateDependencies;
            _downloadAutomatically.CheckedChanged -= UpdateDependencies;
            foreach (var option in new[]
                     {
                         _checkAutomatically,
                         _downloadAutomatically,
                         _restartAutomatically,
                         _includePrereleases
                     })
            {
                option.CheckedChanged -= MarkDirty;
            }
        }

        base.Dispose(disposing);
    }

    private static CheckBox CreateOption(string text, string description, bool isChecked) =>
        new()
        {
            Text = text,
            Checked = isChecked,
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            Margin = new Padding(0, 8, 0, 8),
            AccessibleName = text,
            AccessibleDescription = description
        };

    private void UpdateDependencies(object? sender, EventArgs e)
    {
        _downloadAutomatically.Enabled = _checkAutomatically.Checked;
        _restartAutomatically.Enabled =
            _checkAutomatically.Checked && _downloadAutomatically.Checked;
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
            var preferences = new DesktopUpdatePreferences(
                _checkAutomatically.Checked,
                _downloadAutomatically.Checked,
                _restartAutomatically.Checked,
                _includePrereleases.Checked);
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
                "StorageHub could not save update settings. Your previous settings are unchanged.",
                "Settings",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }
    }
}
