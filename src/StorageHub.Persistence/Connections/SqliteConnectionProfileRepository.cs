using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StorageHub.Application.Connections;
using StorageHub.Domain.Identifiers;
using StorageHub.Security;

namespace StorageHub.Persistence.Connections;

public sealed class SqliteConnectionProfileRepository : IConnectionProfileRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SingleWriterSqliteDatabase _database;
    private readonly ConnectionProfileSchemaInitializer _initializer;
    private readonly TimeProvider _timeProvider;

    public SqliteConnectionProfileRepository(
        SqliteDatabaseOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _database = new SingleWriterSqliteDatabase(options);
        _initializer = new ConnectionProfileSchemaInitializer(options);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<ConnectionProfileWriteResult> CreateAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        if (profile.Version != 1 || profile.DeletedUtc is not null)
        {
            throw new ArgumentException("A new profile must be at version 1 and cannot already be deleted.", nameof(profile));
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await _database.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);
        var connection = lease.Connection;

        var existing = await ReadByIdAsync(connection, profile.Id, includeDeleted: true, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return new ConnectionProfileWriteResult(
                ConnectionProfileWriteStatus.VersionConflict,
                ActualVersion: existing.Version);
        }

        try
        {
            await InsertAsync(connection, profile, cancellationToken).ConfigureAwait(false);
            return new ConnectionProfileWriteResult(ConnectionProfileWriteStatus.Succeeded, profile, profile.Version);
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        {
            existing = await ReadByIdAsync(connection, profile.Id, includeDeleted: true, cancellationToken)
                .ConfigureAwait(false);
            return existing is null
                ? new ConnectionProfileWriteResult(ConnectionProfileWriteStatus.NameConflict)
                : new ConnectionProfileWriteResult(
                    ConnectionProfileWriteStatus.VersionConflict,
                    ActualVersion: existing.Version);
        }
    }

    public async ValueTask<ConnectionProfile?> GetAsync(
        ConnectionProfileId id,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _database.OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadByIdAsync(connection, id, includeDeleted, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<ConnectionProfile>> SearchAsync(
        ConnectionProfileSearch search,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(search);
        var limit = search.ValidatedLimit;
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _database.OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT profile_id, provider, metadata_json, endpoint_json, authentication_json,
                   operational_options_json, is_enabled, version, created_utc, updated_utc, deleted_utc
            FROM connection_profiles AS profiles
            WHERE ($include_deleted = 1 OR deleted_utc IS NULL)
              AND ($include_disabled = 1 OR is_enabled = 1)
              AND ($provider IS NULL OR provider = $provider)
              AND ($favorite IS NULL OR is_favorite = $favorite)
              AND ($folder IS NULL OR lower(folder_path) = lower($folder))
              AND ($tag IS NULL OR EXISTS
                    (SELECT 1 FROM json_each(profiles.tags_json) AS tag
                     WHERE lower(CAST(tag.value AS TEXT)) = lower($tag)))
              AND ($text IS NULL OR lower(display_name) LIKE lower($text) ESCAPE '\'
                    OR lower(COALESCE(folder_path, '')) LIKE lower($text) ESCAPE '\'
                    OR lower(COALESCE(json_extract(metadata_json, '$.notes'), '')) LIKE lower($text) ESCAPE '\')
            ORDER BY is_favorite DESC, display_name COLLATE NOCASE, profile_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$include_deleted", search.IncludeDeleted ? 1 : 0);
        command.Parameters.AddWithValue("$include_disabled", search.IncludeDisabled ? 1 : 0);
        command.Parameters.AddWithValue("$provider", search.Provider is { } provider
            ? ProviderToStorage(provider)
            : DBNull.Value);
        command.Parameters.AddWithValue("$favorite", search.IsFavorite is { } favorite
            ? favorite ? 1 : 0
            : DBNull.Value);
        command.Parameters.AddWithValue("$folder", NormalizeSearchValue(search.FolderPath) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$tag", NormalizeSearchValue(search.Tag) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$text", search.Text is { } text
            ? $"%{EscapeLike(text.Trim())}%"
            : DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);

        var profiles = new List<ConnectionProfile>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            profiles.Add(ReadProfile(reader));
        }

        return profiles;
    }

    public async ValueTask<ConnectionProfileWriteResult> UpdateAsync(
        ConnectionProfile profile,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        ValidateExpectedVersion(expectedVersion);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await _database.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);
        var existing = await ReadByIdAsync(lease.Connection, profile.Id, includeDeleted: true, cancellationToken)
            .ConfigureAwait(false);
        var rejection = RejectUnavailable(existing, expectedVersion);
        if (rejection is not null)
        {
            return rejection;
        }

        var updated = ConnectionProfile.Rehydrate(
            profile.Id,
            profile.Provider,
            profile.Metadata,
            profile.Endpoint,
            profile.Authentication,
            profile.OperationalOptions,
            profile.IsEnabled,
            expectedVersion + 1,
            existing!.CreatedUtc,
            UtcNow(),
            deletedUtc: null);
        try
        {
            if (!await UpdateDocumentAsync(lease.Connection, updated, expectedVersion, cancellationToken)
                    .ConfigureAwait(false))
            {
                return await ReadWriteConflictAsync(
                    lease.Connection, profile.Id, expectedVersion, cancellationToken).ConfigureAwait(false);
            }

            return new ConnectionProfileWriteResult(ConnectionProfileWriteStatus.Succeeded, updated, updated.Version);
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        {
            return new ConnectionProfileWriteResult(
                ConnectionProfileWriteStatus.NameConflict,
                ActualVersion: existing.Version);
        }
    }

    public async ValueTask<ConnectionProfileWriteResult> SetEnabledAsync(
        ConnectionProfileId id,
        bool enabled,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        ValidateExpectedVersion(expectedVersion);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await _database.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);
        var existing = await ReadByIdAsync(lease.Connection, id, includeDeleted: true, cancellationToken)
            .ConfigureAwait(false);
        var rejection = RejectUnavailable(existing, expectedVersion);
        if (rejection is not null)
        {
            return rejection;
        }

        var updated = existing! with
        {
            IsEnabled = enabled,
            Version = expectedVersion + 1,
            UpdatedUtc = UtcNow()
        };
        if (!await UpdateDocumentAsync(lease.Connection, updated, expectedVersion, cancellationToken)
                .ConfigureAwait(false))
        {
            return await ReadWriteConflictAsync(
                lease.Connection, id, expectedVersion, cancellationToken).ConfigureAwait(false);
        }

        return new ConnectionProfileWriteResult(ConnectionProfileWriteStatus.Succeeded, updated, updated.Version);
    }

    public async ValueTask<ConnectionProfileWriteResult> SoftDeleteAsync(
        ConnectionProfileId id,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        ValidateExpectedVersion(expectedVersion);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await _database.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);
        var existing = await ReadByIdAsync(lease.Connection, id, includeDeleted: true, cancellationToken)
            .ConfigureAwait(false);
        var rejection = RejectUnavailable(existing, expectedVersion);
        if (rejection is not null)
        {
            return rejection;
        }

        var now = UtcNow();
        var updated = existing! with
        {
            IsEnabled = false,
            Version = expectedVersion + 1,
            UpdatedUtc = now,
            DeletedUtc = now
        };
        if (!await UpdateDocumentAsync(lease.Connection, updated, expectedVersion, cancellationToken)
                .ConfigureAwait(false))
        {
            return await ReadWriteConflictAsync(
                lease.Connection, id, expectedVersion, cancellationToken).ConfigureAwait(false);
        }

        return new ConnectionProfileWriteResult(ConnectionProfileWriteStatus.Succeeded, updated, updated.Version);
    }

    private async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertAsync(
        SqliteConnection connection,
        ConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO connection_profiles
            (profile_id, provider, display_name, folder_path, tags_json, metadata_json, endpoint_json,
             authentication_json, operational_options_json, is_favorite, is_enabled, version,
             created_utc, updated_utc, deleted_utc)
            VALUES
            ($id, $provider, $display_name, $folder_path, $tags, $metadata, $endpoint,
             $authentication, $options, $favorite, $enabled, $version,
             $created, $updated, $deleted);
            """;
        BindProfile(command, profile);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<bool> UpdateDocumentAsync(
        SqliteConnection connection,
        ConnectionProfile profile,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE connection_profiles
            SET provider = $provider,
                display_name = $display_name,
                folder_path = $folder_path,
                tags_json = $tags,
                metadata_json = $metadata,
                endpoint_json = $endpoint,
                authentication_json = $authentication,
                operational_options_json = $options,
                is_favorite = $favorite,
                is_enabled = $enabled,
                version = $version,
                updated_utc = $updated,
                deleted_utc = $deleted
            WHERE profile_id = $id AND version = $expected_version;
            """;
        BindProfile(command, profile);
        command.Parameters.AddWithValue("$expected_version", expectedVersion);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected == 1;
    }

    private static void BindProfile(SqliteCommand command, ConnectionProfile profile)
    {
        command.Parameters.AddWithValue("$id", profile.Id.ToString());
        command.Parameters.AddWithValue("$provider", ProviderToStorage(profile.Provider));
        command.Parameters.AddWithValue("$display_name", profile.Metadata.DisplayName);
        command.Parameters.AddWithValue("$folder_path", profile.Metadata.FolderPath ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(profile.Metadata.Tags, JsonOptions));
        command.Parameters.AddWithValue("$metadata", SerializeMetadata(profile.Metadata));
        command.Parameters.AddWithValue("$endpoint", SerializeEndpoint(profile.Endpoint));
        command.Parameters.AddWithValue("$authentication", SerializeAuthentication(profile.Authentication));
        command.Parameters.AddWithValue("$options", SerializeOptions(profile.OperationalOptions));
        command.Parameters.AddWithValue("$favorite", profile.Metadata.IsFavorite ? 1 : 0);
        command.Parameters.AddWithValue("$enabled", profile.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$version", profile.Version);
        command.Parameters.AddWithValue("$created", FormatTimestamp(profile.CreatedUtc));
        command.Parameters.AddWithValue("$updated", FormatTimestamp(profile.UpdatedUtc));
        command.Parameters.AddWithValue("$deleted", profile.DeletedUtc is { } deleted
            ? FormatTimestamp(deleted)
            : DBNull.Value);
    }

    private static async ValueTask<ConnectionProfile?> ReadByIdAsync(
        SqliteConnection connection,
        ConnectionProfileId id,
        bool includeDeleted,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT profile_id, provider, metadata_json, endpoint_json, authentication_json,
                   operational_options_json, is_enabled, version, created_utc, updated_utc, deleted_utc
            FROM connection_profiles
            WHERE profile_id = $id AND ($include_deleted = 1 OR deleted_utc IS NULL);
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$include_deleted", includeDeleted ? 1 : 0);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadProfile(reader)
            : null;
    }

    private static ConnectionProfile ReadProfile(SqliteDataReader reader)
    {
        var id = ConnectionProfileId.Parse(reader.GetString(0));
        var metadata = DeserializeMetadata(reader.GetString(2));
        var endpoint = DeserializeEndpoint(reader.GetString(3));
        var provider = endpoint.Provider;
        var authentication = DeserializeAuthentication(reader.GetString(4));
        var options = DeserializeOptions(reader.GetString(5));
        var enabled = reader.GetInt64(6) == 1;
        var version = reader.GetInt64(7);
        var created = ParseTimestamp(reader.GetString(8));
        var updated = ParseTimestamp(reader.GetString(9));
        var deleted = reader.IsDBNull(10)
            ? (DateTimeOffset?)null
            : ParseTimestamp(reader.GetString(10));
        return ConnectionProfile.Rehydrate(
            id, provider, metadata, endpoint, authentication, options, enabled, version, created, updated, deleted);
    }

    private static ConnectionProfileWriteResult? RejectUnavailable(
        ConnectionProfile? existing,
        long expectedVersion)
    {
        if (existing is null)
        {
            return new ConnectionProfileWriteResult(ConnectionProfileWriteStatus.NotFound);
        }

        if (existing.DeletedUtc is not null)
        {
            return new ConnectionProfileWriteResult(
                ConnectionProfileWriteStatus.Deleted,
                ActualVersion: existing.Version);
        }

        return existing.Version != expectedVersion
            ? new ConnectionProfileWriteResult(
                ConnectionProfileWriteStatus.VersionConflict,
                ActualVersion: existing.Version)
            : null;
    }

    private static async ValueTask<ConnectionProfileWriteResult> ReadWriteConflictAsync(
        SqliteConnection connection,
        ConnectionProfileId id,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var current = await ReadByIdAsync(connection, id, includeDeleted: true, cancellationToken)
            .ConfigureAwait(false);
        return RejectUnavailable(current, expectedVersion)
            ?? new ConnectionProfileWriteResult(
                ConnectionProfileWriteStatus.VersionConflict,
                ActualVersion: current?.Version);
    }

    private static string SerializeMetadata(ConnectionProfileMetadata metadata) => JsonSerializer.Serialize(
        new PersistedMetadata(
            metadata.DisplayName,
            metadata.FolderPath,
            [.. metadata.Tags],
            metadata.IsFavorite,
            metadata.DefaultPaths.HomePath,
            metadata.DefaultPaths.UploadPath,
            metadata.DefaultPaths.DownloadPath,
            metadata.IconKey,
            metadata.AccentColor,
            metadata.Notes),
        JsonOptions);

    private static ConnectionProfileMetadata DeserializeMetadata(string json)
    {
        var value = Deserialize<PersistedMetadata>(json);
        return new ConnectionProfileMetadata(
            value.DisplayName,
            value.FolderPath,
            value.Tags,
            value.IsFavorite,
            new ConnectionDefaultPaths(value.HomePath, value.UploadPath, value.DownloadPath),
            value.IconKey,
            value.AccentColor,
            value.Notes);
    }

    private static string SerializeEndpoint(ConnectionEndpoint endpoint)
    {
        var value = endpoint switch
        {
            LocalEndpoint local => new PersistedEndpoint("local", RootPath: local.RootPath),
            S3Endpoint s3 => new PersistedEndpoint(
                "s3", RootPath: s3.RootPrefix, Bucket: s3.Bucket, Region: s3.Region, ServiceEndpoint: s3.ServiceEndpoint?.AbsoluteUri,
                ForcePathStyle: s3.ForcePathStyle, TlsPolicy: s3.TlsPolicy,
                AllowInsecureHttp: s3.AllowInsecureHttp),
            FtpEndpoint ftp => new PersistedEndpoint(
                "ftp", RootPath: ftp.RootPath, Host: ftp.Host, Port: ftp.Port, AllowInsecurePlainText: ftp.AllowInsecurePlainText),
            FtpsEndpoint ftps => new PersistedEndpoint(
                "ftps", Host: ftps.Host, Port: ftps.Port, TlsMode: ftps.TlsMode, TlsPolicy: ftps.TlsPolicy,
                PfxReference: ftps.ClientCertificatePfxReference?.Value,
                PfxPasswordReference: ftps.ClientCertificatePasswordReference?.Value,
                RootPath: ftps.RootPath),
            SftpEndpoint sftp => new PersistedEndpoint(
                "sftp", RootPath: sftp.RootPath, Host: sftp.Host, Port: sftp.Port, HostKeyPolicy: sftp.HostKeyPolicy),
            SshClientEndpoint ssh => new PersistedEndpoint(
                "ssh", Host: ssh.Host, Port: ssh.Port, HostKeyPolicy: ssh.HostKeyPolicy),
            _ => throw new NotSupportedException($"Endpoint type {endpoint.GetType().Name} is not supported.")
        };
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private static ConnectionEndpoint DeserializeEndpoint(string json)
    {
        var value = Deserialize<PersistedEndpoint>(json);
        return value.Kind switch
        {
            "local" => new LocalEndpoint(Required(value.RootPath, "local root path")),
            "s3" => new S3Endpoint(
                Required(value.Bucket, "S3 bucket"),
                Required(value.Region, "S3 region"),
                value.ServiceEndpoint is null ? null : new Uri(value.ServiceEndpoint, UriKind.Absolute),
                value.ForcePathStyle,
                value.TlsPolicy ?? TlsCertificatePolicy.Unspecified,
                value.AllowInsecureHttp,
                value.RootPath),
            "ftp" => new FtpEndpoint(
                Required(value.Host, "FTP host"), Required(value.Port, "FTP port"), value.AllowInsecurePlainText,
                value.RootPath),
            "ftps" => new FtpsEndpoint(
                Required(value.Host, "FTPS host"),
                Required(value.Port, "FTPS port"),
                value.TlsMode ?? 0,
                value.TlsPolicy ?? TlsCertificatePolicy.Unspecified,
                ParseSecretReference(value.PfxReference),
                ParseSecretReference(value.PfxPasswordReference),
                value.RootPath),
            "sftp" => new SftpEndpoint(
                Required(value.Host, "SFTP host"),
                Required(value.Port, "SFTP port"),
                value.HostKeyPolicy ?? SshHostKeyPolicy.Unspecified,
                value.RootPath),
            "ssh" => new SshClientEndpoint(
                Required(value.Host, "SSH host"),
                Required(value.Port, "SSH port"),
                value.HostKeyPolicy ?? SshHostKeyPolicy.Unspecified),
            _ => throw new InvalidDataException("The stored connection endpoint kind is unknown.")
        };
    }

    private static string SerializeAuthentication(ConnectionAuthentication authentication)
    {
        var value = authentication switch
        {
            NoAuthentication => new PersistedAuthentication("none"),
            S3DefaultCredentialChainAuthentication => new PersistedAuthentication("s3-default-chain"),
            CredentialReferenceAuthentication credential => new PersistedAuthentication(
                "credential", CredentialId: credential.CredentialId.ToString()),
            UsernamePasswordAuthentication password => new PersistedAuthentication(
                "password", password.Username, PasswordReference: password.PasswordReference.Value),
            S3AccessKeyAuthentication s3 => new PersistedAuthentication(
                "s3-access-key",
                AccessKeyReference: s3.AccessKeyReference.Value,
                SecretKeyReference: s3.SecretKeyReference.Value,
                SessionTokenReference: s3.SessionTokenReference?.Value),
            SftpPrivateKeyAuthentication key => new PersistedAuthentication(
                "sftp-key", key.Username, PrivateKeyReference: key.PrivateKeyReference.Value,
                PassphraseReference: key.PassphraseReference?.Value, KeyFormat: key.KeyFormat),
            SshPrivateKeyPasswordAuthentication mfa => new PersistedAuthentication(
                "ssh-key-password",
                mfa.Username,
                PasswordReference: mfa.PasswordReference.Value,
                PrivateKeyReference: mfa.PrivateKeyReference.Value,
                PassphraseReference: mfa.PassphraseReference.Value,
                KeyFormat: mfa.KeyFormat),
            _ => throw new NotSupportedException(
                $"Authentication type {authentication.GetType().Name} is not supported.")
        };
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private static ConnectionAuthentication DeserializeAuthentication(string json)
    {
        var value = Deserialize<PersistedAuthentication>(json);
        return value.Kind switch
        {
            "none" => new NoAuthentication(),
            "s3-default-chain" => new S3DefaultCredentialChainAuthentication(),
            "credential" => new CredentialReferenceAuthentication(
                CredentialReferenceId.Parse(Required(value.CredentialId, "credential reference"))),
            "password" => new UsernamePasswordAuthentication(
                Required(value.Username, "username"),
                SecretReference.Parse(Required(value.PasswordReference, "password reference"))),
            "s3-access-key" => new S3AccessKeyAuthentication(
                SecretReference.Parse(Required(value.AccessKeyReference, "S3 access-key reference")),
                SecretReference.Parse(Required(value.SecretKeyReference, "S3 secret-key reference")),
                ParseSecretReference(value.SessionTokenReference)),
            "sftp-key" => new SftpPrivateKeyAuthentication(
                Required(value.Username, "username"),
                SecretReference.Parse(Required(value.PrivateKeyReference, "private-key reference")),
                ParseSecretReference(value.PassphraseReference),
                value.KeyFormat ?? 0),
            "ssh-key-password" => new SshPrivateKeyPasswordAuthentication(
                Required(value.Username, "username"),
                SecretReference.Parse(Required(value.PasswordReference, "password reference")),
                SecretReference.Parse(Required(value.PrivateKeyReference, "private-key reference")),
                SecretReference.Parse(Required(value.PassphraseReference, "private-key passphrase reference")),
                value.KeyFormat ?? 0),
            _ => throw new InvalidDataException("The stored connection authentication kind is unknown.")
        };
    }

    private static string SerializeOptions(ConnectionOperationalOptions options) => JsonSerializer.Serialize(
        new PersistedOperationalOptions(
            options.ConnectTimeout.Ticks,
            options.OperationTimeout.Ticks,
            options.Retry.MaximumAttempts,
            options.Retry.InitialDelay.Ticks,
            options.Retry.MaximumDelay.Ticks,
            options.Proxy?.Endpoint.AbsoluteUri,
            options.Proxy?.CredentialId?.ToString(),
            options.Bandwidth.UploadBytesPerSecond,
            options.Bandwidth.DownloadBytesPerSecond,
            options.EncodingName),
        JsonOptions);

    private static ConnectionOperationalOptions DeserializeOptions(string json)
    {
        var value = Deserialize<PersistedOperationalOptions>(json);
        var proxy = value.ProxyEndpoint is null
            ? null
            : new ConnectionProxy(
                new Uri(value.ProxyEndpoint, UriKind.Absolute),
                value.ProxyCredentialId is null
                    ? null
                    : CredentialReferenceId.Parse(value.ProxyCredentialId));
        return new ConnectionOperationalOptions(
            TimeSpan.FromTicks(value.ConnectTimeoutTicks),
            TimeSpan.FromTicks(value.OperationTimeoutTicks),
            new ConnectionRetryPolicy(
                value.MaximumAttempts,
                TimeSpan.FromTicks(value.InitialRetryDelayTicks),
                TimeSpan.FromTicks(value.MaximumRetryDelayTicks)),
            proxy,
            new ConnectionBandwidthLimits(value.UploadBytesPerSecond, value.DownloadBytesPerSecond),
            value.EncodingName);
    }

    private static T Deserialize<T>(string json) where T : class =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidDataException("Stored connection profile JSON is empty.");

    private static SecretReference? ParseSecretReference(string? value) =>
        value is null ? null : SecretReference.Parse(value);

    private static string Required(string? value, string fieldName) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException($"The stored {fieldName} is missing.");

    private static int Required(int? value, string fieldName) =>
        value ?? throw new InvalidDataException($"The stored {fieldName} is missing.");

    private static string ProviderToStorage(ConnectionProviderKind provider) => provider switch
    {
        ConnectionProviderKind.Local => "local",
        ConnectionProviderKind.S3 => "s3",
        ConnectionProviderKind.Ftp => "ftp",
        ConnectionProviderKind.Ftps => "ftps",
        ConnectionProviderKind.Sftp => "sftp",
        // The original schema constrains this legacy storage-provider column. The endpoint JSON
        // carries the authoritative SSH client discriminator and restores ConnectionProviderKind.Ssh.
        ConnectionProviderKind.Ssh => "sftp",
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    private static ConnectionProviderKind ProviderFromStorage(string provider) => provider switch
    {
        "local" => ConnectionProviderKind.Local,
        "s3" => ConnectionProviderKind.S3,
        "ftp" => ConnectionProviderKind.Ftp,
        "ftps" => ConnectionProviderKind.Ftps,
        "sftp" => ConnectionProviderKind.Sftp,
        _ => throw new InvalidDataException("The stored connection provider is unknown.")
    };

    private static void ValidateId(ConnectionProfileId id)
    {
        if (id.IsEmpty)
        {
            throw new ArgumentException("The profile identifier cannot be empty.", nameof(id));
        }
    }

    private static void ValidateExpectedVersion(long expectedVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedVersion);
    }

    private static string? NormalizeSearchValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ToUniversalTime();

    private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow();

    private sealed record PersistedMetadata(
        string DisplayName,
        string? FolderPath,
        string[] Tags,
        bool IsFavorite,
        string? HomePath,
        string? UploadPath,
        string? DownloadPath,
        string? IconKey,
        string? AccentColor,
        string? Notes);

    private sealed record PersistedEndpoint(
        string Kind,
        string? RootPath = null,
        string? Bucket = null,
        string? Region = null,
        string? ServiceEndpoint = null,
        bool ForcePathStyle = false,
        bool AllowInsecureHttp = false,
        string? Host = null,
        int? Port = null,
        bool AllowInsecurePlainText = false,
        FtpsTlsMode? TlsMode = null,
        TlsCertificatePolicy? TlsPolicy = null,
        string? PfxReference = null,
        string? PfxPasswordReference = null,
        SshHostKeyPolicy? HostKeyPolicy = null);

    private sealed record PersistedAuthentication(
        string Kind,
        string? Username = null,
        string? CredentialId = null,
        string? PasswordReference = null,
        string? AccessKeyReference = null,
        string? SecretKeyReference = null,
        string? SessionTokenReference = null,
        string? PrivateKeyReference = null,
        string? PassphraseReference = null,
        SftpPrivateKeyFormat? KeyFormat = null);

    private sealed record PersistedOperationalOptions(
        long ConnectTimeoutTicks,
        long OperationTimeoutTicks,
        int MaximumAttempts,
        long InitialRetryDelayTicks,
        long MaximumRetryDelayTicks,
        string? ProxyEndpoint,
        string? ProxyCredentialId,
        long? UploadBytesPerSecond,
        long? DownloadBytesPerSecond,
        string EncodingName);
}
