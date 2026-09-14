using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

/// <summary>Which editor tab a caller wants opened.</summary>
public enum ConnectionEditorTab
{
    General = 0,
    Authentication = 1,
    Trust = 2
}

/// <summary>A request to open the connection editor, on a saved connection or on a new one.</summary>
internal sealed record ConnectionEditRequest(Guid? ConnectionId, ConnectionEditorTab Tab);

/// <summary>
/// The foot of the connections panel: what the selected connection is, and what can be done about it.
///
/// Facts are grouped key/value rows — Server, Authentication, Security, Transfer, Organisation,
/// Status — because a saved connection is mostly a pile of settings, and a flat list of them reads
/// as noise. Only rows that carry a value are drawn, so a Local profile does not show five empty
/// S3 fields.
///
/// It renders in two passes. The listing carries the name, folder, tags and health, so those appear
/// the instant a row is clicked; the host, port and authentication method live only on the full
/// profile, which is fetched separately and folded in when it arrives. Blocking selection on that
/// fetch would put an IPC round trip on every arrow key.
/// </summary>
internal sealed class ConnectionDetailView : Panel
{
    private const int KeyWidth = 104;

    private readonly Label _empty;
    private readonly Panel _body;
    private readonly Label _name;
    private readonly Button _favorite;
    private readonly Panel _badge;
    private readonly Panel _scroll;
    private readonly FlowLayoutPanel _facts;
    private readonly Button _attention;
    private readonly Button _open;
    private readonly Button _test;
    private readonly ToolTip _valueTips = new();

    private ConnectionSummary? _connection;
    private ConnectionProfileDocument? _profile;
    private string? _pendingStatus;
    private ConnectionEditorTab _attentionTab = ConnectionEditorTab.General;

    internal ConnectionDetailView()
    {
        Padding = new Padding(12, 10, 12, 10);
        BackColor = StorageHubTheme.SurfaceMuted;
        AccessibleName = "Connection details";

        _empty = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = StorageHubTheme.TextMuted,
            Text = "Select a connection to see its details."
        };

        _badge = new Panel { Dock = DockStyle.Left, Width = 40, BackColor = StorageHubTheme.SurfaceMuted };
        _badge.Paint += PaintBadge;

        _name = new Label
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Font = StorageHubTheme.CreateSectionFont(),
            ForeColor = StorageHubTheme.Text
        };

        _favorite = new Button
        {
            Dock = DockStyle.Right,
            Width = 34,
            Text = "★",
            FlatStyle = FlatStyle.Flat,
            AccessibleName = "Toggle favorite"
        };
        StorageHubTheme.StyleInlineButton(_favorite);
        _favorite.Click += (_, _) => FavoriteToggleRequested?.Invoke(this, EventArgs.Empty);

        var title = new Panel { Dock = DockStyle.Top, Height = 34 };
        title.Controls.Add(_name);
        title.Controls.Add(_favorite);
        title.Controls.Add(_badge);

        _facts = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Location = Point.Empty,
            Margin = Padding.Empty,
            Padding = new Padding(0, 4, 0, 4)
        };
        _scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = StorageHubTheme.SurfaceMuted };
        _scroll.ClientSizeChanged += (_, _) => ResizeRows();
        _scroll.Controls.Add(_facts);

        _attention = new Button
        {
            Dock = DockStyle.Bottom,
            Height = 28,
            Visible = false,
            Margin = new Padding(0, 4, 0, 4)
        };
        StorageHubTheme.StyleSecondaryButton(_attention);
        _attention.ForeColor = StorageHubTheme.Warning;
        _attention.Click += (_, _) => EditRequested?.Invoke(this, _attentionTab);

        _open = CreateAction("Open", (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty));
        _test = CreateAction("Test", (_, _) => TestRequested?.Invoke(this, EventArgs.Empty));
        var edit = CreateAction("Edit", (_, _) => EditRequested?.Invoke(this, ConnectionEditorTab.General));
        var delete = CreateAction("Delete", (_, _) => DeleteRequested?.Invoke(this, EventArgs.Empty));
        StorageHubTheme.StyleDangerButton(delete);

        // Wraps rather than clips: the panel resizes down to a narrow column, and a fixed row of
        // buttons silently loses the last one off the right edge.
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = Padding.Empty,
            Padding = new Padding(0, 4, 0, 0)
        };
        actions.Controls.AddRange([_open, _test, edit, delete]);

        _body = new Panel { Dock = DockStyle.Fill, Visible = false, BackColor = StorageHubTheme.SurfaceMuted };
        _body.Controls.Add(_scroll);
        _body.Controls.Add(_attention);
        _body.Controls.Add(actions);
        _body.Controls.Add(title);

        Controls.Add(_body);
        Controls.Add(_empty);
    }

    internal event EventHandler? OpenRequested;

    internal event EventHandler? TestRequested;

    internal event EventHandler<ConnectionEditorTab>? EditRequested;

    internal event EventHandler? DeleteRequested;

    internal event EventHandler? FavoriteToggleRequested;

    /// <summary>First pass: everything the listing already knows, drawn immediately.</summary>
    internal void Show(ConnectionSummary? connection)
    {
        _connection = connection;
        _profile = null;
        _pendingStatus = null;
        _empty.Visible = connection is null;
        _body.Visible = connection is not null;
        if (connection is null)
        {
            return;
        }

        var descriptor = ConnectionProviderCatalog.Get(ConnectionCardFactory.MapProvider(connection.Provider));
        _name.Text = connection.DisplayName;
        _name.AccessibleName = $"{connection.DisplayName}, {descriptor.DisplayName}";
        _favorite.Text = connection.IsFavorite ? "★" : "☆";
        _favorite.ForeColor = connection.IsFavorite ? StorageHubTheme.Warning : StorageHubTheme.TextMuted;
        _favorite.AccessibleDescription = connection.IsFavorite
            ? $"{connection.DisplayName} is a favorite. Activate to remove it."
            : $"Make {connection.DisplayName} a favorite.";
        _open.Enabled = connection.IsEnabled;
        _test.Enabled = connection.IsEnabled;
        _badge.Invalidate();
        ApplyAttention(connection);
        Rebuild();
    }

    /// <summary>Second pass: the endpoint and authentication detail, once the profile has loaded.</summary>
    internal void ShowProfile(Guid connectionId, ConnectionProfileDocument? profile)
    {
        // The selection may have moved on while this was in flight.
        if (_connection?.ConnectionId != connectionId)
        {
            return;
        }

        _profile = profile;
        Rebuild();
    }

    /// <summary>Shown while a test is in flight, so a slow agent does not look like a dead button.</summary>
    internal void ShowTesting()
    {
        _pendingStatus = "Testing…";
        _test.Enabled = false;
        Rebuild();
    }

    private void Rebuild()
    {
        if (_connection is not { } connection)
        {
            return;
        }

        _scroll.SuspendLayout();
        _facts.SuspendLayout();
        try
        {
            foreach (var control in _facts.Controls.Cast<Control>().ToArray())
            {
                control.Dispose();
            }

            _facts.Controls.Clear();

            var descriptor = ConnectionProviderCatalog.Get(ConnectionCardFactory.MapProvider(connection.Provider));
            var endpoint = _profile?.Draft.Endpoint;
            var auth = _profile?.Draft.Authentication;
            var options = _profile?.Draft.OperationalOptions;

            AddSection("Server");
            AddFact("Provider", descriptor.DisplayName);
            if (endpoint is null)
            {
                AddFact("Address", "Loading…", muted: true);
            }
            else
            {
                AddFact("Host", endpoint.Host);
                AddFact("Port", endpoint.Port?.ToString(System.Globalization.CultureInfo.CurrentCulture));
                AddFact("Bucket", endpoint.Bucket);
                AddFact("Region", endpoint.Region);
                AddFact("Service", endpoint.ServiceEndpoint);
                AddFact("Path style", endpoint.ForcePathStyle ? "Forced" : null);
                AddFact("Path", endpoint.RootPath);
            }

            AddSection("Authentication");
            if (auth is null)
            {
                AddFact("Method", "Loading…", muted: true);
            }
            else
            {
                AddFact("Method", DescribeAuthentication(auth.Kind));
                AddFact("Username", auth.Username);
                AddFact(
                    "Key format",
                    auth.Kind is ConnectionAuthenticationKind.SftpPrivateKey
                        or ConnectionAuthenticationKind.SshPrivateKeyPassword
                        ? auth.PrivateKeyFormat.ToString()
                        : null);

                // Presence, not the handle: the vault reference is noise here, and the fact worth
                // reading is simply whether the credential has been enrolled.
                AddFact("Password", Vaulted(auth.PasswordReference));
                AddFact("Access key", Vaulted(auth.AccessKeyReference));
                AddFact("Secret key", Vaulted(auth.SecretKeyReference));
                AddFact("Session token", Vaulted(auth.SessionTokenReference));
                AddFact("Private key", Vaulted(auth.PrivateKeyReference));
                AddFact("Key passphrase", Vaulted(auth.PrivateKeyPassphraseReference));
            }

            if (endpoint is not null && DescribeSecurity(endpoint) is { Count: > 0 } security)
            {
                AddSection("Security");
                foreach (var row in security)
                {
                    AddFact(row.Key, row.Value);
                }
            }

            if (options is not null)
            {
                AddSection("Transfer");
                AddFact("Connect timeout", $"{options.ConnectTimeoutSeconds}s");
                AddFact("Operation timeout", $"{options.OperationTimeoutSeconds}s");
                AddFact(
                    "Retries",
                    options.MaximumRetryAttempts.ToString(System.Globalization.CultureInfo.CurrentCulture));
                AddFact("Proxy", options.ProxyEndpoint);
                AddFact("Upload limit", DescribeRate(options.UploadBytesPerSecond));
                AddFact("Download limit", DescribeRate(options.DownloadBytesPerSecond));
                AddFact("Encoding", options.EncodingName);
            }

            AddSection("Organisation");
            AddFact("Folder", string.IsNullOrWhiteSpace(connection.FolderPath) ? "Unsorted" : connection.FolderPath);
            AddFact("Tags", connection.Tags.Length == 0 ? "None" : string.Join(" · ", connection.Tags));
            AddFact("Favorite", connection.IsFavorite ? "Yes" : "No");

            AddSection("Status");
            AddStatusFact(connection);
            if (connection.Health is { } health)
            {
                AddFact(
                    "Checked",
                    health.CheckedUtc.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture));
                AddFact("Round trip", $"{health.ElapsedMilliseconds:N0} ms");
                AddFact("Detail", health.Status);
            }

            ResizeRows();
        }
        finally
        {
            _facts.ResumeLayout(true);
            _scroll.ResumeLayout(true);
        }
    }

    private void AddSection(string title) => _facts.Controls.Add(new Label
    {
        Text = title,
        AutoSize = false,
        Height = 22,
        Margin = new Padding(0, _facts.Controls.Count == 0 ? 0 : 8, 0, 2),
        TextAlign = ContentAlignment.BottomLeft,
        ForeColor = StorageHubTheme.Primary,
        Font = new Font(Font, FontStyle.Bold),
        AccessibleRole = AccessibleRole.Grouping
    });

    private void AddFact(string key, string? value, bool muted = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var row = new Panel { Height = 18, Margin = new Padding(0, 0, 0, 2) };
        var valueLabel = new Label
        {
            Text = value,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            ForeColor = muted ? StorageHubTheme.TextMuted : StorageHubTheme.Text,

            // Read together, because the key label beside it is a sibling the screen reader would
            // otherwise announce as an unrelated line.
            AccessibleName = $"{key}: {value}"
        };
        _valueTips.SetToolTip(valueLabel, value);
        row.Controls.Add(valueLabel);
        row.Controls.Add(new Label
        {
            Text = key,
            Dock = DockStyle.Left,
            Width = KeyWidth,
            Margin = Padding.Empty,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            ForeColor = StorageHubTheme.TextMuted
        });
        _facts.Controls.Add(row);
    }

    private void AddStatusFact(ConnectionSummary connection)
    {
        if (_pendingStatus is { } pending)
        {
            AddFact("State", pending, muted: true);
            return;
        }

        if (!connection.IsEnabled)
        {
            AddFact("State", "Disabled");
            return;
        }

        AddFact("State", ConnectionCardFactory.DescribeHealth(connection.Health));
        if (_facts.Controls.Count > 0 &&
            _facts.Controls[^1] is Panel added &&
            added.Controls.OfType<Label>().FirstOrDefault(static label => label.Dock == DockStyle.Fill) is { } state)
        {
            state.ForeColor = connection.Health?.State switch
            {
                ConnectionHealthState.Healthy => StorageHubTheme.Success,
                ConnectionHealthState.NeedsAttention => StorageHubTheme.Warning,
                ConnectionHealthState.Unavailable => StorageHubTheme.Danger,
                _ => StorageHubTheme.TextMuted
            };
        }
    }

    private void ApplyAttention(ConnectionSummary connection)
    {
        // The two states a user can actually resolve get a route to the tab that resolves them.
        if (connection.IsEnabled && connection.Health is { RequiresCredentialAction: true })
        {
            _attentionTab = ConnectionEditorTab.Authentication;
            _attention.Text = "Fix credentials…";
            _attention.Visible = true;
        }
        else if (connection.IsEnabled && connection.Health is { RequiresTrustAction: true })
        {
            _attentionTab = ConnectionEditorTab.Trust;
            _attention.Text = "Review trust…";
            _attention.Visible = true;
        }
        else
        {
            _attention.Visible = false;
        }
    }

    /// <summary>
    /// The rows are docked panels inside a top-down flow, which does not stretch its children, so
    /// their width is set by hand whenever the scroll area changes.
    /// </summary>
    private void ResizeRows()
    {
        var width = Math.Max(
            120,
            _scroll.ClientSize.Width -
                (_scroll.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0));
        _facts.Width = width;
        foreach (Control row in _facts.Controls)
        {
            row.Width = width;
        }
    }

    private static List<KeyValuePair<string, string>> DescribeSecurity(ConnectionEndpointDocument endpoint)
    {
        var rows = new List<KeyValuePair<string, string>>();
        void Add(string key, string value) => rows.Add(new KeyValuePair<string, string>(key, value));

        switch (endpoint.Provider)
        {
            case StorageConnectionProvider.Ftps:
                Add("TLS", DescribeTls(endpoint.TlsPolicy));
                Add("FTPS mode", endpoint.FtpsTlsMode.ToString());
                if (endpoint.ClientCertificatePfxReference is not null)
                {
                    Add("Client cert", "Stored in vault");
                }

                break;
            case StorageConnectionProvider.Sftp:
            case StorageConnectionProvider.Ssh:
                Add(
                    "Host key",
                    endpoint.SshHostKeyPolicy == ConnectionSshHostKeyPolicy.Pinned
                        ? "Pinned"
                        : "Trust on first use");
                break;
            case StorageConnectionProvider.S3:
                Add("TLS", DescribeTls(endpoint.TlsPolicy));
                break;
        }

        if (endpoint.AllowInsecureTransport)
        {
            Add("Transport", "Unencrypted");
        }

        return rows;
    }

    private static string DescribeTls(ConnectionTlsCertificatePolicy policy) => policy switch
    {
        ConnectionTlsCertificatePolicy.SystemTrust => "System trust",
        ConnectionTlsCertificatePolicy.Pinned => "Pinned certificate",
        ConnectionTlsCertificatePolicy.TrustOnFirstUse => "Trust on first use",
        _ => "Unspecified"
    };

    private static string DescribeAuthentication(ConnectionAuthenticationKind kind) => kind switch
    {
        ConnectionAuthenticationKind.None => "Anonymous",
        ConnectionAuthenticationKind.S3DefaultCredentialChain => "AWS default credential chain",
        ConnectionAuthenticationKind.CredentialReference => "Stored credential",
        ConnectionAuthenticationKind.UsernamePassword => "Username and password",
        ConnectionAuthenticationKind.S3AccessKey => "Access key and secret",
        ConnectionAuthenticationKind.SftpPrivateKey => "Private key",
        ConnectionAuthenticationKind.SshPrivateKeyPassword => "Private key and password (MFA)",
        _ => kind.ToString()
    };

    private static string? Vaulted(string? reference) =>
        string.IsNullOrWhiteSpace(reference) ? null : "Stored in vault";

    private static string? DescribeRate(long? bytesPerSecond) => bytesPerSecond switch
    {
        null or <= 0 => null,
        < 1024 => $"{bytesPerSecond} B/s",
        < 1024 * 1024 => $"{bytesPerSecond / 1024d:N1} KB/s",
        _ => $"{bytesPerSecond / (1024d * 1024d):N1} MB/s"
    };

    private void PaintBadge(object? sender, PaintEventArgs e)
    {
        if (_connection is null)
        {
            return;
        }

        var descriptor = ConnectionProviderCatalog.Get(ConnectionCardFactory.MapProvider(_connection.Provider));
        var accent = StorageHubTheme.ParseAccent(_connection.AccentColor ?? descriptor.AccentHex);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var bounds = new RectangleF(0, 3, 36, 28);
        using var path = UiShapes.RoundedRectangle(bounds, 7);
        using var fill = new SolidBrush(accent);
        e.Graphics.FillPath(fill, path);
        TextRenderer.DrawText(
            e.Graphics,
            descriptor.ShortName,
            Font,
            Rectangle.Round(bounds),
            StorageHubTheme.ContrastText(accent),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private static Button CreateAction(string text, EventHandler onClick)
    {
        var button = new Button
        {
            Text = text,
            Width = 58,
            Height = 28,
            Margin = new Padding(0, 0, 4, 4),
            AccessibleName = $"{text} connection"
        };
        StorageHubTheme.StyleSecondaryButton(button);
        button.Click += onClick;
        return button;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _valueTips.Dispose();
        }

        base.Dispose(disposing);
    }
}
