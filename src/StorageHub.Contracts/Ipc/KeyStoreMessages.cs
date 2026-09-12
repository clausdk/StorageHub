using System.Text.Json.Serialization;

namespace StorageHub.Contracts.Ipc;

/// <summary>
/// The independently versioned key and certificate store contract. It carries metadata and opaque
/// vault references only: key material travels exclusively on the dedicated secret pipe, which is
/// write-only, so no message defined here can ever return a private key or a passphrase.
/// </summary>
public static class KeyStoreIpcContract
{
    public const int CurrentVersion = 1;

    public static bool IsSupported(int version) => version == CurrentVersion;
}

public static class KeyStoreIpcMessageTypes
{
    public const string ListRequest = "keystore.entry.list.request";
    public const string ListResponse = "keystore.entry.list.response";
    public const string CreateRequest = "keystore.entry.create.request";
    public const string CreateResponse = "keystore.entry.create.response";
    public const string UpdateRequest = "keystore.entry.update.request";
    public const string UpdateResponse = "keystore.entry.update.response";
    public const string DeleteRequest = "keystore.entry.delete.request";
    public const string DeleteResponse = "keystore.entry.delete.response";
}

public static class KeyStoreIpcLimits
{
    public const int MaximumDisplayNameLength = 128;
    public const int MaximumDescriptionLength = 512;
    public const int MaximumTagCount = 16;
    public const int MaximumTagLength = 48;
    public const int MaximumSearchTextLength = 128;
    public const int MaximumEntriesPerPage = 500;
    public const int MaximumSubjectLength = 512;
    public const int MaximumAlgorithmLength = 64;
    public const int MaximumFingerprintLength = 128;
    public const int MaximumReferencedProfiles = 64;
}

[JsonConverter(typeof(JsonStringEnumConverter<KeyStoreMaterialKind>))]
public enum KeyStoreMaterialKind
{
    Pkcs12Certificate = 1,
    SshPrivateKey = 2
}

[JsonConverter(typeof(JsonStringEnumConverter<KeyStoreWriteOutcome>))]
public enum KeyStoreWriteOutcome
{
    Applied = 1,
    NotFound = 2,
    VersionConflict = 3,
    NameConflict = 4,
    StillReferenced = 5,
    Rejected = 6
}

/// <summary>Non-sensitive facts derived from the material when it was imported.</summary>
public sealed record KeyStoreSummaryDocument(
    string? Subject = null,
    string? Issuer = null,
    DateTimeOffset? NotBefore = null,
    DateTimeOffset? NotAfter = null,
    string? Sha256Thumbprint = null,
    string? KeyAlgorithm = null,
    int? KeySizeBits = null,
    bool? HasPrivateKey = null,
    int? ChainLength = null,
    KeyStorePrivateKeyFormat? KeyFormat = null,
    string? PublicKeyAlgorithm = null,
    string? Sha256Fingerprint = null,
    string? Comment = null)
{
    public bool HasValidBounds =>
        IsSafe(Subject, KeyStoreIpcLimits.MaximumSubjectLength) &&
        IsSafe(Issuer, KeyStoreIpcLimits.MaximumSubjectLength) &&
        IsSafe(Sha256Thumbprint, 64) &&
        IsSafe(KeyAlgorithm, KeyStoreIpcLimits.MaximumAlgorithmLength) &&
        IsSafe(PublicKeyAlgorithm, KeyStoreIpcLimits.MaximumAlgorithmLength) &&
        IsSafe(Sha256Fingerprint, KeyStoreIpcLimits.MaximumFingerprintLength) &&
        IsSafe(Comment, 256) &&
        KeySizeBits is null or >= 0 &&
        ChainLength is null or >= 1 &&
        (KeyFormat is null || Enum.IsDefined(KeyFormat.Value)) &&
        (NotBefore is null || NotAfter is null || NotAfter >= NotBefore);

    internal static bool IsSafe(string? value, int maximumLength) =>
        value is null || (value.Length <= maximumLength && !value.Any(char.IsControl));
}

[JsonConverter(typeof(JsonStringEnumConverter<KeyStorePrivateKeyFormat>))]
public enum KeyStorePrivateKeyFormat
{
    OpenSsh = 1,
    Pem = 2,
    Pkcs8 = 3
}

public sealed record KeyStoreEntryDocument(
    Guid EntryId,
    KeyStoreMaterialKind Kind,
    string DisplayName,
    string? Description,
    string[] Tags,
    string MaterialReference,
    string? PassphraseReference,
    KeyStoreSummaryDocument Summary,
    int Version,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string[] ReferencedByProfiles)
{
    public bool HasValidBounds =>
        EntryId != Guid.Empty &&
        Enum.IsDefined(Kind) &&
        IsSafeRequired(DisplayName, KeyStoreIpcLimits.MaximumDisplayNameLength) &&
        KeyStoreSummaryDocument.IsSafe(Description, KeyStoreIpcLimits.MaximumDescriptionLength) &&
        Tags is { Length: <= KeyStoreIpcLimits.MaximumTagCount } &&
        Tags.All(tag => IsSafeRequired(tag, KeyStoreIpcLimits.MaximumTagLength)) &&
        ConnectionEndpointDocument.IsOpaqueSecretReference(MaterialReference) &&
        MaterialReference is not null &&
        // A certificate may have no password; an SSH key must always have a passphrase.
        ConnectionEndpointDocument.IsOpaqueSecretReference(PassphraseReference) &&
        (Kind is not KeyStoreMaterialKind.SshPrivateKey || PassphraseReference is not null) &&
        Summary is { } summary && summary.HasValidBounds &&
        Version > 0 &&
        ReferencedByProfiles is { Length: <= KeyStoreIpcLimits.MaximumReferencedProfiles } &&
        ReferencedByProfiles.All(name => IsSafeRequired(name, KeyStoreIpcLimits.MaximumDisplayNameLength + 16));

    private static bool IsSafeRequired(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Any(char.IsControl);
}

public sealed record KeyStoreListRequest(
    int ContractVersion,
    string? Text = null,
    KeyStoreMaterialKind? Kind = null,
    string? Tag = null,
    int Limit = 200)
{
    public bool HasValidBounds =>
        ContractVersion > 0 &&
        KeyStoreSummaryDocument.IsSafe(Text, KeyStoreIpcLimits.MaximumSearchTextLength) &&
        KeyStoreSummaryDocument.IsSafe(Tag, KeyStoreIpcLimits.MaximumTagLength) &&
        (Kind is null || Enum.IsDefined(Kind.Value)) &&
        Limit is >= 1 and <= KeyStoreIpcLimits.MaximumEntriesPerPage;
}

public sealed record KeyStoreListResponse(
    int ContractVersion,
    KeyStoreEntryDocument[] Entries,
    StorageIpcFailure? Failure = null);

/// <summary>
/// Registers material that the caller has already enrolled on the secret pipe. The agent resolves
/// both references against the vault and derives the summary itself, so a caller cannot describe
/// material as something it is not.
/// </summary>
public sealed record KeyStoreCreateRequest(
    int ContractVersion,
    KeyStoreMaterialKind Kind,
    string DisplayName,
    string? Description,
    string[] Tags,
    string MaterialReference,
    string? PassphraseReference,
    KeyStorePrivateKeyFormat? KeyFormat = null)
{
    public bool HasValidBounds =>
        ContractVersion > 0 &&
        Enum.IsDefined(Kind) &&
        !string.IsNullOrWhiteSpace(DisplayName) &&
        DisplayName.Length <= KeyStoreIpcLimits.MaximumDisplayNameLength &&
        !DisplayName.Any(char.IsControl) &&
        KeyStoreSummaryDocument.IsSafe(Description, KeyStoreIpcLimits.MaximumDescriptionLength) &&
        Tags is { Length: <= KeyStoreIpcLimits.MaximumTagCount } &&
        Tags.All(tag => !string.IsNullOrWhiteSpace(tag) &&
            tag.Length <= KeyStoreIpcLimits.MaximumTagLength && !tag.Any(char.IsControl)) &&
        MaterialReference is not null &&
        ConnectionEndpointDocument.IsOpaqueSecretReference(MaterialReference) &&
        ConnectionEndpointDocument.IsOpaqueSecretReference(PassphraseReference) &&
        // A password-less PKCS#12 bundle is legitimate; an unprotected SSH key is not.
        (Kind is not KeyStoreMaterialKind.SshPrivateKey || PassphraseReference is not null) &&
        // An SSH key must declare its envelope; a certificate must not.
        (Kind is KeyStoreMaterialKind.SshPrivateKey
            ? KeyFormat is { } format && Enum.IsDefined(format)
            : KeyFormat is null);
}

public sealed record KeyStoreUpdateRequest(
    int ContractVersion,
    Guid EntryId,
    string DisplayName,
    string? Description,
    string[] Tags,
    int ExpectedVersion)
{
    public bool HasValidBounds =>
        ContractVersion > 0 &&
        EntryId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(DisplayName) &&
        DisplayName.Length <= KeyStoreIpcLimits.MaximumDisplayNameLength &&
        !DisplayName.Any(char.IsControl) &&
        KeyStoreSummaryDocument.IsSafe(Description, KeyStoreIpcLimits.MaximumDescriptionLength) &&
        Tags is { Length: <= KeyStoreIpcLimits.MaximumTagCount } &&
        Tags.All(tag => !string.IsNullOrWhiteSpace(tag) &&
            tag.Length <= KeyStoreIpcLimits.MaximumTagLength && !tag.Any(char.IsControl)) &&
        ExpectedVersion > 0;
}

public sealed record KeyStoreDeleteRequest(int ContractVersion, Guid EntryId, int ExpectedVersion)
{
    public bool HasValidBounds => ContractVersion > 0 && EntryId != Guid.Empty && ExpectedVersion > 0;
}

public sealed record KeyStoreWriteResponse(
    int ContractVersion,
    KeyStoreWriteOutcome Outcome,
    KeyStoreEntryDocument? Entry = null,
    int? ActualVersion = null,
    string[]? ReferencedByProfiles = null,
    StorageIpcFailure? Failure = null);
