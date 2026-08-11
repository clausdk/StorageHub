using StorageHub.Agent.Ipc;
using StorageHub.Application.Connections;
using StorageHub.Contracts.Ipc;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Infrastructure.Windows;
using StorageHub.Persistence;
using StorageHub.Persistence.Connections;
using StorageHub.Persistence.Trust;
using StorageHub.Security;
using StorageHub.Storage.Abstractions;
using StorageHub.Storage.CodeLogic;
using StorageHub.Storage.Models;

namespace StorageHub.Agent.Windows;

/// <summary>A disposable, root-scoped storage session opened for one IPC request.</summary>
public interface IStorageIpcSessionLease : IAsyncDisposable
{
    IStorageEndpointSession Session { get; }
}

/// <summary>Opens a saved profile without persisting its resolved runtime credentials.</summary>
public interface IStorageIpcSessionOpener
{
    ValueTask<StorageResult<IStorageIpcSessionLease>> OpenAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Implements only saved-connection discovery, connection health checks, and bounded storage
/// listing. It intentionally has no upload, mutation, delete, or secret-returning command.
/// </summary>
public sealed class StorageIpcCommandService : IAgentIpcCommandHandler
{
    private readonly IConnectionProfileRepository _profiles;
    private readonly IStorageIpcSessionOpener _sessionOpener;

    public StorageIpcCommandService(
        IConnectionProfileRepository profiles,
        IStorageIpcSessionOpener sessionOpener)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _sessionOpener = sessionOpener ?? throw new ArgumentNullException(nameof(sessionOpener));
    }

    /// <summary>
    /// Creates the production service over the authoritative SQLite stores, current-user vault,
    /// and CL.Storage's runtime-only registration adapter.
    /// </summary>
    public StorageIpcCommandService(
        SqliteDatabaseOptions databaseOptions,
        Func<ISecretVault> vaultProvider,
        WindowsRuntimeSecretFileMaterializer secretFileMaterializer,
        CodeLogicStorageSessionFactory sessionFactory,
        TimeProvider? timeProvider = null)
        : this(
            databaseOptions,
            vaultProvider,
            secretFileMaterializer,
            () => sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory)),
            timeProvider)
    {
    }

    /// <summary>
    /// Defers CL.Storage lookup until a command runs. This supports CodeLogic hosts where the
    /// loaded library becomes retrievable only after the configure phase has completed.
    /// </summary>
    public StorageIpcCommandService(
        SqliteDatabaseOptions databaseOptions,
        Func<ISecretVault> vaultProvider,
        WindowsRuntimeSecretFileMaterializer secretFileMaterializer,
        Func<CodeLogicStorageSessionFactory> sessionFactoryProvider,
        TimeProvider? timeProvider = null)
        : this(
            new SqliteConnectionProfileRepository(databaseOptions, timeProvider),
            CreateCodeLogicSessionOpener(
                databaseOptions,
                vaultProvider,
                secretFileMaterializer,
                sessionFactoryProvider,
                timeProvider))
    {
    }

    public bool CanHandle(string messageType) => messageType is
        StorageIpcMessageTypes.ConnectionListRequest or
        StorageIpcMessageTypes.ConnectionTestRequest or
        StorageIpcMessageTypes.StorageListRequest;

    public ValueTask<AgentIpcCommandResponse> HandleAsync(
        IpcEnvelope request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.MessageType switch
        {
            StorageIpcMessageTypes.ConnectionListRequest =>
                ListConnectionsAsync(request, cancellationToken),
            StorageIpcMessageTypes.ConnectionTestRequest =>
                TestConnectionAsync(request, cancellationToken),
            StorageIpcMessageTypes.StorageListRequest =>
                ListStorageAsync(request, cancellationToken),
            _ => ValueTask.FromResult(AgentIpcCommandResponse.Error(
                "ipc.message.unsupported",
                "The requested IPC operation is not supported by this agent version."))
        };
    }

    private async ValueTask<AgentIpcCommandResponse> ListConnectionsAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<ConnectionListRequest>();
        var validation = ValidateRequest(request.ContractVersion, request.HasValidBounds);
        if (validation is not null)
        {
            return validation;
        }

        try
        {
            var profiles = await _profiles.SearchAsync(
                new ConnectionProfileSearch(
                    Text: NormalizeSearchText(request.SearchText),
                    Provider: request.Provider is { } provider ? MapProvider(provider) : null,
                    IncludeDisabled: request.IncludeDisabled,
                    IncludeDeleted: false,
                    Limit: request.Limit),
                cancellationToken).ConfigureAwait(false);
            if (profiles.Count > request.Limit || profiles.Count > StorageIpcLimits.MaximumConnectionResults)
            {
                return ConnectionListFailure(
                    request.ContractVersion,
                    "storage.ipc.response.invalid",
                    StorageIpcFailureCategory.Integrity,
                    "The saved connection list exceeded the negotiated response limit.");
            }

            var summaries = profiles.Select(MapConnection).ToArray();
            return AgentIpcCommandResponse.Create(
                StorageIpcMessageTypes.ConnectionListResponse,
                new ConnectionListResponse(request.ContractVersion, summaries));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ConnectionListFailure(
                request.ContractVersion,
                "storage.connections.unavailable",
                StorageIpcFailureCategory.Unavailable,
                "Saved connections are temporarily unavailable.",
                isTransient: true);
        }
    }

    private async ValueTask<AgentIpcCommandResponse> TestConnectionAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<ConnectionTestRequest>();
        var validation = ValidateRequest(request.ContractVersion, request.HasValidBounds);
        if (validation is not null)
        {
            return validation;
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var profile = await GetProfileAsync(request.ConnectionId, cancellationToken).ConfigureAwait(false);
            if (profile is null)
            {
                return ConnectionTestFailure(
                    request.ContractVersion,
                    request.ConnectionId,
                    started,
                    NotFoundFailure());
            }

            var opened = await _sessionOpener.OpenAsync(profile, cancellationToken).ConfigureAwait(false);
            if (opened.IsFailure)
            {
                return ConnectionTestFailure(
                    request.ContractVersion,
                    request.ConnectionId,
                    started,
                    SanitizeFailure(opened.Error));
            }

            await using var lease = opened.Value;
            var sessionFailure = ValidateSession(lease.Session, profile.Id);
            if (sessionFailure is not null)
            {
                return ConnectionTestFailure(
                    request.ContractVersion,
                    request.ConnectionId,
                    started,
                    sessionFailure);
            }

            var health = await lease.Session.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
            return health.IsSuccess
                ? AgentIpcCommandResponse.Create(
                    StorageIpcMessageTypes.ConnectionTestResponse,
                    new ConnectionTestResponse(
                        request.ContractVersion,
                        request.ConnectionId,
                        Succeeded: true,
                        ElapsedMilliseconds: ElapsedMilliseconds(started)))
                : ConnectionTestFailure(
                    request.ContractVersion,
                    request.ConnectionId,
                    started,
                    SanitizeFailure(health.Error));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ConnectionTestFailure(
                request.ContractVersion,
                request.ConnectionId,
                started,
                new StorageIpcFailure(
                    "storage.connection.unavailable",
                    StorageIpcFailureCategory.Unavailable,
                    "The connection could not be tested.",
                    IsTransient: true));
        }
    }

    private async ValueTask<AgentIpcCommandResponse> ListStorageAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<StorageListPageRequest>();
        var validation = ValidateRequest(request.ContractVersion, request.HasValidBounds);
        if (validation is not null)
        {
            return validation;
        }

        try
        {
            var profile = await GetProfileAsync(request.ConnectionId, cancellationToken).ConfigureAwait(false);
            if (profile is null)
            {
                return StorageListFailure(request, NotFoundFailure());
            }

            var opened = await _sessionOpener.OpenAsync(profile, cancellationToken).ConfigureAwait(false);
            if (opened.IsFailure)
            {
                return StorageListFailure(request, SanitizeFailure(opened.Error));
            }

            await using var lease = opened.Value;
            var session = lease.Session;
            var sessionFailure = ValidateSession(session, profile.Id);
            if (sessionFailure is not null)
            {
                return StorageListFailure(request, sessionFailure);
            }

            var address = StorageAddress.Create(
                profile.Id,
                session.RootIdentity,
                request.RelativePath);
            if (address.IsFailure)
            {
                return StorageListFailure(request, SanitizeFailure(address.Error));
            }

            var listed = await session.ListAsync(
                address.Value,
                new StorageListRequest(
                    request.Recursive,
                    request.PageSize,
                    request.ContinuationToken,
                    request.IncludeVersions),
                cancellationToken).ConfigureAwait(false);
            if (listed.IsFailure)
            {
                return StorageListFailure(request, SanitizeFailure(listed.Error));
            }

            var pageFailure = ValidatePage(
                listed.Value,
                session,
                address.Value,
                request.PageSize,
                request.Recursive);
            if (pageFailure is not null)
            {
                return StorageListFailure(request, pageFailure);
            }

            var includeStableIdentities = StorageIpcContract.SupportsStableItemIdentities(
                request.ContractVersion);
            var entries = listed.Value.Entries
                .Select(entry => MapEntry(entry, includeStableIdentities))
                .ToArray();
            return AgentIpcCommandResponse.Create(
                StorageIpcMessageTypes.StorageListResponse,
                new StorageListPageResponse(
                    request.ContractVersion,
                    request.ConnectionId,
                    address.Value.CanonicalRelativePath,
                    entries,
                    listed.Value.ContinuationToken,
                    RootIdentity: includeStableIdentities ? session.RootIdentity : null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return StorageListFailure(
                request,
                new StorageIpcFailure(
                    "storage.list.unavailable",
                    StorageIpcFailureCategory.Unavailable,
                    "The folder could not be listed.",
                    IsTransient: true));
        }
    }

    private async ValueTask<ConnectionProfile?> GetProfileAsync(
        Guid connectionId,
        CancellationToken cancellationToken) => await _profiles.GetAsync(
        new ConnectionProfileId(connectionId),
        includeDeleted: false,
        cancellationToken).ConfigureAwait(false);

    private static AgentIpcCommandResponse? ValidateRequest(int contractVersion, bool hasValidBounds)
    {
        if (!StorageIpcContract.IsSupported(contractVersion))
        {
            return AgentIpcCommandResponse.Error(
                "ipc.contract.unsupported",
                "The requested storage IPC contract version is not supported.");
        }

        return hasValidBounds
            ? null
            : AgentIpcCommandResponse.Error(
                "ipc.payload.invalid",
                "The request payload exceeded a permitted bound or contained an invalid value.");
    }

    private static string? NormalizeSearchText(string? searchText) =>
        string.IsNullOrWhiteSpace(searchText) ? null : searchText.Trim();

    private static ConnectionProviderKind MapProvider(StorageConnectionProvider provider) => provider switch
    {
        StorageConnectionProvider.Local => ConnectionProviderKind.Local,
        StorageConnectionProvider.S3 => ConnectionProviderKind.S3,
        StorageConnectionProvider.Ftp => ConnectionProviderKind.Ftp,
        StorageConnectionProvider.Ftps => ConnectionProviderKind.Ftps,
        StorageConnectionProvider.Sftp => ConnectionProviderKind.Sftp,
        StorageConnectionProvider.Ssh => ConnectionProviderKind.Ssh,
        _ => throw new InvalidDataException("The connection provider is invalid.")
    };

    private static StorageConnectionProvider MapProvider(ConnectionProviderKind provider) => provider switch
    {
        ConnectionProviderKind.Local => StorageConnectionProvider.Local,
        ConnectionProviderKind.S3 => StorageConnectionProvider.S3,
        ConnectionProviderKind.Ftp => StorageConnectionProvider.Ftp,
        ConnectionProviderKind.Ftps => StorageConnectionProvider.Ftps,
        ConnectionProviderKind.Sftp => StorageConnectionProvider.Sftp,
        ConnectionProviderKind.Ssh => StorageConnectionProvider.Ssh,
        _ => throw new InvalidDataException("The saved connection provider is invalid.")
    };

    private static ConnectionSummary MapConnection(ConnectionProfile profile) => new(
        profile.Id.Value,
        profile.Metadata.DisplayName,
        MapProvider(profile.Provider),
        profile.Metadata.FolderPath,
        profile.Metadata.Tags.ToArray(),
        profile.Metadata.IsFavorite,
        profile.IsEnabled,
        profile.Metadata.IconKey,
        profile.Metadata.AccentColor,
        profile.Version,
        profile.Type == StorageHub.Application.Connections.ConnectionProfileType.Client
            ? StorageHub.Contracts.Ipc.ConnectionProfileType.Client
            : StorageHub.Contracts.Ipc.ConnectionProfileType.Storage);

    private static StorageListItem MapEntry(StorageEntry entry, bool includeStableIdentities) => new(
        entry.Name,
        entry.Address.CanonicalRelativePath,
        entry.Kind switch
        {
            StorageEntryKind.File => StorageItemKind.File,
            StorageEntryKind.Directory => StorageItemKind.Directory,
            StorageEntryKind.Prefix => StorageItemKind.Prefix,
            StorageEntryKind.SymbolicLink => StorageItemKind.SymbolicLink,
            _ => StorageItemKind.Other
        },
        entry.Size,
        entry.LastModifiedUtc,
        entry.ContentType,
        entry.IsContainer,
        NativeItemId: includeStableIdentities ? entry.Address.NativeItemId : null,
        VersionId: includeStableIdentities ? entry.Address.VersionId : null,
        EntityTag: includeStableIdentities ? entry.Address.EntityTag ?? entry.ETag : null);

    private static StorageIpcFailure? ValidateSession(
        IStorageEndpointSession session,
        ConnectionProfileId expectedProfileId)
    {
        if (session is null ||
            session.ProfileId != expectedProfileId ||
            string.IsNullOrWhiteSpace(session.RootIdentity) ||
            session.RootIdentity.Any(char.IsControl) ||
            session.RootIdentity.Length > 8_192)
        {
            return new StorageIpcFailure(
                "storage.session.invalid",
                StorageIpcFailureCategory.Integrity,
                "The provider returned an invalid root-scoped session.",
                IsTransient: false);
        }

        return null;
    }

    private static StorageIpcFailure? ValidatePage(
        StoragePage page,
        IStorageEndpointSession session,
        StorageAddress listedAddress,
        int requestedPageSize,
        bool recursive)
    {
        if (page.Entries.Count > requestedPageSize ||
            page.Entries.Count > StorageIpcLimits.MaximumStoragePageSize ||
            !IsValidContinuationToken(page.ContinuationToken))
        {
            return InvalidProviderPage();
        }

        foreach (var entry in page.Entries)
        {
            if (entry.Address.ProfileId != session.ProfileId ||
                !string.Equals(entry.Address.RootIdentity, session.RootIdentity, StringComparison.Ordinal) ||
                entry.Name.Length > StorageIpcLimits.MaximumItemNameLength ||
                entry.Address.CanonicalRelativePath.Length > StorageIpcLimits.MaximumRelativePathLength ||
                entry.ContentType?.Length > StorageIpcLimits.MaximumContentTypeLength ||
                entry.ContentType?.Any(char.IsControl) == true ||
                !IsBoundedIdentity(entry.Address.NativeItemId) ||
                !IsBoundedIdentity(entry.Address.VersionId) ||
                !IsBoundedIdentity(entry.Address.EntityTag) ||
                !IsBoundedIdentity(entry.ETag) ||
                entry.Address.EntityTag is not null && entry.ETag is not null &&
                    !string.Equals(entry.Address.EntityTag, entry.ETag, StringComparison.Ordinal) ||
                !IsWithinListing(entry.Address.CanonicalRelativePath, listedAddress.CanonicalRelativePath, recursive))
            {
                return InvalidProviderPage();
            }
        }

        return null;
    }

    private static bool IsValidContinuationToken(string? token) => token is null ||
        token.Length <= StorageIpcLimits.MaximumContinuationTokenLength &&
        !token.Any(char.IsControl);

    private static bool IsBoundedIdentity(string? value) => value is null ||
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= StorageIpcLimits.MaximumOpaqueIdentityLength &&
        !value.Any(char.IsControl);

    private static bool IsWithinListing(string itemPath, string listedPath, bool recursive)
    {
        if (string.Equals(itemPath, listedPath, StringComparison.Ordinal))
        {
            return true;
        }

        var prefix = listedPath.Length == 0 ? string.Empty : listedPath + "/";
        if (!itemPath.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = itemPath[prefix.Length..];
        return remainder.Length > 0 && (recursive || !remainder.Contains('/', StringComparison.Ordinal));
    }

    private static StorageIpcFailure InvalidProviderPage() => new(
        "storage.ipc.provider_response_invalid",
        StorageIpcFailureCategory.Integrity,
        "The provider returned a page that could not be exposed safely.",
        IsTransient: false);

    private static StorageIpcFailure NotFoundFailure() => new(
        "storage.connection.not_found",
        StorageIpcFailureCategory.NotFound,
        "The saved connection was not found.",
        IsTransient: false);

    private static StorageIpcFailure SanitizeFailure(StorageFailure failure)
    {
        var category = failure.Kind switch
        {
            StorageFailureKind.Validation => StorageIpcFailureCategory.Validation,
            StorageFailureKind.NotFound => StorageIpcFailureCategory.NotFound,
            StorageFailureKind.Conflict => StorageIpcFailureCategory.Conflict,
            StorageFailureKind.Unsupported => StorageIpcFailureCategory.Unsupported,
            StorageFailureKind.Unauthorized => StorageIpcFailureCategory.Unauthorized,
            StorageFailureKind.Unavailable => StorageIpcFailureCategory.Unavailable,
            StorageFailureKind.Timeout => StorageIpcFailureCategory.Timeout,
            StorageFailureKind.Cancelled => StorageIpcFailureCategory.Cancelled,
            StorageFailureKind.Integrity => StorageIpcFailureCategory.Integrity,
            StorageFailureKind.Security => StorageIpcFailureCategory.Security,
            StorageFailureKind.Provider => StorageIpcFailureCategory.Provider,
            _ => StorageIpcFailureCategory.Unexpected
        };
        var code = IsSafeFailureCode(failure.Code) ? failure.Code : "storage.operation.failed";
        return new StorageIpcFailure(code, category, SafeFailureMessage(category), failure.IsTransient);
    }

    private static bool IsSafeFailureCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > StorageIpcLimits.MaximumFailureCodeLength)
        {
            return false;
        }

        return code.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
    }

    private static string SafeFailureMessage(StorageIpcFailureCategory category) => category switch
    {
        StorageIpcFailureCategory.Validation => "The storage request was invalid.",
        StorageIpcFailureCategory.NotFound => "The requested storage item was not found.",
        StorageIpcFailureCategory.Conflict => "The storage item changed or the connection is disabled.",
        StorageIpcFailureCategory.Unsupported => "The provider does not support this operation safely.",
        StorageIpcFailureCategory.Unauthorized => "The provider rejected the saved credentials.",
        StorageIpcFailureCategory.Unavailable => "The storage provider is temporarily unavailable.",
        StorageIpcFailureCategory.Timeout => "The storage provider did not respond in time.",
        StorageIpcFailureCategory.Cancelled => "The storage operation was cancelled.",
        StorageIpcFailureCategory.Integrity => "The provider response failed an integrity check.",
        StorageIpcFailureCategory.Security => "The connection requires a security or trust decision.",
        StorageIpcFailureCategory.Provider => "The storage provider could not complete the operation.",
        _ => "The storage operation could not be completed."
    };

    private static AgentIpcCommandResponse ConnectionListFailure(
        int contractVersion,
        string code,
        StorageIpcFailureCategory category,
        string message,
        bool isTransient = false) => AgentIpcCommandResponse.Create(
        StorageIpcMessageTypes.ConnectionListResponse,
        new ConnectionListResponse(
            contractVersion,
            [],
            new StorageIpcFailure(code, category, message, isTransient)));

    private static AgentIpcCommandResponse ConnectionTestFailure(
        int contractVersion,
        Guid connectionId,
        long started,
        StorageIpcFailure failure) => AgentIpcCommandResponse.Create(
        StorageIpcMessageTypes.ConnectionTestResponse,
        new ConnectionTestResponse(
            contractVersion,
            connectionId,
            Succeeded: false,
            ElapsedMilliseconds: ElapsedMilliseconds(started),
            failure));

    private static AgentIpcCommandResponse StorageListFailure(
        StorageListPageRequest request,
        StorageIpcFailure failure) => AgentIpcCommandResponse.Create(
        StorageIpcMessageTypes.StorageListResponse,
        new StorageListPageResponse(
            request.ContractVersion,
            request.ConnectionId,
            request.RelativePath ?? string.Empty,
            [],
            ContinuationToken: null,
            failure));

    private static long ElapsedMilliseconds(long started)
    {
        var milliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return (long)Math.Clamp(milliseconds, 0, long.MaxValue);
    }

    private static CodeLogicStorageIpcSessionOpener CreateCodeLogicSessionOpener(
        SqliteDatabaseOptions databaseOptions,
        Func<ISecretVault> vaultProvider,
        WindowsRuntimeSecretFileMaterializer secretFileMaterializer,
        Func<CodeLogicStorageSessionFactory> sessionFactoryProvider,
        TimeProvider? timeProvider)
    {
        ArgumentNullException.ThrowIfNull(databaseOptions);
        ArgumentNullException.ThrowIfNull(vaultProvider);
        ArgumentNullException.ThrowIfNull(secretFileMaterializer);
        ArgumentNullException.ThrowIfNull(sessionFactoryProvider);
        var trustStore = new SqliteTrustStore(new SingleWriterSqliteDatabase(databaseOptions));
        return new CodeLogicStorageIpcSessionOpener(
            () => new CodeLogicConnectionProfileConnector(
                sessionFactoryProvider(),
                vaultProvider(),
                trustStore,
                secretFileMaterializer,
                timeProvider));
    }

    private sealed class CodeLogicStorageIpcSessionOpener(
        Func<CodeLogicConnectionProfileConnector> connectorProvider) : IStorageIpcSessionOpener
    {
        private readonly Func<CodeLogicConnectionProfileConnector> _connectorProvider =
            connectorProvider ?? throw new ArgumentNullException(nameof(connectorProvider));

        public async ValueTask<StorageResult<IStorageIpcSessionLease>> OpenAsync(
            ConnectionProfile profile,
            CancellationToken cancellationToken = default)
        {
            var opened = await _connectorProvider().OpenAsync(profile, cancellationToken).ConfigureAwait(false);
            return opened.IsSuccess
                ? StorageResult<IStorageIpcSessionLease>.Success(new RuntimeSessionLease(opened.Value))
                : StorageResult<IStorageIpcSessionLease>.Fail(opened.Error);
        }
    }

    private sealed class RuntimeSessionLease(RuntimeStorageConnection connection) : IStorageIpcSessionLease
    {
        private readonly RuntimeStorageConnection _connection =
            connection ?? throw new ArgumentNullException(nameof(connection));

        public IStorageEndpointSession Session => _connection.Session;

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }
}
