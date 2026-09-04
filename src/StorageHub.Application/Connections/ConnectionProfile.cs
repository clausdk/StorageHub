using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using StorageHub.Domain.Identifiers;
using StorageHub.Security;

namespace StorageHub.Application.Connections;

public enum ConnectionProfileType
{
    Storage = 0,
    Client = 1
}

public enum ConnectionProviderKind
{
    Local = 1,
    S3 = 2,
    Ftp = 3,
    Ftps = 4,
    Sftp = 5,
    Ssh = 6
}

public enum TlsCertificatePolicy
{
    Unspecified = 0,
    SystemTrust = 1,
    Pinned = 2,
    TrustOnFirstUse = 3
}

public enum SshHostKeyPolicy
{
    Unspecified = 0,
    Pinned = 1,
    TrustOnFirstUse = 2
}

public enum FtpsTlsMode
{
    Explicit = 1,
    Implicit = 2
}

public enum SftpPrivateKeyFormat
{
    OpenSsh = 1,
    Pem = 2,
    Pkcs8 = 3
}

public sealed record ConnectionDefaultPaths
{
    public ConnectionDefaultPaths(string? homePath = null, string? uploadPath = null, string? downloadPath = null)
    {
        HomePath = NormalizeOptional(homePath, nameof(homePath));
        UploadPath = NormalizeOptional(uploadPath, nameof(uploadPath));
        DownloadPath = NormalizeOptional(downloadPath, nameof(downloadPath));
    }

    public string? HomePath { get; }
    public string? UploadPath { get; }
    public string? DownloadPath { get; }

    private static string? NormalizeOptional(string? value, string parameterName)
    {
        var normalized = value?.Trim();
        if (normalized?.Length > 2_048)
        {
            throw new ArgumentOutOfRangeException(parameterName, "A default path cannot exceed 2,048 characters.");
        }

        if (normalized is not null && (normalized.Any(char.IsControl) ||
            normalized.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(static part => part == "..")))
        {
            throw new ArgumentException("A default path cannot contain control characters or parent traversal.", parameterName);
        }

        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}

public sealed record ConnectionProfileMetadata
{
    private static readonly Regex AccentColorPattern = new(
        "^#[0-9A-Fa-f]{6}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public ConnectionProfileMetadata(
        string displayName,
        string? folderPath = null,
        IEnumerable<string>? tags = null,
        bool isFavorite = false,
        ConnectionDefaultPaths? defaultPaths = null,
        string? iconKey = null,
        string? accentColor = null,
        string? notes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        var normalizedName = displayName.Trim();
        if (normalizedName.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(displayName), "The display name cannot exceed 128 characters.");
        }
        if (normalizedName.Any(char.IsControl))
        {
            throw new ArgumentException("The display name cannot contain control characters.", nameof(displayName));
        }

        DisplayName = normalizedName;
        FolderPath = NormalizeOptional(folderPath, 512, nameof(folderPath));
        Tags = NormalizeTags(tags);
        IsFavorite = isFavorite;
        DefaultPaths = defaultPaths ?? new ConnectionDefaultPaths();
        IconKey = NormalizeOptional(iconKey, 64, nameof(iconKey));
        AccentColor = NormalizeAccentColor(accentColor);
        Notes = NormalizeOptional(notes, 4_096, nameof(notes), allowLayoutControls: true);
    }

    public string DisplayName { get; }
    public string? FolderPath { get; }
    public ImmutableArray<string> Tags { get; }
    public bool IsFavorite { get; }
    public ConnectionDefaultPaths DefaultPaths { get; }
    public string? IconKey { get; }
    public string? AccentColor { get; }
    public string? Notes { get; }

    private static ImmutableArray<string> NormalizeTags(IEnumerable<string>? tags)
    {
        if (tags is null)
        {
            return [];
        }

        var normalized = tags
            .Select(static tag => tag?.Trim() ?? string.Empty)
            .Where(static tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        if (normalized.Length > 64 || normalized.Any(static tag => tag.Length > 64))
        {
            throw new ArgumentOutOfRangeException(nameof(tags), "At most 64 tags of 64 characters are allowed.");
        }
        if (normalized.Any(static tag => tag.Any(char.IsControl)))
        {
            throw new ArgumentException("Tags cannot contain control characters.", nameof(tags));
        }

        return normalized;
    }

    private static string? NormalizeAccentColor(string? accentColor)
    {
        var normalized = NormalizeOptional(accentColor, 7, nameof(accentColor));
        if (normalized is not null && !AccentColorPattern.IsMatch(normalized))
        {
            throw new ArgumentException("The accent color must use #RRGGBB notation.", nameof(accentColor));
        }

        return normalized?.ToUpperInvariant();
    }

    private static string? NormalizeOptional(
        string? value,
        int maximumLength,
        string parameterName,
        bool allowLayoutControls = false)
    {
        var normalized = value?.Trim();
        if (normalized?.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"The value cannot exceed {maximumLength} characters.");
        }

        if (normalized is not null && normalized.Any(character => char.IsControl(character) &&
            (!allowLayoutControls || character is not '\r' and not '\n' and not '\t')))
        {
            throw new ArgumentException("The value contains unsupported control characters.", parameterName);
        }

        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}

public sealed record ConnectionRetryPolicy
{
    public ConnectionRetryPolicy(int maximumAttempts, TimeSpan initialDelay, TimeSpan maximumDelay)
    {
        if (maximumAttempts is < 0 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        if (initialDelay < TimeSpan.Zero || initialDelay > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(initialDelay));
        }

        if (maximumDelay < initialDelay || maximumDelay > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDelay));
        }

        MaximumAttempts = maximumAttempts;
        InitialDelay = initialDelay;
        MaximumDelay = maximumDelay;
    }

    public int MaximumAttempts { get; }
    public TimeSpan InitialDelay { get; }
    public TimeSpan MaximumDelay { get; }
}

public sealed record ConnectionProxy
{
    public ConnectionProxy(Uri endpoint, CredentialReferenceId? credentialId = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https" or "socks5"))
        {
            throw new ArgumentException("The proxy must be an absolute HTTP, HTTPS, or SOCKS5 URI.", nameof(endpoint));
        }

        if (!string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment) ||
            endpoint.AbsolutePath is not ("" or "/"))
        {
            throw new ArgumentException(
                "Proxy endpoints cannot contain credentials, query parameters, fragments, or paths; use a credential reference.",
                nameof(endpoint));
        }

        if (credentialId is { IsEmpty: true })
        {
            throw new ArgumentException("The proxy credential reference cannot be empty.", nameof(credentialId));
        }

        Endpoint = endpoint;
        CredentialId = credentialId;
    }

    public Uri Endpoint { get; }
    public CredentialReferenceId? CredentialId { get; }
}

public sealed record ConnectionBandwidthLimits
{
    public ConnectionBandwidthLimits(long? uploadBytesPerSecond, long? downloadBytesPerSecond)
    {
        Validate(uploadBytesPerSecond, nameof(uploadBytesPerSecond));
        Validate(downloadBytesPerSecond, nameof(downloadBytesPerSecond));
        UploadBytesPerSecond = uploadBytesPerSecond;
        DownloadBytesPerSecond = downloadBytesPerSecond;
    }

    public long? UploadBytesPerSecond { get; }
    public long? DownloadBytesPerSecond { get; }

    private static void Validate(long? value, string parameterName)
    {
        if (value is <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "A bandwidth limit must be positive.");
        }
    }
}

public sealed record ConnectionOperationalOptions
{
    public ConnectionOperationalOptions(
        TimeSpan connectTimeout,
        TimeSpan operationTimeout,
        ConnectionRetryPolicy retry,
        ConnectionProxy? proxy,
        ConnectionBandwidthLimits bandwidth,
        string encodingName)
    {
        if (connectTimeout < TimeSpan.FromSeconds(1) || connectTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(connectTimeout));
        }

        if (operationTimeout < TimeSpan.FromSeconds(1) || operationTimeout > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        }

        ArgumentNullException.ThrowIfNull(retry);
        ArgumentNullException.ThrowIfNull(bandwidth);
        ArgumentException.ThrowIfNullOrWhiteSpace(encodingName);
        if (encodingName.Length > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(encodingName));
        }

        ConnectTimeout = connectTimeout;
        OperationTimeout = operationTimeout;
        Retry = retry;
        Proxy = proxy;
        Bandwidth = bandwidth;
        EncodingName = encodingName.Trim().ToLowerInvariant();
    }

    public TimeSpan ConnectTimeout { get; }
    public TimeSpan OperationTimeout { get; }
    public ConnectionRetryPolicy Retry { get; }
    public ConnectionProxy? Proxy { get; }
    public ConnectionBandwidthLimits Bandwidth { get; }
    public string EncodingName { get; }
}

public abstract record ConnectionEndpoint
{
    protected ConnectionEndpoint(ConnectionProviderKind provider) => Provider = provider;
    public ConnectionProviderKind Provider { get; }

    protected static string ValidateHost(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var value = host.Trim().TrimEnd('.').Trim('[', ']');
        if (IPAddress.TryParse(value, out var address))
        {
            return address.ToString().ToLowerInvariant();
        }

        if (value.Length > 253 || value.Any(static character =>
            char.IsControl(character) || char.IsWhiteSpace(character) || character is '/' or '\\' or '@' or ':' or '?' or '#'))
        {
            throw new ArgumentException("The endpoint host is invalid.", nameof(host));
        }

        try
        {
            var normalized = new IdnMapping().GetAscii(value).ToLowerInvariant();
            return Uri.CheckHostName(normalized) == UriHostNameType.Unknown
                ? throw new ArgumentException("The endpoint host is invalid.", nameof(host))
                : normalized;
        }
        catch (ArgumentException)
        {
            throw new ArgumentException("The endpoint host is invalid.", nameof(host));
        }
    }

    protected static int ValidatePort(int port) => port is >= 1 and <= 65_535
        ? port
        : throw new ArgumentOutOfRangeException(nameof(port));

    protected static void RejectCredentialBearingUri(Uri endpoint, string parameterName)
    {
        if (!string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException(
                "Endpoint URLs cannot contain credentials, query parameters, or fragments.",
                parameterName);
        }
    }

    protected static string NormalizeProviderRoot(string? rootPath, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return string.Empty;
        }

        var normalized = rootPath.Trim().Normalize().Replace('\\', '/').Trim('/');
        if (normalized.Length > 8_192 || normalized.Any(char.IsControl) ||
            normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(static part => part is "." or ".."))
        {
            throw new ArgumentException("The provider root is invalid or contains parent traversal.", parameterName);
        }

        return normalized;
    }
}

public sealed record LocalEndpoint : ConnectionEndpoint
{
    public LocalEndpoint(string rootPath) : base(ConnectionProviderKind.Local)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!Path.IsPathFullyQualified(rootPath))
        {
            throw new ArgumentException("The local root must be an absolute drive or UNC path.", nameof(rootPath));
        }

        RootPath = Path.GetFullPath(rootPath.Trim());
    }

    public string RootPath { get; }
}

public sealed record S3Endpoint : ConnectionEndpoint
{
    public S3Endpoint(
        string bucket,
        string region,
        Uri? serviceEndpoint = null,
        bool forcePathStyle = false,
        TlsCertificatePolicy tlsPolicy = TlsCertificatePolicy.SystemTrust,
        bool allowInsecureHttp = false,
        string? rootPrefix = null)
        : base(ConnectionProviderKind.S3)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        var normalizedBucket = bucket.Trim();
        var normalizedRegion = region.Trim();
        if (normalizedBucket.Length is < 3 or > 255 ||
            normalizedBucket.Any(static character => char.IsControl(character) || char.IsWhiteSpace(character) || character is '/' or '\\'))
        {
            throw new ArgumentException("The S3 bucket name is invalid.", nameof(bucket));
        }
        if (normalizedRegion.Length > 128 || normalizedRegion.Any(static character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            throw new ArgumentException("The S3 region is invalid.", nameof(region));
        }
        if (serviceEndpoint is not null && !serviceEndpoint.IsAbsoluteUri)
        {
            throw new ArgumentException("The S3 service endpoint must be absolute.", nameof(serviceEndpoint));
        }


        if (serviceEndpoint is not null)
        {
            RejectCredentialBearingUri(serviceEndpoint, nameof(serviceEndpoint));
            if (serviceEndpoint.Scheme == Uri.UriSchemeHttp && !allowInsecureHttp)
            {
                throw new ArgumentException(
                    "An HTTP S3 endpoint is insecure and requires explicit acknowledgement.",
                    nameof(allowInsecureHttp));
            }

            if (!string.Equals(serviceEndpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(serviceEndpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The S3 endpoint must use HTTP or HTTPS.", nameof(serviceEndpoint));
            }
        }

        ValidateTlsPolicy(tlsPolicy);
        Bucket = normalizedBucket;
        Region = normalizedRegion;
        ServiceEndpoint = serviceEndpoint;
        ForcePathStyle = forcePathStyle;
        TlsPolicy = tlsPolicy;
        AllowInsecureHttp = serviceEndpoint?.Scheme == Uri.UriSchemeHttp && allowInsecureHttp;
        RootPrefix = NormalizeProviderRoot(rootPrefix, nameof(rootPrefix));
    }

    public string Bucket { get; }
    public string Region { get; }
    public Uri? ServiceEndpoint { get; }
    public bool ForcePathStyle { get; }
    public TlsCertificatePolicy TlsPolicy { get; }
    public bool AllowInsecureHttp { get; }
    public string RootPrefix { get; }

    private static void ValidateTlsPolicy(TlsCertificatePolicy policy)
    {
        if (!Enum.IsDefined(policy) || policy == TlsCertificatePolicy.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "An S3 TLS certificate policy is required.");
        }
    }
}

public sealed record FtpEndpoint : ConnectionEndpoint
{
    public FtpEndpoint(string host, int port, bool allowInsecurePlainText, string? rootPath = null)
        : base(ConnectionProviderKind.Ftp)
    {
        if (!allowInsecurePlainText)
        {
            throw new ArgumentException(
                "Plain FTP is insecure and requires explicit acknowledgement.",
                nameof(allowInsecurePlainText));
        }

        Host = ValidateHost(host);
        Port = ValidatePort(port);
        AllowInsecurePlainText = true;
        RootPath = NormalizeProviderRoot(rootPath, nameof(rootPath));
    }

    public string Host { get; }
    public int Port { get; }
    public bool AllowInsecurePlainText { get; }
    public string RootPath { get; }
}

public sealed record FtpsEndpoint : ConnectionEndpoint
{
    public FtpsEndpoint(
        string host,
        int port,
        FtpsTlsMode tlsMode,
        TlsCertificatePolicy tlsPolicy,
        SecretReference? clientCertificatePfxReference = null,
        SecretReference? clientCertificatePasswordReference = null,
        string? rootPath = null)
        : base(ConnectionProviderKind.Ftps)
    {
        if (!Enum.IsDefined(tlsMode))
        {
            throw new ArgumentOutOfRangeException(nameof(tlsMode));
        }

        if (!Enum.IsDefined(tlsPolicy) || tlsPolicy == TlsCertificatePolicy.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(tlsPolicy), "An FTPS TLS certificate policy is required.");
        }

        ValidateReference(clientCertificatePfxReference, nameof(clientCertificatePfxReference));
        ValidateReference(clientCertificatePasswordReference, nameof(clientCertificatePasswordReference));
        if (clientCertificatePasswordReference is not null && clientCertificatePfxReference is null)
        {
            throw new ArgumentException(
                "A client-certificate password reference requires a PFX reference.",
                nameof(clientCertificatePasswordReference));
        }

        if (clientCertificatePfxReference is not null && clientCertificatePasswordReference is null)
        {
            throw new ArgumentException(
                "A client PFX must use a vault-backed password reference.",
                nameof(clientCertificatePasswordReference));
        }

        Host = ValidateHost(host);
        Port = ValidatePort(port);
        TlsMode = tlsMode;
        TlsPolicy = tlsPolicy;
        ClientCertificatePfxReference = clientCertificatePfxReference;
        ClientCertificatePasswordReference = clientCertificatePasswordReference;
        RootPath = NormalizeProviderRoot(rootPath, nameof(rootPath));
    }

    public string Host { get; }
    public int Port { get; }
    public FtpsTlsMode TlsMode { get; }
    public TlsCertificatePolicy TlsPolicy { get; }
    public SecretReference? ClientCertificatePfxReference { get; }
    public SecretReference? ClientCertificatePasswordReference { get; }
    public string RootPath { get; }

    private static void ValidateReference(SecretReference? reference, string parameterName)
    {
        if (reference is { } value && !SecretReference.TryParse(value.Value, out _))
        {
            throw new ArgumentException("The PFX secret reference is invalid.", parameterName);
        }
    }
}

public sealed record SftpEndpoint : ConnectionEndpoint
{
    public SftpEndpoint(string host, int port, SshHostKeyPolicy hostKeyPolicy, string? rootPath = null)
        : base(ConnectionProviderKind.Sftp)
    {
        if (!Enum.IsDefined(hostKeyPolicy) || hostKeyPolicy == SshHostKeyPolicy.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(hostKeyPolicy), "An SFTP host-key policy is required.");
        }

        Host = ValidateHost(host);
        Port = ValidatePort(port);
        HostKeyPolicy = hostKeyPolicy;
        RootPath = NormalizeProviderRoot(rootPath, nameof(rootPath));
    }

    public string Host { get; }
    public int Port { get; }
    public SshHostKeyPolicy HostKeyPolicy { get; }
    public string RootPath { get; }
}

public sealed record SshClientEndpoint : ConnectionEndpoint
{
    public SshClientEndpoint(string host, int port, SshHostKeyPolicy hostKeyPolicy)
        : base(ConnectionProviderKind.Ssh)
    {
        if (!Enum.IsDefined(hostKeyPolicy) || hostKeyPolicy == SshHostKeyPolicy.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(hostKeyPolicy), "An SSH host-key policy is required.");
        }

        Host = ValidateHost(host);
        Port = ValidatePort(port);
        HostKeyPolicy = hostKeyPolicy;
    }

    public string Host { get; }
    public int Port { get; }
    public SshHostKeyPolicy HostKeyPolicy { get; }
}

public abstract record ConnectionAuthentication;

public sealed record NoAuthentication : ConnectionAuthentication;

/// <summary>Explicitly opts an S3 profile into the host process's AWS credential chain.</summary>
public sealed record S3DefaultCredentialChainAuthentication : ConnectionAuthentication;

public sealed record CredentialReferenceAuthentication : ConnectionAuthentication
{
    public CredentialReferenceAuthentication(CredentialReferenceId credentialId)
    {
        if (credentialId.IsEmpty)
        {
            throw new ArgumentException("The credential reference cannot be empty.", nameof(credentialId));
        }

        CredentialId = credentialId;
    }

    public CredentialReferenceId CredentialId { get; }
}

public sealed record UsernamePasswordAuthentication : ConnectionAuthentication
{
    public UsernamePasswordAuthentication(string username, SecretReference passwordReference)
    {
        ValidateUsername(username);
        if (!SecretReference.TryParse(passwordReference.Value, out _))
        {
            throw new ArgumentException("The password secret reference is invalid.", nameof(passwordReference));
        }

        Username = username.Trim();
        PasswordReference = passwordReference;
    }

    public string Username { get; }
    public SecretReference PasswordReference { get; }

    private static void ValidateUsername(string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        if (username.Length > 256 || username.Any(char.IsControl))
        {
            throw new ArgumentException("The username is invalid.", nameof(username));
        }
    }
}

public sealed record S3AccessKeyAuthentication : ConnectionAuthentication
{
    public S3AccessKeyAuthentication(
        SecretReference accessKeyReference,
        SecretReference secretKeyReference,
        SecretReference? sessionTokenReference = null)
    {
        ValidateReference(accessKeyReference, nameof(accessKeyReference));
        ValidateReference(secretKeyReference, nameof(secretKeyReference));
        if (sessionTokenReference is { } token)
        {
            ValidateReference(token, nameof(sessionTokenReference));
        }

        AccessKeyReference = accessKeyReference;
        SecretKeyReference = secretKeyReference;
        SessionTokenReference = sessionTokenReference;
    }

    public SecretReference AccessKeyReference { get; }
    public SecretReference SecretKeyReference { get; }
    public SecretReference? SessionTokenReference { get; }

    private static void ValidateReference(SecretReference reference, string parameterName)
    {
        if (!SecretReference.TryParse(reference.Value, out _))
        {
            throw new ArgumentException("The S3 credential secret reference is invalid.", parameterName);
        }
    }
}

public sealed record SftpPrivateKeyAuthentication : ConnectionAuthentication
{
    public SftpPrivateKeyAuthentication(
        string username,
        SecretReference privateKeyReference,
        SecretReference? passphraseReference,
        SftpPrivateKeyFormat keyFormat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        if (username.Length > 256 || username.Any(char.IsControl))
        {
            throw new ArgumentException("The username is invalid.", nameof(username));
        }
        if (!SecretReference.TryParse(privateKeyReference.Value, out _))
        {
            throw new ArgumentException("The private-key secret reference is invalid.", nameof(privateKeyReference));
        }

        if (passphraseReference is { } passphrase && !SecretReference.TryParse(passphrase.Value, out _))
        {
            throw new ArgumentException("The passphrase secret reference is invalid.", nameof(passphraseReference));
        }

        if (!Enum.IsDefined(keyFormat))
        {
            throw new ArgumentOutOfRangeException(
                nameof(keyFormat),
                "SFTP accepts OpenSSH, PEM, or PKCS#8 keys; PFX is an FTPS certificate format.");
        }

        if (passphraseReference is null)
        {
            throw new ArgumentException(
                "StorageHub requires encrypted SSH private keys with a vault-backed passphrase.",
                nameof(passphraseReference));
        }

        Username = username.Trim();
        PrivateKeyReference = privateKeyReference;
        PassphraseReference = passphraseReference;
        KeyFormat = keyFormat;
    }

    public string Username { get; }
    public SecretReference PrivateKeyReference { get; }
    public SecretReference? PassphraseReference { get; }
    public SftpPrivateKeyFormat KeyFormat { get; }
}

/// <summary>
/// SSH multi-factor authentication where the server requires both a private key
/// and the account password. The private-key passphrase protects the key itself
/// and is intentionally stored as a separate vault reference.
/// </summary>
public sealed record SshPrivateKeyPasswordAuthentication : ConnectionAuthentication
{
    public SshPrivateKeyPasswordAuthentication(
        string username,
        SecretReference passwordReference,
        SecretReference privateKeyReference,
        SecretReference passphraseReference,
        SftpPrivateKeyFormat keyFormat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        if (username.Length > 256 || username.Any(char.IsControl))
        {
            throw new ArgumentException("The username is invalid.", nameof(username));
        }
        ValidateReference(passwordReference, nameof(passwordReference));
        ValidateReference(privateKeyReference, nameof(privateKeyReference));
        ValidateReference(passphraseReference, nameof(passphraseReference));
        if (!Enum.IsDefined(keyFormat))
        {
            throw new ArgumentOutOfRangeException(nameof(keyFormat));
        }

        Username = username.Trim();
        PasswordReference = passwordReference;
        PrivateKeyReference = privateKeyReference;
        PassphraseReference = passphraseReference;
        KeyFormat = keyFormat;
    }

    public string Username { get; }
    public SecretReference PasswordReference { get; }
    public SecretReference PrivateKeyReference { get; }
    public SecretReference PassphraseReference { get; }
    public SftpPrivateKeyFormat KeyFormat { get; }

    private static void ValidateReference(SecretReference reference, string parameterName)
    {
        if (!SecretReference.TryParse(reference.Value, out _))
        {
            throw new ArgumentException("The SSH secret reference is invalid.", parameterName);
        }
    }
}

public sealed record ConnectionProfile
{
    private ConnectionProfile(
        ConnectionProfileId id,
        ConnectionProviderKind provider,
        ConnectionProfileMetadata metadata,
        ConnectionEndpoint endpoint,
        ConnectionAuthentication authentication,
        ConnectionOperationalOptions operationalOptions,
        bool isEnabled,
        long version,
        DateTimeOffset createdUtc,
        DateTimeOffset updatedUtc,
        DateTimeOffset? deletedUtc)
    {
        Id = id;
        Provider = provider;
        Metadata = metadata;
        Endpoint = endpoint;
        Authentication = authentication;
        OperationalOptions = operationalOptions;
        IsEnabled = isEnabled;
        Version = version;
        CreatedUtc = createdUtc;
        UpdatedUtc = updatedUtc;
        DeletedUtc = deletedUtc;
        Validate();
    }

    public ConnectionProfileId Id { get; init; }
    public ConnectionProviderKind Provider { get; init; }
    public ConnectionProfileType Type => Provider == ConnectionProviderKind.Ssh
        ? ConnectionProfileType.Client
        : ConnectionProfileType.Storage;
    public ConnectionProfileMetadata Metadata { get; init; }
    public ConnectionEndpoint Endpoint { get; init; }
    public ConnectionAuthentication Authentication { get; init; }
    public ConnectionOperationalOptions OperationalOptions { get; init; }
    public bool IsEnabled { get; init; }
    public long Version { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
    public DateTimeOffset? DeletedUtc { get; init; }

    public static ConnectionProfile Create(
        ConnectionProfileId id,
        ConnectionProfileMetadata metadata,
        ConnectionEndpoint endpoint,
        ConnectionAuthentication authentication,
        ConnectionOperationalOptions operationalOptions,
        DateTimeOffset createdUtc,
        ConnectionProviderKind? provider = null) =>
        new(
            id,
            provider ?? endpoint.Provider,
            metadata,
            endpoint,
            authentication,
            operationalOptions,
            isEnabled: true,
            version: 1,
            createdUtc,
            createdUtc,
            deletedUtc: null);

    public static ConnectionProfile Rehydrate(
        ConnectionProfileId id,
        ConnectionProviderKind provider,
        ConnectionProfileMetadata metadata,
        ConnectionEndpoint endpoint,
        ConnectionAuthentication authentication,
        ConnectionOperationalOptions operationalOptions,
        bool isEnabled,
        long version,
        DateTimeOffset createdUtc,
        DateTimeOffset updatedUtc,
        DateTimeOffset? deletedUtc) =>
        new(id, provider, metadata, endpoint, authentication, operationalOptions, isEnabled, version,
            createdUtc, updatedUtc, deletedUtc);

    public void Validate()
    {
        if (Id.IsEmpty)
        {
            throw new ArgumentException("The profile identifier cannot be empty.", nameof(Id));
        }

        ArgumentNullException.ThrowIfNull(Metadata);
        ArgumentNullException.ThrowIfNull(Endpoint);
        ArgumentNullException.ThrowIfNull(Authentication);
        ArgumentNullException.ThrowIfNull(OperationalOptions);
        if (!Enum.IsDefined(Provider) || Provider != Endpoint.Provider)
        {
            throw new ArgumentException("The profile provider and endpoint type must match.", nameof(Provider));
        }

        if (Version <= 0 || createdOrUpdatedInvalid())
        {
            throw new ArgumentException("The profile version and UTC timestamps must be valid.");
        }

        if (DeletedUtc is not null && IsEnabled)
        {
            throw new ArgumentException("A deleted connection profile cannot remain enabled.", nameof(IsEnabled));
        }

        ValidateAuthentication();
        return;

        bool createdOrUpdatedInvalid() =>
            CreatedUtc.Offset != TimeSpan.Zero || UpdatedUtc.Offset != TimeSpan.Zero ||
            UpdatedUtc < CreatedUtc ||
            (DeletedUtc is { } deleted &&
                (deleted.Offset != TimeSpan.Zero || deleted < CreatedUtc));
    }

    private void ValidateAuthentication()
    {
        var valid = Provider switch
        {
            ConnectionProviderKind.Local => Authentication is NoAuthentication,
            ConnectionProviderKind.S3 => Authentication is
                S3DefaultCredentialChainAuthentication or
                CredentialReferenceAuthentication or
                S3AccessKeyAuthentication,
            ConnectionProviderKind.Ftp or ConnectionProviderKind.Ftps =>
                Authentication is NoAuthentication or UsernamePasswordAuthentication,
            ConnectionProviderKind.Sftp =>
                Authentication is UsernamePasswordAuthentication or SftpPrivateKeyAuthentication,
            ConnectionProviderKind.Ssh =>
                Authentication is UsernamePasswordAuthentication or SftpPrivateKeyAuthentication or
                    SshPrivateKeyPasswordAuthentication,
            _ => false
        };
        if (!valid)
        {
            throw new ArgumentException(
                $"Authentication type {Authentication.GetType().Name} is not valid for {Provider}.",
                nameof(Authentication));
        }
    }
}
