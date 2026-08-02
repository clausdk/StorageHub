using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Formats.Asn1;
using System.Security.Cryptography;
using Renci.SshNet;
using StorageHub.Application.Connections;

namespace StorageHub.Storage.CodeLogic;

internal enum PrivateKeyValidationResult
{
    Valid,
    Unencrypted,
    Invalid
}

internal static class PrivateKeyEncryptionValidator
{
    private const int MaximumKeyBytes = 16 * 1024 * 1024;
    private const int MaximumOpenSshPublicKeys = 1;
    private const uint MaximumOpenSshBcryptRounds = 256;
    private const int MaximumPkcs8Pbkdf2Iterations = 100_000;
    private const string Pbes2Oid = "1.2.840.113549.1.5.13";
    private const string Pbkdf2Oid = "1.2.840.113549.1.5.12";
    private static ReadOnlySpan<byte> OpenSshMagic => "openssh-key-v1\0"u8;

    internal static PrivateKeyValidationResult Validate(
        ReadOnlySpan<byte> key,
        string passphrase,
        SftpPrivateKeyFormat format)
    {
        if (key.IsEmpty || key.Length > MaximumKeyBytes || string.IsNullOrEmpty(passphrase))
        {
            return PrivateKeyValidationResult.Invalid;
        }

        var envelope = format switch
        {
            SftpPrivateKeyFormat.OpenSsh => ValidateOpenSshEnvelope(key),
            SftpPrivateKeyFormat.Pem => ValidateLegacyPemEnvelope(key),
            SftpPrivateKeyFormat.Pkcs8 => ValidatePkcs8Envelope(key),
            _ => PrivateKeyValidationResult.Invalid
        };

        return envelope == PrivateKeyValidationResult.Valid && !CanDecryptWithSshNet(key, passphrase)
            ? PrivateKeyValidationResult.Invalid
            : envelope;
    }

    private static PrivateKeyValidationResult ValidateOpenSshEnvelope(ReadOnlySpan<byte> key)
    {
        if (!TryDecodeSimplePem(
                key,
                "-----BEGIN OPENSSH PRIVATE KEY-----"u8,
                "-----END OPENSSH PRIVATE KEY-----"u8,
                out var decoded,
                out var decodedLength))
        {
            return PrivateKeyValidationResult.Invalid;
        }

        try
        {
            return InspectOpenSshPayload(decoded.AsSpan(0, decodedLength));
        }
        finally
        {
            ReturnSensitiveBuffer(decoded);
        }
    }

    private static PrivateKeyValidationResult InspectOpenSshPayload(ReadOnlySpan<byte> payload)
    {
        if (!payload.StartsWith(OpenSshMagic))
        {
            return PrivateKeyValidationResult.Invalid;
        }

        var reader = new SshBinaryReader(payload[OpenSshMagic.Length..]);
        if (!reader.TryReadString(out var cipherName) ||
            !reader.TryReadString(out var kdfName) ||
            !reader.TryReadString(out var kdfOptions) ||
            !reader.TryReadUInt32(out var publicKeyCount) ||
            publicKeyCount is 0 or > MaximumOpenSshPublicKeys ||
            !IsPrintableAsciiIdentifier(cipherName) ||
            !IsPrintableAsciiIdentifier(kdfName))
        {
            return PrivateKeyValidationResult.Invalid;
        }

        for (var index = 0U; index < publicKeyCount; index++)
        {
            if (!reader.TryReadString(out var publicKey) || publicKey.IsEmpty)
            {
                return PrivateKeyValidationResult.Invalid;
            }
        }

        if (!reader.TryReadString(out var privateKeyList) ||
            privateKeyList.IsEmpty ||
            !reader.IsEmpty)
        {
            return PrivateKeyValidationResult.Invalid;
        }

        if (cipherName.SequenceEqual("none"u8))
        {
            return kdfName.SequenceEqual("none"u8) && kdfOptions.IsEmpty
                ? PrivateKeyValidationResult.Unencrypted
                : PrivateKeyValidationResult.Invalid;
        }

        if (!kdfName.SequenceEqual("bcrypt"u8))
        {
            return PrivateKeyValidationResult.Invalid;
        }

        var optionsReader = new SshBinaryReader(kdfOptions);
        return optionsReader.TryReadString(out var salt) &&
            salt is { Length: > 0 and <= 1024 } &&
            optionsReader.TryReadUInt32(out var rounds) &&
            rounds is > 0 and <= MaximumOpenSshBcryptRounds &&
            optionsReader.IsEmpty
                ? PrivateKeyValidationResult.Valid
                : PrivateKeyValidationResult.Invalid;
    }

    private static PrivateKeyValidationResult ValidatePkcs8Envelope(ReadOnlySpan<byte> key)
    {
        if (TryDecodeSimplePem(
                key,
                "-----BEGIN ENCRYPTED PRIVATE KEY-----"u8,
                "-----END ENCRYPTED PRIVATE KEY-----"u8,
                out var decoded,
                out var decodedLength))
        {
            try
            {
                return HasBoundedPkcs8Kdf(decoded.AsMemory(0, decodedLength))
                    ? PrivateKeyValidationResult.Valid
                    : PrivateKeyValidationResult.Invalid;
            }
            finally
            {
                ReturnSensitiveBuffer(decoded);
            }
        }

        return TryDecodeAndDiscardSimplePem(
            key,
            "-----BEGIN PRIVATE KEY-----"u8,
            "-----END PRIVATE KEY-----"u8)
                ? PrivateKeyValidationResult.Unencrypted
                : PrivateKeyValidationResult.Invalid;
    }

    private static bool HasBoundedPkcs8Kdf(ReadOnlyMemory<byte> encryptedPrivateKeyInfo)
    {
        try
        {
            var root = new AsnReader(encryptedPrivateKeyInfo, AsnEncodingRules.DER);
            var info = root.ReadSequence();
            var algorithm = info.ReadSequence();
            if (!string.Equals(algorithm.ReadObjectIdentifier(), Pbes2Oid, StringComparison.Ordinal))
            {
                return false;
            }

            var pbes2Parameters = algorithm.ReadSequence();
            algorithm.ThrowIfNotEmpty();
            var keyDerivationFunction = pbes2Parameters.ReadSequence();
            if (!string.Equals(keyDerivationFunction.ReadObjectIdentifier(), Pbkdf2Oid, StringComparison.Ordinal))
            {
                return false;
            }

            var pbkdf2Parameters = keyDerivationFunction.ReadSequence();
            keyDerivationFunction.ThrowIfNotEmpty();
            if (!pbkdf2Parameters.TryReadPrimitiveOctetString(out var salt) ||
                salt.Length is < 8 or > 1024 ||
                !pbkdf2Parameters.TryReadInt32(out var iterationCount) ||
                iterationCount is <= 0 or > MaximumPkcs8Pbkdf2Iterations)
            {
                return false;
            }

            if (HasUniversalTag(pbkdf2Parameters, UniversalTagNumber.Integer) &&
                (!pbkdf2Parameters.TryReadInt32(out var keyLength) || keyLength is <= 0 or > 1024))
            {
                return false;
            }

            if (pbkdf2Parameters.HasData && !ReadSupportedPbkdf2Prf(pbkdf2Parameters))
            {
                return false;
            }

            pbkdf2Parameters.ThrowIfNotEmpty();

            var encryptionScheme = pbes2Parameters.ReadSequence();
            var cipherOid = encryptionScheme.ReadObjectIdentifier();
            if (!IsSupportedAesCbcOid(cipherOid) ||
                !encryptionScheme.TryReadPrimitiveOctetString(out var initializationVector) ||
                initializationVector.Length != 16)
            {
                return false;
            }

            encryptionScheme.ThrowIfNotEmpty();
            pbes2Parameters.ThrowIfNotEmpty();

            if (!info.TryReadPrimitiveOctetString(out var ciphertext) ||
                ciphertext.IsEmpty ||
                ciphertext.Length % 16 != 0)
            {
                return false;
            }

            info.ThrowIfNotEmpty();
            root.ThrowIfNotEmpty();
            return true;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    private static bool ReadSupportedPbkdf2Prf(AsnReader parameters)
    {
        var prf = parameters.ReadSequence();
        var oid = prf.ReadObjectIdentifier();
        if (oid is not ("1.2.840.113549.2.7" or
            "1.2.840.113549.2.9" or
            "1.2.840.113549.2.10" or
            "1.2.840.113549.2.11"))
        {
            return false;
        }

        if (prf.HasData)
        {
            prf.ReadNull();
        }

        prf.ThrowIfNotEmpty();
        return true;
    }

    private static bool HasUniversalTag(AsnReader reader, UniversalTagNumber tagNumber) =>
        reader.HasData &&
        reader.PeekTag() is { TagClass: TagClass.Universal } tag &&
        tag.TagValue == (int)tagNumber;

    private static bool IsSupportedAesCbcOid(string oid) => oid is
        "2.16.840.1.101.3.4.1.2" or
        "2.16.840.1.101.3.4.1.22" or
        "2.16.840.1.101.3.4.1.42";

    private static PrivateKeyValidationResult ValidateLegacyPemEnvelope(ReadOnlySpan<byte> key)
    {
        var reader = new PemLineReader(key);
        if (!reader.TryRead(out var header) || !TryGetLegacyFooter(header, out var footer))
        {
            return PrivateKeyValidationResult.Invalid;
        }

        if (!reader.TryRead(out var firstContentLine))
        {
            return PrivateKeyValidationResult.Invalid;
        }

        var encrypted = firstContentLine.SequenceEqual("Proc-Type: 4,ENCRYPTED"u8);
        ReadOnlySpan<byte> firstBodyLine = default;
        if (encrypted)
        {
            if (!reader.TryRead(out var dekInfo) ||
                !IsValidDekInfo(dekInfo) ||
                !reader.TryRead(out var separator) ||
                !separator.IsEmpty)
            {
                return PrivateKeyValidationResult.Invalid;
            }
        }
        else
        {
            firstBodyLine = firstContentLine;
        }

        if (!TryDecodePemBody(ref reader, footer, firstBodyLine, out var decoded, out _))
        {
            return PrivateKeyValidationResult.Invalid;
        }

        ReturnSensitiveBuffer(decoded);
        return encrypted
            ? PrivateKeyValidationResult.Valid
            : PrivateKeyValidationResult.Unencrypted;
    }

    private static bool TryGetLegacyFooter(ReadOnlySpan<byte> header, out ReadOnlySpan<byte> footer)
    {
        if (header.SequenceEqual("-----BEGIN RSA PRIVATE KEY-----"u8))
        {
            footer = "-----END RSA PRIVATE KEY-----"u8;
            return true;
        }

        if (header.SequenceEqual("-----BEGIN DSA PRIVATE KEY-----"u8))
        {
            footer = "-----END DSA PRIVATE KEY-----"u8;
            return true;
        }

        if (header.SequenceEqual("-----BEGIN EC PRIVATE KEY-----"u8))
        {
            footer = "-----END EC PRIVATE KEY-----"u8;
            return true;
        }

        footer = default;
        return false;
    }

    private static bool IsValidDekInfo(ReadOnlySpan<byte> line)
    {
        const int maximumCipherNameLength = 64;
        ReadOnlySpan<byte> prefix = "DEK-Info: "u8;
        if (!line.StartsWith(prefix))
        {
            return false;
        }

        var value = line[prefix.Length..];
        var separator = value.IndexOf((byte)',');
        if (separator is <= 0 or > maximumCipherNameLength ||
            value[(separator + 1)..].IndexOf((byte)',') >= 0)
        {
            return false;
        }

        var cipherName = value[..separator];
        var initializationVector = value[(separator + 1)..];
        if (initializationVector.Length is < 16 or > 64 ||
            initializationVector.Length % 2 != 0)
        {
            return false;
        }

        foreach (var valueByte in cipherName)
        {
            if (valueByte is not (>= (byte)'A' and <= (byte)'Z') and
                not (>= (byte)'0' and <= (byte)'9') and
                not (byte)'-')
            {
                return false;
            }
        }

        foreach (var valueByte in initializationVector)
        {
            if (!IsHexadecimal(valueByte))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryDecodeAndDiscardSimplePem(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> header,
        ReadOnlySpan<byte> footer)
    {
        if (!TryDecodeSimplePem(key, header, footer, out var decoded, out _))
        {
            return false;
        }

        ReturnSensitiveBuffer(decoded);
        return true;
    }

    private static bool TryDecodeSimplePem(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> expectedHeader,
        ReadOnlySpan<byte> expectedFooter,
        out byte[] decoded,
        out int decodedLength)
    {
        decoded = [];
        decodedLength = 0;
        var reader = new PemLineReader(key);
        return reader.TryRead(out var header) &&
            header.SequenceEqual(expectedHeader) &&
            TryDecodePemBody(ref reader, expectedFooter, default, out decoded, out decodedLength);
    }

    private static bool TryDecodePemBody(
        ref PemLineReader reader,
        ReadOnlySpan<byte> expectedFooter,
        ReadOnlySpan<byte> firstBodyLine,
        out byte[] decoded,
        out int decodedLength)
    {
        decoded = [];
        decodedLength = 0;
        var compact = ArrayPool<byte>.Shared.Rent(Math.Max(1, reader.RemainingLength + firstBodyLine.Length));
        var compactLength = 0;
        try
        {
            if (!firstBodyLine.IsEmpty && !TryAppendBase64Line(firstBodyLine, compact, ref compactLength))
            {
                return false;
            }

            var foundFooter = false;
            while (reader.TryRead(out var line))
            {
                if (line.SequenceEqual(expectedFooter))
                {
                    foundFooter = true;
                    break;
                }

                if (!TryAppendBase64Line(line, compact, ref compactLength))
                {
                    return false;
                }
            }

            if (!foundFooter || compactLength == 0 || !OnlyBlankLinesRemain(ref reader))
            {
                return false;
            }

            decoded = ArrayPool<byte>.Shared.Rent(Base64.GetMaxDecodedFromUtf8Length(compactLength));
            var status = Base64.DecodeFromUtf8(
                compact.AsSpan(0, compactLength),
                decoded,
                out var consumed,
                out decodedLength);
            if (status == OperationStatus.Done && consumed == compactLength && decodedLength > 0)
            {
                return true;
            }

            ReturnSensitiveBuffer(decoded);
            decoded = [];
            decodedLength = 0;
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(compact.AsSpan(0, compactLength));
            ArrayPool<byte>.Shared.Return(compact, clearArray: true);
        }
    }

    private static bool OnlyBlankLinesRemain(ref PemLineReader reader)
    {
        while (reader.TryRead(out var line))
        {
            if (!line.IsEmpty)
            {
                return false;
            }
        }

        return reader.IsValid;
    }

    private static bool TryAppendBase64Line(
        ReadOnlySpan<byte> line,
        byte[] destination,
        ref int destinationLength)
    {
        if (line.IsEmpty || line.Length > destination.Length - destinationLength)
        {
            return false;
        }

        foreach (var value in line)
        {
            if (!IsBase64(value))
            {
                return false;
            }
        }

        line.CopyTo(destination.AsSpan(destinationLength));
        destinationLength += line.Length;
        return true;
    }

    private static bool CanDecryptWithSshNet(ReadOnlySpan<byte> key, string passphrase)
    {
        var keyCopy = key.ToArray();
        try
        {
            using var stream = new MemoryStream(keyCopy, writable: false);
            using var parsedKey = new PrivateKeyFile(stream, passphrase);
            return parsedKey.HostKeyAlgorithms.Count > 0;
        }
#pragma warning disable CA1031 // Malformed hostile key material must fail closed regardless of parser exception type.
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
#pragma warning restore CA1031
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyCopy);
        }
    }

    private static void ReturnSensitiveBuffer(byte[] buffer)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(buffer);
        ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
    }

    private static bool IsPrintableAsciiIdentifier(ReadOnlySpan<byte> value)
    {
        if (value is { Length: 0 or > 64 })
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is < 0x21 or > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsHexadecimal(byte value) =>
        value is >= (byte)'0' and <= (byte)'9' or
            >= (byte)'A' and <= (byte)'F' or
            >= (byte)'a' and <= (byte)'f';

    private static bool IsBase64(byte value) =>
        value is >= (byte)'A' and <= (byte)'Z' or
            >= (byte)'a' and <= (byte)'z' or
            >= (byte)'0' and <= (byte)'9' or
            (byte)'+' or (byte)'/' or (byte)'=';

    private ref struct PemLineReader(ReadOnlySpan<byte> value)
    {
        private readonly ReadOnlySpan<byte> _value = value;
        private int _offset;

        internal bool IsValid { get; private set; } = true;
        internal int RemainingLength => _value.Length - _offset;

        internal bool TryRead(out ReadOnlySpan<byte> line)
        {
            if (!IsValid || _offset >= _value.Length)
            {
                line = default;
                return false;
            }

            var remainder = _value[_offset..];
            var newline = remainder.IndexOf((byte)'\n');
            if (newline < 0)
            {
                line = remainder;
                _offset = _value.Length;
            }
            else
            {
                line = remainder[..newline];
                _offset += newline + 1;
            }

            if (!line.IsEmpty && line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }

            if (line.IndexOf((byte)'\r') >= 0)
            {
                IsValid = false;
                line = default;
                return false;
            }

            return true;
        }
    }

    private ref struct SshBinaryReader(ReadOnlySpan<byte> value)
    {
        private readonly ReadOnlySpan<byte> _value = value;
        private int _offset;

        internal bool IsEmpty => _offset == _value.Length;

        internal bool TryReadUInt32(out uint result)
        {
            if (_value.Length - _offset < sizeof(uint))
            {
                result = 0;
                return false;
            }

            result = BinaryPrimitives.ReadUInt32BigEndian(_value[_offset..]);
            _offset += sizeof(uint);
            return true;
        }

        internal bool TryReadString(out ReadOnlySpan<byte> result)
        {
            if (!TryReadUInt32(out var unsignedLength) ||
                unsignedLength > int.MaxValue ||
                unsignedLength > _value.Length - _offset)
            {
                result = default;
                return false;
            }

            var length = (int)unsignedLength;
            result = _value.Slice(_offset, length);
            _offset += length;
            return true;
        }
    }
}
