namespace StorageHub.Desktop;

internal sealed class DeleteItemsConfirmationForm : Form
{
    private readonly CheckBox _doNotShowAgain;

    internal DeleteItemsConfirmationForm(IReadOnlyList<PaneTransferItem> items, bool local)
    {
        ArgumentNullException.ThrowIfNull(items);
        Text = "Review delete";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(520, 330);
        BackColor = StorageHubTheme.Surface;
        ForeColor = StorageHubTheme.Text;
        Font = new Font("Segoe UI", 9F);

        var preview = string.Join(Environment.NewLine, items.Take(6).Select(static item => $"• {item.Name}"));
        if (items.Count > 6) preview += $"{Environment.NewLine}• …and {items.Count - 6:N0} more";
        var message = new Label
        {
            Left = 24,
            Top = 22,
            Width = 470,
            Height = 190,
            Text = $"Delete {items.Count:N0} selected item(s)?\n\n{preview}\n\n" +
                (local
                    ? "Local items will be sent to the Windows Recycle Bin."
                    : "Remote deletion may be permanent and cannot be undone."),
            AccessibleName = "Delete review"
        };
        _doNotShowAgain = new CheckBox
        {
            Left = 24,
            Top = 225,
            Width = 300,
            Text = "Don't show this warning again",
            AccessibleName = "Don't show delete warning again"
        };
        var delete = new Button
        {
            Text = "Delete",
            DialogResult = DialogResult.OK,
            Left = 326,
            Top = 270,
            Width = 82
        };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Left = 414,
            Top = 270,
            Width = 82
        };
        StorageHubTheme.StylePrimaryButton(delete);
        StorageHubTheme.StyleSecondaryButton(cancel);
        Controls.AddRange([message, _doNotShowAgain, delete, cancel]);
        AcceptButton = delete;
        CancelButton = cancel;
        StorageHubTheme.Register(this);
        StorageHubTheme.Apply(this);
    }

    internal bool DoNotShowAgain => _doNotShowAgain.Checked;
}
