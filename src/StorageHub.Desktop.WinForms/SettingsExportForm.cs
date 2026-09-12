namespace StorageHub.Desktop;

/// <summary>
/// Chooses what goes into an export file and where it is written.
///
/// The section list is built from <see cref="SettingsSectionCatalog"/>, so this dialog and the
/// import wizard cannot offer different things. What can never be exported is shown here too,
/// greyed with its reason: a silent omission would be a surprise the first time somebody moved to
/// a new machine and found their connections could not open.
/// </summary>
public sealed class SettingsExportForm : Form
{
    private readonly SettingsExportService _exporter;
    private readonly Dictionary<SettingsSectionId, CheckBox> _sections = [];
    private readonly CheckBox _protect;
    private readonly TextBox _password;
    private readonly TextBox _confirm;
    private readonly Label _passwordHint;
    private readonly Label _status;
    private readonly Button _export;
    private bool _updatingSections;

    internal SettingsExportForm(SettingsExportService exporter)
    {
        _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));

        Text = "Export Settings";
        AccessibleName = "Export settings";
        AccessibleDescription = "Choose which settings to write to a file.";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(660, 640);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = StorageHubTheme.Canvas;
        ForeColor = StorageHubTheme.Text;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        StorageHubTheme.Register(this);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoScroll = true,
            Padding = new Padding(18, 16, 18, 8)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        content.Controls.Add(new Label
        {
            Text = "Choose what to include",
            AutoSize = true,
            Font = StorageHubTheme.CreateSectionFont(),
            ForeColor = StorageHubTheme.Text,
            Margin = new Padding(0, 0, 0, 10)
        });

        foreach (var section in SettingsSectionCatalog.Sections)
        {
            var check = new CheckBox
            {
                Text = section.Label,
                Checked = section.CheckedByDefault,
                AutoSize = true,
                Margin = new Padding(0, 6, 0, 0),
                AccessibleName = section.Label,
                AccessibleDescription = section.Description
            };
            check.CheckedChanged += SectionCheckedChanged;
            _sections[section.Id] = check;
            content.Controls.Add(check);
            content.Controls.Add(new Label
            {
                Text = section.Description,
                AutoSize = true,
                MaximumSize = new Size(600, 0),
                ForeColor = StorageHubTheme.TextMuted,
                Margin = new Padding(20, 0, 0, 4)
            });
        }

        content.Controls.Add(CreateExclusions());

        _protect = new CheckBox
        {
            Text = "Protect this file with a password",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 4),
            AccessibleName = "Protect the export with a password"
        };
        _protect.CheckedChanged += (_, _) => { UpdatePasswordState(); UpdateExportState(); };
        _password = CreatePasswordBox("Export password");
        _confirm = CreatePasswordBox("Confirm export password");
        _password.TextChanged += (_, _) => UpdateExportState();
        _confirm.TextChanged += (_, _) => UpdateExportState();
        _passwordHint = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(600, 0),
            ForeColor = StorageHubTheme.TextMuted,
            Margin = new Padding(0, 2, 0, 0)
        };

        // Docked rather than added to the scrolling list above: whether the file is readable or
        // sealed changes what the file *is*, and it should never sit below the fold.
        var passwordGroup = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            ColumnCount = 1,
            Padding = new Padding(18, 8, 18, 4),
            BackColor = StorageHubTheme.Canvas
        };
        passwordGroup.Controls.Add(_protect);
        passwordGroup.Controls.Add(Labelled("Password", _password));
        passwordGroup.Controls.Add(Labelled("Confirm", _confirm));
        passwordGroup.Controls.Add(_passwordHint);
        passwordGroup.Controls.Add(new Label
        {
            Text = "Without a password the file is readable JSON you can review before sharing. " +
                "A lost password cannot be recovered.",
            AutoSize = true,
            MaximumSize = new Size(600, 0),
            ForeColor = StorageHubTheme.TextMuted,
            Margin = new Padding(0, 4, 0, 0)
        });

        _status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 24,
            ForeColor = StorageHubTheme.TextMuted,
            Padding = new Padding(18, 0, 18, 0),
            AccessibleName = "Export status"
        };

        _export = new Button { Text = "Export...", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        _export.Click += ExportClicked;
        StorageHubTheme.StylePrimaryButton(_export);
        var cancel = new Button
        {
            Text = "Cancel",
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
            Margin = new Padding(8, 0, 0, 0)
        };
        StorageHubTheme.StyleSecondaryButton(cancel);
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 56,
            Padding = new Padding(16, 12, 16, 10)
        };
        buttons.Controls.Add(_export);
        buttons.Controls.Add(cancel);

        Controls.Add(content);
        Controls.Add(passwordGroup);
        Controls.Add(_status);
        Controls.Add(buttons);
        CancelButton = cancel;

        UpdatePasswordState();
        UpdateExportState();
    }

    /// <summary>The file that was written, or null when the dialog was cancelled.</summary>
    internal string? ExportedPath { get; private set; }

    internal IReadOnlyCollection<SettingsSectionId> SelectedSections =>
        [.. _sections.Where(pair => pair.Value.Checked).Select(pair => pair.Key)];

    /// <summary>
    /// Keeps the ticks consistent with what a file can actually describe: choosing schedules also
    /// takes their sync tasks and connections, and clearing connections clears what depended on
    /// them. Doing this as the user clicks avoids an export that silently contains more, or less,
    /// than the boxes showed.
    /// </summary>
    private void SectionCheckedChanged(object? sender, EventArgs e)
    {
        if (_updatingSections) return;
        _updatingSections = true;
        try
        {
            var selected = sender is CheckBox { Checked: true }
                ? SettingsSectionCatalog.ExpandForExport(SelectedSections)
                : SettingsSectionCatalog.CollapseForExport(SelectedSections);
            foreach (var (id, check) in _sections)
            {
                check.Checked = selected.Contains(id);
            }
        }
        finally
        {
            _updatingSections = false;
        }

        UpdateExportState();
    }

    private void UpdatePasswordState()
    {
        _password.Enabled = _protect.Checked;
        _confirm.Enabled = _protect.Checked;
        if (!_protect.Checked)
        {
            _password.Clear();
            _confirm.Clear();
        }
    }

    private void UpdateExportState()
    {
        var sections = SelectedSections.Count > 0;
        var passwordProblem = _protect.Checked ? DescribePasswordProblem() : null;
        _passwordHint.Text = passwordProblem ?? string.Empty;
        _passwordHint.ForeColor = passwordProblem is null
            ? StorageHubTheme.TextMuted
            : StorageHubTheme.Warning;
        _export.Enabled = sections && passwordProblem is null;
        _status.Text = sections
            ? string.Empty
            : "Choose at least one thing to export.";
    }

    private string? DescribePasswordProblem() =>
        Security.SettingsExportEnvelope.ValidatePassword(_password.Text) ??
        (string.Equals(_password.Text, _confirm.Text, StringComparison.Ordinal)
            ? null
            : "The two passwords do not match.");

    private void ExportClicked(object? sender, EventArgs e)
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Export Settings",
            Filter = SettingsExportSerializer.FileFilter,
            DefaultExt = SettingsExportSerializer.FileExtension.TrimStart('.'),
            AddExtension = true,
            FileName = $"storagehub-settings-{DateTime.Now:yyyyMMdd}{SettingsExportSerializer.FileExtension}"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            SettingsExportService.Write(
                dialog.FileName,
                _exporter.Capture(SelectedSections),
                _protect.Checked ? _password.Text : null);
            ExportedPath = dialog.FileName;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException)
        {
            _ = MessageBox.Show(
                this,
                $"StorageHub could not write the export. {error.Message}",
                "Export Settings",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static TableLayoutPanel CreateExclusions()
    {
        var panel = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 1,
            Margin = new Padding(0, 14, 0, 0)
        };
        panel.Controls.Add(new Label
        {
            Text = "Never included",
            AutoSize = true,
            Font = StorageHubTheme.CreateSectionFont(),
            ForeColor = StorageHubTheme.Text,
            Margin = new Padding(0, 0, 0, 4)
        });
        foreach (var (title, reason) in new[]
        {
            ("Saved passwords and private keys",
                "They are held for this Windows account only and cannot be read back, even by StorageHub."),
            ("Key store entries",
                "Each entry is derived from its key material, which stays on this computer."),
            ("Host trust decisions",
                "These record what you verified on this computer, so they are not copied elsewhere.")
        })
        {
            panel.Controls.Add(new Label
            {
                Text = $"{title} — {reason}",
                AutoSize = true,
                MaximumSize = new Size(600, 0),
                ForeColor = StorageHubTheme.TextMuted,
                Margin = new Padding(0, 0, 0, 2)
            });
        }

        return panel;
    }

    private static TextBox CreatePasswordBox(string accessibleName) => new()
    {
        Width = 280,
        UseSystemPasswordChar = true,
        MaxLength = Security.SettingsExportEnvelope.MaximumPasswordLength,
        AccessibleName = accessibleName
    };

    private static TableLayoutPanel Labelled(string text, Control control)
    {
        var row = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(20, 4, 0, 0)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = StorageHubTheme.Text,
            Margin = new Padding(0, 4, 0, 0)
        });
        row.Controls.Add(control);
        return row;
    }
}
