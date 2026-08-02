using System.Security.Cryptography;
using System.Text;
using Krypton.Toolkit;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

public sealed class ConnectionManagerForm : KryptonForm
{
    private readonly List<Image> _ownedImages = [];
    private readonly ListBox _profileCards;
    private readonly TextBox _searchBox;
    private readonly ComboBox _providerSelector;
    private readonly Label _providerSummary;
    private readonly Panel _providerAccent;
    private readonly Label _testState;
    private readonly TabPage _generalPage;
    private readonly TabPage _authenticationPage;
    private readonly TabPage _securityPage;
    private readonly List<ConnectionCardModel> _allCards;
    private readonly Dictionary<string, Control> _editorFields = new(StringComparer.Ordinal);
    private readonly IRemoteStorageAgentClient _storageClient;
    private readonly IRemoteConnectionProfileClient _profileClient;
    private readonly IRemoteSecretVaultClient _secretClient;
    private readonly ConnectionManagerController _controller;
    private readonly CancellationTokenSource _formLifetime = new();
    private readonly bool _ownsStorageClient;
    private readonly bool _ownsProfileClient;
    private readonly bool _ownsSecretClient;
    private readonly bool _quickConnectMode;
    private readonly string _initialEndpoint;
    private ConnectionProfileDocument? _selectedProfile;
    private CancellationTokenSource? _profileLoadCancellation;
    private bool _loadingEditor;
    private bool _profileLoading;

    public ConnectionManagerForm(
        StorageProviderKind initialProvider = StorageProviderKind.S3,
        bool quickConnectMode = false,
        string initialEndpoint = "",
        IRemoteStorageAgentClient? storageClient = null,
        IRemoteConnectionProfileClient? profileClient = null,
        IRemoteSecretVaultClient? secretClient = null)
    {
        _quickConnectMode = quickConnectMode;
        _initialEndpoint = initialEndpoint;
        _ownsStorageClient = storageClient is null;
        _ownsProfileClient = profileClient is null;
        _ownsSecretClient = secretClient is null;
        _storageClient = storageClient ?? new NamedPipeRemoteStorageAgentClient();
        _profileClient = profileClient ?? new NamedPipeRemoteConnectionProfileClient();
        _secretClient = secretClient ?? new NamedPipeRemoteSecretVaultClient();
        _controller = new ConnectionManagerController(_profileClient, _secretClient);
        Text = quickConnectMode ? "Quick Connect — StorageHub" : "Connection Manager — StorageHub";
        AccessibleName = quickConnectMode ? "Quick Connect" : "Connection Manager";
        AccessibleDescription = "Configure provider endpoints, vault credential references, and explicit server trust.";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(980, 660);
        Size = new Size(1160, 780);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = StorageHubTheme.Canvas;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        var toolbar = BuildToolbar();
        _allCards = [];

        _profileCards = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 58,
            IntegralHeight = false,
            AccessibleName = "Connection profiles",
            AccessibleDescription = "Saved connection profiles. Use New connection to create another profile."
        };
        _profileCards.Items.AddRange(_allCards.Cast<object>().ToArray());
        _profileCards.DrawItem += DrawProfileCard;
        _profileCards.SelectedIndexChanged += ProfileCardSelected;

        _searchBox = new TextBox
        {
            Dock = DockStyle.Top,
            PlaceholderText = "Search connections…",
            AccessibleName = "Search connections",
            Margin = new Padding(0, 0, 0, 8)
        };
        _searchBox.TextChanged += SearchTextChanged;

        var leftHeader = new Panel
        {
            Dock = DockStyle.Top,
            Height = 62,
            Padding = new Padding(0, 0, 0, 8)
        };
        var connectionsLabel = UiControlFactory.CreateSectionTitle("Saved connections");
        connectionsLabel.Dock = DockStyle.Top;
        _searchBox.Dock = DockStyle.Bottom;
        leftHeader.Controls.Add(_searchBox);
        leftHeader.Controls.Add(connectionsLabel);

        var left = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            BackColor = StorageHubTheme.Surface
        };
        left.Controls.Add(_profileCards);
        left.Controls.Add(leftHeader);

        _providerAccent = new Panel { Dock = DockStyle.Top, Height = 4, BackColor = StorageHubTheme.Primary };
        _providerSelector = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 260,
            AccessibleName = "Connection provider",
            AccessibleDescription = "Changes the provider-specific endpoint, authentication, and trust fields."
        };
        _providerSelector.Items.AddRange(ConnectionProviderCatalog.All.Cast<object>().ToArray());
        _providerSelector.SelectedItem = ConnectionProviderCatalog.Get(initialProvider);
        _providerSelector.SelectedIndexChanged += ProviderSelectionChanged;

        _providerSummary = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(650, 0),
            ForeColor = StorageHubTheme.TextMuted,
            AccessibleName = "Provider summary"
        };
        var editorHeader = BuildEditorHeader();

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            AccessibleName = "Connection settings",
            Padding = new Point(14, 5),
            HotTrack = true
        };
        _generalPage = NewPage("General");
        _authenticationPage = NewPage("Authentication");
        _securityPage = NewPage("TLS / SSH Trust");
        tabs.TabPages.Add(_generalPage);
        tabs.TabPages.Add(_authenticationPage);
        tabs.TabPages.Add(_securityPage);

        var editor = new Panel { Dock = DockStyle.Fill, BackColor = StorageHubTheme.Surface };
        editor.Controls.Add(tabs);
        editor.Controls.Add(editorHeader);
        editor.Controls.Add(_providerAccent);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Size = new Size(1050, 650),
            SplitterDistance = 310,
            FixedPanel = FixedPanel.Panel1,
            Panel1MinSize = 250,
            Panel2MinSize = 540,
            BackColor = StorageHubTheme.Border,
            AccessibleName = "Connections and profile editor"
        };
        split.Panel1.Padding = new Padding(0, 0, 3, 0);
        split.Panel2.Padding = new Padding(3, 0, 0, 0);
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(editor);
        split.Panel1Collapsed = quickConnectMode;

        _testState = new Label
        {
            Text = "Not tested",
            AutoSize = true,
            ForeColor = StorageHubTheme.TextMuted,
            Padding = new Padding(8, 9, 8, 0),
            AccessibleName = "Connection test status"
        };
        var footer = BuildFooter();

        Controls.Add(split);
        Controls.Add(footer);
        Controls.Add(toolbar);

        UpdateProviderEditor(ConnectionProviderCatalog.Get(initialProvider));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _profileCards.DrawItem -= DrawProfileCard;
            _profileCards.SelectedIndexChanged -= ProfileCardSelected;
            _searchBox.TextChanged -= SearchTextChanged;
            _providerSelector.SelectedIndexChanged -= ProviderSelectionChanged;
            _formLifetime.Cancel();
            _profileLoadCancellation?.Cancel();
        }

        base.Dispose(disposing);

        if (disposing)
        {
            foreach (var image in _ownedImages)
            {
                image.Dispose();
            }

            _ownedImages.Clear();
            _formLifetime.Dispose();
            _profileLoadCancellation?.Dispose();
            DisposeOwnedClient(_storageClient, _ownsStorageClient);
            DisposeOwnedClient(_profileClient, _ownsProfileClient);
            DisposeOwnedClient(_secretClient, _ownsSecretClient);
        }
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!_quickConnectMode)
        {
            await ReloadProfilesAsync(_formLifetime.Token);
        }
    }

    private ToolStrip BuildToolbar()
    {
        var toolbar = new ToolStrip
        {
            GripStyle = ToolStripGripStyle.Hidden,
            Dock = DockStyle.Top,
            ImageScalingSize = new Size(18, 18),
            Padding = new Padding(6, 4, 6, 4),
            BackColor = StorageHubTheme.Surface,
            Renderer = StorageHubTheme.CreateToolStripRenderer(),
            AccessibleName = "Connection Manager commands"
        };
        toolbar.Items.Add(CreateToolbarButton(UiGlyph.Add, "New connection", (_, _) => StartNewProfile()));
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(CreateToolbarButton(UiGlyph.Test, "Test connection", async (_, _) =>
            await TestSelectedConnectionAsync(_formLifetime.Token)));
        if (!_quickConnectMode)
        {
            toolbar.Items.Add(CreateToolbarButton(UiGlyph.Save, "Save profile", async (_, _) =>
                await SaveProfileAsync(_formLifetime.Token)));
            toolbar.Items.Add(CreateToolbarButton(UiGlyph.Delete, "Delete profile", async (_, _) =>
                await DeleteProfileAsync(_formLifetime.Token)));
        }

        return toolbar;
    }

    private TableLayoutPanel BuildEditorHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 94,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(18, 12, 18, 10),
            BackColor = StorageHubTheme.Surface
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.Controls.Add(new Label
        {
            Text = "Provider",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = StorageHubTheme.Text
        }, 0, 0);
        header.Controls.Add(_providerSelector, 1, 0);
        header.Controls.Add(_providerSummary, 1, 1);
        return header;
    }

    private TableLayoutPanel BuildFooter()
    {
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            ColumnCount = 2,
            Padding = new Padding(12, 8, 12, 8),
            BackColor = StorageHubTheme.Surface
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(_testState, 0, 0);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty
        };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        StorageHubTheme.StyleSecondaryButton(cancel);
        var primary = new Button
        {
            Text = _quickConnectMode ? "Connect without saving" : "Save profile",
            AccessibleDescription = _quickConnectMode
                ? "Connect for this session; secrets remain vault references."
                : "Save non-secret profile data and vault references."
        };
        if (_quickConnectMode)
        {
            primary.DialogResult = DialogResult.OK;
        }
        else
        {
            primary.Click += async (_, _) => await SaveProfileAsync(_formLifetime.Token);
        }
        StorageHubTheme.StylePrimaryButton(primary);
        actions.Controls.Add(cancel);
        actions.Controls.Add(primary);
        footer.Controls.Add(actions, 1, 0);
        AcceptButton = primary;
        CancelButton = cancel;
        return footer;
    }

    private static TabPage NewPage(string name) => new(name)
    {
        BackColor = StorageHubTheme.Surface,
        Padding = new Padding(8)
    };

    private void ProviderSelectionChanged(object? sender, EventArgs e)
    {
        if (_providerSelector.SelectedItem is ConnectionProviderDescriptor provider)
        {
            UpdateProviderEditor(provider);
        }
    }

    private async void ProfileCardSelected(object? sender, EventArgs e)
    {
        if (_profileCards.SelectedItem is ConnectionCardModel card)
        {
            _providerSelector.SelectedItem = ConnectionProviderCatalog.Get(card.Provider);
            if (!_loadingEditor && card.ConnectionId is { } connectionId)
            {
                _profileLoadCancellation?.Cancel();
                _profileLoadCancellation?.Dispose();
                var loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(_formLifetime.Token);
                _profileLoadCancellation = loadCancellation;
                _profileLoading = true;
                try
                {
                    await LoadProfileAsync(connectionId, loadCancellation.Token);
                }
                finally
                {
                    if (ReferenceEquals(_profileLoadCancellation, loadCancellation))
                    {
                        _profileLoading = false;
                    }
                }
            }
            else if (!_loadingEditor)
            {
                _profileLoadCancellation?.Cancel();
                _profileLoading = false;
                _selectedProfile = null;
            }
        }
    }

    private void SearchTextChanged(object? sender, EventArgs e)
    {
        var query = _searchBox.Text.Trim();
        var filtered = string.IsNullOrEmpty(query)
            ? _allCards
            : _allCards.Where(card =>
                    card.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                    card.Endpoint.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .ToList();
        _profileCards.BeginUpdate();
        try
        {
            _profileCards.Items.Clear();
            _profileCards.Items.AddRange(filtered.Cast<object>().ToArray());
        }
        finally
        {
            _profileCards.EndUpdate();
        }
    }

    private void UpdateProviderEditor(ConnectionProviderDescriptor provider)
    {
        _editorFields.Clear();
        _providerSummary.Text = provider.Summary;
        _providerAccent.BackColor = StorageHubTheme.ParseAccent(provider.AccentHex);
        ReplacePageContent(_generalPage, BuildProviderPage("Endpoint", provider.EndpointExample, provider.GeneralFields, provider));
        ReplacePageContent(_authenticationPage, BuildProviderPage(
            "Authentication",
            "Secret values live in the encrypted StorageHub vault; this profile stores references only.",
            provider.AuthenticationFields,
            provider));
        ReplacePageContent(_securityPage, BuildSecurityPage(provider));
        _testState.Text = "Not tested";
        _testState.ForeColor = StorageHubTheme.TextMuted;
    }

    private Panel BuildProviderPage(
        string title,
        string description,
        IReadOnlyList<ConnectionFieldDescriptor> fields,
        ConnectionProviderDescriptor provider)
    {
        var content = CreateScrollableContent(title, description, out var table);
        if (string.Equals(title, "Endpoint", StringComparison.Ordinal))
        {
            var profileName = new TextBox
            {
                Text = _quickConnectMode ? $"Temporary {provider.ShortName} connection" : $"New {provider.DisplayName}"
            };
            _editorFields["profileName"] = profileName;
            UiControlFactory.AddLabeledRow(
                table,
                "Profile name *",
                profileName,
                _quickConnectMode
                    ? "Used only to identify this session; the temporary profile is not saved."
                    : "The display name shown in connection cards and pane selectors.");
            var folderTags = new TextBox { PlaceholderText = "Team / Project · archive, production" };
            _editorFields["folderTags"] = folderTags;
            UiControlFactory.AddLabeledRow(
                table,
                "Folder and tags",
                folderTags,
                "Organizational metadata only; never enter credentials or recovery codes.");
            var badge = new Label
            {
                Text = $"  {provider.ShortName}  · provider color {provider.AccentHex}",
                AutoSize = true,
                ForeColor = StorageHubTheme.ParseAccent(provider.AccentHex),
                BackColor = StorageHubTheme.SurfaceMuted,
                Padding = new Padding(6, 5, 6, 5)
            };
            UiControlFactory.AddLabeledRow(
                table,
                "Connection badge",
                badge,
                "The provider glyph and accent make concurrent connections easy to distinguish.");
        }

        foreach (var field in fields)
        {
            var control = BuildFieldControl(field, provider);
            _editorFields[field.Key] = control;
            var requiredSuffix = field.Required ? " *" : string.Empty;
            UiControlFactory.AddLabeledRow(table, field.Label + requiredSuffix, control, field.HelpText);
        }

        return content;
    }

    private Panel BuildSecurityPage(ConnectionProviderDescriptor provider)
    {
        var content = CreateScrollableContent("Transport and server identity", provider.TrustNotice, out var table);
        var notice = new Panel
        {
            Height = 62,
            Dock = DockStyle.Top,
            BackColor = provider.EncryptedByDefault ? Color.FromArgb(232, 247, 240) : Color.FromArgb(255, 242, 222),
            Margin = new Padding(4, 4, 4, 12),
            AccessibleName = provider.EncryptedByDefault ? "Secure transport policy" : "Plaintext transport warning"
        };
        var icon = new PictureBox
        {
            Image = CreateOwnedImage(provider.EncryptedByDefault ? UiGlyph.Lock : UiGlyph.Warning, provider.EncryptedByDefault ? StorageHubTheme.Success : StorageHubTheme.Warning),
            SizeMode = PictureBoxSizeMode.CenterImage,
            Dock = DockStyle.Left,
            Width = 48
        };
        var warning = new Label
        {
            Text = provider.TrustNotice,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            Padding = new Padding(0, 9, 10, 6),
            ForeColor = provider.EncryptedByDefault ? StorageHubTheme.Success : StorageHubTheme.Warning
        };
        notice.Controls.Add(warning);
        notice.Controls.Add(icon);
        table.Controls.Add(notice, 0, table.RowCount);
        table.SetColumnSpan(notice, 2);
        table.RowCount++;

        foreach (var field in provider.SecurityFields)
        {
            var control = BuildFieldControl(field, provider);
            _editorFields[field.Key] = control;
            UiControlFactory.AddLabeledRow(
                table,
                field.Label + (field.Required ? " *" : string.Empty),
                control,
                field.HelpText);
        }

        return content;
    }

    private Control BuildFieldControl(ConnectionFieldDescriptor field, ConnectionProviderDescriptor provider)
    {
        switch (field.Kind)
        {
            case ConnectionFieldKind.Toggle:
                var isEnabled = bool.TryParse(field.DefaultValue, out var selected) && selected;
                return Toggle(isEnabled, isEnabled ? "Enabled" : "Disabled");
            case ConnectionFieldKind.Number:
                var defaultNumber = decimal.TryParse(field.DefaultValue, out var number)
                    ? number
                    : provider.DefaultPort ?? 0;
                return Numeric(defaultNumber, 0, 65535);
            case ConnectionFieldKind.Choice:
                return Choice(field.DefaultValue, field.Choices?.ToArray() ?? []);
            case ConnectionFieldKind.SecretReference:
                return VaultReferencePicker(field);
            case ConnectionFieldKind.CertificateReference:
                return VaultReferencePicker(field);
            case ConnectionFieldKind.Fingerprint:
                return FingerprintPicker(field);
            case ConnectionFieldKind.Path:
                return ReferencePicker(field.Placeholder, "Browse…", readOnly: false, initialText: ResolveInitialValue(field));
            default:
                return new TextBox
                {
                    Text = ResolveInitialValue(field),
                    PlaceholderText = field.Placeholder
                };
        }
    }

    private string ResolveInitialValue(ConnectionFieldDescriptor field) =>
        string.Equals(field.Key, "host", StringComparison.Ordinal) ||
        string.Equals(field.Key, "endpoint", StringComparison.Ordinal) ||
        string.Equals(field.Key, "rootPath", StringComparison.Ordinal)
            ? (string.IsNullOrWhiteSpace(_initialEndpoint) ? field.DefaultValue : _initialEndpoint)
            : field.DefaultValue;

    private static Panel CreateScrollableContent(string title, string description, out TableLayoutPanel table)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = StorageHubTheme.Surface,
            Padding = new Padding(10)
        };
        table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(8),
            BackColor = StorageHubTheme.Surface
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var heading = UiControlFactory.CreateSectionTitle(title);
        var summary = UiControlFactory.CreateDescription(description);
        table.Controls.Add(heading, 0, 0);
        table.SetColumnSpan(heading, 2);
        table.Controls.Add(summary, 0, 1);
        table.SetColumnSpan(summary, 2);
        table.RowCount = 2;
        panel.Controls.Add(table);
        return panel;
    }

    private static NumericUpDown Numeric(decimal value, decimal minimum, decimal maximum) => new()
    {
        Minimum = minimum,
        Maximum = maximum,
        Value = Math.Clamp(value, minimum, maximum),
        Width = 130
    };

    private static ComboBox Choice(string selected, params string[] choices)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        combo.Items.AddRange(choices.Cast<object>().ToArray());
        if (combo.Items.Count > 0)
        {
            var index = combo.FindStringExact(selected);
            combo.SelectedIndex = index >= 0 ? index : 0;
        }

        return combo;
    }

    private static CheckBox Toggle(bool selected, string text) => new()
    {
        Text = text,
        Checked = selected,
        AutoSize = true
    };

    private static TableLayoutPanel ReferencePicker(
        string placeholder,
        string buttonText,
        bool readOnly = true,
        string initialText = "")
    {
        var panel = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            Dock = DockStyle.Top,
            Margin = Padding.Empty
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var value = new TextBox
        {
            ReadOnly = readOnly,
            Text = initialText,
            PlaceholderText = placeholder,
            Dock = DockStyle.Fill,
            AccessibleDescription = readOnly ? "Stores a vault or certificate reference, not a secret value." : string.Empty
        };
        var select = new Button { Text = buttonText, AutoSize = true };
        StorageHubTheme.StyleSecondaryButton(select);
        select.Margin = new Padding(6, 0, 0, 0);
        if (!readOnly)
        {
            select.Click += (_, _) =>
            {
                using var dialog = new FolderBrowserDialog
                {
                    Description = "Choose the connection root folder",
                    InitialDirectory = Directory.Exists(value.Text) ? value.Text : string.Empty,
                    ShowNewFolderButton = false,
                    UseDescriptionForTitle = true
                };
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    value.Text = dialog.SelectedPath;
                }
            };
        }

        panel.Controls.Add(value, 0, 0);
        panel.Controls.Add(select, 1, 0);
        return panel;
    }

    private TableLayoutPanel VaultReferencePicker(ConnectionFieldDescriptor field)
    {
        var panel = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 3,
            Dock = DockStyle.Top,
            Margin = Padding.Empty
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var value = new TextBox
        {
            ReadOnly = true,
            PlaceholderText = field.Placeholder,
            Dock = DockStyle.Fill,
            AccessibleName = field.Label,
            AccessibleDescription = "An opaque vault reference. Secret material is never displayed."
        };
        var enroll = new Button { Text = "Enroll / replace…", AutoSize = true };
        var delete = new Button { Text = "Delete…", AutoSize = true };
        StorageHubTheme.StyleSecondaryButton(enroll);
        StorageHubTheme.StyleSecondaryButton(delete);
        enroll.Margin = new Padding(6, 0, 0, 0);
        delete.Margin = new Padding(6, 0, 0, 0);
        enroll.Click += async (_, _) => await EnrollOrUpdateSecretAsync(field, value, _formLifetime.Token);
        delete.Click += async (_, _) => await DeleteSecretAsync(field, value, _formLifetime.Token);
        panel.Controls.Add(value, 0, 0);
        panel.Controls.Add(enroll, 1, 0);
        panel.Controls.Add(delete, 2, 0);
        return panel;
    }

    private TableLayoutPanel FingerprintPicker(ConnectionFieldDescriptor field)
    {
        var panel = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            Dock = DockStyle.Top,
            Margin = Padding.Empty
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var value = new TextBox
        {
            PlaceholderText = field.Placeholder,
            Dock = DockStyle.Fill,
            AccessibleName = field.Label,
            AccessibleDescription = "Verify this SHA-256 fingerprint through a separate trusted channel before saving."
        };
        var reject = new Button
        {
            Text = "Reject…",
            AutoSize = true,
            AccessibleDescription = "Record this exact fingerprint as rejected for the saved endpoint."
        };
        StorageHubTheme.StyleSecondaryButton(reject);
        reject.Margin = new Padding(6, 0, 0, 0);
        reject.Click += async (_, _) => await RejectFingerprintAsync(value, _formLifetime.Token);
        panel.Controls.Add(value, 0, 0);
        panel.Controls.Add(reject, 1, 0);
        return panel;
    }

    private ToolStripButton CreateToolbarButton(UiGlyph glyph, string tooltip, EventHandler click)
    {
        var image = CreateOwnedImage(glyph, StorageHubTheme.Text);
        var button = new ToolStripButton
        {
            Image = image,
            Text = tooltip,
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            ToolTipText = tooltip,
            AccessibleName = tooltip
        };
        button.Click += click;

        return button;
    }

    private Bitmap CreateOwnedImage(UiGlyph glyph, Color color)
    {
        var image = UiIconFactory.Create(glyph, color, 18, DeviceDpi / 96F);
        _ownedImages.Add(image);
        return image;
    }

    private void DrawProfileCard(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index >= 0 && _profileCards.Items[e.Index] is ConnectionCardModel card)
        {
            var selected = (e.State & DrawItemState.Selected) != 0;
            var foreground = selected ? SystemColors.HighlightText : StorageHubTheme.Text;
            var muted = selected ? SystemColors.HighlightText : StorageHubTheme.TextMuted;
            var accent = StorageHubTheme.ParseAccent(card.Descriptor.AccentHex);
            using var accentBrush = new SolidBrush(accent);
            var badgeBounds = new Rectangle(e.Bounds.Left + 8, e.Bounds.Top + 10, 38, 38);
            e.Graphics.FillRectangle(accentBrush, badgeBounds);
            TextRenderer.DrawText(
                e.Graphics,
                card.Descriptor.ShortName,
                e.Font ?? Font,
                badgeBounds,
                Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(
                e.Graphics,
                card.Name,
                e.Font ?? Font,
                new Rectangle(e.Bounds.Left + 56, e.Bounds.Top + 8, e.Bounds.Width - 64, 22),
                foreground,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(
                e.Graphics,
                card.Endpoint,
                e.Font ?? Font,
                new Rectangle(e.Bounds.Left + 56, e.Bounds.Top + 31, e.Bounds.Width - 64, 19),
                muted,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }

        e.DrawFocusRectangle();
    }

    private void MarkConnectionTested()
    {
        _testState.Text = "Ready to test when the background agent is connected";
        _testState.ForeColor = StorageHubTheme.Warning;
    }

    private async Task ReloadProfilesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _storageClient.ListConnectionsAsync(
                new ConnectionListRequest(
                    StorageIpcContract.CurrentVersion,
                    IncludeDisabled: true,
                    Limit: StorageIpcLimits.MaximumConnectionResults),
                cancellationToken);
            if (response.Failure is not null)
            {
                ShowStatus(response.Failure.Message, StorageHubTheme.Warning);
                return;
            }

            var selectedId = _selectedProfile?.ConnectionId;
            _allCards.Clear();
            _allCards.AddRange(response.Connections.Select(CreateSavedConnectionCard));
            RefreshProfileCards(selectedId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            ShowStatus("The background agent is unavailable; saved connections could not be loaded.", StorageHubTheme.Warning);
        }
    }

    private async Task LoadProfileAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        try
        {
            ShowStatus("Loading profile…", StorageHubTheme.TextMuted);
            var response = await _controller.GetAsync(connectionId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (response.Profile is null)
            {
                ShowStatus(response.Failure?.Message ?? "The profile could not be loaded.", StorageHubTheme.Warning);
                return;
            }

            _loadingEditor = true;
            try
            {
                _selectedProfile = response.Profile;
                var provider = MapProvider(response.Profile.Draft.Endpoint.Provider);
                _providerSelector.SelectedItem = ConnectionProviderCatalog.Get(provider);
                var values = ConnectionEditorDraftFactory.ToEditorValues(response.Profile);
                foreach (var pair in values)
                {
                    if (_editorFields.TryGetValue(pair.Key, out var control))
                    {
                        SetControlValue(control, pair.Value);
                    }
                }


                await LoadTrustIntoEditorAsync(response.Profile, cancellationToken);
            }
            finally
            {
                _loadingEditor = false;
            }

            ShowStatus($"Loaded version {response.Profile.Version}", StorageHubTheme.Success);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            ShowStatus("The profile could not be loaded from the background agent.", StorageHubTheme.Warning);
        }
    }

    private async Task SaveProfileAsync(CancellationToken cancellationToken)
    {
        if (_quickConnectMode || _providerSelector.SelectedItem is not ConnectionProviderDescriptor provider)
        {
            return;
        }

        if (_profileLoading)
        {
            ShowStatus("Wait for the selected profile to finish loading before saving.", StorageHubTheme.Warning);
            return;
        }

        try
        {
            var draft = ConnectionEditorDraftFactory.Build(provider.Kind, ReadEditorValues());
            ShowStatus("Saving profile…", StorageHubTheme.TextMuted);
            var response = await _controller.SaveAsync(draft, _selectedProfile, cancellationToken);
            if (response.Status != ConnectionProfileWriteStatus.Succeeded || response.Profile is null)
            {
                ShowStatus(response.Failure?.Message ?? "The profile could not be saved.", StorageHubTheme.Warning);
                return;
            }

            _selectedProfile = response.Profile;
            var fingerprint = GetPinnedFingerprint(response.Profile, ReadEditorValues());
            if (fingerprint is not null)
            {
                ShowStatus("Saving verified server trust…", StorageHubTheme.TextMuted);
                var trustResponse = await _controller.TrustOrRolloverAsync(
                    response.Profile,
                    fingerprint,
                    cancellationToken);
                if (trustResponse.Status != ConnectionTrustMutationStatus.Succeeded)
                {
                    ShowStatus(
                        trustResponse.Failure?.Message ??
                        "The profile was saved, but its pin was not enrolled. It remains fail-closed.",
                        StorageHubTheme.Warning);
                    return;
                }
            }

            await ReloadProfilesAsync(cancellationToken);
            ShowStatus($"Saved version {response.Profile.Version}", StorageHubTheme.Success);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ArgumentException error)
        {
            ShowStatus(error.Message, StorageHubTheme.Warning);
        }
        catch (Exception)
        {
            ShowStatus("The profile could not be saved through the background agent.", StorageHubTheme.Warning);
        }
    }

    private async Task DeleteProfileAsync(CancellationToken cancellationToken)
    {
        if (_profileLoading)
        {
            ShowStatus("Wait for the selected profile to finish loading before deleting.", StorageHubTheme.Warning);
            return;
        }

        if (_selectedProfile is null)
        {
            ShowStatus("Select a saved profile before deleting it.", StorageHubTheme.Warning);
            return;
        }

        if (MessageBox.Show(
                this,
                $"Delete '{_selectedProfile.Draft.Metadata.DisplayName}'? Queued work will keep its immutable history, but the connection can no longer be opened.",
                "Delete saved connection",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        try
        {
            var response = await _controller.DeleteAsync(_selectedProfile, cancellationToken);
            if (response.Status != ConnectionProfileWriteStatus.Succeeded)
            {
                ShowStatus(response.Failure?.Message ?? "The profile could not be deleted.", StorageHubTheme.Warning);
                return;
            }

            _selectedProfile = null;
            await ReloadProfilesAsync(cancellationToken);
            StartNewProfile();
            ShowStatus("Profile deleted.", StorageHubTheme.Success);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            ShowStatus("The profile could not be deleted through the background agent.", StorageHubTheme.Warning);
        }
    }

    private async Task TestSelectedConnectionAsync(CancellationToken cancellationToken)
    {
        if (_selectedProfile is null)
        {
            MarkConnectionTested();
            return;
        }

        try
        {
            ShowStatus("Testing connection…", StorageHubTheme.TextMuted);
            var response = await _storageClient.TestConnectionAsync(
                new ConnectionTestRequest(StorageIpcContract.CurrentVersion, _selectedProfile.ConnectionId),
                cancellationToken);
            ShowStatus(
                response.Succeeded
                    ? $"Connection succeeded in {response.ElapsedMilliseconds} ms"
                    : response.Failure?.Message ?? "Connection test failed.",
                response.Succeeded ? StorageHubTheme.Success : StorageHubTheme.Warning);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            ShowStatus("The background agent could not test this connection.", StorageHubTheme.Warning);
        }
    }

    private async Task EnrollOrUpdateSecretAsync(
        ConnectionFieldDescriptor field,
        TextBox referenceBox,
        CancellationToken cancellationToken)
    {
        byte[]? material = null;
        try
        {
            material = await PromptForSecretMaterialAsync(field, cancellationToken);
            if (material is null)
            {
                return;
            }

            ShowStatus("Writing encrypted vault entry…", StorageHubTheme.TextMuted);
            var response = await _controller.EnrollOrUpdateSecretAsync(
                MapSecretPurpose(field.Key),
                string.IsNullOrWhiteSpace(referenceBox.Text) ? null : referenceBox.Text,
                material,
                cancellationToken);
            if (!response.Succeeded || response.Reference is null)
            {
                ShowStatus(response.Failure?.Message ?? "The vault entry could not be written.", StorageHubTheme.Warning);
                return;
            }

            referenceBox.Text = response.Reference;
            ShowStatus($"Vault reference ready (version {response.Version}).", StorageHubTheme.Success);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            ShowStatus("The secret operation failed without changing the profile.", StorageHubTheme.Warning);
        }
        finally
        {
            if (material is not null)
            {
                CryptographicOperations.ZeroMemory(material);
            }
        }
    }

    private async Task DeleteSecretAsync(
        ConnectionFieldDescriptor field,
        TextBox referenceBox,
        CancellationToken cancellationToken)
    {
        var reference = referenceBox.Text.Trim();
        if (!ConnectionEndpointDocument.IsOpaqueSecretReference(reference) || reference.Length == 0)
        {
            ShowStatus("No vault reference is selected.", StorageHubTheme.Warning);
            return;
        }

        if (MessageBox.Show(
                this,
                "Permanently delete this vault secret? Any other profile that still references it will stop working. This action cannot be undone.",
                "Delete vault secret",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        try
        {
            var response = await _controller.DeleteSecretAsync(
                reference,
                MapSecretPurpose(field.Key),
                cancellationToken);
            if (!response.Succeeded)
            {
                ShowStatus(response.Failure?.Message ?? "The vault secret could not be deleted.", StorageHubTheme.Warning);
                return;
            }

            referenceBox.Clear();
            ShowStatus("Vault secret deleted. Save the profile to remove its old reference.", StorageHubTheme.Success);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            ShowStatus("The secret delete operation failed.", StorageHubTheme.Warning);
        }
    }

    private async Task<byte[]?> PromptForSecretMaterialAsync(
        ConnectionFieldDescriptor field,
        CancellationToken cancellationToken)
    {
        if (field.Kind == ConnectionFieldKind.CertificateReference ||
            string.Equals(field.Key, "privateKeyReference", StringComparison.Ordinal))
        {
            using var picker = new OpenFileDialog
            {
                Title = field.Kind == ConnectionFieldKind.CertificateReference
                    ? "Select a password-protected PFX certificate"
                    : "Select an encrypted SSH private key",
                Filter = field.Kind == ConnectionFieldKind.CertificateReference
                    ? "PKCS#12 certificates (*.pfx;*.p12)|*.pfx;*.p12|All files (*.*)|*.*"
                    : "SSH private keys (*.key;*.pem)|*.key;*.pem|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (picker.ShowDialog(this) != DialogResult.OK)
            {
                return null;
            }

            var info = new FileInfo(picker.FileName);
            if (info.Length is <= 0 or > SecretVaultIpcContract.MaximumSecretBytes)
            {
                throw new ArgumentException("The selected secret file is empty or exceeds 16 MiB.");
            }

            return await File.ReadAllBytesAsync(picker.FileName, cancellationToken);
        }

        using var dialog = new Form
        {
            Text = $"Enroll {field.Label}",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(520, 150),
            Padding = new Padding(14)
        };
        var input = new TextBox
        {
            Dock = DockStyle.Top,
            UseSystemPasswordChar = true,
            AccessibleName = field.Label,
            AccessibleDescription = "Secret material is sent only to the current-user agent vault."
        };
        var notice = new Label
        {
            Dock = DockStyle.Top,
            Height = 48,
            Text = "The value is encrypted into the StorageHub vault. Only an opaque reference is saved in the profile.",
            ForeColor = StorageHubTheme.TextMuted
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            FlowDirection = FlowDirection.RightToLeft
        };
        var accept = new Button { Text = "Enroll", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(accept);
        buttons.Controls.Add(cancel);
        dialog.Controls.Add(input);
        dialog.Controls.Add(notice);
        dialog.Controls.Add(buttons);
        dialog.AcceptButton = accept;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) != DialogResult.OK || input.Text.Length == 0)
        {
            input.Clear();
            return null;
        }

        var material = Encoding.UTF8.GetBytes(input.Text);
        input.Clear();
        return material;
    }

    private Dictionary<string, string> ReadEditorValues()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in _editorFields)
        {
            values[pair.Key] = GetControlValue(pair.Value);
        }

        return values;
    }

    private async Task LoadTrustIntoEditorAsync(
        ConnectionProfileDocument profile,
        CancellationToken cancellationToken)
    {
        var fieldKey = TrustFingerprintField(profile);
        if (fieldKey is null || !_editorFields.TryGetValue(fieldKey, out var control))
        {
            return;
        }

        var response = await _controller.GetTrustAsync(profile, cancellationToken);
        if (response.Snapshot is null)
        {
            throw new InvalidDataException(
                response.Failure?.Message ?? "The saved server trust state could not be loaded.");
        }

        var now = DateTimeOffset.UtcNow;
        var active = response.Snapshot.Records
            .Where(record => record.Decision == ConnectionTrustDecision.Trusted &&
                (record.ExpiresUtc is null || record.ExpiresUtc > now))
            .ToArray();
        SetControlValue(control, active.Length == 1 ? active[0].Sha256Fingerprint : string.Empty);
        if (active.Length > 1)
        {
            throw new InvalidDataException(
                "Multiple active server trust records require reconciliation before editing this profile.");
        }
    }

    private async Task RejectFingerprintAsync(TextBox value, CancellationToken cancellationToken)
    {
        if (_selectedProfile is null)
        {
            ShowStatus("Save the connection before recording a rejected fingerprint.", StorageHubTheme.Warning);
            return;
        }

        var fingerprint = value.Text.Trim();
        if (!ConnectionTrustIpcLimits.IsValidFingerprint(fingerprint))
        {
            ShowStatus("Enter a valid SHA-256 fingerprint before rejecting it.", StorageHubTheme.Warning);
            return;
        }

        if (MessageBox.Show(
                this,
                "Record this exact server identity as rejected? Connections will continue to fail closed unless a different verified identity is trusted.",
                "Reject server identity",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning) != DialogResult.OK)
        {
            return;
        }

        try
        {
            ShowStatus("Recording rejected server identity…", StorageHubTheme.TextMuted);
            var response = await _controller.RejectAsync(_selectedProfile, fingerprint, cancellationToken);
            if (response.Status != ConnectionTrustMutationStatus.Succeeded)
            {
                ShowStatus(response.Failure?.Message ?? "The rejected identity could not be recorded.", StorageHubTheme.Warning);
                return;
            }

            value.Clear();
            ShowStatus("Rejected server identity recorded; the profile remains fail-closed.", StorageHubTheme.Success);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            ShowStatus("The rejected identity could not be recorded through the background agent.", StorageHubTheme.Warning);
        }
    }

    private static string? GetPinnedFingerprint(
        ConnectionProfileDocument profile,
        Dictionary<string, string> values)
    {
        var key = TrustFingerprintField(profile);
        return key is not null && values.TryGetValue(key, out var fingerprint) &&
            !string.IsNullOrWhiteSpace(fingerprint)
                ? fingerprint.Trim()
                : null;
    }

    private static string? TrustFingerprintField(ConnectionProfileDocument profile) =>
        profile.Draft.Endpoint switch
        {
            { Provider: StorageConnectionProvider.Ftps, TlsPolicy: ConnectionTlsCertificatePolicy.Pinned } =>
                "certificatePin",
            { Provider: StorageConnectionProvider.Sftp, SshHostKeyPolicy: ConnectionSshHostKeyPolicy.Pinned } =>
                "hostKeyFingerprint",
            _ => null
        };

    private void StartNewProfile()
    {
        _profileCards.ClearSelected();
        _selectedProfile = null;
        if (_providerSelector.SelectedItem is ConnectionProviderDescriptor provider)
        {
            UpdateProviderEditor(provider);
        }

        ShowStatus("New unsaved profile", StorageHubTheme.TextMuted);
    }

    private void RefreshProfileCards(Guid? selectedId = null)
    {
        _loadingEditor = true;
        try
        {
            _profileCards.BeginUpdate();
            _profileCards.Items.Clear();
            _profileCards.Items.AddRange(_allCards.Cast<object>().ToArray());
            var selectedIndex = selectedId is { } id
                ? _allCards.FindIndex(card => card.ConnectionId == id)
                : -1;
            _profileCards.SelectedIndex = selectedIndex;
        }
        finally
        {
            _profileCards.EndUpdate();
            _loadingEditor = false;
        }
    }

    private static ConnectionCardModel CreateSavedConnectionCard(ConnectionSummary connection)
    {
        var provider = MapProvider(connection.Provider);
        var providerName = ConnectionProviderCatalog.Get(provider).DisplayName;
        var summary = string.IsNullOrWhiteSpace(connection.FolderPath)
            ? $"{providerName} saved profile"
            : $"{providerName} · {connection.FolderPath}";
        return new ConnectionCardModel(
            connection.DisplayName,
            provider,
            summary,
            connection.IsEnabled ? "Saved" : "Disabled",
            connection.IsFavorite,
            connection.ConnectionId,
            connection.IsEnabled,
            connection.AccentColor);
    }

    private void ShowStatus(string message, Color color)
    {
        _testState.Text = message;
        _testState.ForeColor = color;
    }

    private static string GetControlValue(Control control) => control switch
    {
        TextBox textBox => textBox.Text,
        ComboBox comboBox => comboBox.SelectedItem?.ToString() ?? comboBox.Text,
        CheckBox checkBox => checkBox.Checked.ToString(System.Globalization.CultureInfo.InvariantCulture),
        NumericUpDown numeric => numeric.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => control.Controls.OfType<TextBox>().FirstOrDefault()?.Text ?? string.Empty
    };

    private static void SetControlValue(Control control, string value)
    {
        switch (control)
        {
            case TextBox textBox:
                textBox.Text = value;
                break;
            case ComboBox comboBox:
                var index = comboBox.FindStringExact(value);
                if (index >= 0)
                {
                    comboBox.SelectedIndex = index;
                }
                break;
            case CheckBox checkBox when bool.TryParse(value, out var selected):
                checkBox.Checked = selected;
                break;
            case NumericUpDown numeric when decimal.TryParse(
                value,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out var number):
                numeric.Value = Math.Clamp(number, numeric.Minimum, numeric.Maximum);
                break;
            default:
                var nested = control.Controls.OfType<TextBox>().FirstOrDefault();
                if (nested is not null)
                {
                    nested.Text = value;
                }
                break;
        }
    }

    private static SecretMaterialPurpose MapSecretPurpose(string fieldKey) => fieldKey switch
    {
        "accessKeyReference" => SecretMaterialPurpose.AccessKey,
        "secretAccessKeyReference" => SecretMaterialPurpose.SecretAccessKey,
        "sessionTokenReference" => SecretMaterialPurpose.SessionToken,
        "privateKeyReference" => SecretMaterialPurpose.SshPrivateKey,
        "privateKeyPassphraseReference" => SecretMaterialPurpose.SshPrivateKeyPassphrase,
        "clientCertificateReference" => SecretMaterialPurpose.ClientCertificatePfx,
        "clientCertificatePasswordReference" => SecretMaterialPurpose.ClientCertificatePassword,
        "credentialReference" => SecretMaterialPurpose.ProxyCredential,
        _ => SecretMaterialPurpose.Password
    };

    private static StorageProviderKind MapProvider(StorageConnectionProvider provider) => provider switch
    {
        StorageConnectionProvider.Local => StorageProviderKind.Local,
        StorageConnectionProvider.S3 => StorageProviderKind.S3,
        StorageConnectionProvider.Ftp => StorageProviderKind.Ftp,
        StorageConnectionProvider.Ftps => StorageProviderKind.Ftps,
        StorageConnectionProvider.Sftp => StorageProviderKind.Sftp,
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    private static void DisposeOwnedClient(IAsyncDisposable client, bool ownsClient)
    {
        if (ownsClient)
        {
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void ReplacePageContent(TabPage page, Control content)
    {
        var previousControls = page.Controls.Cast<Control>().ToArray();
        page.Controls.Clear();
        foreach (var previous in previousControls)
        {
            previous.Dispose();
        }

        page.Controls.Add(content);
    }
}
