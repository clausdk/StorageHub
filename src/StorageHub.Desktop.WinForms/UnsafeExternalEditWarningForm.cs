namespace StorageHub.Desktop;

internal readonly record struct UnsafeExternalEditDecision(bool Continue, bool DontShowAgain);

internal sealed class UnsafeExternalEditWarningForm : Form
{
    private readonly CheckBox _dontShowAgain;
    private readonly Bitmap _warningImage;

    private UnsafeExternalEditWarningForm(string fileName)
    {
        Text = "Unprotected external editing";
        AccessibleName = "Unprotected external editing warning";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(560, 260);
        BackColor = StorageHubTheme.Canvas;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        StorageHubTheme.Register(this);

        _warningImage = SystemIcons.Warning.ToBitmap();
        var icon = new PictureBox
        {
            Image = _warningImage,
            SizeMode = PictureBoxSizeMode.CenterImage,
            Size = new Size(54, 54),
            Margin = new Padding(0, 2, 14, 0),
            AccessibleName = "Warning"
        };
        var message = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(440, 0),
            Text = $"StorageHub cannot protect '{fileName}' with a remote version or ETag check.\n\n" +
                "If you continue, the file will be downloaded and uploaded without change protection. " +
                "A newer remote version could be overwritten.",
            ForeColor = StorageHubTheme.Text
        };
        _dontShowAgain = new CheckBox
        {
            AutoSize = true,
            Text = "Don't show this warning again",
            AccessibleDescription = "You can restore this warning later in Settings under Editing."
        };

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(20, 20, 20, 8),
            BackColor = StorageHubTheme.Surface
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        content.Controls.Add(icon, 0, 0);
        content.Controls.Add(message, 1, 0);
        content.Controls.Add(_dontShowAgain, 1, 1);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 62,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(12, 12, 12, 8),
            BackColor = StorageHubTheme.SurfaceMuted
        };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        StorageHubTheme.StyleSecondaryButton(cancel);
        var continueButton = new Button { Text = "Continue anyway", DialogResult = DialogResult.OK };
        StorageHubTheme.StylePrimaryButton(continueButton);
        footer.Controls.Add(continueButton);
        footer.Controls.Add(cancel);

        Controls.Add(content);
        Controls.Add(footer);
        AcceptButton = continueButton;
        CancelButton = cancel;
        StorageHubTheme.Apply(this);
    }

    internal static UnsafeExternalEditDecision Ask(IWin32Window owner, string fileName)
    {
        using var dialog = new UnsafeExternalEditWarningForm(fileName);
        var result = dialog.ShowDialog(owner);
        return new UnsafeExternalEditDecision(
            result == DialogResult.OK,
            result == DialogResult.OK && dialog._dontShowAgain.Checked);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _warningImage.Dispose();
        }

        base.Dispose(disposing);
    }
}
