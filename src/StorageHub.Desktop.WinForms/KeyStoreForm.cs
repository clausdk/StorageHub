using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

/// <summary>
/// Manages the shared key and certificate store: import once, reference from any number of Storage
/// and Client profiles, see what is held and when it expires, and rotate in one place.
///
/// Importing is deliberately a two-step flow. Material is enrolled on the dedicated secret pipe,
/// which returns an opaque reference and never reads anything back; only that reference is then
/// registered here. Nothing in this form ever holds key material beyond the moment it is sent, and
/// the buffers it does hold are zeroed.
/// </summary>
public sealed class KeyStoreForm : Form
{
    private const int MaximumMaterialBytes = 16 * 1024 * 1024;

    private readonly IKeyStoreAgentClient _client;
    private readonly IRemoteSecretVaultClient _secrets;
    private readonly ListView _entries;
    private readonly TextBox _search;
    private readonly Label _status;
    private readonly Button _importCertificate;
    private readonly Button _importKey;
    private readonly Button _rename;
    private readonly Button _delete;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<KeyStoreEntryDocument> _loaded = [];
    private bool _busy;

    public KeyStoreForm(IKeyStoreAgentClient client, IRemoteSecretVaultClient secrets)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));

        Text = "Key Store";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(820, 460);
        Size = new Size(980, 560);
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
        _entries.Columns.Add("Kind", 110);
        _entries.Columns.Add("Identity", 260);
        _entries.Columns.Add("Expires", 130);
        _entries.Columns.Add("Used by", 80, HorizontalAlignment.Right);
        _entries.Columns.Add("Tags", 140);
        StorageHubTheme.ConfigureList(_entries);
        _entries.SelectedIndexChanged += (_, _) => UpdateActionState();

        _search = new TextBox
        {
            Width = 220,
            PlaceholderText = "Search name or description",
            AccessibleName = "Key store search"
        };
        _search.TextChanged += async (_, _) => await ReloadAsync().ConfigureAwait(true);

        _importCertificate = CreateButton("Import certificate...", ImportCertificateAsync);
        _importKey = CreateButton("Import SSH key...", ImportSshKeyAsync);
        _rename = CreateButton("Rename...", RenameSelectedAsync);
        _delete = CreateButton("Delete", DeleteSelectedAsync);
        StorageHubTheme.StylePrimaryButton(_importCertificate);
        StorageHubTheme.StyleSecondaryButton(_importKey);
        StorageHubTheme.StyleSecondaryButton(_rename);
        StorageHubTheme.StyleSecondaryButton(_delete);
        _delete.BackColor = StorageHubTheme.Danger;
        _delete.ForeColor = Color.White;

        _status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 28,
            Padding = new Padding(10, 6, 10, 4),
            ForeColor = StorageHubTheme.TextMuted,
            BackColor = StorageHubTheme.Surface,
            Text = "Loading..."
        };

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(10, 10, 10, 6),
            BackColor = StorageHubTheme.Canvas,
            WrapContents = false
        };
        toolbar.Controls.AddRange([_importCertificate, _importKey, _rename, _delete, _search]);

        var body = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 0, 10, 6),
            BackColor = StorageHubTheme.Canvas
        };
        body.Controls.Add(_entries);

        Controls.Add(body);
        Controls.Add(toolbar);
        Controls.Add(_status);
        UpdateActionState();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await ReloadAsync().ConfigureAwait(true);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        base.OnFormClosed(e);
    }

    private Button CreateButton(string text, Func<Task> handler)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(0, 0, 8, 0),
            Height = 30
        };
        button.Click += async (_, _) =>
        {
            if (_busy) return;
            await RunAsync(handler).ConfigureAwait(true);
        };
        return button;
    }

    private async Task RunAsync(Func<Task> handler)
    {
        SetBusy(true);
        try
        {
            await handler().ConfigureAwait(true);
        }
        catch (Exception error) when (IsExpected(error))
        {
            ShowStatus(error.Message, StorageHubTheme.Danger);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ReloadAsync()
    {
        if (_lifetime.IsCancellationRequested) return;
        try
        {
            var response = await _client.ListAsync(
                new KeyStoreListRequest(
                    KeyStoreIpcContract.CurrentVersion,
                    string.IsNullOrWhiteSpace(_search.Text) ? null : _search.Text.Trim()),
                _lifetime.Token).ConfigureAwait(true);
            if (response.Failure is not null)
            {
                ShowStatus(response.Failure.Message, StorageHubTheme.Danger);
                return;
            }

            Populate(response.Entries);
            ShowStatus(
                response.Entries.Length == 0
                    ? "No keys or certificates are stored yet."
                    : $"{response.Entries.Length:N0} stored item(s).",
                StorageHubTheme.TextMuted);
        }
        catch (Exception error) when (IsExpected(error))
        {
            ShowStatus($"The key store is unavailable: {error.Message}", StorageHubTheme.Danger);
        }
    }

    private void Populate(KeyStoreEntryDocument[] entries)
    {
        _loaded.Clear();
        _loaded.AddRange(entries);
        _entries.BeginUpdate();
        try
        {
            _entries.Items.Clear();
            foreach (var entry in entries)
            {
                var item = new ListViewItem(entry.DisplayName) { Tag = entry };
                item.SubItems.Add(entry.Kind is KeyStoreMaterialKind.Pkcs12Certificate
                    ? "Certificate"
                    : "SSH key");
                item.SubItems.Add(DescribeIdentity(entry));
                var expiry = item.SubItems.Add(DescribeExpiry(entry, out var severity));
                expiry.ForeColor = severity;
                item.SubItems.Add(entry.ReferencedByProfiles.Length.ToString(CultureInfo.CurrentCulture));
                item.SubItems.Add(string.Join(", ", entry.Tags));
                _entries.Items.Add(item);
            }
        }
        finally
        {
            _entries.EndUpdate();
        }

        UpdateActionState();
    }

    private static string DescribeIdentity(KeyStoreEntryDocument entry) =>
        entry.Kind is KeyStoreMaterialKind.Pkcs12Certificate
            ? entry.Summary.Subject ?? "(unknown subject)"
            : entry.Summary.Sha256Fingerprint ?? "(unknown fingerprint)";

    /// <summary>
    /// Certificates carry a hard expiry that silently breaks a connection when it passes, so the
    /// column warns before that happens. SSH keys have no expiry to report.
    /// </summary>
    private static string DescribeExpiry(KeyStoreEntryDocument entry, out Color severity)
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

    private async Task ImportCertificateAsync() => await ImportAsync(
        KeyStoreMaterialKind.Pkcs12Certificate,
        "PKCS#12 certificates (*.pfx;*.p12)|*.pfx;*.p12",
        SecretMaterialPurpose.ClientCertificatePfx,
        SecretMaterialPurpose.ClientCertificatePassword,
        "Certificate password",
        keyFormat: null).ConfigureAwait(true);

    private async Task ImportSshKeyAsync()
    {
        using var picker = new KeyFormatPromptForm();
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        await ImportAsync(
            KeyStoreMaterialKind.SshPrivateKey,
            "Private keys (*.key;*.pem;*)|*.key;*.pem;*",
            SecretMaterialPurpose.SshPrivateKey,
            SecretMaterialPurpose.SshPrivateKeyPassphrase,
            "Key passphrase",
            picker.SelectedFormat).ConfigureAwait(true);
    }

    private async Task ImportAsync(
        KeyStoreMaterialKind kind,
        string filter,
        SecretMaterialPurpose materialPurpose,
        SecretMaterialPurpose passphrasePurpose,
        string passphraseCaption,
        KeyStorePrivateKeyFormat? keyFormat)
    {
        using var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true, Title = "Select material" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var file = new FileInfo(dialog.FileName);
        if (file.Length is 0 or > MaximumMaterialBytes)
        {
            ShowStatus("The selected file is empty or larger than 16 MB.", StorageHubTheme.Danger);
            return;
        }

        using var passphrasePrompt = new SecretPromptForm(passphraseCaption);
        if (passphrasePrompt.ShowDialog(this) != DialogResult.OK) return;

        if (DescribeMissingPassphrase(kind, passphrasePrompt.Value) is { } missing)
        {
            ShowStatus(missing, StorageHubTheme.Danger);
            return;
        }

        using var namePrompt = new TextPromptForm("Name this entry", Path.GetFileNameWithoutExtension(file.Name));
        if (namePrompt.ShowDialog(this) != DialogResult.OK) return;

        var material = await File.ReadAllBytesAsync(file.FullName, _lifetime.Token).ConfigureAwait(true);
        var passphrase = Encoding.UTF8.GetBytes(passphrasePrompt.Value);
        string? materialReference = null;
        string? passphraseReference = null;
        try
        {
            var enrolledMaterial = await _secrets
                .EnrollAsync(materialPurpose, material, _lifetime.Token).ConfigureAwait(true);
            if (!enrolledMaterial.Succeeded || enrolledMaterial.Reference is null)
            {
                ShowStatus(
                    enrolledMaterial.Failure?.Message ?? "The material could not be enrolled.",
                    StorageHubTheme.Danger);
                return;
            }

            materialReference = enrolledMaterial.Reference;
            var enrolledPassphrase = await _secrets
                .EnrollAsync(passphrasePurpose, passphrase, _lifetime.Token).ConfigureAwait(true);
            if (!enrolledPassphrase.Succeeded || enrolledPassphrase.Reference is null)
            {
                ShowStatus(
                    enrolledPassphrase.Failure?.Message ?? "The passphrase could not be enrolled.",
                    StorageHubTheme.Danger);
                return;
            }

            passphraseReference = enrolledPassphrase.Reference;
            var created = await _client.CreateAsync(
                new KeyStoreCreateRequest(
                    KeyStoreIpcContract.CurrentVersion,
                    kind,
                    namePrompt.Value,
                    null,
                    [],
                    materialReference,
                    passphraseReference,
                    keyFormat),
                _lifetime.Token).ConfigureAwait(true);

            if (created.Outcome is KeyStoreWriteOutcome.Applied)
            {
                // The agent now owns both envelopes; clearing the locals stops the cleanup below.
                materialReference = null;
                passphraseReference = null;
                await ReloadAsync().ConfigureAwait(true);
                ShowStatus($"Imported '{namePrompt.Value}'.", StorageHubTheme.Success);
                return;
            }

            ShowStatus(DescribeFailure(created), StorageHubTheme.Danger);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
            CryptographicOperations.ZeroMemory(passphrase);
            // An enrollment that never became an entry would otherwise leave an orphan envelope.
            await DiscardOrphanAsync(materialReference, materialPurpose).ConfigureAwait(true);
            await DiscardOrphanAsync(passphraseReference, passphrasePurpose).ConfigureAwait(true);
        }
    }

    private async Task DiscardOrphanAsync(string? reference, SecretMaterialPurpose purpose)
    {
        if (reference is null) return;
        try
        {
            _ = await _secrets.DeleteAsync(reference, purpose, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception error) when (IsExpected(error))
        {
            // Best effort: the entry was not created, so nothing references the envelope.
        }
    }

    private async Task RenameSelectedAsync()
    {
        if (Selected() is not { } entry) return;
        using var prompt = new TextPromptForm("Rename entry", entry.DisplayName);
        if (prompt.ShowDialog(this) != DialogResult.OK) return;

        var updated = await _client.UpdateAsync(
            new KeyStoreUpdateRequest(
                KeyStoreIpcContract.CurrentVersion,
                entry.EntryId,
                prompt.Value,
                entry.Description,
                entry.Tags,
                entry.Version),
            _lifetime.Token).ConfigureAwait(true);

        if (updated.Outcome is KeyStoreWriteOutcome.Applied)
        {
            await ReloadAsync().ConfigureAwait(true);
            ShowStatus("Renamed.", StorageHubTheme.Success);
            return;
        }

        ShowStatus(DescribeFailure(updated), StorageHubTheme.Danger);
    }

    private async Task DeleteSelectedAsync()
    {
        if (Selected() is not { } entry) return;
        if (entry.ReferencedByProfiles.Length > 0)
        {
            ShowStatus(
                $"'{entry.DisplayName}' is still used by {string.Join(", ", entry.ReferencedByProfiles)}.",
                StorageHubTheme.Danger);
            return;
        }

        var confirmed = MessageBox.Show(
            this,
            $"Permanently delete '{entry.DisplayName}'? The stored material cannot be recovered.",
            "Delete stored key",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirmed != DialogResult.Yes) return;

        var deleted = await _client.DeleteAsync(
            new KeyStoreDeleteRequest(KeyStoreIpcContract.CurrentVersion, entry.EntryId, entry.Version),
            _lifetime.Token).ConfigureAwait(true);

        if (deleted.Outcome is KeyStoreWriteOutcome.Applied)
        {
            await ReloadAsync().ConfigureAwait(true);
            ShowStatus("Deleted.", StorageHubTheme.Success);
            return;
        }

        ShowStatus(DescribeFailure(deleted), StorageHubTheme.Danger);
    }

    /// <summary>
    /// Explains why unprotected material is refused, or null when the secret is usable.
    ///
    /// StorageHub requires key material to carry its own password. The profile model enforces it
    /// too: an FTPS client certificate must have a vault-backed password reference, and the SFTP
    /// connector rejects an unprotected private key outright. Catching it here turns what would
    /// otherwise surface as a vault range error into something actionable.
    /// </summary>
    internal static string? DescribeMissingPassphrase(KeyStoreMaterialKind kind, string? value) =>
        !string.IsNullOrEmpty(value)
            ? null
            : kind is KeyStoreMaterialKind.Pkcs12Certificate
                ? "StorageHub cannot store a certificate without a password. Export the .pfx again with one, then import it."
                : "StorageHub cannot store an unprotected private key. Add a passphrase to the key, then import it.";

    internal static string DescribeFailure(KeyStoreWriteResponse response) => response.Outcome switch
    {
        KeyStoreWriteOutcome.NameConflict => "Another entry already uses that name.",
        KeyStoreWriteOutcome.VersionConflict =>
            "The entry changed elsewhere. Reopen the key store and try again.",
        KeyStoreWriteOutcome.NotFound => "The entry no longer exists.",
        KeyStoreWriteOutcome.StillReferenced => response.ReferencedByProfiles is { Length: > 0 } names
            ? $"The entry is still used by {string.Join(", ", names)}."
            : "The entry is still used by a saved connection.",
        _ => response.Failure?.Message ?? "The key store rejected the request."
    };

    private KeyStoreEntryDocument? Selected() => _entries.SelectedItems.Count == 1
        ? _entries.SelectedItems[0].Tag as KeyStoreEntryDocument
        : null;

    private void UpdateActionState()
    {
        var hasSelection = Selected() is not null;
        _rename.Enabled = hasSelection && !_busy;
        _delete.Enabled = hasSelection && !_busy;
        _importCertificate.Enabled = !_busy;
        _importKey.Enabled = !_busy;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UseWaitCursor = busy;
        UpdateActionState();
    }

    private void ShowStatus(string message, Color color)
    {
        _status.Text = message;
        _status.ForeColor = color;
    }

    private static bool IsExpected(Exception error) => error is
        IOException or UnauthorizedAccessException or InvalidDataException or
        InvalidOperationException or TimeoutException or OperationCanceledException or
        System.Text.Json.JsonException or ArgumentException;
}

/// <summary>Collects a passphrase without echoing it or keeping it in a control's history.</summary>
internal sealed class SecretPromptForm : Form
{
    private readonly TextBox _value;

    public SecretPromptForm(string caption)
    {
        Text = caption;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(400, 130);
        BackColor = StorageHubTheme.Canvas;
        ForeColor = StorageHubTheme.Text;

        var label = new Label
        {
            Text = caption,
            Location = new Point(14, 16),
            AutoSize = true,
            ForeColor = StorageHubTheme.Text
        };
        _value = new TextBox
        {
            Location = new Point(14, 42),
            Width = 370,
            UseSystemPasswordChar = true,
            AccessibleName = caption
        };
        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(214, 82),
            Size = new Size(84, 30)
        };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(300, 82),
            Size = new Size(84, 30)
        };
        StorageHubTheme.StylePrimaryButton(ok);
        StorageHubTheme.StyleSecondaryButton(cancel);
        Controls.AddRange([label, _value, ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public string Value => _value.Text;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _value.Clear();
        }

        base.Dispose(disposing);
    }
}

internal sealed class TextPromptForm : Form
{
    private readonly TextBox _value;

    public TextPromptForm(string caption, string initial)
    {
        Text = caption;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(400, 130);
        BackColor = StorageHubTheme.Canvas;
        ForeColor = StorageHubTheme.Text;

        var label = new Label
        {
            Text = caption,
            Location = new Point(14, 16),
            AutoSize = true,
            ForeColor = StorageHubTheme.Text
        };
        _value = new TextBox
        {
            Location = new Point(14, 42),
            Width = 370,
            Text = initial,
            MaxLength = KeyStoreIpcLimits.MaximumDisplayNameLength,
            AccessibleName = caption
        };
        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(214, 82),
            Size = new Size(84, 30)
        };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(300, 82),
            Size = new Size(84, 30)
        };
        StorageHubTheme.StylePrimaryButton(ok);
        StorageHubTheme.StyleSecondaryButton(cancel);
        Controls.AddRange([label, _value, ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public string Value => _value.Text.Trim();
}

/// <summary>
/// Asks which envelope an SSH private key uses. StorageHub cannot infer it safely, and the agent
/// validates the declared format against the material before the entry is created.
/// </summary>
internal sealed class KeyFormatPromptForm : Form
{
    private readonly ComboBox _format;

    public KeyFormatPromptForm()
    {
        Text = "Private key format";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(400, 130);
        BackColor = StorageHubTheme.Canvas;
        ForeColor = StorageHubTheme.Text;

        var label = new Label
        {
            Text = "Which envelope does the key use?",
            Location = new Point(14, 16),
            AutoSize = true,
            ForeColor = StorageHubTheme.Text
        };
        _format = new ComboBox
        {
            Location = new Point(14, 42),
            Width = 370,
            DropDownStyle = ComboBoxStyle.DropDownList,
            AccessibleName = "Private key format"
        };
        _format.Items.AddRange(["OpenSSH (openssh-key-v1)", "Legacy PEM", "PKCS#8"]);
        _format.SelectedIndex = 0;
        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(214, 82),
            Size = new Size(84, 30)
        };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(300, 82),
            Size = new Size(84, 30)
        };
        StorageHubTheme.StylePrimaryButton(ok);
        StorageHubTheme.StyleSecondaryButton(cancel);
        Controls.AddRange([label, _format, ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public KeyStorePrivateKeyFormat SelectedFormat => _format.SelectedIndex switch
    {
        1 => KeyStorePrivateKeyFormat.Pem,
        2 => KeyStorePrivateKeyFormat.Pkcs8,
        _ => KeyStorePrivateKeyFormat.OpenSsh
    };
}
