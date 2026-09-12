using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace StorageHub.Security;

/// <summary>
/// The password-protected container for a settings export file.
///
/// The envelope is a pure wrapper: the bytes sealed here are byte-for-byte the bytes an
/// unprotected export would contain. One payload, one schema, and the absence of plaintext in the
/// ciphertext is directly provable.
///
/// The layout follows <see cref="VersionedFileSecretVault"/>'s envelope discipline — an 8-byte
/// magic, little-endian lengths, every declared length range-checked before anything is allocated,
/// and every failure collapsing to a single exception that reveals nothing.
///
/// <code>
/// off  len  field
/// 0    8    magic "SHEXP001"
/// 8    4    envelopeVersion
/// 12   4    kdfId
/// 16   4    iterations
/// 20   4    saltLength
/// 24   4    nonceLength
/// 28   4    tagLength
/// 32   4    payloadLength
/// 36   32   salt
/// 68   12   nonce
/// 80   N    ciphertext
/// 80+N 16   tag
/// </code>
/// </summary>
public static class SettingsExportEnvelope
{
    private static readonly byte[] Magic = "SHEXP001"u8.ToArray();

    public const int EnvelopeVersion = 1;

    /// <summary>PBKDF2-HMAC-SHA256. A second identifier would mean a second code path here.</summary>
    public const int Pbkdf2HmacSha256 = 1;

    public const int SaltLength = 32;
    public const int NonceLength = 12;
    public const int TagLength = 16;
    public const int KeyLength = 32;
    public const int FixedHeaderLength = 8 + (sizeof(int) * 7);

    /// <summary>The header, salt and nonce, which are authenticated as one contiguous span.</summary>
    public const int AuthenticatedHeaderLength = FixedHeaderLength + SaltLength + NonceLength;

    /// <summary>
    /// Written into new files. Stored rather than assumed so this can be raised later without a
    /// new envelope version, leaving older files readable.
    /// </summary>
    public const int DefaultIterations = 600_000;

    /// <summary>
    /// A tampered header must not be able to downgrade the work factor. The authentication tag
    /// already catches that; this is the cheap second line.
    /// </summary>
    public const int MinimumIterations = 100_000;

    /// <summary>
    /// A hostile file must not turn "open this" into minutes of CPU. Derivation happens before the
    /// tag can be checked, so this bound is the only thing standing in front of it.
    /// </summary>
    public const int MaximumIterations = 5_000_000;

    public const int MinimumPasswordLength = 8;
    public const int MaximumPasswordLength = 256;

    /// <summary>
    /// Desktop settings are capped at 64 KiB and each agent-backed list caps at 100 items, so this
    /// is generous for any real export while still bounding an attacker-supplied file.
    /// </summary>
    public const int MaximumPayloadBytes = 4 * 1024 * 1024;

    public const int MaximumEnvelopeBytes = AuthenticatedHeaderLength + MaximumPayloadBytes + TagLength;

    /// <summary>
    /// Whether these bytes are a protected envelope rather than plain JSON. JSON can never begin
    /// with this magic, so the two forms can share one file extension without ambiguity.
    /// </summary>
    public static bool LooksEncrypted(ReadOnlySpan<byte> content) =>
        content.Length >= Magic.Length && content[..Magic.Length].SequenceEqual(Magic);

    /// <summary>Reports why a password cannot be used, or null when it is acceptable.</summary>
    public static string? ValidatePassword(string? password) => password switch
    {
        null or "" => "Enter a password.",
        _ when password.Trim().Length == 0 => "A password cannot be only spaces.",
        _ when password.Length < MinimumPasswordLength =>
            $"Use at least {MinimumPasswordLength} characters.",
        _ when password.Length > MaximumPasswordLength =>
            $"Use at most {MaximumPasswordLength} characters.",
        _ => null
    };

    /// <summary>Seals a payload under a password.</summary>
    public static byte[] Protect(ReadOnlySpan<byte> payload, string password, int iterations = DefaultIterations)
    {
        if (ValidatePassword(password) is { } passwordError)
        {
            throw new ArgumentException(passwordError, nameof(password));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, MinimumIterations);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(iterations, MaximumIterations);
        if (payload.Length > MaximumPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload), payload.Length, "The settings payload is too large to export.");
        }

        var envelope = new byte[AuthenticatedHeaderLength + payload.Length + TagLength];
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        WriteHeader(envelope, iterations, payload.Length, salt, nonce);

        var key = new byte[KeyLength];
        try
        {
            DeriveKey(password, salt, iterations, key);
            using var cipher = new AesGcm(key, TagLength);
            cipher.Encrypt(
                nonce,
                payload,
                envelope.AsSpan(AuthenticatedHeaderLength, payload.Length),
                envelope.AsSpan(AuthenticatedHeaderLength + payload.Length, TagLength),
                // The whole header, so a tampered work factor or algorithm id fails the tag
                // instead of quietly taking effect. The tag itself cannot be covered, which is
                // why it is last and the authenticated span is contiguous.
                envelope.AsSpan(0, AuthenticatedHeaderLength));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return envelope;
    }

    /// <summary>
    /// Opens a sealed envelope. Every failure — wrong password, truncation, tamper, an unsupported
    /// version — throws <see cref="SettingsExportCorruptedException"/> with the same message.
    /// </summary>
    public static byte[] Unprotect(ReadOnlySpan<byte> envelope, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        if (envelope.Length is < AuthenticatedHeaderLength + TagLength or > MaximumEnvelopeBytes ||
            !LooksEncrypted(envelope))
        {
            throw new SettingsExportCorruptedException();
        }

        var envelopeVersion = BinaryPrimitives.ReadInt32LittleEndian(envelope[8..]);
        var kdfId = BinaryPrimitives.ReadInt32LittleEndian(envelope[12..]);
        var iterations = BinaryPrimitives.ReadInt32LittleEndian(envelope[16..]);
        var saltLength = BinaryPrimitives.ReadInt32LittleEndian(envelope[20..]);
        var nonceLength = BinaryPrimitives.ReadInt32LittleEndian(envelope[24..]);
        var tagLength = BinaryPrimitives.ReadInt32LittleEndian(envelope[28..]);
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(envelope[32..]);

        if (envelopeVersion != EnvelopeVersion ||
            kdfId != Pbkdf2HmacSha256 ||
            iterations is < MinimumIterations or > MaximumIterations ||
            saltLength != SaltLength ||
            nonceLength != NonceLength ||
            tagLength != TagLength ||
            payloadLength is < 0 or > MaximumPayloadBytes ||
            envelope.Length != AuthenticatedHeaderLength + payloadLength + TagLength)
        {
            throw new SettingsExportCorruptedException();
        }

        var key = new byte[KeyLength];
        var payload = new byte[payloadLength];
        try
        {
            DeriveKey(password, envelope.Slice(FixedHeaderLength, SaltLength), iterations, key);
            using var cipher = new AesGcm(key, TagLength);
            cipher.Decrypt(
                envelope.Slice(FixedHeaderLength + SaltLength, NonceLength),
                envelope.Slice(AuthenticatedHeaderLength, payloadLength),
                envelope.Slice(AuthenticatedHeaderLength + payloadLength, TagLength),
                payload,
                envelope[..AuthenticatedHeaderLength]);
            return payload;
        }
        catch (CryptographicException)
        {
            // Covers a wrong password and a tampered file alike; see the exception's own remarks
            // for why the two are never told apart.
            CryptographicOperations.ZeroMemory(payload);
            throw new SettingsExportCorruptedException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static void WriteHeader(
        Span<byte> envelope,
        int iterations,
        int payloadLength,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> nonce)
    {
        Magic.CopyTo(envelope);
        BinaryPrimitives.WriteInt32LittleEndian(envelope[8..], EnvelopeVersion);
        BinaryPrimitives.WriteInt32LittleEndian(envelope[12..], Pbkdf2HmacSha256);
        BinaryPrimitives.WriteInt32LittleEndian(envelope[16..], iterations);
        BinaryPrimitives.WriteInt32LittleEndian(envelope[20..], SaltLength);
        BinaryPrimitives.WriteInt32LittleEndian(envelope[24..], NonceLength);
        BinaryPrimitives.WriteInt32LittleEndian(envelope[28..], TagLength);
        BinaryPrimitives.WriteInt32LittleEndian(envelope[32..], payloadLength);
        salt.CopyTo(envelope[FixedHeaderLength..]);
        nonce.CopyTo(envelope[(FixedHeaderLength + SaltLength)..]);
    }

    /// <summary>
    /// UTF-8 is pinned into the format by deriving from bytes rather than the char overload, so a
    /// future runtime cannot reinterpret a non-ASCII password and orphan existing files.
    ///
    /// The caller's <see cref="string"/> cannot be wiped — it comes from a text box and .NET
    /// strings are immutable. That is an accepted limitation of a desktop password prompt, not an
    /// oversight; everything this method owns is wiped.
    /// </summary>
    private static void DeriveKey(string password, ReadOnlySpan<byte> salt, int iterations, Span<byte> key)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            // The static overload, not the disposable one: it is the form the analyzers accept
            // with an explicit SHA-2 and a work factor above their floor, so no suppression is
            // needed anywhere in this file.
            Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, key, iterations, HashAlgorithmName.SHA256);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }
}
