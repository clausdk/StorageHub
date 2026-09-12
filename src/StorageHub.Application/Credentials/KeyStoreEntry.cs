using StorageHub.Application.Connections;
using StorageHub.Domain.Identifiers;
using StorageHub.Security;

namespace StorageHub.Application.Credentials;

/// <summary>
/// What a stored key actually is. The kind is authoritative: a profile may only bind an entry whose
/// kind its provider accepts, which is what keeps a PKCS#12 bundle from ever being handed to an SSH
/// authentication path that expects an OpenSSH, PEM, or PKCS#8 private key.
/// </summary>
public enum KeyMaterialKind
{
    Pkcs12Certificate = 1,
    SshPrivateKey = 2
}

/// <summary>
/// Non-sensitive facts derived from key material at import time. These exist so the key store can
/// be browsed, searched, and checked for expiry without ever reading the material again - the
/// secret pipe is write-only and never returns bytes to the desktop.
/// </summary>
public abstract record KeyMaterialSummary;

public sealed record Pkcs12CertificateSummary : KeyMaterialSummary
{
    public Pkcs12CertificateSummary(
        string subject,
        string issuer,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter,
        string sha256Thumbprint,
        string keyAlgorithm,
        int keySizeBits,
        bool hasPrivateKey,
        int chainLength)
    {
        Subject = KeyStoreText.Require(subject, 512, nameof(subject));
        Issuer = KeyStoreText.Require(issuer, 512, nameof(issuer));
        Sha256Thumbprint = KeyStoreText.RequireHex(sha256Thumbprint, 64, nameof(sha256Thumbprint));
        KeyAlgorithm = KeyStoreText.Require(keyAlgorithm, 64, nameof(keyAlgorithm));
        if (notAfter < notBefore)
        {
            throw new ArgumentOutOfRangeException(
                nameof(notAfter),
                "A certificate cannot expire before it becomes valid.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(keySizeBits);
        ArgumentOutOfRangeException.ThrowIfLessThan(chainLength, 1);
        NotBefore = notBefore;
        NotAfter = notAfter;
        KeySizeBits = keySizeBits;
        HasPrivateKey = hasPrivateKey;
        ChainLength = chainLength;
    }

    public string Subject { get; }

    public string Issuer { get; }

    public DateTimeOffset NotBefore { get; }

    public DateTimeOffset NotAfter { get; }

    public string Sha256Thumbprint { get; }

    public string KeyAlgorithm { get; }

    public int KeySizeBits { get; }

    public bool HasPrivateKey { get; }

    public int ChainLength { get; }

    public bool IsExpiredAt(DateTimeOffset instant) => instant > NotAfter;

    public bool IsNotYetValidAt(DateTimeOffset instant) => instant < NotBefore;
}

public sealed record SshPrivateKeySummary : KeyMaterialSummary
{
    public SshPrivateKeySummary(
        SftpPrivateKeyFormat format,
        string publicKeyAlgorithm,
        string sha256Fingerprint,
        string? comment)
    {
        if (!Enum.IsDefined(format))
        {
            throw new ArgumentOutOfRangeException(nameof(format), "The private key format is not recognized.");
        }

        // Deliberately the same "SHA256:<base64>" shape already used for host-key pins, so the two
        // can be compared by eye when a key is verified through a separate channel.
        if (sha256Fingerprint is null || !sha256Fingerprint.StartsWith("SHA256:", StringComparison.Ordinal))
        {
            throw new ArgumentException("An SSH key fingerprint must be SHA-256.", nameof(sha256Fingerprint));
        }

        Format = format;
        PublicKeyAlgorithm = KeyStoreText.Require(publicKeyAlgorithm, 64, nameof(publicKeyAlgorithm));
        Sha256Fingerprint = KeyStoreText.Require(sha256Fingerprint, 128, nameof(sha256Fingerprint));
        Comment = KeyStoreText.Optional(comment, 256, nameof(comment));
    }

    public SftpPrivateKeyFormat Format { get; }

    public string PublicKeyAlgorithm { get; }

    public string Sha256Fingerprint { get; }

    public string? Comment { get; }
}

/// <summary>
/// One importable private key or certificate bundle, shared by any number of connection profiles.
/// The entry holds opaque vault references only; the material itself never leaves the vault except
/// as the short-lived runtime file the connector materializes for a single connection.
/// </summary>
public sealed record KeyStoreEntry
{
    private KeyStoreEntry(
        KeyStoreEntryId id,
        KeyMaterialKind kind,
        string displayName,
        string? description,
        IReadOnlyList<string> tags,
        SecretReference materialReference,
        SecretReference? passphraseReference,
        KeyMaterialSummary summary,
        int version,
        DateTimeOffset createdUtc,
        DateTimeOffset updatedUtc)
    {
        Id = id;
        Kind = kind;
        DisplayName = displayName;
        Description = description;
        Tags = tags;
        MaterialReference = materialReference;
        PassphraseReference = passphraseReference;
        Summary = summary;
        Version = version;
        CreatedUtc = createdUtc;
        UpdatedUtc = updatedUtc;
    }

    public KeyStoreEntryId Id { get; }

    public KeyMaterialKind Kind { get; }

    public string DisplayName { get; }

    public string? Description { get; }

    public IReadOnlyList<string> Tags { get; }

    public SecretReference MaterialReference { get; }

    /// <summary>
    /// Required for an SSH private key, because the SFTP connector refuses an unprotected key
    /// outright. Optional for a PKCS#12 bundle, which may legitimately carry no password.
    /// </summary>
    public SecretReference? PassphraseReference { get; }

    public KeyMaterialSummary Summary { get; }

    public int Version { get; }

    public DateTimeOffset CreatedUtc { get; }

    public DateTimeOffset UpdatedUtc { get; }

    public static KeyStoreEntry Create(
        KeyStoreEntryId id,
        KeyMaterialKind kind,
        string displayName,
        SecretReference materialReference,
        SecretReference? passphraseReference,
        KeyMaterialSummary summary,
        DateTimeOffset createdUtc,
        string? description = null,
        IEnumerable<string>? tags = null) =>
        Rehydrate(
            id,
            kind,
            displayName,
            materialReference,
            passphraseReference,
            summary,
            version: 1,
            createdUtc,
            createdUtc,
            description,
            tags);

    public static KeyStoreEntry Rehydrate(
        KeyStoreEntryId id,
        KeyMaterialKind kind,
        string displayName,
        SecretReference materialReference,
        SecretReference? passphraseReference,
        KeyMaterialSummary summary,
        int version,
        DateTimeOffset createdUtc,
        DateTimeOffset updatedUtc,
        string? description = null,
        IEnumerable<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (id.IsEmpty)
        {
            throw new ArgumentException("A key store entry requires a non-empty identifier.", nameof(id));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), "The key material kind is not recognized.");
        }

        ValidateReference(materialReference, nameof(materialReference));
        if (passphraseReference is { } passphrase)
        {
            ValidateReference(passphrase, nameof(passphraseReference));
        }
        else if (kind is KeyMaterialKind.SshPrivateKey)
        {
            // The SFTP connector rejects an unprotected private key, so storing one would be
            // storing something StorageHub could never use.
            throw new ArgumentException(
                "An SSH private key requires a vault-backed passphrase.",
                nameof(passphraseReference));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);

        var expected = kind is KeyMaterialKind.Pkcs12Certificate
            ? typeof(Pkcs12CertificateSummary)
            : typeof(SshPrivateKeySummary);
        if (summary.GetType() != expected)
        {
            throw new ArgumentException($"A {kind} entry requires a {expected.Name} summary.", nameof(summary));
        }

        return new KeyStoreEntry(
            id,
            kind,
            KeyStoreText.Require(displayName, 128, nameof(displayName)),
            KeyStoreText.Optional(description, 512, nameof(description)),
            KeyStoreText.NormalizeTags(tags),
            materialReference,
            passphraseReference,
            summary,
            version,
            createdUtc,
            updatedUtc);
    }

    /// <summary>
    /// Records replacement material under the same identity. Consuming profiles are not rewritten:
    /// each one keeps the same opaque reference and picks up the new revision, and their root
    /// identities change on their own because those hash the vault reference revisions.
    /// </summary>
    public KeyStoreEntry WithRotatedMaterial(KeyMaterialSummary summary, DateTimeOffset updatedUtc) =>
        Rehydrate(
            Id,
            Kind,
            DisplayName,
            MaterialReference,
            PassphraseReference,
            summary,
            Version + 1,
            CreatedUtc,
            updatedUtc,
            Description,
            Tags);

    public KeyStoreEntry WithDetails(
        string displayName,
        string? description,
        IEnumerable<string>? tags,
        DateTimeOffset updatedUtc) =>
        Rehydrate(
            Id,
            Kind,
            displayName,
            MaterialReference,
            PassphraseReference,
            Summary,
            Version + 1,
            CreatedUtc,
            updatedUtc,
            description,
            tags);

    private static void ValidateReference(SecretReference reference, string parameterName)
    {
        if (!SecretReference.TryParse(reference.Value, out _))
        {
            throw new ArgumentException("The vault reference is malformed.", parameterName);
        }
    }
}

internal static class KeyStoreText
{
    public static string Require(string? value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"The value cannot exceed {maximumLength} characters.");
        }

        if (normalized.Any(char.IsControl))
        {
            throw new ArgumentException("The value cannot contain control characters.", parameterName);
        }

        return normalized;
    }

    public static string? Optional(string? value, int maximumLength, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? null : Require(value, maximumLength, parameterName);

    public static string RequireHex(string? value, int length, string parameterName)
    {
        var normalized = Require(value, length, parameterName);
        if (normalized.Length != length || !normalized.All(Uri.IsHexDigit))
        {
            throw new ArgumentException($"The value must be {length} hexadecimal characters.", parameterName);
        }

        return normalized.ToUpperInvariant();
    }

    public static IReadOnlyList<string> NormalizeTags(IEnumerable<string>? tags)
    {
        if (tags is null)
        {
            return [];
        }

        var normalized = new List<string>();
        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }

            var trimmed = Require(tag, 48, nameof(tags));
            if (!normalized.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                normalized.Add(trimmed);
            }
        }

        if (normalized.Count > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(tags), "An entry cannot carry more than 16 tags.");
        }

        normalized.Sort(StringComparer.OrdinalIgnoreCase);
        return normalized;
    }
}
