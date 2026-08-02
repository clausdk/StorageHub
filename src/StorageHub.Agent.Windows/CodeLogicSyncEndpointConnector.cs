using StorageHub.Application.Connections;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Identifiers;
using StorageHub.Storage.Abstractions;
using StorageHub.Storage.CodeLogic;
using StorageHub.Sync;

namespace StorageHub.Agent.Windows;

/// <summary>
/// Opens an authoritative saved profile for a sync scan and binds the runtime-only CL.Storage
/// registration to the returned session lifetime.
/// </summary>
internal sealed class CodeLogicSyncEndpointConnector(
    IConnectionProfileRepository profiles,
    Func<CodeLogicConnectionProfileConnector> connectorProvider) : ISyncEndpointConnector
{
    private readonly IConnectionProfileRepository _profiles =
        profiles ?? throw new ArgumentNullException(nameof(profiles));
    private readonly Func<CodeLogicConnectionProfileConnector> _connectorProvider =
        connectorProvider ?? throw new ArgumentNullException(nameof(connectorProvider));

    public async ValueTask<StorageResult<ISyncEndpointConnection>> OpenAsync(
        ConnectionProfileId profileId,
        CancellationToken cancellationToken = default)
    {
        ConnectionProfile? profile;
        try
        {
            profile = await _profiles.GetAsync(profileId, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Fail(
                "storage.profile.store_unavailable",
                StorageFailureKind.Unavailable,
                "The saved connection store is unavailable.",
                isTransient: true);
        }

        if (profile is null)
        {
            return Fail(
                "storage.profile.not_found",
                StorageFailureKind.NotFound,
                "The saved connection no longer exists.");
        }

        if (!profile.IsEnabled)
        {
            return Fail(
                "storage.profile.disabled",
                StorageFailureKind.Unauthorized,
                "The saved connection is disabled.");
        }

        try
        {
            var opened = await _connectorProvider().OpenAsync(profile, cancellationToken)
                .ConfigureAwait(false);
            return opened.IsSuccess
                ? StorageResult<ISyncEndpointConnection>.Success(
                    new RuntimeSyncEndpointConnection(opened.Value))
                : StorageResult<ISyncEndpointConnection>.Fail(opened.Error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Fail(
                "storage.connection.unexpected",
                StorageFailureKind.Unexpected,
                "The provider connection could not be opened.");
        }
    }

    private static StorageResult<ISyncEndpointConnection> Fail(
        string code,
        StorageFailureKind kind,
        string message,
        bool isTransient = false) => StorageResult<ISyncEndpointConnection>.Fail(
            new StorageFailure(code, kind, message, isTransient));

    private sealed class RuntimeSyncEndpointConnection(RuntimeStorageConnection connection)
        : ISyncEndpointConnection
    {
        private readonly RuntimeStorageConnection _connection =
            connection ?? throw new ArgumentNullException(nameof(connection));

        public IStorageEndpointSession Session => _connection.Session;

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }
}
