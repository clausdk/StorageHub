using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using StorageHub.Security;

namespace StorageHub.Persistence.Trust;

public sealed class TrustRecordConcurrencyException(string trustId) : InvalidOperationException(
    $"Trust record '{trustId}' changed or conflicts with an existing fingerprint.");

/// <summary>Persists non-secret certificate and host-key trust decisions in the authoritative database.</summary>
public sealed partial class SqliteTrustStore(SingleWriterSqliteDatabase database) : ITrustStore
{
    private readonly SingleWriterSqliteDatabase _database =
        database ?? throw new ArgumentNullException(nameof(database));

    public async ValueTask<IReadOnlyList<TrustRecord>> FindAsync(
        TrustArtifactKind artifactKind,
        string canonicalHost,
        int port,
        CancellationToken cancellationToken = default)
    {
        ValidateEnum(artifactKind, nameof(artifactKind));
        var host = NormalizeHost(canonicalHost);
        ValidatePort(port);
        await using var connection = await _database
            .OpenReadConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT trust_id, artifact_kind, canonical_host, port, algorithm,
                   sha256_fingerprint, decision, decision_source, first_seen_utc,
                   last_seen_utc, expires_utc, previous_fingerprint, record_version
            FROM trust_records
            WHERE artifact_kind = $kind AND canonical_host = $host AND port = $port
            ORDER BY last_seen_utc DESC, trust_id;
            """;
        command.Parameters.AddWithValue("$kind", ToStorage(artifactKind));
        command.Parameters.AddWithValue("$host", host);
        command.Parameters.AddWithValue("$port", port);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<TrustRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(ReadRecord(reader));
        }

        return records.AsReadOnly();
    }

    public async ValueTask UpsertAsync(
        TrustRecord record,
        CancellationToken cancellationToken = default)
    {
        var normalized = ValidateAndNormalize(record);
        await using var lease = await _database.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await lease.Connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await ValidateRevisionAsync(
            lease.Connection,
            (SqliteTransaction)transaction,
            normalized,
            cancellationToken).ConfigureAwait(false);
        await using var command = lease.Connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO trust_records
            (
                trust_id, profile_id, artifact_kind, canonical_host, port, algorithm,
                sha256_fingerprint, decision, decision_source, first_seen_utc,
                last_seen_utc, expires_utc, previous_fingerprint, record_version
            )
            VALUES
            (
                $id, NULL, $kind, $host, $port, $algorithm, $fingerprint, $decision,
                $source, $first_seen, $last_seen, $expires, $previous, $version
            )
            ON CONFLICT(trust_id) DO UPDATE SET
                artifact_kind = excluded.artifact_kind,
                canonical_host = excluded.canonical_host,
                port = excluded.port,
                algorithm = excluded.algorithm,
                sha256_fingerprint = excluded.sha256_fingerprint,
                decision = excluded.decision,
                decision_source = excluded.decision_source,
                last_seen_utc = excluded.last_seen_utc,
                expires_utc = excluded.expires_utc,
                previous_fingerprint = excluded.previous_fingerprint,
                record_version = excluded.record_version
            WHERE excluded.record_version = trust_records.record_version + 1
              AND excluded.first_seen_utc = trust_records.first_seen_utc;
            """;
        AddParameters(command, normalized);
        int affected;
        try
        {
            affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        {
            throw new TrustRecordConcurrencyException(normalized.TrustId);
        }

        if (affected != 1)
        {
            throw new TrustRecordConcurrencyException(normalized.TrustId);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> RemoveAsync(
        string trustId,
        int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var normalizedId = ValidateIdentifier(trustId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedVersion);
        await using var lease = await _database.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);
        await using var command = lease.Connection.CreateCommand();
        command.CommandText = "DELETE FROM trust_records WHERE trust_id = $id AND record_version = $version;";
        command.Parameters.AddWithValue("$id", normalizedId);
        command.Parameters.AddWithValue("$version", expectedVersion);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static async ValueTask ValidateRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TrustRecord record,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT artifact_kind, canonical_host, port, algorithm, sha256_fingerprint,
                   first_seen_utc, record_version
            FROM trust_records
            WHERE trust_id = $id;
            """;
        command.Parameters.AddWithValue("$id", record.TrustId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (record.Version != 1)
            {
                throw new TrustRecordConcurrencyException(record.TrustId);
            }

            return;
        }

        var identityMatches = string.Equals(reader.GetString(0), ToStorage(record.ArtifactKind), StringComparison.Ordinal) &&
            string.Equals(reader.GetString(1), record.CanonicalHost, StringComparison.Ordinal) &&
            reader.GetInt32(2) == record.Port &&
            string.Equals(reader.GetString(3), record.Algorithm, StringComparison.Ordinal) &&
            string.Equals(reader.GetString(4), record.Sha256Fingerprint, StringComparison.Ordinal) &&
            string.Equals(reader.GetString(5), FormatTimestamp(record.FirstSeenUtc), StringComparison.Ordinal) &&
            record.Version == reader.GetInt32(6) + 1;
        if (!identityMatches)
        {
            throw new TrustRecordConcurrencyException(record.TrustId);
        }
    }

    private static TrustRecord ValidateAndNormalize(TrustRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var id = ValidateIdentifier(record.TrustId);
        ValidateEnum(record.ArtifactKind, nameof(record.ArtifactKind));
        var host = NormalizeHost(record.CanonicalHost);
        ValidatePort(record.Port);
        var algorithm = ValidateBounded(record.Algorithm, 64, "algorithm").ToUpperInvariant();
        var fingerprint = NormalizeFingerprint(record.Sha256Fingerprint, nameof(record.Sha256Fingerprint));
        ValidateEnum(record.Decision, nameof(record.Decision));
        ValidateEnum(record.DecisionSource, nameof(record.DecisionSource));
        if (record.FirstSeenUtc.Offset != TimeSpan.Zero ||
            record.LastSeenUtc.Offset != TimeSpan.Zero ||
            record.LastSeenUtc < record.FirstSeenUtc ||
            record.ExpiresUtc is { Offset: var offset } && offset != TimeSpan.Zero ||
            record.ExpiresUtc is { } expires && expires < record.FirstSeenUtc)
        {
            throw new ArgumentException("Trust timestamps must be UTC and chronologically valid.", nameof(record));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(record.Version);
        var previous = record.PreviousFingerprint is null
            ? null
            : NormalizeFingerprint(record.PreviousFingerprint, nameof(record.PreviousFingerprint));
        return record with
        {
            TrustId = id,
            CanonicalHost = host,
            Algorithm = algorithm,
            Sha256Fingerprint = fingerprint,
            PreviousFingerprint = previous
        };
    }

    private static TrustRecord ReadRecord(SqliteDataReader reader) => new(
        reader.GetString(0),
        ParseArtifactKind(reader.GetString(1)),
        reader.GetString(2),
        reader.GetInt32(3),
        reader.GetString(4),
        reader.GetString(5),
        ParseDecision(reader.GetString(6)),
        ParseDecisionSource(reader.GetString(7)),
        ParseTimestamp(reader.GetString(8)),
        ParseTimestamp(reader.GetString(9)),
        reader.IsDBNull(10) ? null : ParseTimestamp(reader.GetString(10)),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.GetInt32(12));

    private static void AddParameters(SqliteCommand command, TrustRecord record)
    {
        command.Parameters.AddWithValue("$id", record.TrustId);
        command.Parameters.AddWithValue("$kind", ToStorage(record.ArtifactKind));
        command.Parameters.AddWithValue("$host", record.CanonicalHost);
        command.Parameters.AddWithValue("$port", record.Port);
        command.Parameters.AddWithValue("$algorithm", record.Algorithm);
        command.Parameters.AddWithValue("$fingerprint", record.Sha256Fingerprint);
        command.Parameters.AddWithValue("$decision", ToStorage(record.Decision));
        command.Parameters.AddWithValue("$source", ToStorage(record.DecisionSource));
        command.Parameters.AddWithValue("$first_seen", FormatTimestamp(record.FirstSeenUtc));
        command.Parameters.AddWithValue("$last_seen", FormatTimestamp(record.LastSeenUtc));
        command.Parameters.AddWithValue("$expires", record.ExpiresUtc is { } expires
            ? FormatTimestamp(expires)
            : DBNull.Value);
        command.Parameters.AddWithValue("$previous", record.PreviousFingerprint ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$version", record.Version);
    }

    private static string NormalizeHost(string host)
    {
        var value = ValidateBounded(host, 253, "host").TrimEnd('.');
        if (IPAddress.TryParse(value.Trim('[', ']'), out var address))
        {
            return address.ToString().ToLowerInvariant();
        }

        if (value.Any(static character => char.IsControl(character) || char.IsWhiteSpace(character) || character is '/' or '\\'))
        {
            throw new ArgumentException("The trust host is invalid.", nameof(host));
        }

        try
        {
            var ascii = new IdnMapping().GetAscii(value).ToLowerInvariant();
            return Uri.CheckHostName(ascii) == UriHostNameType.Unknown
                ? throw new ArgumentException("The trust host is invalid.", nameof(host))
                : ascii;
        }
        catch (ArgumentException)
        {
            throw new ArgumentException("The trust host is invalid.", nameof(host));
        }
    }

    private static string NormalizeFingerprint(string fingerprint, string parameterName)
    {
        var original = ValidateBounded(fingerprint, 128, "fingerprint");
        var hexadecimal = original.Replace(":", string.Empty, StringComparison.Ordinal);
        if (HexSha256().IsMatch(hexadecimal))
        {
            return hexadecimal.ToUpperInvariant();
        }

        if (original.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
        {
            var payload = original[7..];
            try
            {
                var bytes = Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '='));
                return bytes.Length == 32
                    ? "SHA256:" + Convert.ToBase64String(bytes).TrimEnd('=')
                    : throw new ArgumentException("A SHA-256 fingerprint must contain 32 bytes.", parameterName);
            }
            catch (FormatException)
            {
            }
        }

        throw new ArgumentException("The fingerprint must be SHA-256 hexadecimal or SHA256 base64.", parameterName);
    }

    private static string ValidateIdentifier(string value) => ValidateBounded(value, 128, "trust ID");

    private static string ValidateBounded(string value, int maximumLength, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (normalized.Length > maximumLength || normalized.Any(char.IsControl))
        {
            throw new ArgumentException($"The {description} is invalid.", nameof(value));
        }

        return normalized;
    }

    private static void ValidatePort(int port)
    {
        if (port is < 1 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
    }

    private static void ValidateEnum<T>(T value, string parameterName) where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static string ToStorage(TrustArtifactKind value) => value switch
    {
        TrustArtifactKind.TlsCertificate => "tls-certificate",
        TrustArtifactKind.SshHostKey => "ssh-host-key",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string ToStorage(TrustDecision value) => value.ToString().ToLowerInvariant();

    private static string ToStorage(TrustDecisionSource value) => value switch
    {
        TrustDecisionSource.UserVerified => "user-verified",
        TrustDecisionSource.AdministratorPolicy => "administrator-policy",
        TrustDecisionSource.ImportedPolicy => "imported-policy",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static TrustArtifactKind ParseArtifactKind(string value) => value switch
    {
        "tls-certificate" => TrustArtifactKind.TlsCertificate,
        "ssh-host-key" => TrustArtifactKind.SshHostKey,
        _ => throw new InvalidDataException("The stored trust artifact kind is invalid.")
    };

    private static TrustDecision ParseDecision(string value) => value switch
    {
        "trusted" => TrustDecision.Trusted,
        "rejected" => TrustDecision.Rejected,
        "revoked" => TrustDecision.Revoked,
        _ => throw new InvalidDataException("The stored trust decision is invalid.")
    };

    private static TrustDecisionSource ParseDecisionSource(string value) => value switch
    {
        "user-verified" => TrustDecisionSource.UserVerified,
        "administrator-policy" => TrustDecisionSource.AdministratorPolicy,
        "imported-policy" => TrustDecisionSource.ImportedPolicy,
        _ => throw new InvalidDataException("The stored trust decision source is invalid.")
    };

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ToUniversalTime();

    [GeneratedRegex("^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex HexSha256();
}
