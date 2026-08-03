using System.Globalization;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

/// <summary>Maps visible protocol-aware editor fields to the reference-only IPC draft.</summary>
public static class ConnectionEditorDraftFactory
{
    internal const string AmazonS3ServiceType = "Amazon S3";
    internal const string CloudflareR2ServiceType = "Cloudflare R2";
    internal const string OtherS3ServiceType = "Other S3-compatible";

    public static ConnectionProfileDraft Build(
        StorageProviderKind provider,
        IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var descriptor = ConnectionProviderCatalog.Get(provider);
        var (folder, tags) = ParseFolderAndTags(Get(values, "folderTags"));
        var metadata = new ConnectionProfileMetadataDocument(
            Require(values, "profileName", "A profile name is required."),
            folder,
            tags,
            IconKey: descriptor.ShortName.ToLowerInvariant(),
            AccentColor: descriptor.AccentHex);
        var endpoint = BuildEndpoint(provider, values);
        var authentication = BuildAuthentication(provider, values);
        var draft = new ConnectionProfileDraft(
            metadata,
            endpoint,
            authentication,
            BuildOperationalOptions(provider));
        if (!draft.HasValidBounds)
        {
            throw new ArgumentException(
                "One or more connection fields are outside the supported profile bounds.",
                nameof(values));
        }

        return draft;
    }

    private static ConnectionOperationalOptionsDocument BuildOperationalOptions(StorageProviderKind provider) =>
        provider switch
        {
            StorageProviderKind.Local => new ConnectionOperationalOptionsDocument(),
            StorageProviderKind.S3 => new ConnectionOperationalOptionsDocument(OperationTimeoutSeconds: 30),
            StorageProviderKind.Ftp or StorageProviderKind.Ftps or StorageProviderKind.Sftp =>
                new ConnectionOperationalOptionsDocument(
                    OperationTimeoutSeconds: 30,
                    MaximumRetryAttempts: 0),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };

    public static IReadOnlyDictionary<string, string> ToEditorValues(ConnectionProfileDocument profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["profileName"] = profile.Draft.Metadata.DisplayName,
            ["folderTags"] = FormatFolderAndTags(profile.Draft.Metadata)
        };
        AddEndpoint(values, profile.Draft.Endpoint);
        AddAuthentication(values, profile.Draft.Authentication);
        return values;
    }

    private static ConnectionEndpointDocument BuildEndpoint(
        StorageProviderKind provider,
        IReadOnlyDictionary<string, string> values) => provider switch
        {
            StorageProviderKind.Local => new ConnectionEndpointDocument(
                StorageConnectionProvider.Local,
                RootPath: Require(values, "rootPath", "An absolute local or UNC root is required.")),
            StorageProviderKind.S3 => BuildS3Endpoint(values),
            StorageProviderKind.Ftp => BuildFtpEndpoint(values),
            StorageProviderKind.Ftps => BuildFtpsEndpoint(values),
            StorageProviderKind.Sftp => BuildSftpEndpoint(values),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };

    private static ConnectionEndpointDocument BuildSftpEndpoint(IReadOnlyDictionary<string, string> values)
    {
        ValidateFingerprint(Require(
            values,
            "hostKeyFingerprint",
            "A verified SSH host-key SHA-256 fingerprint is required."));
        return new ConnectionEndpointDocument(
            StorageConnectionProvider.Sftp,
            RootPath: NormalizeProviderRoot(Get(values, "initialPath")),
            Host: Require(values, "host", "An SFTP host is required."),
            Port: ParsePort(values, 22),
            SshHostKeyPolicy: ConnectionSshHostKeyPolicy.Pinned);
    }

    private static ConnectionEndpointDocument BuildS3Endpoint(IReadOnlyDictionary<string, string> values)
    {
        var endpoint = NormalizeS3ServiceEndpoint(Require(
            values,
            "endpoint",
            "An HTTPS S3 service endpoint is required."));
        var serviceType = Get(values, "s3ServiceType") ?? AmazonS3ServiceType;
        var isCloudflareR2 = string.Equals(
                serviceType,
                CloudflareR2ServiceType,
                StringComparison.Ordinal) ||
            IsCloudflareR2Endpoint(endpoint);
        var region = isCloudflareR2
            ? "auto"
            : Require(values, "region", "A signing region is required for this S3-compatible service.");
        var bucket = Require(values, "bucket", "An S3 bucket is required.");
        if (bucket.Length is < 3 or > 255 ||
            bucket.Any(static character => char.IsControl(character) ||
                char.IsWhiteSpace(character) || character is '/' or '\\'))
        {
            throw new ArgumentException("The S3 bucket name must be 3-255 characters with no spaces or slashes.", nameof(values));
        }

        return new ConnectionEndpointDocument(
            StorageConnectionProvider.S3,
            RootPath: NormalizeProviderRoot(Get(values, "prefix")),
            Bucket: bucket,
            Region: region,
            ServiceEndpoint: endpoint,
            ForcePathStyle: isCloudflareR2 || string.Equals(
                Get(values, "addressingStyle"),
                "Path-style",
                StringComparison.Ordinal));
    }

    internal static bool IsCloudflareR2Endpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        var candidate = endpoint.Contains("://", StringComparison.Ordinal)
            ? endpoint
            : "https://" + endpoint;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
            (uri.IdnHost.Equals("r2.cloudflarestorage.com", StringComparison.OrdinalIgnoreCase) ||
                uri.IdnHost.EndsWith(".r2.cloudflarestorage.com", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeS3ServiceEndpoint(string value)
    {
        var candidate = value.Contains("://", StringComparison.Ordinal)
            ? value
            : "https://" + value;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new ArgumentException(
                "The S3 service endpoint must be a valid hostname or absolute HTTPS URL.",
                nameof(value));
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The S3 service endpoint must use HTTPS; insecure HTTP endpoints are not enabled.",
                nameof(value));
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath is not "" and not "/")
        {
            throw new ArgumentException(
                "Enter the S3 service endpoint only, without credentials, query text, fragments, or a bucket path.",
                nameof(value));
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static ConnectionEndpointDocument BuildFtpEndpoint(IReadOnlyDictionary<string, string> values)
    {
        if (!ParseBoolean(values, "acknowledgePlaintext"))
        {
            throw new ArgumentException(
                "Plain FTP requires explicit plaintext-transport acknowledgement.",
                nameof(values));
        }

        return new ConnectionEndpointDocument(
            StorageConnectionProvider.Ftp,
            RootPath: NormalizeProviderRoot(Get(values, "initialPath")),
            Host: Require(values, "host", "An FTP host is required."),
            Port: ParsePort(values, 21),
            AllowInsecureTransport: true);
    }

    private static ConnectionEndpointDocument BuildFtpsEndpoint(IReadOnlyDictionary<string, string> values)
    {
        var pinned = string.Equals(
            Get(values, "trustMode"),
            "System trust + certificate pin",
            StringComparison.Ordinal);
        if (pinned)
        {
            ValidateFingerprint(Require(
                values,
                "certificatePin",
                "A verified certificate SHA-256 pin is required for pinned FTPS."));
        }

        return new ConnectionEndpointDocument(
            StorageConnectionProvider.Ftps,
            RootPath: NormalizeProviderRoot(Get(values, "initialPath")),
            Host: Require(values, "host", "An FTPS host is required."),
            Port: ParsePort(values, 21),
            TlsPolicy: pinned
                ? ConnectionTlsCertificatePolicy.Pinned
                : ConnectionTlsCertificatePolicy.SystemTrust,
            FtpsTlsMode: string.Equals(
                Get(values, "tlsMode"),
                "Implicit TLS",
                StringComparison.Ordinal)
                    ? ConnectionFtpsTlsMode.Implicit
                    : ConnectionFtpsTlsMode.Explicit,
            ClientCertificatePfxReference: Get(values, "clientCertificateReference"),
            ClientCertificatePasswordReference: Get(values, "clientCertificatePasswordReference"));
    }

    private static ConnectionAuthenticationDocument BuildAuthentication(
        StorageProviderKind provider,
        IReadOnlyDictionary<string, string> values) => provider switch
        {
            StorageProviderKind.Local => BuildLocalAuthentication(values),
            StorageProviderKind.S3 => BuildS3Authentication(values),
            StorageProviderKind.Ftp or StorageProviderKind.Ftps =>
                new ConnectionAuthenticationDocument(
                    ConnectionAuthenticationKind.UsernamePassword,
                    Username: Require(values, "username", "A username is required."),
                    PasswordReference: Require(values, "passwordReference", "A vault password reference is required.")),
            StorageProviderKind.Sftp => BuildSftpAuthentication(values),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };

    private static ConnectionAuthenticationDocument BuildLocalAuthentication(
        IReadOnlyDictionary<string, string> values)
    {
        if (!string.IsNullOrEmpty(Get(values, "credentialReference")))
        {
            throw new ArgumentException(
                "Alternate Windows identities are not available until a provider-enforced impersonation boundary is implemented.",
                nameof(values));
        }

        return new ConnectionAuthenticationDocument(ConnectionAuthenticationKind.None);
    }

    private static ConnectionAuthenticationDocument BuildS3Authentication(
        IReadOnlyDictionary<string, string> values)
    {
        var access = Get(values, "accessKeyReference");
        var secret = Get(values, "secretAccessKeyReference");
        var token = Get(values, "sessionTokenReference");
        if (access is null && secret is null && token is null)
        {
            return new ConnectionAuthenticationDocument(ConnectionAuthenticationKind.S3DefaultCredentialChain);
        }

        if (access is null || secret is null)
        {
            throw new ArgumentException(
                "S3 access-key authentication requires both access-key and secret-key vault references.",
                nameof(values));
        }

        return new ConnectionAuthenticationDocument(
            ConnectionAuthenticationKind.S3AccessKey,
            AccessKeyReference: access,
            SecretKeyReference: secret,
            SessionTokenReference: token);
    }

    private static ConnectionAuthenticationDocument BuildSftpAuthentication(
        IReadOnlyDictionary<string, string> values)
    {
        var mode = Get(values, "authenticationMode") ?? "Private key reference";
        var username = Require(values, "username", "An SFTP username is required.");
        return mode switch
        {
            "Private key reference" => new ConnectionAuthenticationDocument(
                ConnectionAuthenticationKind.SftpPrivateKey,
                Username: username,
                PrivateKeyReference: Require(
                    values,
                    "privateKeyReference",
                    "An encrypted private-key vault reference is required."),
                PrivateKeyPassphraseReference: Require(
                    values,
                    "privateKeyPassphraseReference",
                    "A private-key passphrase vault reference is required.")),
            "Password reference" => new ConnectionAuthenticationDocument(
                ConnectionAuthenticationKind.UsernamePassword,
                Username: username,
                PasswordReference: Require(
                    values,
                    "passwordReference",
                    "A vault password reference is required.")),
            _ => throw new ArgumentException(
                "SSH agent authentication is not available in the current provider adapter.",
                nameof(values))
        };
    }

    private static void AddEndpoint(
        IDictionary<string, string> values,
        ConnectionEndpointDocument endpoint)
    {
        Add(values, "rootPath", endpoint.Provider == StorageConnectionProvider.Local ? endpoint.RootPath : null);
        Add(values, "initialPath", endpoint.Provider is StorageConnectionProvider.Ftp or
            StorageConnectionProvider.Ftps or StorageConnectionProvider.Sftp ? endpoint.RootPath : null);
        Add(values, "prefix", endpoint.Provider == StorageConnectionProvider.S3 ? endpoint.RootPath : null);
        Add(values, "host", endpoint.Host);
        Add(values, "port", endpoint.Port?.ToString(CultureInfo.InvariantCulture));
        Add(values, "bucket", endpoint.Bucket);
        Add(values, "region", endpoint.Region);
        Add(values, "endpoint", endpoint.ServiceEndpoint);
        Add(values, "s3ServiceType", endpoint.Provider == StorageConnectionProvider.S3
            ? IsCloudflareR2Endpoint(endpoint.ServiceEndpoint)
                ? CloudflareR2ServiceType
                : endpoint.ServiceEndpoint?.Contains("amazonaws.com", StringComparison.OrdinalIgnoreCase) == true
                    ? AmazonS3ServiceType
                    : OtherS3ServiceType
            : null);
        Add(values, "addressingStyle", endpoint.ForcePathStyle
            ? "Path-style"
            : "Virtual-hosted (recommended)");
        Add(values, "acknowledgePlaintext", endpoint.AllowInsecureTransport.ToString(CultureInfo.InvariantCulture));
        Add(values, "tlsMode", endpoint.FtpsTlsMode == ConnectionFtpsTlsMode.Implicit
            ? "Implicit TLS"
            : "Explicit TLS (recommended)");
        Add(values, "clientCertificateReference", endpoint.ClientCertificatePfxReference);
        Add(values, "clientCertificatePasswordReference", endpoint.ClientCertificatePasswordReference);
    }

    private static void AddAuthentication(
        IDictionary<string, string> values,
        ConnectionAuthenticationDocument authentication)
    {
        Add(values, "username", authentication.Username);
        Add(values, "passwordReference", authentication.PasswordReference);
        Add(values, "accessKeyReference", authentication.AccessKeyReference);
        Add(values, "secretAccessKeyReference", authentication.SecretKeyReference);
        Add(values, "sessionTokenReference", authentication.SessionTokenReference);
        Add(values, "privateKeyReference", authentication.PrivateKeyReference);
        Add(values, "privateKeyPassphraseReference", authentication.PrivateKeyPassphraseReference);
        Add(values, "authenticationMode", authentication.Kind switch
        {
            ConnectionAuthenticationKind.SftpPrivateKey => "Private key reference",
            ConnectionAuthenticationKind.UsernamePassword => "Password reference",
            _ => null
        });
    }

    private static void Add(IDictionary<string, string> values, string key, string? value)
    {
        if (value is not null)
        {
            values[key] = value;
        }
    }

    private static string FormatFolderAndTags(ConnectionProfileMetadataDocument metadata)
    {
        var tags = metadata.Tags is { Length: > 0 } ? string.Join(", ", metadata.Tags) : string.Empty;
        return (metadata.FolderPath, tags) switch
        {
            (null, "") => string.Empty,
            ({ } folder, "") => folder,
            (null, { } tagText) => $"· {tagText}",
            ({ } folder, { } tagText) => $"{folder} · {tagText}"
        };
    }

    private static (string? Folder, string[] Tags) ParseFolderAndTags(string? value)
    {
        if (value is null)
        {
            return (null, []);
        }

        var separator = value.IndexOf('·');
        if (separator < 0)
        {
            return (value.Trim(), []);
        }

        var folder = value[..separator].Trim();
        var tags = value[(separator + 1)..]
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return (folder.Length == 0 ? null : folder, tags);
    }

    private static string? NormalizeProviderRoot(string? value)
    {
        var normalized = value?.Trim().Trim('/');
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    private static int ParsePort(IReadOnlyDictionary<string, string> values, int fallback)
    {
        var value = Get(values, "port");
        if (value is null)
        {
            return fallback;
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) &&
            port is >= 1 and <= 65_535
                ? port
                : throw new ArgumentException("The port must be between 1 and 65,535.", nameof(values));
    }

    private static bool ParseBoolean(IReadOnlyDictionary<string, string> values, string key) =>
        bool.TryParse(Get(values, key), out var result) && result;

    private static void ValidateFingerprint(string value)
    {
        if (!ConnectionTrustIpcLimits.IsValidFingerprint(value))
        {
            throw new ArgumentException(
                "The server identity fingerprint must be SHA-256 hexadecimal or SHA256 base64.",
                nameof(value));
        }
    }

    private static string Require(
        IReadOnlyDictionary<string, string> values,
        string key,
        string message) => Get(values, key) ?? throw new ArgumentException(message, nameof(values));

    private static string? Get(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
}
