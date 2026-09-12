using System.Globalization;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

/// <summary>
/// Chooses an already-imported key or certificate for a connection slot. The list is pre-filtered to
/// the kind the slot accepts, so a certificate can never be offered where an SSH key is required.
/// Only metadata is shown; the material itself never leaves the vault.
/// </summary>
internal sealed class KeyStorePickerForm : Form
{
    private readonly ListView _entries;

    public KeyStorePickerForm(IReadOnlyList<KeyStoreEntryDocument> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        Text = "Choose from Key Store";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        MinimumSize = new Size(640, 360);
        ClientSize = new Size(720, 400);
        BackColor = StorageHubTheme.Canvas;
        ForeColor = StorageHubTheme.Text;

        _entries = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            BorderStyle = BorderStyle.None,
            BackColor = StorageHubTheme.Surface,
            ForeColor = StorageHubTheme.Text,
            AccessibleName = "Stored keys and certificates"
        };
        _entries.Columns.Add("Name", 200);
        _entries.Columns.Add("Identity", 300);
        _entries.Columns.Add("Expires", 110);
        _entries.Columns.Add("Used by", 70, HorizontalAlignment.Right);
        StorageHubTheme.ConfigureList(_entries);

        foreach (var entry in entries)
        {
            var item = new ListViewItem(entry.DisplayName) { Tag = entry };
            item.SubItems.Add(entry.Summary.Subject ?? entry.Summary.Sha256Fingerprint ?? "-");
            var expiry = item.SubItems.Add(DescribeExpiry(entry, out var severity));
            expiry.ForeColor = severity;
            item.SubItems.Add(entry.ReferencedByProfiles.Length.ToString(CultureInfo.CurrentCulture));
            _entries.Items.Add(item);
        }

        if (_entries.Items.Count > 0)
        {
            _entries.Items[0].Selected = true;
        }

        var use = new Button
        {
            Text = "Use",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            Margin = new Padding(0, 0, 8, 0)
        };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        StorageHubTheme.StylePrimaryButton(use);
        StorageHubTheme.StyleSecondaryButton(cancel);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 48,
            Padding = new Padding(10, 8, 10, 8),
            BackColor = StorageHubTheme.Canvas
        };
        actions.Controls.Add(cancel);
        actions.Controls.Add(use);

        var body = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 10, 10, 0),
            BackColor = StorageHubTheme.Canvas
        };
        body.Controls.Add(_entries);

        Controls.Add(body);
        Controls.Add(actions);
        AcceptButton = use;
        CancelButton = cancel;
        _entries.DoubleClick += (_, _) =>
        {
            if (Selected is not null)
            {
                DialogResult = DialogResult.OK;
                Close();
            }
        };
    }

    public KeyStoreEntryDocument? Selected => _entries.SelectedItems.Count == 1
        ? _entries.SelectedItems[0].Tag as KeyStoreEntryDocument
        : null;

    /// <summary>
    /// Certificates carry a hard expiry that silently breaks a connection once it passes, so the
    /// picker warns before one is chosen. SSH keys have no expiry to report.
    /// </summary>
    internal static string DescribeExpiry(KeyStoreEntryDocument entry, out Color severity)
    {
        severity = StorageHubTheme.TextMuted;
        if (entry.Summary.NotAfter is not { } notAfter)
        {
            return "-";
        }

        var remaining = notAfter - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            severity = StorageHubTheme.Danger;
            return "Expired";
        }

        severity = remaining <= TimeSpan.FromDays(30) ? StorageHubTheme.Warning : StorageHubTheme.TextMuted;
        return notAfter.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
    }
}
