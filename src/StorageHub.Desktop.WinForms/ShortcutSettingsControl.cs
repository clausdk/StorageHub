namespace StorageHub.Desktop;

internal sealed class ShortcutSettingsControl : UserControl
{
    private readonly DataGridView _commands;
    private readonly ShortcutCaptureBox _capture;
    private readonly Label _message;
    private Dictionary<string, Keys> _shortcuts;

    internal ShortcutSettingsControl(IReadOnlyDictionary<string, Keys>? shortcuts)
    {
        _shortcuts = ShortcutSettings.Resolve(shortcuts);
        Height = 460;
        Width = 700;
        Margin = Padding.Empty;
        AccessibleName = "Keyboard shortcut assignments";
        _commands = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
            AllowUserToDeleteRows = false, RowHeadersVisible = false, MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            AccessibleName = "Commands and shortcuts", BackgroundColor = StorageHubTheme.Surface
        };
        _commands.Columns.Add("Command", "Command");
        _commands.Columns.Add("Shortcut", "Shortcut");
        _commands.Columns.Add("Default", "Default");
        foreach (var command in ShortcutSettings.Commands)
        {
            var row = _commands.Rows[_commands.Rows.Add($"{command.Menu}: {command.Label}", ShortcutSettings.Format(_shortcuts[command.Id]), ShortcutSettings.Format(command.Shortcut))];
            row.Tag = command.Id;
        }
        _capture = new ShortcutCaptureBox { Width = 180, AccessibleName = "Press a new shortcut", PlaceholderText = "Press shortcut keys" };
        var assign = new Button { Text = "Assign", AutoSize = true };
        var clear = new Button { Text = "Clear", AutoSize = true };
        var reset = new Button { Text = "Restore defaults", AutoSize = true };
        foreach (var button in new[] { assign, clear, reset }) StorageHubTheme.StyleSecondaryButton(button);
        assign.Click += (_, _) => SetSelected(_capture.CapturedKeys);
        clear.Click += (_, _) => SetSelected(Keys.None);
        reset.Click += (_, _) => { _shortcuts = ShortcutSettings.Resolve(null); RefreshRows(); Changed?.Invoke(this, EventArgs.Empty); };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true, Padding = new Padding(0, 8, 0, 0) };
        actions.Controls.AddRange([_capture, assign, clear, reset]);
        _message = new Label { Dock = DockStyle.Bottom, Height = 48, AutoEllipsis = true, AccessibleName = "Shortcut assignment status" };
        _commands.SelectionChanged += (_, _) => { _capture.Reset(); _message.Text = "Select a command, press the new keys, then choose Assign."; };
        Controls.Add(_commands);
        Controls.Add(actions);
        Controls.Add(_message);
    }

    internal event EventHandler? Changed;
    internal Dictionary<string, Keys> ReadShortcuts() => new(_shortcuts, StringComparer.Ordinal);

    private void SetSelected(Keys keys)
    {
        if (_commands.CurrentRow?.Tag is not string id) return;
        var candidate = new Dictionary<string, Keys>(_shortcuts, StringComparer.Ordinal) { [id] = keys };
        if (ShortcutSettings.Validate(candidate) is { } error) { _message.Text = error; return; }
        _shortcuts = candidate;
        RefreshRows();
        _message.Text = "Shortcut updated. Choose Apply or OK to save.";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshRows()
    {
        foreach (DataGridViewRow row in _commands.Rows)
            if (row.Tag is string id) row.Cells[1].Value = ShortcutSettings.Format(_shortcuts[id]);
    }

    private sealed class ShortcutCaptureBox : TextBox
    {
        internal Keys CapturedKeys { get; private set; }
        internal ShortcutCaptureBox() { ReadOnly = true; }
        internal void Reset() { CapturedKeys = Keys.None; Text = string.Empty; }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData is Keys.Tab or (Keys.Shift | Keys.Tab) or Keys.Escape) return base.ProcessCmdKey(ref msg, keyData);
            CapturedKeys = keyData;
            Text = ShortcutSettings.Format(keyData);
            return true;
        }
    }
}
