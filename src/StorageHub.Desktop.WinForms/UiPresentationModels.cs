using System.Collections.ObjectModel;
using System.Globalization;

namespace StorageHub.Desktop;

public enum StorageProviderKind
{
    Local,
    S3,
    Ftp,
    Ftps,
    Sftp
}

public enum ConnectionFieldKind
{
    Text,
    Path,
    Number,
    Choice,
    Toggle,
    SecretReference,
    CertificateReference,
    Fingerprint
}

public sealed record ConnectionFieldDescriptor(
    string Key,
    string Label,
    ConnectionFieldKind Kind,
    bool Required = false,
    string DefaultValue = "",
    string Placeholder = "",
    string HelpText = "",
    IReadOnlyList<string>? Choices = null);

public sealed record ConnectionProviderDescriptor(
    StorageProviderKind Kind,
    string DisplayName,
    string ShortName,
    string AccentHex,
    int? DefaultPort,
    bool EncryptedByDefault,
    string Summary,
    string EndpointExample,
    string TrustNotice,
    IReadOnlyList<ConnectionFieldDescriptor> GeneralFields,
    IReadOnlyList<ConnectionFieldDescriptor> AuthenticationFields,
    IReadOnlyList<ConnectionFieldDescriptor> SecurityFields)
{
    public override string ToString() => DisplayName;
}

public static class ConnectionProviderCatalog
{
    private static readonly ReadOnlyCollection<string> AddressingStyles =
        Array.AsReadOnly(new[] { "Virtual-hosted (recommended)", "Path-style" });

    private static readonly ReadOnlyCollection<string> S3ServiceTypes =
        Array.AsReadOnly(new[]
        {
            ConnectionEditorDraftFactory.AmazonS3ServiceType,
            ConnectionEditorDraftFactory.CloudflareR2ServiceType,
            ConnectionEditorDraftFactory.OtherS3ServiceType
        });

    private static readonly ReadOnlyCollection<string> FtpTlsModes =
        Array.AsReadOnly(new[] { "Explicit TLS (recommended)", "Implicit TLS" });

    private static readonly ReadOnlyCollection<string> TrustModes =
        Array.AsReadOnly(new[] { "System trust + hostname", "System trust + certificate pin" });

    private static readonly ReadOnlyCollection<string> SshAuthenticationModes =
        Array.AsReadOnly(new[] { "Private key reference", "Password reference" });

    private static readonly ReadOnlyCollection<ConnectionProviderDescriptor> Providers = Array.AsReadOnly(
    new ConnectionProviderDescriptor[]
    {
        new(
            StorageProviderKind.Local,
            "Local / UNC",
            "LOCAL",
            "#4C8BF5",
            null,
            true,
            "Local disks, mapped drives, and Windows network shares.",
            @"C:\Data or \\server\share",
            "The current Windows identity is used. Operations remain restricted to the configured root.",
            [
                Field("rootPath", "Root path", ConnectionFieldKind.Path, required: true, placeholder: @"C:\Data or \\server\share")
            ],
            [],
            []),
        new(
            StorageProviderKind.S3,
            "S3 / Object Storage",
            "S3",
            "#F59E0B",
            443,
            true,
            "Amazon S3 and S3-compatible object stores through CL.Storage.",
            "https://s3.example.com",
            "The current CL.Storage S3 provider uses system TLS and hostname validation. Per-connection S3 certificate pins are not offered until the provider can enforce them.",
            [
                Field("s3ServiceType", "Object-store service", ConnectionFieldKind.Choice, defaultValue: S3ServiceTypes[0], choices: S3ServiceTypes),
                Field("endpoint", "Service endpoint", ConnectionFieldKind.Text, required: true, defaultValue: "https://s3.amazonaws.com", placeholder: "account-id.r2.cloudflarestorage.com", help: "A hostname is accepted and upgraded to HTTPS automatically. Do not use a public bucket URL."),
                Field("region", "Signing region", ConnectionFieldKind.Text, defaultValue: "us-east-1", help: "Amazon and most compatible services require a signing region. Cloudflare R2 uses 'auto', which StorageHub selects automatically."),
                Field("bucket", "Bucket", ConnectionFieldKind.Text, required: true),
                Field("prefix", "Initial prefix", ConnectionFieldKind.Text, placeholder: "team/archive/"),
                Field("addressingStyle", "Addressing style", ConnectionFieldKind.Choice, defaultValue: AddressingStyles[0], choices: AddressingStyles, help: "Cloudflare R2 and many compatible services use path-style addressing; Amazon S3 normally uses virtual-hosted addressing.")
            ],
            [
                Field("accessKeyReference", "Access key reference", ConnectionFieldKind.SecretReference, placeholder: "Optional when using a provider credential chain"),
                Field("secretAccessKeyReference", "Secret access key reference", ConnectionFieldKind.SecretReference, placeholder: "Select a vault entry"),
                Field("sessionTokenReference", "Session token reference", ConnectionFieldKind.SecretReference, placeholder: "Optional vault entry")
            ],
            []),
        new(
            StorageProviderKind.Ftp,
            "FTP",
            "FTP",
            "#64748B",
            21,
            false,
            "Legacy FTP for compatible servers. Prefer FTPS or SFTP.",
            "ftp.example.com",
            "FTP sends credentials and data without transport encryption. StorageHub shows a persistent warning and never silently upgrades or suppresses trust checks.",
            [
                Field("host", "Host", ConnectionFieldKind.Text, required: true, placeholder: "ftp.example.com"),
                Field("port", "Port", ConnectionFieldKind.Number, required: true, defaultValue: "21"),
                Field("initialPath", "Initial path", ConnectionFieldKind.Text, defaultValue: "/")
            ],
            [
                Field("username", "Username", ConnectionFieldKind.Text, required: true),
                Field("passwordReference", "Password reference", ConnectionFieldKind.SecretReference, required: true, placeholder: "Select a vault entry")
            ],
            [
                Field("acknowledgePlaintext", "Acknowledge plaintext transport", ConnectionFieldKind.Toggle, defaultValue: "false", help: "Required before this connection can be enabled.")
            ]),
        new(
            StorageProviderKind.Ftps,
            "FTPS",
            "FTPS",
            "#10B981",
            21,
            true,
            "FTP secured with explicit or implicit TLS.",
            "ftps.example.com",
            "The server certificate must pass hostname and chain validation. An optional SHA-256 pin adds protection; client PFX material remains in the vault.",
            [
                Field("host", "Host", ConnectionFieldKind.Text, required: true, placeholder: "ftps.example.com"),
                Field("port", "Port", ConnectionFieldKind.Number, required: true, defaultValue: "21"),
                Field("initialPath", "Initial path", ConnectionFieldKind.Text, defaultValue: "/"),
                Field("tlsMode", "TLS mode", ConnectionFieldKind.Choice, defaultValue: FtpTlsModes[0], choices: FtpTlsModes)
            ],
            [
                Field("username", "Username", ConnectionFieldKind.Text, required: true),
                Field("passwordReference", "Password reference", ConnectionFieldKind.SecretReference, required: true, placeholder: "Select a vault entry")
            ],
            [
                Field("trustMode", "Server trust", ConnectionFieldKind.Choice, defaultValue: TrustModes[0], choices: TrustModes),
                Field("certificatePin", "Certificate SHA-256 pin", ConnectionFieldKind.Fingerprint, placeholder: "Optional; verify out of band"),
                Field("clientCertificateReference", "Client PFX certificate reference", ConnectionFieldKind.CertificateReference, placeholder: "Select an imported certificate"),
                Field("clientCertificatePasswordReference", "PFX password reference", ConnectionFieldKind.SecretReference, placeholder: "Required for imported PFX", help: "StorageHub does not materialize unprotected client private keys.")
            ]),
        new(
            StorageProviderKind.Sftp,
            "SFTP",
            "SFTP",
            "#8B5CF6",
            22,
            true,
            "SSH File Transfer Protocol using open-source .NET libraries; no PuTTY dependency.",
            "sftp.example.com",
            "SSH host keys are never accepted silently. Verify and pin the SHA-256 fingerprint obtained through a separate trusted channel.",
            [
                Field("host", "Host", ConnectionFieldKind.Text, required: true, placeholder: "sftp.example.com"),
                Field("port", "Port", ConnectionFieldKind.Number, required: true, defaultValue: "22"),
                Field("initialPath", "Initial path", ConnectionFieldKind.Text, defaultValue: "/")
            ],
            [
                Field("username", "Username", ConnectionFieldKind.Text, required: true),
                Field("authenticationMode", "Authentication", ConnectionFieldKind.Choice, defaultValue: SshAuthenticationModes[0], choices: SshAuthenticationModes),
                Field("passwordReference", "Password reference", ConnectionFieldKind.SecretReference, placeholder: "Optional vault entry"),
                Field("privateKeyReference", "OpenSSH / PEM private-key reference", ConnectionFieldKind.SecretReference, placeholder: "Select a vault entry"),
                Field("privateKeyPassphraseReference", "Private-key passphrase reference", ConnectionFieldKind.SecretReference, required: true, placeholder: "Required vault entry", help: "Only encrypted private keys are accepted.")
            ],
            [
                Field("hostKeyFingerprint", "SSH host-key SHA-256 fingerprint", ConnectionFieldKind.Fingerprint, required: true, placeholder: "SHA256:...")
            ])
    });

    public static IReadOnlyList<ConnectionProviderDescriptor> All => Providers;

    public static ConnectionProviderDescriptor Get(StorageProviderKind kind) =>
        Providers.First(provider => provider.Kind == kind);

    private static ConnectionFieldDescriptor Field(
        string key,
        string label,
        ConnectionFieldKind kind,
        bool required = false,
        string defaultValue = "",
        string placeholder = "",
        string help = "",
        IReadOnlyList<string>? choices = null) =>
        new(key, label, kind, required, defaultValue, placeholder, help, choices);
}

public enum AgentConnectionState
{
    Starting,
    Connected,
    RecoveryOnly,
    Disconnected
}

public sealed record ShellStatusSnapshot(
    string Location,
    int SelectedItems,
    long SelectedBytes,
    long TransferBytesPerSecond,
    int QueuedJobs,
    int ActiveJobs,
    AgentConnectionState AgentState)
{
    public static ShellStatusSnapshot Initial { get; } =
        new("No connection", 0, 0, 0, 0, 0, AgentConnectionState.Starting);

    public string SelectionText => SelectedItems == 0
        ? "0 selected"
        : string.Create(CultureInfo.CurrentCulture, $"{SelectedItems:N0} selected · {UiFormatting.FormatBytes(SelectedBytes)}");

    public string TransferRateText => $"{UiFormatting.FormatBytes(TransferBytesPerSecond)}/s";

    public string QueueText => ActiveJobs == 0
        ? string.Create(CultureInfo.CurrentCulture, $"Queue: {QueuedJobs:N0}")
        : string.Create(CultureInfo.CurrentCulture, $"Queue: {QueuedJobs:N0} · Active: {ActiveJobs:N0}");

    public string AgentText => AgentState switch
    {
        AgentConnectionState.Starting => "Agent: starting",
        AgentConnectionState.Connected => "Agent: connected",
        AgentConnectionState.RecoveryOnly => "Agent: recovery mode",
        _ => "Agent: not connected"
    };
}

public sealed record ConnectionCardModel(
    string Name,
    StorageProviderKind Provider,
    string Endpoint,
    string State,
    bool IsFavorite = false,
    Guid? ConnectionId = null,
    bool IsEnabled = true,
    string? AccentColor = null,
    string? FolderPath = null,
    string[]? Tags = null)
{
    public ConnectionProviderDescriptor Descriptor => ConnectionProviderCatalog.Get(Provider);

    public string AccentHex => string.IsNullOrWhiteSpace(AccentColor)
        ? Descriptor.AccentHex
        : AccentColor;

    public IReadOnlyList<string> DisplayTags => Tags ?? [];

    public override string ToString() => Name;
}

public sealed record QuickConnectDraft(
    StorageProviderKind Provider,
    string HostOrPath,
    int? Port,
    string UserName,
    bool UseSecureTransport)
{
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(HostOrPath))
        {
            errors.Add(Provider == StorageProviderKind.Local ? "A local or UNC path is required." : "A host or endpoint is required.");
        }

        if (Provider != StorageProviderKind.Local && (Port is < 1 or > 65535))
        {
            errors.Add("Port must be between 1 and 65535.");
        }

        if (Provider == StorageProviderKind.Ftp && !UseSecureTransport)
        {
            errors.Add("Plain FTP is unencrypted; use FTPS or SFTP unless compatibility requires it.");
        }

        return errors.AsReadOnly();
    }
}

public enum SyncModeKind
{
    BackupLeftToRight,
    UpdateLeftToRight,
    ExactMirror,
    TwoWay,
    CompareOnly
}

public sealed record SyncModeDescriptor(
    SyncModeKind Kind,
    string DisplayName,
    string Summary,
    bool CanPropagateDeletes,
    bool RequiresPreview);

public static class SyncPresentationCatalog
{
    private static readonly ReadOnlyCollection<SyncModeDescriptor> Modes = Array.AsReadOnly(
    new SyncModeDescriptor[]
    {
        new(SyncModeKind.BackupLeftToRight, "Backup left → right", "Copy new and changed items; never delete destination items.", false, true),
        new(SyncModeKind.UpdateLeftToRight, "Update left → right", "Copy new and changed items; deletion propagation stays opt-in.", true, true),
        new(SyncModeKind.ExactMirror, "Exact mirror", "Make the destination match the source, including reviewed deletions.", true, true),
        new(SyncModeKind.TwoWay, "Two-way", "Merge changes using the last complete baseline and explicit conflict policy.", true, true),
        new(SyncModeKind.CompareOnly, "Compare only", "Build a plan without changing either endpoint.", false, false)
    });

    public static IReadOnlyList<SyncModeDescriptor> AllModes => Modes;

    public static int DefaultMassDeleteItemLimit => 100;

    public static decimal DefaultMassDeletePercentageLimit => 10m;

    public static bool DeletePropagationEnabledByDefault => false;
}

public static class UiFormatting
{
    private static readonly string[] SizeSuffixes = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];

    public static string FormatBytes(long bytes)
    {
        var negative = bytes < 0;
        var value = Math.Abs((double)bytes);
        var suffix = 0;
        while (value >= 1024 && suffix < SizeSuffixes.Length - 1)
        {
            value /= 1024;
            suffix++;
        }

        var prefix = negative ? "−" : string.Empty;
        return string.Create(CultureInfo.CurrentCulture, $"{prefix}{value:0.#} {SizeSuffixes[suffix]}");
    }
}
