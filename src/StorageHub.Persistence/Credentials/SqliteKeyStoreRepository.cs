using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using StorageHub.Application.Connections;
using StorageHub.Application.Credentials;
using StorageHub.Domain.Identifiers;
using StorageHub.Persistence.Connections;
using StorageHub.Security;

namespace StorageHub.Persistence.Credentials;

/// <summary>
/// Durable storage for the key and certificate store. Rows hold opaque vault references and derived
/// non-sensitive summaries only; key material never reaches this layer.
/// </summary>
public sealed class SqliteKeyStoreRepository : IKeyStoreRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SingleWriterSqliteDatabase _database;
    private readonly ConnectionProfileSchemaInitializer _initializer;

    public SqliteKeyStoreRepository(SqliteDatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _database = new SingleWriterSqliteDatabase(options);
        _initializer = new ConnectionProfileSchemaInitializer(options);
    }

    public async ValueTask<KeyStoreWriteResult> CreateAsync(
        KeyStoreEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Version != 1)
        {
            throw new ArgumentException("A new key store entry must be at version 1.", nameof(entry));
        }

        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await _database.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);

        await using var command = lease.Connection.CreateCommand();
        command.CommandText = """
            INSERT INTO credential_references
            (credential_id, credential_kind, display_name, description, tags_json,
             material_reference, passphrase_reference, summary_json, version, created_utc, updated_utc)
            VALUES ($id, $kind, $name, $description, $tags,
                    $material, $passphrase, $summary, $version, $created, $updated);
            """;
        Bind(command, entry);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new KeyStoreWriteResult(KeyStoreWriteStatus.Succeeded, entry, entry.Version);
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        {
            // Constraint violation: either the id is taken or the display name collides.
            var existing = await ReadAsync(lease.Connection, entry.Id, cancellationToken).ConfigureAwait(false);
            return existing is null
                ? new KeyStoreWriteResult(KeyStoreWriteStatus.NameConflict)
                : new KeyStoreWriteResult(KeyStoreWriteStatus.VersionConflict, existing, existing.Version);
        }
    }

    public async ValueTask<KeyStoreEntry?> GetAsync(
        KeyStoreEntryId id,
        CancellationToken cancellationToken = default)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await _database.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);
        return await ReadAsync(lease.Connection, id, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<KeyStoreEntry?> FindByMaterialReferenceAsync(
        string materialReference,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(materialReference))
        {
            return null;
        }

        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await _database.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);

        await using var command = lease.Connection.CreateCommand();
        command.CommandText = """
            SELECT credential_id, credential_kind, display_name, description, tags_json,
                   material_reference, passphrase_reference, summary_json, version, created_utc, updated_utc
            FROM credential_references
            WHERE material_reference = $reference;
            """;
        command.Parameters.AddWithValue("$reference", materialReference.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async ValueTask<IReadOnlyList<KeyStoreEntryUsage>> SearchAsync(
        KeyStoreSearch search,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(search);
        var limit = search.ValidatedLimit;
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await _database.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);

        await using var command = lease.Connection.CreateCommand();
        command.CommandText = """
            SELECT credential_id, credential_kind, display_name, description, tags_json,
                   material_reference, passphrase_reference, summary_json, version, created_utc, updated_utc
            FROM credential_references
            WHERE ($kind IS NULL OR credential_kind = $kind)
              AND ($text IS NULL OR display_name LIKE $text OR IFNULL(description, '') LIKE $text)
              AND ($tag IS NULL OR EXISTS (
                    SELECT 1 FROM json_each(tags_json)
                    WHERE json_each.value = $tag COLLATE NOCASE))
            ORDER BY display_name COLLATE NOCASE
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$kind", search.Kind is { } kind ? KindToStorage(kind) : DBNull.Value);
        command.Parameters.AddWithValue(
            "$text",
            string.IsNullOrWhiteSpace(search.Text) ? DBNull.Value : "%" + search.Text.Trim() + "%");
        command.Parameters.AddWithValue(
            "$tag",
            string.IsNullOrWhiteSpace(search.Tag) ? DBNull.Value : search.Tag.Trim());
        command.Parameters.AddWithValue("$limit", limit);

        var entries = new List<KeyStoreEntry>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                entries.Add(Read(reader));
            }
        }

        var usage = new List<KeyStoreEntryUsage>(entries.Count);
        foreach (var entry in entries)
        {
            usage.Add(new KeyStoreEntryUsage(
                entry,
                await ReadConsumersAsync(lease.Connection, entry.Id, cancellationToken).ConfigureAwait(false)));
        }

        return usage;
    }

    public async ValueTask<KeyStoreWriteResult> UpdateAsync(
        KeyStoreEntry entry,
        int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await _database.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);

        var existing = await ReadAsync(lease.Connection, entry.Id, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return new KeyStoreWriteResult(KeyStoreWriteStatus.NotFound);
        }

        if (existing.Version != expectedVersion)
        {
            return new KeyStoreWriteResult(KeyStoreWriteStatus.VersionConflict, existing, existing.Version);
        }

        await using var command = lease.Connection.CreateCommand();
        command.CommandText = """
            UPDATE credential_references
            SET credential_kind = $kind,
                display_name = $name,
                description = $description,
                tags_json = $tags,
                material_reference = $material,
                passphrase_reference = $passphrase,
                summary_json = $summary,
                version = $version,
                updated_utc = $updated
            WHERE credential_id = $id AND version = $expected;
            """;
        Bind(command, entry);
        command.Parameters.AddWithValue("$expected", expectedVersion);
        try
        {
            var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return changed == 1
                ? new KeyStoreWriteResult(KeyStoreWriteStatus.Succeeded, entry, entry.Version)
                : new KeyStoreWriteResult(KeyStoreWriteStatus.VersionConflict, existing, existing.Version);
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        {
            return new KeyStoreWriteResult(KeyStoreWriteStatus.NameConflict, existing, existing.Version);
        }
    }

    public async ValueTask<KeyStoreWriteResult> DeleteAsync(
        KeyStoreEntryId id,
        int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await _database.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);

        var existing = await ReadAsync(lease.Connection, id, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return new KeyStoreWriteResult(KeyStoreWriteStatus.NotFound);
        }

        if (existing.Version != expectedVersion)
        {
            return new KeyStoreWriteResult(KeyStoreWriteStatus.VersionConflict, existing, existing.Version);
        }

        // Refuse before deleting rather than relying on the foreign key to raise, so the caller can
        // be told exactly which profiles still bind the entry.
        var consumers = await ReadConsumersAsync(lease.Connection, id, cancellationToken).ConfigureAwait(false);
        if (consumers.Count > 0)
        {
            return new KeyStoreWriteResult(
                KeyStoreWriteStatus.StillReferenced,
                existing,
                existing.Version,
                consumers);
        }

        await using var command = lease.Connection.CreateCommand();
        command.CommandText = "DELETE FROM credential_references WHERE credential_id = $id AND version = $expected;";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$expected", expectedVersion);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return changed == 1
            ? new KeyStoreWriteResult(KeyStoreWriteStatus.Succeeded, existing, existing.Version)
            : new KeyStoreWriteResult(KeyStoreWriteStatus.VersionConflict, existing, existing.Version);
    }

    public async ValueTask BindAsync(
        ConnectionProfileId profileId,
        string credentialSlot,
        KeyStoreEntryId entryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialSlot);
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await _database.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);

        await using var command = lease.Connection.CreateCommand();
        command.CommandText = """
            INSERT INTO profile_credentials (profile_id, credential_slot, credential_id)
            VALUES ($profile, $slot, $credential)
            ON CONFLICT (profile_id, credential_slot)
            DO UPDATE SET credential_id = excluded.credential_id;
            """;
        command.Parameters.AddWithValue("$profile", profileId.ToString());
        command.Parameters.AddWithValue("$slot", credentialSlot.Trim());
        command.Parameters.AddWithValue("$credential", entryId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask UnbindAsync(
        ConnectionProfileId profileId,
        string credentialSlot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialSlot);
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await _database.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);

        await using var command = lease.Connection.CreateCommand();
        command.CommandText =
            "DELETE FROM profile_credentials WHERE profile_id = $profile AND credential_slot = $slot;";
        command.Parameters.AddWithValue("$profile", profileId.ToString());
        command.Parameters.AddWithValue("$slot", credentialSlot.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<KeyStoreEntry?> ReadAsync(
        SqliteConnection connection,
        KeyStoreEntryId id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT credential_id, credential_kind, display_name, description, tags_json,
                   material_reference, passphrase_reference, summary_json, version, created_utc, updated_utc
            FROM credential_references
            WHERE credential_id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <summary>
    /// Names the profiles that bind an entry. Soft-deleted profiles are included and labelled: they
    /// keep their binding rows, so the foreign key would refuse the delete anyway, and a report that
    /// disagreed with the constraint would turn a clear refusal into an opaque failure. The profile
    /// provider column deliberately conflates SSH with SFTP, so rows are joined by identity only.
    /// </summary>
    private static async ValueTask<IReadOnlyList<string>> ReadConsumersAsync(
        SqliteConnection connection,
        KeyStoreEntryId id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT p.display_name, p.deleted_utc
            FROM profile_credentials c
            JOIN connection_profiles p ON p.profile_id = c.profile_id
            WHERE c.credential_id = $id
            ORDER BY p.display_name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            names.Add(reader.IsDBNull(1) ? reader.GetString(0) : reader.GetString(0) + " (deleted)");
        }

        return names;
    }

    private static void Bind(SqliteCommand command, KeyStoreEntry entry)
    {
        command.Parameters.AddWithValue("$id", entry.Id.ToString());
        command.Parameters.AddWithValue("$kind", KindToStorage(entry.Kind));
        command.Parameters.AddWithValue("$name", entry.DisplayName);
        command.Parameters.AddWithValue("$description", (object?)entry.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(entry.Tags, JsonOptions));
        command.Parameters.AddWithValue("$material", entry.MaterialReference.Value);
        command.Parameters.AddWithValue(
            "$passphrase",
            (object?)entry.PassphraseReference?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$summary", SerializeSummary(entry.Summary));
        command.Parameters.AddWithValue("$version", entry.Version);
        command.Parameters.AddWithValue("$created", Format(entry.CreatedUtc));
        command.Parameters.AddWithValue("$updated", Format(entry.UpdatedUtc));
    }

    private static KeyStoreEntry Read(SqliteDataReader reader)
    {
        var kind = KindFromStorage(reader.GetString(1));
        return KeyStoreEntry.Rehydrate(
            KeyStoreEntryId.Parse(reader.GetString(0)),
            kind,
            reader.GetString(2),
            SecretReference.Parse(reader.GetString(5)),
            reader.IsDBNull(6) ? null : SecretReference.Parse(reader.GetString(6)),
            DeserializeSummary(kind, reader.GetString(7)),
            reader.GetInt32(8),
            Parse(reader.GetString(9)),
            Parse(reader.GetString(10)),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            JsonSerializer.Deserialize<string[]>(reader.GetString(4), JsonOptions) ?? []);
    }

    private static string SerializeSummary(KeyMaterialSummary summary) => summary switch
    {
        Pkcs12CertificateSummary certificate => JsonSerializer.Serialize(
            new PersistedPkcs12Summary(
                certificate.Subject,
                certificate.Issuer,
                certificate.NotBefore,
                certificate.NotAfter,
                certificate.Sha256Thumbprint,
                certificate.KeyAlgorithm,
                certificate.KeySizeBits,
                certificate.HasPrivateKey,
                certificate.ChainLength),
            JsonOptions),
        SshPrivateKeySummary key => JsonSerializer.Serialize(
            new PersistedSshKeySummary(key.Format, key.PublicKeyAlgorithm, key.Sha256Fingerprint, key.Comment),
            JsonOptions),
        _ => throw new InvalidOperationException("The key material summary type is not persistable.")
    };

    private static KeyMaterialSummary DeserializeSummary(KeyMaterialKind kind, string json)
    {
        if (kind is KeyMaterialKind.Pkcs12Certificate)
        {
            var stored = JsonSerializer.Deserialize<PersistedPkcs12Summary>(json, JsonOptions)
                ?? throw new InvalidOperationException("The stored certificate summary is unreadable.");
            return new Pkcs12CertificateSummary(
                stored.Subject,
                stored.Issuer,
                stored.NotBefore,
                stored.NotAfter,
                stored.Sha256Thumbprint,
                stored.KeyAlgorithm,
                stored.KeySizeBits,
                stored.HasPrivateKey,
                stored.ChainLength);
        }

        var key = JsonSerializer.Deserialize<PersistedSshKeySummary>(json, JsonOptions)
            ?? throw new InvalidOperationException("The stored private key summary is unreadable.");
        return new SshPrivateKeySummary(key.Format, key.PublicKeyAlgorithm, key.Sha256Fingerprint, key.Comment);
    }

    private static string KindToStorage(KeyMaterialKind kind) => kind switch
    {
        KeyMaterialKind.Pkcs12Certificate => "pkcs12-certificate",
        KeyMaterialKind.SshPrivateKey => "ssh-private-key",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static KeyMaterialKind KindFromStorage(string value) => value switch
    {
        "pkcs12-certificate" => KeyMaterialKind.Pkcs12Certificate,
        "ssh-private-key" => KeyMaterialKind.SshPrivateKey,
        _ => throw new InvalidOperationException($"The stored credential kind '{value}' is not recognized.")
    };

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed record PersistedPkcs12Summary(
        string Subject,
        string Issuer,
        DateTimeOffset NotBefore,
        DateTimeOffset NotAfter,
        string Sha256Thumbprint,
        string KeyAlgorithm,
        int KeySizeBits,
        bool HasPrivateKey,
        int ChainLength);

    private sealed record PersistedSshKeySummary(
        [property: JsonConverter(typeof(JsonStringEnumConverter<SftpPrivateKeyFormat>))]
        SftpPrivateKeyFormat Format,
        string PublicKeyAlgorithm,
        string Sha256Fingerprint,
        string? Comment);
}
