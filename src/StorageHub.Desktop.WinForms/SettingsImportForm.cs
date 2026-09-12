namespace StorageHub.Desktop;

/// <summary>Which part of the import the user is looking at.</summary>
internal enum SettingsImportStep
{
    ChooseFile = 1,
    Review = 2,
    Result = 3
}

/// <summary>
/// Walks a settings file through the gates that decide whether it is safe to apply, shows what it
/// would change, and applies only what was ticked.
///
/// Three steps in one form rather than three dialogs: pick the file, choose and review, then see
/// what happened. Nothing is written until the middle step has been confirmed, so a file picked by
/// mistake is always recoverable by closing the window.
/// </summary>
public sealed class SettingsImportForm : Form
{
    private readonly SettingsImportService _importer;
    private readonly Panel _filePanel;
    private readonly Panel _reviewPanel;
    private readonly Panel _resultPanel;
    private readonly Label _fileSummary;
    private readonly Label _passwordPrompt;
    private readonly TextBox _password;
    private readonly Button _open;
    private readonly Label _reviewHeading;
    private readonly TableLayoutPanel _sectionList;
    private readonly Label _concurrencyNotice;
    private readonly ComboBox _conflictPolicy;
    private readonly Label _conflictLabel;
    private readonly Label _resultSummary;
    private readonly LinkLabel _backupLink;
    private readonly Button _back;
    private readonly Button _import;
    private readonly Button _close;
    private readonly Dictionary<SettingsSectionId, CheckBox> _sections = [];
    private SettingsExportDocument? _document;
    private string? _path;

    /// <summary>
    /// Whether the chosen file is sealed. Held rather than read back from the password box, since
    /// a control reports itself invisible until its form is shown and this decides whether a
    /// password is offered at all.
    /// </summary>
    private bool _needsPassword;

    internal SettingsImportForm(SettingsImportService importer)
    {
        _importer = importer ?? throw new ArgumentNullException(nameof(importer));

        Text = "Import Settings";
        AccessibleName = "Import settings";
        AccessibleDescription = "Review a settings file and choose what to apply.";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(660, 560);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = StorageHubTheme.Canvas;
        ForeColor = StorageHubTheme.Text;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        StorageHubTheme.Register(this);

        _fileSummary = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(580, 0),
            ForeColor = StorageHubTheme.Text,
            Margin = new Padding(0, 0, 0, 14),
            AccessibleName = "Selected file"
        };
        _passwordPrompt = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(580, 0),
            Visible = false,
            ForeColor = StorageHubTheme.TextMuted,
            Margin = new Padding(0, 0, 0, 4)
        };
        _password = new TextBox
        {
            Width = 280,
            Visible = false,
            UseSystemPasswordChar = true,
            MaxLength = Security.SettingsExportEnvelope.MaximumPasswordLength,
            Margin = new Padding(0, 0, 0, 12),
            AccessibleName = "File password"
        };
        _open = new Button { Text = "Open", AutoSize = true, Visible = false };
        _open.Click += (_, _) => OpenSelectedFile();
        StorageHubTheme.StylePrimaryButton(_open);
        _password.KeyDown += (_, args) =>
        {
            if (args.KeyCode != Keys.Enter) return;
            args.SuppressKeyPress = true;
            OpenSelectedFile();
        };

        // A single-column table rather than stacked Dock.Top controls: docking applies in reverse
        // z-order, which puts these in the wrong order and stretches the button across the form.
        var fileLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoScroll = true
        };
        fileLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        fileLayout.Controls.Add(_fileSummary);
        fileLayout.Controls.Add(_passwordPrompt);
        fileLayout.Controls.Add(_password);
        fileLayout.Controls.Add(_open);

        _filePanel = NewPanel();
        _filePanel.Controls.Add(fileLayout);

        _reviewHeading = new Label
        {
            Dock = DockStyle.Top,
            Height = 44,
            ForeColor = StorageHubTheme.Text,
            AccessibleName = "File summary"
        };
        _sectionList = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoScroll = true
        };
        _sectionList.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _concurrencyNotice = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            Visible = false,
            ForeColor = StorageHubTheme.Warning,
            AccessibleName = "Agent restart notice"
        };
        _conflictPolicy = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 320,
            FormattingEnabled = true,
            AccessibleName = "What to do about connections and tasks that are already here"
        };
        _conflictPolicy.Items.AddRange([.. Enum.GetValues<SettingsConflictPolicy>().Cast<object>()]);
        _conflictPolicy.Format += (_, args) => args.Value = args.ListItem switch
        {
            SettingsConflictPolicy.Replace => "Replace what is already here",
            SettingsConflictPolicy.ImportAsCopy => "Keep both, importing as a copy",
            _ => "Leave what is already here alone"
        };
        // Skip by default: an import should not overwrite work that is already on this machine
        // unless the user says so.
        _conflictPolicy.SelectedItem = SettingsConflictPolicy.Skip;
        _conflictLabel = new Label
        {
            Text = "Connections and tasks that already exist here:",
            AutoSize = true,
            Visible = false,
            ForeColor = StorageHubTheme.Text
        };

        var conflictRow = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            ColumnCount = 1,
            Padding = new Padding(0, 6, 0, 6)
        };
        conflictRow.Controls.Add(_conflictLabel);
        conflictRow.Controls.Add(_conflictPolicy);

        _reviewPanel = NewPanel();
        _reviewPanel.Visible = false;
        _reviewPanel.Controls.Add(_sectionList);
        _reviewPanel.Controls.Add(conflictRow);
        _reviewPanel.Controls.Add(_concurrencyNotice);
        _reviewPanel.Controls.Add(_reviewHeading);

        _resultSummary = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 300,
            ForeColor = StorageHubTheme.Text,
            AccessibleName = "Import result"
        };
        _backupLink = new LinkLabel
        {
            Dock = DockStyle.Top,
            Height = 24,
            Visible = false,
            AccessibleName = "Show the backup file"
        };
        _backupLink.LinkClicked += (_, _) => ShowBackup();
        _resultPanel = NewPanel();
        _resultPanel.Visible = false;
        _resultPanel.Controls.Add(_backupLink);
        _resultPanel.Controls.Add(_resultSummary);

        _back = new Button { Text = "Back", AutoSize = true, Visible = false, Margin = new Padding(8, 0, 0, 0) };
        _back.Click += (_, _) => ShowStep(_filePanel);
        StorageHubTheme.StyleSecondaryButton(_back);
        _import = new Button { Text = "Import", AutoSize = true, Visible = false, Margin = new Padding(8, 0, 0, 0) };
        _import.Click += async (_, _) => await ApplyImportAsync().ConfigureAwait(true);
        StorageHubTheme.StylePrimaryButton(_import);
        _close = new Button
        {
            Text = "Cancel",
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
            Margin = new Padding(8, 0, 0, 0)
        };
        StorageHubTheme.StyleSecondaryButton(_close);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 56,
            Padding = new Padding(16, 12, 16, 10)
        };
        buttons.Controls.Add(_import);
        buttons.Controls.Add(_close);
        buttons.Controls.Add(_back);

        Controls.Add(_filePanel);
        Controls.Add(_reviewPanel);
        Controls.Add(_resultPanel);
        Controls.Add(buttons);
        CancelButton = _close;
    }

    /// <summary>What the import did, once it has run.</summary>
    internal SettingsImportReport? Report { get; private set; }

    /// <summary>
    /// Which step is showing. Tracked rather than read back from the panels because a control
    /// reports itself invisible while its form has not been shown, which would make this
    /// untestable and any caller that asked subtly wrong.
    /// </summary>
    internal SettingsImportStep Step { get; private set; } = SettingsImportStep.ChooseFile;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_path is null && !PromptForFile())
        {
            // Cancelling the file picker means cancelling the import; there is nothing else this
            // window can offer.
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }

    internal bool PromptForFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import Settings",
            Filter = SettingsExportSerializer.FileFilter,
            DefaultExt = SettingsExportSerializer.FileExtension.TrimStart('.'),
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        LoadFile(dialog.FileName);
        return true;
    }

    internal void LoadFile(string path)
    {
        _path = path;
        var inspection = SettingsImportService.Inspect(path);
        var name = Path.GetFileName(path);
        if (inspection.Kind == SettingsFileKind.Unreadable)
        {
            ShowFileProblem(name, inspection.Message ?? "That file could not be read.");
            return;
        }

        _fileSummary.Text =
            $"{name}\r\n{inspection.SizeBytes:N0} bytes\r\n\r\n" +
            (inspection.Kind == SettingsFileKind.PasswordProtected
                ? "This file is protected with a password."
                : "This file is not password protected.");
        _needsPassword = inspection.Kind == SettingsFileKind.PasswordProtected;
        var needsPassword = _needsPassword;
        _passwordPrompt.Text = needsPassword ? "Enter the file's password:" : string.Empty;
        _passwordPrompt.ForeColor = StorageHubTheme.TextMuted;
        _passwordPrompt.Visible = needsPassword;
        _password.Visible = needsPassword;
        _open.Visible = true;
        ShowStep(_filePanel);
        if (needsPassword) _password.Focus();
    }

    private void OpenSelectedFile()
    {
        if (_path is null) return;
        var result = SettingsImportService.Open(_path, _needsPassword ? _password.Text : null);
        if (result.Document is null)
        {
            // Stays on this step so the password can simply be retyped. A wrong password and a
            // damaged file read the same, deliberately.
            _passwordPrompt.Text = result.Message ?? "That file could not be opened.";
            _passwordPrompt.ForeColor = StorageHubTheme.Warning;
            _passwordPrompt.Visible = true;
            _password.SelectAll();
            _password.Focus();
            return;
        }

        _document = result.Document;
        BuildReview(_document);
        ShowStep(_reviewPanel);
    }

    private void BuildReview(SettingsExportDocument document)
    {
        var present = document.PresentSections();
        _reviewHeading.Text =
            $"Exported {document.CreatedUtc.ToLocalTime():g} by {document.Application}\r\n" +
            (SettingsExportFingerprint.MatchesThisMachine(document.MachineFingerprint)
                ? "Written on this computer."
                : "Written on another computer.");

        _sectionList.Controls.Clear();
        _sections.Clear();
        foreach (var section in SettingsSectionCatalog.Sections)
        {
            var inFile = present.Contains(section.Id);
            var check = new CheckBox
            {
                Text = section.Label,
                AutoSize = true,
                Enabled = inFile,
                // Ticked only where the file has something and the section is on by default, so
                // "this computer only" still has to be asked for even when the file carries it.
                Checked = inFile && section.CheckedByDefault,
                Margin = new Padding(0, 6, 0, 0),
                AccessibleName = section.Label
            };
            _sections[section.Id] = check;
            _sectionList.Controls.Add(check);
            _sectionList.Controls.Add(new Label
            {
                Text = inFile ? DescribeContents(document, section) : "Not in this file.",
                AutoSize = true,
                MaximumSize = new Size(580, 0),
                ForeColor = StorageHubTheme.TextMuted,
                Margin = new Padding(20, 0, 0, 4)
            });
        }

        var hasAgentItems = SettingsSectionCatalog.Sections
            .Any(section => section.RequiresAgent && present.Contains(section.Id));
        _conflictLabel.Visible = hasAgentItems;
        _conflictPolicy.Visible = hasAgentItems;
        _concurrencyNotice.Visible = document.DesktopGeneral is not null;
        _concurrencyNotice.Text = _concurrencyNotice.Visible
            ? "Transfer concurrency changes take effect after the background agent restarts."
            : string.Empty;
    }

    private static string DescribeContents(SettingsExportDocument document, SettingsSectionDefinition section)
    {
        var count = document.CountIn(section.Id);
        return section.Id switch
        {
            SettingsSectionId.DesktopGeneral => "Replaces appearance, concurrency, editing and update preferences.",
            SettingsSectionId.MachineSpecific => "Replaces the editor path and the pinned and recent workspaces.",
            SettingsSectionId.Shortcuts => $"Replaces {count} keyboard shortcuts.",
            SettingsSectionId.ConnectionDefaults => $"Replaces {count} connection default values.",
            _ => count == 1 ? "1 item." : $"{count} items."
        };
    }

    internal async Task ApplyImportAsync()
    {
        if (_document is null) return;
        _import.Enabled = false;
        try
        {
            Report = await _importer.ApplyAsync(
                _document,
                [.. _sections.Where(pair => pair.Value is { Checked: true, Enabled: true }).Select(pair => pair.Key)],
                _conflictPolicy.SelectedItem is SettingsConflictPolicy chosen
                    ? chosen
                    : SettingsConflictPolicy.Skip).ConfigureAwait(true);
            BuildResult(Report);
            ShowStep(_resultPanel);
            // Deliberately not setting DialogResult here: assigning it to a form shown with
            // ShowDialog closes the form at once, and the result screen would never be seen. The
            // Close button carries the result instead.
        }
        finally
        {
            _import.Enabled = true;
        }
    }

    private void BuildResult(SettingsImportReport report)
    {
        var lines = new List<string>();
        if (report.Failure is { } failure)
        {
            lines.Add(failure);
        }
        else if (report.Applied.Count == 0)
        {
            lines.Add("Nothing was imported.");
        }
        else
        {
            lines.Add("Imported:");
            lines.AddRange(report.Applied
                .OrderBy(id => (int)id)
                .Select(id => $"    {SettingsSectionCatalog.Get(id).Label}"));
        }

        if (report.Blocked.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Not imported:");
            lines.AddRange(report.Blocked
                .OrderBy(pair => (int)pair.Key)
                .Select(pair => $"    {SettingsSectionCatalog.Get(pair.Key).Label} — {pair.Value}"));
        }

        if (report.Agent is { Count: > 0 } agent)
        {
            foreach (var (title, items) in new[]
            {
                ("Connections", agent.Connections),
                ("Sync tasks", agent.SyncProfiles),
                ("Schedules", agent.Schedules)
            })
            {
                if (items.Count == 0) continue;
                lines.Add(string.Empty);
                lines.Add($"{title}:");
                lines.AddRange(items.Select(item => $"    {item.Name} — {item.Result}"));
            }
        }

        if (report.NeedsCredentials.Count > 0)
        {
            // Said plainly here rather than left to be discovered the first time one of these
            // fails to connect.
            lines.Add(string.Empty);
            lines.Add("These connections need their credentials entered before they will work:");
            lines.AddRange(report.NeedsCredentials.Select(name => $"    {name}"));
        }

        if (report.ConcurrencyChanged)
        {
            lines.Add(string.Empty);
            lines.Add("Transfer concurrency changed. The background agent restarts to pick it up.");
        }

        _resultSummary.Text = string.Join(Environment.NewLine, lines);
        // Kept short so it fits on one line; the file name itself is a timestamp and a guid, and
        // goes in the tooltip rather than wrapping out of view.
        _backupLink.Visible = report.BackupPath is not null;
        _backupLink.Text = report.BackupPath is null
            ? string.Empty
            : "Show the backup of your previous settings";
        _backupLink.AccessibleDescription = report.BackupPath ?? string.Empty;
        // The one way out of the result screen, carrying whether anything actually changed so the
        // shell knows whether to refresh itself.
        _close.Text = "Close";
        _close.DialogResult = report.Succeeded && report.Applied.Count > 0
            ? DialogResult.OK
            : DialogResult.Cancel;
        _import.Visible = false;
        _back.Visible = false;
    }

    private void ShowBackup()
    {
        if (Report?.BackupPath is not { } path) return;
        try
        {
            using var explorer = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                {
                    UseShellExecute = true
                });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or
            InvalidOperationException or System.IO.FileNotFoundException)
        {
            _ = MessageBox.Show(
                this, path, "Backup location", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void ShowFileProblem(string name, string message)
    {
        _fileSummary.Text = $"{name}\r\n\r\n{message}";
        _needsPassword = false;
        _passwordPrompt.Visible = false;
        _password.Visible = false;
        _open.Visible = false;
        ShowStep(_filePanel);
    }

    private void ShowStep(Panel step)
    {
        Step = step == _reviewPanel
            ? SettingsImportStep.Review
            : step == _resultPanel
                ? SettingsImportStep.Result
                : SettingsImportStep.ChooseFile;
        _filePanel.Visible = step == _filePanel;
        _reviewPanel.Visible = step == _reviewPanel;
        _resultPanel.Visible = step == _resultPanel;
        _back.Visible = step == _reviewPanel;
        _import.Visible = step == _reviewPanel;
    }

    private static Panel NewPanel() => new()
    {
        Dock = DockStyle.Fill,
        Padding = new Padding(18, 16, 18, 8),
        BackColor = StorageHubTheme.Canvas
    };
}
