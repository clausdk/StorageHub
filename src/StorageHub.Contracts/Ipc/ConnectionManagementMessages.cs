using System.Text.Json.Serialization;

namespace StorageHub.Contracts.Ipc;

/// <summary>The independently versioned saved-connection management contract.</summary>
public static class ConnectionProfileIpcContract
{
    public const int CurrentVersion = 1;

    public static bool IsSupported(int version) => version == CurrentVersion;
}

public static class ConnectionProfileIpcMessageTypes
{
    public const string GetRequest = "connection.profile.get.request";
    public const string GetResponse = "connection.profile.get.response";
    public const string CreateRequest = "connection.profile.create.request";
    public const string CreateResponse = "connection.profile.create.response";
    public const string UpdateRequest = "connection.profile.update.request";
    public const string UpdateResponse = "connection.profile.update.response";
    public const string DeleteRequest = "connection.profile.delete.request";
    public const string DeleteResponse = "connection.profile.delete.response";
}

public static class ConnectionProfileIpcLimits
{
    public const int MaximumDisplayNameLength = 128;
    public const int MaximumFolderPathLength = 512;
    public const int MaximumTagCount = 64;
    public const int MaximumTagLength = 64;
    public const int MaximumPathLength = 8_192;
    public const int MaximumEndpointLength = 2_048;
    public const int MaximumUsernameLength = 256;
    public const int MaximumIconKeyLength = 64;
    public const int MaximumEncodingNameLength = 64;
}

[JsonConverter(typeof(JsonStringEnumConverter<ConnectionTlsCertificatePolicy>))]
public enum ConnectionTlsCertificatePolicy
{
    SystemTrust = 1,
    Pinned = 2,
    TrustOnFirstUse = 3
}

[JsonConverter(typeof(JsonStringEnumConverter<ConnectionSshHostKeyPolicy>))]
public enum ConnectionSshHostKeyPolicy
{
    Pinned = 1,
    TrustOnFirstUse = 2
}

[JsonConverter(typeof(JsonStringEnumConverter<ConnectionFtpsTlsMode>))]
public enum ConnectionFtpsTlsMode
{
    Explicit = 1,
    Implicit = 2
}

[JsonConverter(typeof(JsonStringEnumConverter<ConnectionAuthenticationKind>))]
public enum ConnectionAuthenticationKind
{
    None = 1,
    S3DefaultCredentialChain = 2,
    CredentialReference = 3,
    UsernamePassword = 4,
    S3AccessKey = 5,
    SftpPrivateKey = 6
}

[JsonConverter(typeof(JsonStringEnumConverter<ConnectionSftpPrivateKeyFormat>))]
public enum ConnectionSftpPrivateKeyFormat
{
    OpenSsh = 1,
    Pem = 2,
    Pkcs8 = 3
}

[JsonConverter(typeof(JsonStringEnumConverter<ConnectionProfileWriteStatus>))]
public enum ConnectionProfileWriteStatus
{
    Succeeded = 1,
    NotFound = 2,
    VersionConflict = 3,
    NameConflict = 4,
    Deleted = 5,
    ValidationFailed = 6,
    Unavailable = 7
}

public sealed record ConnectionProfileMetadataDocument(
    string DisplayName,
    string? FolderPath = null,
    string[]? Tags = null,
    bool IsFavorite = false,
    string? HomePath = null,
    string? UploadPath = null,
    string? DownloadPath = null,
    string? IconKey = null,
    string? AccentColor = null)
{
    public bool HasValidBounds =>
        IsSafeText(DisplayName, ConnectionProfileIpcLimits.MaximumDisplayNameLength, required: true) &&
        IsSafeText(FolderPath, ConnectionProfileIpcLimits.MaximumFolderPathLength) &&
        Tags is not null && Tags.Length <= ConnectionProfileIpcLimits.MaximumTagCount &&
        Tags.All(static tag => IsSafeText(tag, ConnectionProfileIpcLimits.MaximumTagLength, required: true)) &&
        IsSafePath(HomePath, 2_048) &&
        IsSafePath(UploadPath, 2_048) &&
        IsSafePath(DownloadPath, 2_048) &&
        IsSafeText(IconKey, ConnectionProfileIpcLimits.MaximumIconKeyLength) &&
        IsValidAccentColor(AccentColor);

    internal static bool IsSafeText(string? value, int maximumLength, bool required = false) =>
        value is null
            ? !required
            : (!required || !string.IsNullOrWhiteSpace(value)) &&
              value.Length <= maximumLength &&
              !value.Any(char.IsControl);

    internal static bool IsSafePath(string? value, int maximumLength) =>
        IsSafeText(value, maximumLength) &&
        (value is null || !value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(static segment => segment == ".."));

    private static bool IsValidAccentColor(string? value) => value is null ||
        value.Length == 7 && value[0] == '#' && value.AsSpan(1).ToArray().All(Uri.IsHexDigit);

}

/// <summary>
/// A provider-discriminated endpoint document. The agent rejects values in fields that do not
/// belong to the selected provider, so unused strings cannot be used to smuggle secret material.
/// </summary>
public sealed record ConnectionEndpointDocument(
    StorageConnectionProvider Provider,
    string? RootPath = null,
    string? Host = null,
    int? Port = null,
    string? Bucket = null,
    string? Region = null,
    string? ServiceEndpoint = null,
    bool ForcePathStyle = false,
    ConnectionTlsCertificatePolicy TlsPolicy = ConnectionTlsCertificatePolicy.SystemTrust,
    bool AllowInsecureTransport = false,
    ConnectionFtpsTlsMode FtpsTlsMode = ConnectionFtpsTlsMode.Explicit,
    string? ClientCertificatePfxReference = null,
    string? ClientCertificatePasswordReference = null,
    ConnectionSshHostKeyPolicy SshHostKeyPolicy = ConnectionSshHostKeyPolicy.Pinned)
{
    public bool HasValidBounds =>
        Enum.IsDefined(Provider) &&
        ConnectionProfileMetadataDocument.IsSafePath(RootPath, ConnectionProfileIpcLimits.MaximumPathLength) &&
        ConnectionProfileMetadataDocument.IsSafeText(Host, 253) &&
        Port is null or >= 1 and <= 65_535 &&
        ConnectionProfileMetadataDocument.IsSafeText(Bucket, 255) &&
        ConnectionProfileMetadataDocument.IsSafeText(Region, 128) &&
        ConnectionProfileMetadataDocument.IsSafeText(ServiceEndpoint, ConnectionProfileIpcLimits.MaximumEndpointLength) &&
        Enum.IsDefined(TlsPolicy) &&
        Enum.IsDefined(FtpsTlsMode) &&
        Enum.IsDefined(SshHostKeyPolicy) &&
        IsOpaqueSecretReference(ClientCertificatePfxReference) &&
        IsOpaqueSecretReference(ClientCertificatePasswordReference) &&
        HasProviderShape();

    public static bool IsOpaqueSecretReference(string? value) => value is null ||
        value.Length == 47 && value.StartsWith("shs_", StringComparison.Ordinal) &&
        value.AsSpan(4).ToArray().All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private bool HasProviderShape() => Provider switch
    {
        StorageConnectionProvider.Local =>
            Present(RootPath) && Empty(Host) && Port is null && Empty(Bucket) && Empty(Region) &&
            Empty(ServiceEndpoint) && !ForcePathStyle && !AllowInsecureTransport &&
            Empty(ClientCertificatePfxReference) && Empty(ClientCertificatePasswordReference) &&
            HasDefaultNonProviderPolicies(),
        StorageConnectionProvider.S3 =>
            Empty(Host) && Port is null && Present(Bucket) && Present(Region) &&
            Empty(ClientCertificatePfxReference) && Empty(ClientCertificatePasswordReference) &&
            FtpsTlsMode == ConnectionFtpsTlsMode.Explicit &&
            SshHostKeyPolicy == ConnectionSshHostKeyPolicy.Pinned,
        StorageConnectionProvider.Ftp =>
            Present(Host) && Port is not null && AllowInsecureTransport && Empty(Bucket) &&
            Empty(Region) && Empty(ServiceEndpoint) && !ForcePathStyle &&
            Empty(ClientCertificatePfxReference) && Empty(ClientCertificatePasswordReference) &&
            HasDefaultNonProviderPolicies(),
        StorageConnectionProvider.Ftps =>
            Present(Host) && Port is not null && Empty(Bucket) && Empty(Region) &&
            Empty(ServiceEndpoint) && !ForcePathStyle && !AllowInsecureTransport &&
            (Empty(ClientCertificatePfxReference) == Empty(ClientCertificatePasswordReference)) &&
            SshHostKeyPolicy == ConnectionSshHostKeyPolicy.Pinned,
        StorageConnectionProvider.Sftp =>
            Present(Host) && Port is not null && Empty(Bucket) && Empty(Region) &&
            Empty(ServiceEndpoint) && !ForcePathStyle && !AllowInsecureTransport &&
            Empty(ClientCertificatePfxReference) && Empty(ClientCertificatePasswordReference) &&
            TlsPolicy == ConnectionTlsCertificatePolicy.SystemTrust &&
            FtpsTlsMode == ConnectionFtpsTlsMode.Explicit,
        _ => false
    };

    private bool HasDefaultNonProviderPolicies() =>
        TlsPolicy == ConnectionTlsCertificatePolicy.SystemTrust &&
        FtpsTlsMode == ConnectionFtpsTlsMode.Explicit &&
        SshHostKeyPolicy == ConnectionSshHostKeyPolicy.Pinned;

    private static bool Present(string? value) => !string.IsNullOrWhiteSpace(value);

    private static bool Empty(string? value) => string.IsNullOrEmpty(value);
}

public sealed record ConnectionAuthenticationDocument(
    ConnectionAuthenticationKind Kind,
    string? Username = null,
    Guid? CredentialReferenceId = null,
    string? PasswordReference = null,
    string? AccessKeyReference = null,
    string? SecretKeyReference = null,
    string? SessionTokenReference = null,
    string? PrivateKeyReference = null,
    string? PrivateKeyPassphraseReference = null,
    ConnectionSftpPrivateKeyFormat PrivateKeyFormat = ConnectionSftpPrivateKeyFormat.OpenSsh)
{
    public bool HasValidBounds =>
        Enum.IsDefined(Kind) &&
        ConnectionProfileMetadataDocument.IsSafeText(
            Username,
            ConnectionProfileIpcLimits.MaximumUsernameLength) &&
        (CredentialReferenceId is null || CredentialReferenceId.Value != Guid.Empty) &&
        ConnectionEndpointDocument.IsOpaqueSecretReference(PasswordReference) &&
        ConnectionEndpointDocument.IsOpaqueSecretReference(AccessKeyReference) &&
        ConnectionEndpointDocument.IsOpaqueSecretReference(SecretKeyReference) &&
        ConnectionEndpointDocument.IsOpaqueSecretReference(SessionTokenReference) &&
        ConnectionEndpointDocument.IsOpaqueSecretReference(PrivateKeyReference) &&
        ConnectionEndpointDocument.IsOpaqueSecretReference(PrivateKeyPassphraseReference) &&
        Enum.IsDefined(PrivateKeyFormat) &&
        HasKindShape();

    private bool HasKindShape()
    {
        var hasUsername = Present(Username);
        var hasCredential = CredentialReferenceId is not null;
        var hasPassword = Present(PasswordReference);
        var hasAccess = Present(AccessKeyReference);
        var hasSecret = Present(SecretKeyReference);
        var hasToken = Present(SessionTokenReference);
        var hasKey = Present(PrivateKeyReference);
        var hasPassphrase = Present(PrivateKeyPassphraseReference);
        return Kind switch
        {
            ConnectionAuthenticationKind.None or ConnectionAuthenticationKind.S3DefaultCredentialChain =>
                !hasUsername && !hasCredential && !hasPassword && !hasAccess && !hasSecret &&
                !hasToken && !hasKey && !hasPassphrase,
            ConnectionAuthenticationKind.CredentialReference =>
                !hasUsername && hasCredential && !hasPassword && !hasAccess && !hasSecret &&
                !hasToken && !hasKey && !hasPassphrase,
            ConnectionAuthenticationKind.UsernamePassword =>
                hasUsername && !hasCredential && hasPassword && !hasAccess && !hasSecret &&
                !hasToken && !hasKey && !hasPassphrase,
            ConnectionAuthenticationKind.S3AccessKey =>
                !hasUsername && !hasCredential && !hasPassword && hasAccess && hasSecret &&
                !hasKey && !hasPassphrase,
            ConnectionAuthenticationKind.SftpPrivateKey =>
                hasUsername && !hasCredential && !hasPassword && !hasAccess && !hasSecret &&
                !hasToken && hasKey && hasPassphrase,
            _ => false
        };
    }

    private static bool Present(string? value) => !string.IsNullOrWhiteSpace(value);
}

public sealed record ConnectionOperationalOptionsDocument(
    int ConnectTimeoutSeconds = 30,
    int OperationTimeoutSeconds = 60,
    int MaximumRetryAttempts = 3,
    int InitialRetryDelayMilliseconds = 250,
    int MaximumRetryDelayMilliseconds = 5_000,
    string? ProxyEndpoint = null,
    Guid? ProxyCredentialReferenceId = null,
    long? UploadBytesPerSecond = null,
    long? DownloadBytesPerSecond = null,
    string EncodingName = "utf-8")
{
    public bool HasValidBounds =>
        ConnectTimeoutSeconds is >= 1 and <= 600 &&
        OperationTimeoutSeconds is >= 1 and <= 86_400 &&
        MaximumRetryAttempts is >= 0 and <= 20 &&
        InitialRetryDelayMilliseconds is >= 0 and <= 300_000 &&
        MaximumRetryDelayMilliseconds >= InitialRetryDelayMilliseconds &&
        MaximumRetryDelayMilliseconds <= 3_600_000 &&
        ConnectionProfileMetadataDocument.IsSafeText(
            ProxyEndpoint,
            ConnectionProfileIpcLimits.MaximumEndpointLength) &&
        (ProxyCredentialReferenceId is null || ProxyCredentialReferenceId.Value != Guid.Empty) &&
        UploadBytesPerSecond is null or > 0 &&
        DownloadBytesPerSecond is null or > 0 &&
        ConnectionProfileMetadataDocument.IsSafeText(
            EncodingName,
            ConnectionProfileIpcLimits.MaximumEncodingNameLength,
            required: true) &&
        (ProxyEndpoint is not null || ProxyCredentialReferenceId is null);
}

/// <summary>A complete profile draft containing references, but never secret values.</summary>
public sealed record ConnectionProfileDraft(
    ConnectionProfileMetadataDocument Metadata,
    ConnectionEndpointDocument Endpoint,
    ConnectionAuthenticationDocument Authentication,
    ConnectionOperationalOptionsDocument OperationalOptions,
    bool IsEnabled = true)
{
    public bool HasValidBounds =>
        Metadata is { HasValidBounds: true } &&
        Endpoint is { HasValidBounds: true } &&
        Authentication is { HasValidBounds: true } &&
        OperationalOptions is { HasValidBounds: true } &&
        HasCompatibleAuthentication();

    private bool HasCompatibleAuthentication() => Endpoint.Provider switch
    {
        StorageConnectionProvider.Local => Authentication.Kind == ConnectionAuthenticationKind.None,
        StorageConnectionProvider.S3 => Authentication.Kind is
            ConnectionAuthenticationKind.S3DefaultCredentialChain or
            ConnectionAuthenticationKind.CredentialReference or
            ConnectionAuthenticationKind.S3AccessKey,
        StorageConnectionProvider.Ftp or StorageConnectionProvider.Ftps => Authentication.Kind is
            ConnectionAuthenticationKind.None or ConnectionAuthenticationKind.UsernamePassword,
        StorageConnectionProvider.Sftp => Authentication.Kind is
            ConnectionAuthenticationKind.UsernamePassword or ConnectionAuthenticationKind.SftpPrivateKey,
        _ => false
    };
}

public sealed record ConnectionProfileDocument(
    Guid ConnectionId,
    long Version,
    ConnectionProfileDraft Draft,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc)
{
    public bool HasValidBounds =>
        ConnectionId != Guid.Empty &&
        Version > 0 &&
        Draft is { HasValidBounds: true } &&
        CreatedUtc.Offset == TimeSpan.Zero &&
        UpdatedUtc.Offset == TimeSpan.Zero &&
        UpdatedUtc >= CreatedUtc;
}

public sealed record ConnectionProfileGetRequest(
    int ContractVersion,
    Guid ConnectionId)
{
    public bool HasValidBounds => ContractVersion > 0 && ConnectionId != Guid.Empty;
}

public sealed record ConnectionProfileGetResponse(
    int ContractVersion,
    ConnectionProfileDocument? Profile,
    StorageIpcFailure? Failure = null);

public sealed record ConnectionProfileCreateRequest(
    int ContractVersion,
    ConnectionProfileDraft Draft)
{
    public bool HasValidBounds => ContractVersion > 0 && Draft is { HasValidBounds: true };
}

public sealed record ConnectionProfileUpdateRequest(
    int ContractVersion,
    Guid ConnectionId,
    long ExpectedVersion,
    ConnectionProfileDraft Draft)
{
    public bool HasValidBounds =>
        ContractVersion > 0 &&
        ConnectionId != Guid.Empty &&
        ExpectedVersion > 0 &&
        Draft is { HasValidBounds: true };
}

public sealed record ConnectionProfileDeleteRequest(
    int ContractVersion,
    Guid ConnectionId,
    long ExpectedVersion)
{
    public bool HasValidBounds =>
        ContractVersion > 0 && ConnectionId != Guid.Empty && ExpectedVersion > 0;
}

public sealed record ConnectionProfileWriteResponse(
    int ContractVersion,
    ConnectionProfileWriteStatus Status,
    ConnectionProfileDocument? Profile = null,
    long? ActualVersion = null,
    StorageIpcFailure? Failure = null);
