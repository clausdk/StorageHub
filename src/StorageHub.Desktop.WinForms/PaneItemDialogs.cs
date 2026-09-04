using System.Buffers;
using System.Text.RegularExpressions;

namespace StorageHub.Desktop;

internal static partial class PaneItemNameRules
{
    private const int MaximumNameLength = 255;
    private static readonly SearchValues<char> InvalidCharacters = SearchValues.Create(['<', '>', ':', '"', '/', '\\', '|', '?', '*']);

    internal static string? Validate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Enter a name.";
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) || value.EndsWith('.'))
            return "Names cannot begin or end with spaces, or end with a period.";
        if (value.Length > MaximumNameLength) return $"Names cannot exceed {MaximumNameLength} characters.";
        if (value is "." or ".." || value.Any(char.IsControl) || value.AsSpan().ContainsAny(InvalidCharacters))
            return "The name contains characters that are not portable across storage providers.";
        var stem = value.Split('.')[0];
        if (ReservedWindowsName().IsMatch(stem)) return "That name is reserved by Windows.";
        return null;
    }

    [GeneratedRegex("^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedWindowsName();
}

internal sealed class PaneItemNameDialog : Form
{
    private readonly TextBox _name;
    private readonly Label _error;
    private readonly Button _accept;

    internal PaneItemNameDialog(string title, string prompt, string initialName, string acceptText)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(460, 175);
        BackColor = StorageHubTheme.Surface;
        ForeColor = StorageHubTheme.Text;
        Font = new Font("Segoe UI", 9F);
        var label = new Label { Text = prompt, Left = 20, Top = 18, Width = 415, Height = 22 };
        _name = new TextBox { Text = initialName, Left = 20, Top = 45, Width = 415, AccessibleName = prompt };
        _error = new Label { Left = 20, Top = 76, Width = 415, Height = 35, ForeColor = StorageHubTheme.Danger };
        _accept = new Button { Text = acceptText, DialogResult = DialogResult.OK, Left = 265, Top = 125, Width = 82 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 353, Top = 125, Width = 82 };
        StorageHubTheme.StylePrimaryButton(_accept);
        StorageHubTheme.StyleSecondaryButton(cancel);
        _name.TextChanged += (_, _) => ValidateName();
        Controls.AddRange([label, _name, _error, _accept, cancel]);
        AcceptButton = _accept;
        CancelButton = cancel;
        Shown += (_, _) => { _name.SelectAll(); _name.Focus(); };
        ValidateName();
        StorageHubTheme.Register(this);
        StorageHubTheme.Apply(this);
    }

    internal string ItemName => _name.Text;

    private void ValidateName()
    {
        _error.Text = PaneItemNameRules.Validate(_name.Text) ?? string.Empty;
        _accept.Enabled = _error.Text.Length == 0;
    }
}

internal sealed class BatchRenameDialog : Form
{
    private readonly IReadOnlyList<string> _sourceNames;
    private readonly HashSet<string> _occupiedNames;
    private readonly TextBox _find;
    private readonly TextBox _replace;
    private readonly ListBox _preview;
    private readonly Label _error;
    private readonly Button _accept;
    private IReadOnlyDictionary<string, string> _renameMap = new Dictionary<string, string>();

    internal BatchRenameDialog(IReadOnlyList<string> sourceNames, IEnumerable<string> occupiedNames)
    {
        _sourceNames = sourceNames;
        _occupiedNames = new HashSet<string>(occupiedNames, StringComparer.OrdinalIgnoreCase);
        Text = "Batch rename";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(620, 450);
        BackColor = StorageHubTheme.Surface;
        ForeColor = StorageHubTheme.Text;
        Font = new Font("Segoe UI", 9F);
        Controls.Add(new Label { Text = "Find", Left = 20, Top = 18, Width = 275 });
        Controls.Add(new Label { Text = "Replace with", Left = 315, Top = 18, Width = 280 });
        _find = new TextBox { Left = 20, Top = 42, Width = 275, AccessibleName = "Text to find" };
        _replace = new TextBox { Left = 315, Top = 42, Width = 280, AccessibleName = "Replacement text" };
        _preview = new ListBox { Left = 20, Top = 92, Width = 575, Height = 260, AccessibleName = "Rename preview" };
        _error = new Label { Left = 20, Top = 360, Width = 575, Height = 35, ForeColor = StorageHubTheme.Danger };
        _accept = new Button { Text = "Rename", DialogResult = DialogResult.OK, Left = 425, Top = 405, Width = 82 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 513, Top = 405, Width = 82 };
        StorageHubTheme.StylePrimaryButton(_accept);
        StorageHubTheme.StyleSecondaryButton(cancel);
        _find.TextChanged += (_, _) => UpdatePreview();
        _replace.TextChanged += (_, _) => UpdatePreview();
        Controls.AddRange([_find, _replace, _preview, _error, _accept, cancel]);
        AcceptButton = _accept;
        CancelButton = cancel;
        UpdatePreview();
        StorageHubTheme.Register(this);
        StorageHubTheme.Apply(this);
    }

    internal IReadOnlyDictionary<string, string> RenameMap => _renameMap;

    private void UpdatePreview()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? error = null;
        _preview.Items.Clear();
        foreach (var source in _sourceNames)
        {
            var target = _find.Text.Length == 0
                ? source
                : source.Replace(_find.Text, _replace.Text, StringComparison.OrdinalIgnoreCase);
            _preview.Items.Add(string.Equals(source, target, StringComparison.Ordinal) ? $"{source}  (unchanged)" : $"{source}  →  {target}");
            if (string.Equals(source, target, StringComparison.Ordinal)) continue;
            error ??= PaneItemNameRules.Validate(target);
            if (!targets.Add(target)) error ??= "Two selected items would receive the same name.";
            if (_occupiedNames.Contains(target) && !_sourceNames.Contains(target, StringComparer.OrdinalIgnoreCase))
                error ??= $"An item named ‘{target}’ already exists.";
            if (_sourceNames.Contains(target, StringComparer.OrdinalIgnoreCase) &&
                !string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                error ??= "A target name collides with another selected item. Rename those items separately.";
            map[source] = target;
        }
        if (_find.Text.Length == 0) error = "Enter text to find in the selected names.";
        else if (map.Count == 0) error = "None of the selected names contain that text.";
        _renameMap = map;
        _error.Text = error ?? $"{map.Count:N0} item(s) will be renamed. Processing stops if a provider rejects a change.";
        _error.ForeColor = error is null ? StorageHubTheme.TextMuted : StorageHubTheme.Danger;
        _accept.Enabled = error is null;
    }
}
