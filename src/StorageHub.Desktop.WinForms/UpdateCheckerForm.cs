namespace StorageHub.Desktop;

/// <summary>
/// A single place to see the installed version, check for a newer one, watch it download, and
/// install it.
///
/// The manual check used to be a chain of message boxes with no feedback: the check and the
/// download both blocked with nothing on screen, and a download of a hundred-odd megabytes looked
/// like the app had hung. The updater already publishes progress through its status events; this
/// shows it, and keeps every step of the flow in one window that can be cancelled.
/// </summary>
internal sealed class UpdateCheckerForm : Form
{
    private readonly DesktopUpdater _updater;
    private readonly Label _headline;
    private readonly Label _detail;
    private readonly Label _installed;
    private readonly Label _channel;
    private readonly ProgressBar _progress;
    private readonly Button _primary;
    private readonly Button _close;
    private CancellationTokenSource _operation = new();
    private bool _closing;

    public UpdateCheckerForm(DesktopUpdater updater)
    {
        _updater = updater ?? throw new ArgumentNullException(nameof(updater));

        Text = "StorageHub Updates";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(520, 250);
        BackColor = StorageHubTheme.Canvas;
        ForeColor = StorageHubTheme.Text;

        _headline = new Label
        {
            Dock = DockStyle.Top,
            Height = 30,
            Font = StorageHubTheme.CreateSectionFont(),
            ForeColor = StorageHubTheme.Text
        };
        _detail = new Label
        {
            Dock = DockStyle.Top,
            Height = 52,
            ForeColor = StorageHubTheme.TextMuted,
            AccessibleName = "Update detail"
        };
        _progress = new ProgressBar
        {
            Dock = DockStyle.Top,
            Height = 10,
            Minimum = 0,
            Maximum = 100,
            Visible = false,
            AccessibleName = "Update download progress"
        };
        _installed = new Label { Dock = DockStyle.Top, Height = 24, ForeColor = StorageHubTheme.TextMuted };
        _channel = new Label { Dock = DockStyle.Top, Height = 24, ForeColor = StorageHubTheme.TextMuted };

        _primary = new Button { Text = "Check for updates", AutoSize = true, Height = 30, Margin = new Padding(0, 0, 8, 0) };
        _close = new Button { Text = "Close", AutoSize = true, Height = 30, DialogResult = DialogResult.Cancel };
        StorageHubTheme.StylePrimaryButton(_primary);
        StorageHubTheme.StyleSecondaryButton(_close);
        _primary.Click += async (_, _) => await PrimaryClickedAsync().ConfigureAwait(true);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(14, 8, 14, 8),
            BackColor = StorageHubTheme.Canvas
        };
        actions.Controls.AddRange([_primary, _close]);

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 14, 14, 0) };
        // Docked children stack in reverse order of addition.
        body.Controls.Add(_channel);
        body.Controls.Add(_installed);
        body.Controls.Add(_progress);
        body.Controls.Add(_detail);
        body.Controls.Add(_headline);

        Controls.Add(body);
        Controls.Add(actions);
        CancelButton = _close;

        _updater.StatusChanged += UpdaterStatusChanged;
        Render(_updater.Snapshot);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closing = true;
        _updater.StatusChanged -= UpdaterStatusChanged;
        _operation.Cancel();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _operation.Dispose();
        base.OnFormClosed(e);
    }

    private void UpdaterStatusChanged(object? sender, DesktopUpdateSnapshot snapshot)
    {
        if (_closing || IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(new Action(() =>
            {
                if (!_closing && !IsDisposed)
                {
                    Render(snapshot);
                }
            }));
        }
        catch (InvalidOperationException)
        {
            // The handle can disappear between the guard and BeginInvoke while closing.
        }
    }

    private async Task PrimaryClickedAsync()
    {
        var action = NextAction(_updater.Snapshot.State);
        _operation.Dispose();
        _operation = new CancellationTokenSource();
        try
        {
            switch (action)
            {
                case UpdateAction.Check:
                    await _updater.CheckForUpdatesAsync(_operation.Token).ConfigureAwait(true);
                    break;
                case UpdateAction.Download:
                    await _updater.DownloadAvailableAsync(_operation.Token).ConfigureAwait(true);
                    break;
                case UpdateAction.Restart:
                    if (!_updater.ApplyAndRestart())
                    {
                        Render(_updater.Snapshot with
                        {
                            State = DesktopUpdateState.Failed,
                            Message = "StorageHub could not start the updater. Reopen the application and try again."
                        });
                    }

                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Closing the window cancels in-flight work; nothing to report.
        }
        catch (Exception error) when (error is IOException or TimeoutException or
            InvalidOperationException or UnauthorizedAccessException or HttpRequestException)
        {
            Render(_updater.Snapshot with
            {
                State = DesktopUpdateState.Failed,
                Message = $"The update check failed: {error.Message}"
            });
        }
    }

    private void Render(DesktopUpdateSnapshot snapshot)
    {
        _installed.Text = "Installed version: " + DesktopApplicationVersion.Current;
        _channel.Text = _updater.Preferences.IncludePrereleases
            ? "Channel: stable releases and release candidates"
            : "Channel: stable releases only";

        _headline.Text = DescribeHeadline(snapshot);
        _headline.ForeColor = snapshot.State switch
        {
            DesktopUpdateState.ReadyToRestart => StorageHubTheme.Success,
            DesktopUpdateState.UpToDate => StorageHubTheme.Success,
            DesktopUpdateState.UpdateAvailable => StorageHubTheme.Warning,
            DesktopUpdateState.Failed => StorageHubTheme.Danger,
            _ => StorageHubTheme.Text
        };
        _detail.Text = DescribeDetail(snapshot);

        var downloading = snapshot.State == DesktopUpdateState.Downloading;
        _progress.Visible = downloading;
        if (downloading)
        {
            _progress.Value = Math.Clamp(snapshot.ProgressPercent ?? 0, 0, 100);
        }

        var action = NextAction(snapshot.State);
        _primary.Text = action switch
        {
            UpdateAction.Download => "Download update",
            UpdateAction.Restart => "Restart and install",
            UpdateAction.None => "Working…",
            _ => "Check for updates"
        };
        _primary.Enabled = action != UpdateAction.None;
        _close.Text = snapshot.State == DesktopUpdateState.Downloading ? "Cancel" : "Close";
    }

    /// <summary>
    /// What the one action button should do next. Keeping this a pure function of state is what
    /// lets the window drive check, download, and install without a separate wizard for each.
    /// </summary>
    internal static UpdateAction NextAction(DesktopUpdateState state) => state switch
    {
        DesktopUpdateState.UpdateAvailable => UpdateAction.Download,
        DesktopUpdateState.ReadyToRestart => UpdateAction.Restart,
        DesktopUpdateState.Checking or DesktopUpdateState.Downloading or
            DesktopUpdateState.Installing => UpdateAction.None,
        DesktopUpdateState.Disabled or DesktopUpdateState.Unavailable => UpdateAction.None,
        _ => UpdateAction.Check
    };

    internal static string DescribeHeadline(DesktopUpdateSnapshot snapshot) => snapshot.State switch
    {
        DesktopUpdateState.Checking => "Checking for updates…",
        DesktopUpdateState.UpdateAvailable => $"StorageHub {snapshot.Version} is available",
        DesktopUpdateState.Downloading => $"Downloading StorageHub {snapshot.Version}…",
        DesktopUpdateState.ReadyToRestart => $"StorageHub {snapshot.Version} is ready to install",
        DesktopUpdateState.Installing => "Installing…",
        DesktopUpdateState.UpToDate => "StorageHub is up to date",
        DesktopUpdateState.Unavailable => "Updates are not available for this build",
        DesktopUpdateState.Disabled => "Automatic updates are turned off",
        DesktopUpdateState.Failed => "The update could not be completed",
        _ => "Updates"
    };

    internal static string DescribeDetail(DesktopUpdateSnapshot snapshot) => snapshot.State switch
    {
        DesktopUpdateState.UpdateAvailable =>
            "The release has not been downloaded yet. Downloading does not change the installed version.",
        DesktopUpdateState.ReadyToRestart =>
            "The download is integrity-checked. Restarting installs it silently; durable queued work is preserved.",
        DesktopUpdateState.UpToDate =>
            "No newer release was found on the selected channel.",
        DesktopUpdateState.Unavailable =>
            "Portable and developer builds are never modified. Only an installed StorageHub can update itself.",
        DesktopUpdateState.Disabled =>
            "Automatic checks are disabled in Settings. You can still check manually here.",
        DesktopUpdateState.Failed => snapshot.Message,
        DesktopUpdateState.Idle => "Check whether a newer StorageHub release is available.",
        _ => snapshot.Message
    };
}

internal enum UpdateAction
{
    Check = 1,
    Download = 2,
    Restart = 3,
    None = 4
}
