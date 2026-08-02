using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Sync;

namespace StorageHub.Persistence.Sync;

internal static class SyncPersistenceUtilities
{
    internal const int MaximumJsonBytes = 65_536;
    internal const int MaximumTextLength = 8_192;
    internal const int MaximumKindLength = 128;
    internal const int MaximumOwnerLength = 256;
    internal const int MaximumPageSize = 1_000;
    internal const int MaximumBaselineItems = 1_000_000;
    private static readonly ConnectionProfileId ValidationProfileId =
        new(Guid.Parse("29DA4C82-401A-41B7-898A-B89B41142610"));

    internal static string ValidateRelativePath(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        var normalized = StorageAddress.Create(ValidationProfileId, "sync-persistence-validation", value);
        if (normalized.IsFailure ||
            !string.Equals(value, normalized.Value.CanonicalRelativePath, StringComparison.Ordinal))
        {
            throw new ArgumentException("The relative path must already be canonical and root-relative.", parameterName);
        }

        return value;
    }

    internal static string ValidateText(
        string value,
        string parameterName,
        int maximumLength = MaximumTextLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new ArgumentException("The value is too long or contains control characters.", parameterName);
        }

        return value;
    }

    internal static string? ValidateOptionalText(
        string? value,
        string parameterName,
        int maximumLength = MaximumTextLength) =>
        value is null ? null : ValidateText(value, parameterName, maximumLength);

    internal static string ValidateSafeJsonObject(string json, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(json, parameterName);
        var byteCount = Encoding.UTF8.GetByteCount(json);
        if (byteCount > MaximumJsonBytes)
        {
            throw new ArgumentException("The JSON payload exceeds the durable serialization limit.", parameterName);
        }

        var utf8 = Encoding.UTF8.GetBytes(json);
        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            var objectProperties = new Stack<HashSet<string>>();
            var tokenCount = 0;
            while (reader.Read())
            {
                tokenCount++;
                if (tokenCount == 1 && reader.TokenType != JsonTokenType.StartObject)
                {
                    throw new ArgumentException("The JSON payload root must be an object.", parameterName);
                }

                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        objectProperties.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.EndObject:
                        if (objectProperties.Count == 0)
                        {
                            throw new ArgumentException("The JSON object structure is invalid.", parameterName);
                        }

                        _ = objectProperties.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        if (objectProperties.Count == 0 ||
                            !objectProperties.Peek().Add(reader.GetString()!))
                        {
                            throw new ArgumentException("Duplicate JSON property names are not accepted.", parameterName);
                        }

                        break;
                }
            }

            if (tokenCount == 0 || objectProperties.Count != 0)
            {
                throw new ArgumentException("The JSON payload is incomplete.", parameterName);
            }

            return json;
        }
        catch (JsonException error)
        {
            throw new ArgumentException("The JSON payload is invalid or exceeds the nesting limit.", parameterName, error);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(utf8);
        }
    }

    internal static string ComputeBaselineDigest(
        IReadOnlyDictionary<string, SyncBaselineObservation> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count > MaximumBaselineItems)
        {
            throw new ArgumentException("The baseline contains too many items.", nameof(items));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendInt64(hash, 1);
        AppendInt64(hash, items.Count);
        foreach (var (path, observation) in items.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            ValidateRelativePath(path, nameof(items));
            ArgumentNullException.ThrowIfNull(observation);
            ValidateObservation(observation, nameof(items));
            AppendString(hash, path);
            AppendInt64(hash, observation.Exists ? 1 : 0);
            AppendInt64(hash, observation.Length);
            AppendNullableString(hash, observation.Digest?.Algorithm);
            AppendNullableString(hash, observation.Digest?.Value);
            AppendNullableString(hash, observation.LeftVersionId);
            AppendNullableString(hash, observation.RightVersionId);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static void ValidateObservation(SyncBaselineObservation value, string parameterName)
    {
        if (!value.Exists &&
            (value.Length != 0 || value.Digest is not null ||
             value.LeftVersionId is not null || value.RightVersionId is not null))
        {
            throw new ArgumentException("A missing baseline item cannot carry content metadata.", parameterName);
        }

        if (value.Digest is { } digest)
        {
            ValidateText(digest.Algorithm, parameterName, 64);
            ValidateText(digest.Value, parameterName);
        }

        _ = ValidateOptionalText(value.LeftVersionId, parameterName);
        _ = ValidateOptionalText(value.RightVersionId, parameterName);
    }

    internal static string FormatTimestamp(DateTimeOffset value)
    {
        ValidateUtc(value, nameof(value));
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    internal static DateTimeOffset ParseTimestamp(string value, string description) =>
        DateTimeOffset.TryParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed) && parsed.Offset == TimeSpan.Zero
                ? parsed
                : throw new InvalidDataException($"The persisted {description} is not a UTC timestamp.");

    internal static void ValidateUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The timestamp must be UTC.", parameterName);
        }
    }

    internal static Guid ParseGuid(string value, string description) =>
        Guid.TryParseExact(value, "D", out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new InvalidDataException($"The persisted {description} is invalid.");

    internal static T ParseEnum<T>(string value, string description) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidDataException($"The persisted {description} is unsupported.");

    private static void AppendNullableString(IncrementalHash hash, string? value)
    {
        AppendInt64(hash, value is null ? 0 : 1);
        if (value is not null)
        {
            AppendString(hash, value);
        }
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        var maximum = Encoding.UTF8.GetMaxByteCount(value.Length);
        var bytes = new byte[maximum];
        try
        {
            var length = Encoding.UTF8.GetBytes(value, bytes);
            AppendInt64(hash, length);
            hash.AppendData(bytes, 0, length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }
}
