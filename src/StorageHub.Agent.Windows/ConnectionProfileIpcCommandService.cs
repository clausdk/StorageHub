using StorageHub.Agent.Ipc;
using StorageHub.Application.Connections;
using StorageHub.Contracts.Ipc;
using StorageHub.Domain.Identifiers;
using StorageHub.Persistence;
using StorageHub.Persistence.Connections;
using StorageHub.Security;
using ContractWriteStatus = StorageHub.Contracts.Ipc.ConnectionProfileWriteStatus;
using DomainWriteStatus = StorageHub.Application.Connections.ConnectionProfileWriteStatus;

namespace StorageHub.Agent.Windows;

/// <summary>Versioned normal-IPC CRUD for non-secret profile documents and opaque references.</summary>
public sealed class ConnectionProfileIpcCommandService : IAgentIpcCommandHandler
{
    private readonly IConnectionProfileRepository _profiles;
    private readonly TimeProvider _timeProvider;

    public ConnectionProfileIpcCommandService(
        IConnectionProfileRepository profiles,
        TimeProvider? timeProvider = null)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ConnectionProfileIpcCommandService(
        SqliteDatabaseOptions databaseOptions,
        TimeProvider? timeProvider = null)
        : this(new SqliteConnectionProfileRepository(databaseOptions, timeProvider), timeProvider)
    {
    }

    public bool CanHandle(string messageType) => messageType is
        ConnectionProfileIpcMessageTypes.GetRequest or
        ConnectionProfileIpcMessageTypes.CreateRequest or
        ConnectionProfileIpcMessageTypes.UpdateRequest or
        ConnectionProfileIpcMessageTypes.DeleteRequest;

    public ValueTask<AgentIpcCommandResponse> HandleAsync(
        IpcEnvelope request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.MessageType switch
        {
            ConnectionProfileIpcMessageTypes.GetRequest => GetAsync(request, cancellationToken),
            ConnectionProfileIpcMessageTypes.CreateRequest => CreateAsync(request, cancellationToken),
            ConnectionProfileIpcMessageTypes.UpdateRequest => UpdateAsync(request, cancellationToken),
            ConnectionProfileIpcMessageTypes.DeleteRequest => DeleteAsync(request, cancellationToken),
            _ => ValueTask.FromResult(AgentIpcCommandResponse.Error(
                "ipc.message.unsupported",
                "The requested IPC operation is not supported by this agent version."))
        };
    }

    private async ValueTask<AgentIpcCommandResponse> GetAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<ConnectionProfileGetRequest>();
        if (!ConnectionProfileIpcContract.IsSupported(request.ContractVersion) || !request.HasValidBounds)
        {
            return GetFailure("connection.profile.request.invalid", StorageIpcFailureCategory.Validation,
                "The profile request was invalid or outside the negotiated bounds.");
        }

        try
        {
            var profile = await _profiles.GetAsync(
                new ConnectionProfileId(request.ConnectionId),
                includeDeleted: false,
                cancellationToken).ConfigureAwait(false);
            return profile is null
                ? GetFailure("connection.profile.not_found", StorageIpcFailureCategory.NotFound,
                    "The saved connection profile was not found.")
                : AgentIpcCommandResponse.Create(
                    ConnectionProfileIpcMessageTypes.GetResponse,
                    new ConnectionProfileGetResponse(
                        ConnectionProfileIpcContract.CurrentVersion,
                        ConnectionProfileIpcMapper.ToDocument(profile)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return GetFailure("connection.profile.unavailable", StorageIpcFailureCategory.Unavailable,
                "Saved connection profiles are temporarily unavailable.", isTransient: true);
        }
    }

    private async ValueTask<AgentIpcCommandResponse> CreateAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<ConnectionProfileCreateRequest>();
        if (!ConnectionProfileIpcContract.IsSupported(request.ContractVersion) || !request.HasValidBounds)
        {
            return WriteFailure(
                ConnectionProfileIpcMessageTypes.CreateResponse,
                ContractWriteStatus.ValidationFailed,
                "connection.profile.request.invalid",
                StorageIpcFailureCategory.Validation,
                "The profile draft was invalid or outside the negotiated bounds.");
        }

        try
        {
            var now = UtcNow();
            var profile = ConnectionProfileIpcMapper.ToNewProfile(
                ConnectionProfileId.New(),
                request.Draft,
                now);
            var result = await _profiles.CreateAsync(profile, cancellationToken).ConfigureAwait(false);
            return MapWriteResult(ConnectionProfileIpcMessageTypes.CreateResponse, result);
        }
        catch (Exception error) when (IsValidationError(error))
        {
            return WriteFailure(
                ConnectionProfileIpcMessageTypes.CreateResponse,
                ContractWriteStatus.ValidationFailed,
                "connection.profile.validation_failed",
                StorageIpcFailureCategory.Validation,
                "The profile settings are invalid for the selected provider.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return WriteFailure(
                ConnectionProfileIpcMessageTypes.CreateResponse,
                ContractWriteStatus.Unavailable,
                "connection.profile.unavailable",
                StorageIpcFailureCategory.Unavailable,
                "The profile could not be saved.",
                isTransient: true);
        }
    }

    private async ValueTask<AgentIpcCommandResponse> UpdateAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<ConnectionProfileUpdateRequest>();
        if (!ConnectionProfileIpcContract.IsSupported(request.ContractVersion) || !request.HasValidBounds)
        {
            return WriteFailure(
                ConnectionProfileIpcMessageTypes.UpdateResponse,
                ContractWriteStatus.ValidationFailed,
                "connection.profile.request.invalid",
                StorageIpcFailureCategory.Validation,
                "The profile update was invalid or outside the negotiated bounds.");
        }

        try
        {
            var id = new ConnectionProfileId(request.ConnectionId);
            var existing = await _profiles.GetAsync(
                id,
                includeDeleted: true,
                cancellationToken).ConfigureAwait(false);
            var profile = ConnectionProfileIpcMapper.ToUpdateProfile(
                id,
                request.ExpectedVersion,
                request.Draft,
                UtcNow(),
                existing?.Metadata.Notes);
            var result = await _profiles.UpdateAsync(
                profile,
                request.ExpectedVersion,
                cancellationToken).ConfigureAwait(false);
            return MapWriteResult(ConnectionProfileIpcMessageTypes.UpdateResponse, result);
        }
        catch (Exception error) when (IsValidationError(error))
        {
            return WriteFailure(
                ConnectionProfileIpcMessageTypes.UpdateResponse,
                ContractWriteStatus.ValidationFailed,
                "connection.profile.validation_failed",
                StorageIpcFailureCategory.Validation,
                "The profile settings are invalid for the selected provider.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return WriteFailure(
                ConnectionProfileIpcMessageTypes.UpdateResponse,
                ContractWriteStatus.Unavailable,
                "connection.profile.unavailable",
                StorageIpcFailureCategory.Unavailable,
                "The profile could not be updated.",
                isTransient: true);
        }
    }

    private async ValueTask<AgentIpcCommandResponse> DeleteAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<ConnectionProfileDeleteRequest>();
        if (!ConnectionProfileIpcContract.IsSupported(request.ContractVersion) || !request.HasValidBounds)
        {
            return WriteFailure(
                ConnectionProfileIpcMessageTypes.DeleteResponse,
                ContractWriteStatus.ValidationFailed,
                "connection.profile.request.invalid",
                StorageIpcFailureCategory.Validation,
                "The profile delete request was invalid or outside the negotiated bounds.");
        }

        try
        {
            var result = await _profiles.SoftDeleteAsync(
                new ConnectionProfileId(request.ConnectionId),
                request.ExpectedVersion,
                cancellationToken).ConfigureAwait(false);
            return MapWriteResult(ConnectionProfileIpcMessageTypes.DeleteResponse, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return WriteFailure(
                ConnectionProfileIpcMessageTypes.DeleteResponse,
                ContractWriteStatus.Unavailable,
                "connection.profile.unavailable",
                StorageIpcFailureCategory.Unavailable,
                "The profile could not be deleted.",
                isTransient: true);
        }
    }

    private static AgentIpcCommandResponse MapWriteResult(
        string responseType,
        ConnectionProfileWriteResult result)
    {
        var status = result.Status switch
        {
            DomainWriteStatus.Succeeded => ContractWriteStatus.Succeeded,
            DomainWriteStatus.NotFound => ContractWriteStatus.NotFound,
            DomainWriteStatus.VersionConflict => ContractWriteStatus.VersionConflict,
            DomainWriteStatus.NameConflict => ContractWriteStatus.NameConflict,
            DomainWriteStatus.Deleted => ContractWriteStatus.Deleted,
            _ => ContractWriteStatus.Unavailable
        };
        var failure = status == ContractWriteStatus.Succeeded
            ? null
            : WriteStatusFailure(status);
        return AgentIpcCommandResponse.Create(
            responseType,
            new ConnectionProfileWriteResponse(
                ConnectionProfileIpcContract.CurrentVersion,
                status,
                result.Profile is null ? null : ConnectionProfileIpcMapper.ToDocument(result.Profile),
                result.ActualVersion,
                failure));
    }

    private static StorageIpcFailure WriteStatusFailure(ContractWriteStatus status) => status switch
    {
        ContractWriteStatus.NotFound => new StorageIpcFailure(
            "connection.profile.not_found", StorageIpcFailureCategory.NotFound,
            "The saved connection profile was not found.", IsTransient: false),
        ContractWriteStatus.VersionConflict => new StorageIpcFailure(
            "connection.profile.version_conflict", StorageIpcFailureCategory.Conflict,
            "The profile changed after it was opened. Reload it before saving.", IsTransient: false),
        ContractWriteStatus.NameConflict => new StorageIpcFailure(
            "connection.profile.name_conflict", StorageIpcFailureCategory.Conflict,
            "Another saved connection already uses this name.", IsTransient: false),
        ContractWriteStatus.Deleted => new StorageIpcFailure(
            "connection.profile.deleted", StorageIpcFailureCategory.Conflict,
            "The saved connection profile has already been deleted.", IsTransient: false),
        _ => new StorageIpcFailure(
            "connection.profile.unavailable", StorageIpcFailureCategory.Unavailable,
            "The profile operation could not be completed.", IsTransient: true)
    };

    private static AgentIpcCommandResponse GetFailure(
        string code,
        StorageIpcFailureCategory category,
        string message,
        bool isTransient = false) => AgentIpcCommandResponse.Create(
        ConnectionProfileIpcMessageTypes.GetResponse,
        new ConnectionProfileGetResponse(
            ConnectionProfileIpcContract.CurrentVersion,
            Profile: null,
            new StorageIpcFailure(code, category, message, isTransient)));

    private static AgentIpcCommandResponse WriteFailure(
        string responseType,
        ContractWriteStatus status,
        string code,
        StorageIpcFailureCategory category,
        string message,
        bool isTransient = false) => AgentIpcCommandResponse.Create(
        responseType,
        new ConnectionProfileWriteResponse(
            ConnectionProfileIpcContract.CurrentVersion,
            status,
            Failure: new StorageIpcFailure(code, category, message, isTransient)));

    private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();

    private static bool IsValidationError(Exception error) => error is
        ArgumentException or FormatException or UriFormatException;
}

internal static class ConnectionProfileIpcMapper
{
    public static ConnectionProfile ToNewProfile(
        ConnectionProfileId id,
        ConnectionProfileDraft draft,
        DateTimeOffset now)
    {
        var profile = ConnectionProfile.Create(
            id,
            ToMetadata(draft.Metadata, notes: null),
            ToEndpoint(draft.Endpoint),
            ToAuthentication(draft.Authentication),
            ToOperationalOptions(draft.OperationalOptions),
            now);
        return draft.IsEnabled
            ? profile
            : ConnectionProfile.Rehydrate(
                profile.Id,
                profile.Provider,
                profile.Metadata,
                profile.Endpoint,
                profile.Authentication,
                profile.OperationalOptions,
                isEnabled: false,
                version: 1,
                now,
                now,
                deletedUtc: null);
    }

    public static ConnectionProfile ToUpdateProfile(
        ConnectionProfileId id,
        long expectedVersion,
        ConnectionProfileDraft draft,
        DateTimeOffset now,
        string? preservedNotes) => ConnectionProfile.Rehydrate(
        id,
        MapProvider(draft.Endpoint.Provider),
        ToMetadata(draft.Metadata, preservedNotes),
        ToEndpoint(draft.Endpoint),
        ToAuthentication(draft.Authentication),
        ToOperationalOptions(draft.OperationalOptions),
        draft.IsEnabled,
        expectedVersion,
        now,
        now,
        deletedUtc: null);

    public static ConnectionProfileDocument ToDocument(ConnectionProfile profile) => new(
        profile.Id.Value,
        profile.Version,
        new ConnectionProfileDraft(
            ToMetadataDocument(profile.Metadata),
            ToEndpointDocument(profile.Endpoint),
            ToAuthenticationDocument(profile.Authentication),
            ToOperationalOptionsDocument(profile.OperationalOptions),
            profile.IsEnabled),
        profile.CreatedUtc,
        profile.UpdatedUtc);

    private static ConnectionProfileMetadata ToMetadata(
        ConnectionProfileMetadataDocument value,
        string? notes) => new(
        value.DisplayName,
        value.FolderPath,
        value.Tags,
        value.IsFavorite,
        new ConnectionDefaultPaths(value.HomePath, value.UploadPath, value.DownloadPath),
        value.IconKey,
        value.AccentColor,
        notes);

    private static ConnectionProfileMetadataDocument ToMetadataDocument(ConnectionProfileMetadata value) => new(
        value.DisplayName,
        value.FolderPath,
        [.. value.Tags],
        value.IsFavorite,
        value.DefaultPaths.HomePath,
        value.DefaultPaths.UploadPath,
        value.DefaultPaths.DownloadPath,
        value.IconKey,
        value.AccentColor);

    private static ConnectionEndpoint ToEndpoint(ConnectionEndpointDocument value)
    {
        EnsureEndpointShape(value);
        return value.Provider switch
        {
            StorageConnectionProvider.Local => new LocalEndpoint(value.RootPath!),
            StorageConnectionProvider.S3 => new S3Endpoint(
                value.Bucket!,
                value.Region!,
                ParseOptionalAbsoluteUri(value.ServiceEndpoint),
                value.ForcePathStyle,
                MapTlsPolicy(value.TlsPolicy),
                value.AllowInsecureTransport,
                value.RootPath),
            StorageConnectionProvider.Ftp => new FtpEndpoint(
                value.Host!, value.Port!.Value, value.AllowInsecureTransport, value.RootPath),
            StorageConnectionProvider.Ftps => new FtpsEndpoint(
                value.Host!,
                value.Port!.Value,
                value.FtpsTlsMode == ConnectionFtpsTlsMode.Explicit
                    ? FtpsTlsMode.Explicit
                    : FtpsTlsMode.Implicit,
                MapTlsPolicy(value.TlsPolicy),
                ParseOptionalSecretReference(value.ClientCertificatePfxReference),
                ParseOptionalSecretReference(value.ClientCertificatePasswordReference),
                value.RootPath),
            StorageConnectionProvider.Sftp => new SftpEndpoint(
                value.Host!, value.Port!.Value, MapSshPolicy(value.SshHostKeyPolicy), value.RootPath),
            _ => throw new ArgumentOutOfRangeException(nameof(value), "The provider is invalid.")
        };
    }

    private static ConnectionEndpointDocument ToEndpointDocument(ConnectionEndpoint value) => value switch
    {
        LocalEndpoint endpoint => new(StorageConnectionProvider.Local, RootPath: endpoint.RootPath),
        S3Endpoint endpoint => new(
            StorageConnectionProvider.S3,
            RootPath: endpoint.RootPrefix,
            Bucket: endpoint.Bucket,
            Region: endpoint.Region,
            ServiceEndpoint: endpoint.ServiceEndpoint?.AbsoluteUri,
            ForcePathStyle: endpoint.ForcePathStyle,
            TlsPolicy: MapTlsPolicy(endpoint.TlsPolicy),
            AllowInsecureTransport: endpoint.AllowInsecureHttp),
        FtpEndpoint endpoint => new(
            StorageConnectionProvider.Ftp,
            RootPath: endpoint.RootPath,
            Host: endpoint.Host,
            Port: endpoint.Port,
            AllowInsecureTransport: endpoint.AllowInsecurePlainText),
        FtpsEndpoint endpoint => new(
            StorageConnectionProvider.Ftps,
            RootPath: endpoint.RootPath,
            Host: endpoint.Host,
            Port: endpoint.Port,
            TlsPolicy: MapTlsPolicy(endpoint.TlsPolicy),
            FtpsTlsMode: endpoint.TlsMode == FtpsTlsMode.Explicit
                ? ConnectionFtpsTlsMode.Explicit
                : ConnectionFtpsTlsMode.Implicit,
            ClientCertificatePfxReference: endpoint.ClientCertificatePfxReference?.Value,
            ClientCertificatePasswordReference: endpoint.ClientCertificatePasswordReference?.Value),
        SftpEndpoint endpoint => new(
            StorageConnectionProvider.Sftp,
            RootPath: endpoint.RootPath,
            Host: endpoint.Host,
            Port: endpoint.Port,
            SshHostKeyPolicy: MapSshPolicy(endpoint.HostKeyPolicy)),
        _ => throw new NotSupportedException("The connection endpoint type is not supported by IPC.")
    };

    private static ConnectionAuthentication ToAuthentication(ConnectionAuthenticationDocument value)
    {
        EnsureAuthenticationShape(value);
        return value.Kind switch
        {
            ConnectionAuthenticationKind.None => new NoAuthentication(),
            ConnectionAuthenticationKind.S3DefaultCredentialChain => new S3DefaultCredentialChainAuthentication(),
            ConnectionAuthenticationKind.CredentialReference => new CredentialReferenceAuthentication(
                new CredentialReferenceId(value.CredentialReferenceId!.Value)),
            ConnectionAuthenticationKind.UsernamePassword => new UsernamePasswordAuthentication(
                value.Username!, SecretReference.Parse(value.PasswordReference!)),
            ConnectionAuthenticationKind.S3AccessKey => new S3AccessKeyAuthentication(
                SecretReference.Parse(value.AccessKeyReference!),
                SecretReference.Parse(value.SecretKeyReference!),
                ParseOptionalSecretReference(value.SessionTokenReference)),
            ConnectionAuthenticationKind.SftpPrivateKey => new SftpPrivateKeyAuthentication(
                value.Username!,
                SecretReference.Parse(value.PrivateKeyReference!),
                SecretReference.Parse(value.PrivateKeyPassphraseReference!),
                MapPrivateKeyFormat(value.PrivateKeyFormat)),
            _ => throw new ArgumentOutOfRangeException(nameof(value), "The authentication kind is invalid.")
        };
    }

    private static ConnectionAuthenticationDocument ToAuthenticationDocument(ConnectionAuthentication value) => value switch
    {
        NoAuthentication => new(ConnectionAuthenticationKind.None),
        S3DefaultCredentialChainAuthentication => new(ConnectionAuthenticationKind.S3DefaultCredentialChain),
        CredentialReferenceAuthentication authentication => new(
            ConnectionAuthenticationKind.CredentialReference,
            CredentialReferenceId: authentication.CredentialId.Value),
        UsernamePasswordAuthentication authentication => new(
            ConnectionAuthenticationKind.UsernamePassword,
            Username: authentication.Username,
            PasswordReference: authentication.PasswordReference.Value),
        S3AccessKeyAuthentication authentication => new(
            ConnectionAuthenticationKind.S3AccessKey,
            AccessKeyReference: authentication.AccessKeyReference.Value,
            SecretKeyReference: authentication.SecretKeyReference.Value,
            SessionTokenReference: authentication.SessionTokenReference?.Value),
        SftpPrivateKeyAuthentication authentication => new(
            ConnectionAuthenticationKind.SftpPrivateKey,
            Username: authentication.Username,
            PrivateKeyReference: authentication.PrivateKeyReference.Value,
            PrivateKeyPassphraseReference: authentication.PassphraseReference?.Value,
            PrivateKeyFormat: MapPrivateKeyFormat(authentication.KeyFormat)),
        _ => throw new NotSupportedException("The connection authentication type is not supported by IPC.")
    };

    private static ConnectionOperationalOptions ToOperationalOptions(ConnectionOperationalOptionsDocument value)
    {
        ConnectionProxy? proxy = null;
        if (value.ProxyEndpoint is not null)
        {
            proxy = new ConnectionProxy(
                new Uri(value.ProxyEndpoint, UriKind.Absolute),
                value.ProxyCredentialReferenceId is { } id ? new CredentialReferenceId(id) : null);
        }
        else if (value.ProxyCredentialReferenceId is not null)
        {
            throw new ArgumentException("A proxy credential reference requires a proxy endpoint.", nameof(value));
        }

        return new ConnectionOperationalOptions(
            TimeSpan.FromSeconds(value.ConnectTimeoutSeconds),
            TimeSpan.FromSeconds(value.OperationTimeoutSeconds),
            new ConnectionRetryPolicy(
                value.MaximumRetryAttempts,
                TimeSpan.FromMilliseconds(value.InitialRetryDelayMilliseconds),
                TimeSpan.FromMilliseconds(value.MaximumRetryDelayMilliseconds)),
            proxy,
            new ConnectionBandwidthLimits(value.UploadBytesPerSecond, value.DownloadBytesPerSecond),
            value.EncodingName);
    }

    private static ConnectionOperationalOptionsDocument ToOperationalOptionsDocument(ConnectionOperationalOptions value) => new(
        checked((int)value.ConnectTimeout.TotalSeconds),
        checked((int)value.OperationTimeout.TotalSeconds),
        value.Retry.MaximumAttempts,
        checked((int)value.Retry.InitialDelay.TotalMilliseconds),
        checked((int)value.Retry.MaximumDelay.TotalMilliseconds),
        value.Proxy?.Endpoint.AbsoluteUri,
        value.Proxy?.CredentialId?.Value,
        value.Bandwidth.UploadBytesPerSecond,
        value.Bandwidth.DownloadBytesPerSecond,
        value.EncodingName);

    private static void EnsureEndpointShape(ConnectionEndpointDocument value)
    {
        var valid = value.Provider switch
        {
            StorageConnectionProvider.Local =>
                Present(value.RootPath) && Empty(value.Host) && value.Port is null && Empty(value.Bucket) &&
                Empty(value.Region) && Empty(value.ServiceEndpoint) && !value.ForcePathStyle &&
                !value.AllowInsecureTransport && Empty(value.ClientCertificatePfxReference) &&
                Empty(value.ClientCertificatePasswordReference) &&
                value.TlsPolicy == ConnectionTlsCertificatePolicy.SystemTrust &&
                value.FtpsTlsMode == ConnectionFtpsTlsMode.Explicit &&
                value.SshHostKeyPolicy == ConnectionSshHostKeyPolicy.Pinned,
            StorageConnectionProvider.S3 =>
                Empty(value.Host) && value.Port is null && Present(value.Bucket) && Present(value.Region) &&
                Empty(value.ClientCertificatePfxReference) && Empty(value.ClientCertificatePasswordReference) &&
                value.FtpsTlsMode == ConnectionFtpsTlsMode.Explicit &&
                value.SshHostKeyPolicy == ConnectionSshHostKeyPolicy.Pinned,
            StorageConnectionProvider.Ftp =>
                Present(value.Host) && value.Port is not null && value.AllowInsecureTransport &&
                Empty(value.Bucket) && Empty(value.Region) && Empty(value.ServiceEndpoint) &&
                !value.ForcePathStyle && Empty(value.ClientCertificatePfxReference) &&
                Empty(value.ClientCertificatePasswordReference) &&
                value.TlsPolicy == ConnectionTlsCertificatePolicy.SystemTrust &&
                value.FtpsTlsMode == ConnectionFtpsTlsMode.Explicit &&
                value.SshHostKeyPolicy == ConnectionSshHostKeyPolicy.Pinned,
            StorageConnectionProvider.Ftps =>
                Present(value.Host) && value.Port is not null && Empty(value.Bucket) && Empty(value.Region) &&
                Empty(value.ServiceEndpoint) && !value.ForcePathStyle && !value.AllowInsecureTransport &&
                (Empty(value.ClientCertificatePfxReference) == Empty(value.ClientCertificatePasswordReference)) &&
                value.SshHostKeyPolicy == ConnectionSshHostKeyPolicy.Pinned,
            StorageConnectionProvider.Sftp =>
                Present(value.Host) && value.Port is not null && Empty(value.Bucket) && Empty(value.Region) &&
                Empty(value.ServiceEndpoint) && !value.ForcePathStyle && !value.AllowInsecureTransport &&
                Empty(value.ClientCertificatePfxReference) && Empty(value.ClientCertificatePasswordReference) &&
                value.TlsPolicy == ConnectionTlsCertificatePolicy.SystemTrust &&
                value.FtpsTlsMode == ConnectionFtpsTlsMode.Explicit,
            _ => false
        };
        if (!valid)
        {
            throw new ArgumentException(
                "The endpoint contains missing or provider-irrelevant fields.", nameof(value));
        }
    }

    private static void EnsureAuthenticationShape(ConnectionAuthenticationDocument value)
    {
        var hasUsername = Present(value.Username);
        var hasCredential = value.CredentialReferenceId is not null;
        var hasPassword = Present(value.PasswordReference);
        var hasAccess = Present(value.AccessKeyReference);
        var hasSecret = Present(value.SecretKeyReference);
        var hasToken = Present(value.SessionTokenReference);
        var hasKey = Present(value.PrivateKeyReference);
        var hasPassphrase = Present(value.PrivateKeyPassphraseReference);
        var valid = value.Kind switch
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
        if (!valid)
        {
            throw new ArgumentException(
                "The authentication contains missing or kind-irrelevant fields.", nameof(value));
        }
    }

    private static bool Present(string? value) => !string.IsNullOrWhiteSpace(value);

    private static bool Empty(string? value) => string.IsNullOrEmpty(value);

    private static Uri? ParseOptionalAbsoluteUri(string? value) => value is null
        ? null
        : new Uri(value, UriKind.Absolute);

    private static SecretReference? ParseOptionalSecretReference(string? value) => value is null
        ? null
        : SecretReference.Parse(value);

    private static ConnectionProviderKind MapProvider(StorageConnectionProvider value) => value switch
    {
        StorageConnectionProvider.Local => ConnectionProviderKind.Local,
        StorageConnectionProvider.S3 => ConnectionProviderKind.S3,
        StorageConnectionProvider.Ftp => ConnectionProviderKind.Ftp,
        StorageConnectionProvider.Ftps => ConnectionProviderKind.Ftps,
        StorageConnectionProvider.Sftp => ConnectionProviderKind.Sftp,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "The provider is invalid.")
    };

    private static TlsCertificatePolicy MapTlsPolicy(ConnectionTlsCertificatePolicy value) => value switch
    {
        ConnectionTlsCertificatePolicy.SystemTrust => TlsCertificatePolicy.SystemTrust,
        ConnectionTlsCertificatePolicy.Pinned => TlsCertificatePolicy.Pinned,
        ConnectionTlsCertificatePolicy.TrustOnFirstUse => TlsCertificatePolicy.TrustOnFirstUse,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "The TLS policy is invalid.")
    };

    private static ConnectionTlsCertificatePolicy MapTlsPolicy(TlsCertificatePolicy value) => value switch
    {
        TlsCertificatePolicy.SystemTrust => ConnectionTlsCertificatePolicy.SystemTrust,
        TlsCertificatePolicy.Pinned => ConnectionTlsCertificatePolicy.Pinned,
        TlsCertificatePolicy.TrustOnFirstUse => ConnectionTlsCertificatePolicy.TrustOnFirstUse,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "The TLS policy is invalid.")
    };

    private static SshHostKeyPolicy MapSshPolicy(ConnectionSshHostKeyPolicy value) => value switch
    {
        ConnectionSshHostKeyPolicy.Pinned => SshHostKeyPolicy.Pinned,
        ConnectionSshHostKeyPolicy.TrustOnFirstUse => SshHostKeyPolicy.TrustOnFirstUse,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "The SSH policy is invalid.")
    };

    private static ConnectionSshHostKeyPolicy MapSshPolicy(SshHostKeyPolicy value) => value switch
    {
        SshHostKeyPolicy.Pinned => ConnectionSshHostKeyPolicy.Pinned,
        SshHostKeyPolicy.TrustOnFirstUse => ConnectionSshHostKeyPolicy.TrustOnFirstUse,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "The SSH policy is invalid.")
    };

    private static SftpPrivateKeyFormat MapPrivateKeyFormat(ConnectionSftpPrivateKeyFormat value) => value switch
    {
        ConnectionSftpPrivateKeyFormat.OpenSsh => SftpPrivateKeyFormat.OpenSsh,
        ConnectionSftpPrivateKeyFormat.Pem => SftpPrivateKeyFormat.Pem,
        ConnectionSftpPrivateKeyFormat.Pkcs8 => SftpPrivateKeyFormat.Pkcs8,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "The private-key format is invalid.")
    };

    private static ConnectionSftpPrivateKeyFormat MapPrivateKeyFormat(SftpPrivateKeyFormat value) => value switch
    {
        SftpPrivateKeyFormat.OpenSsh => ConnectionSftpPrivateKeyFormat.OpenSsh,
        SftpPrivateKeyFormat.Pem => ConnectionSftpPrivateKeyFormat.Pem,
        SftpPrivateKeyFormat.Pkcs8 => ConnectionSftpPrivateKeyFormat.Pkcs8,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "The private-key format is invalid.")
    };
}
