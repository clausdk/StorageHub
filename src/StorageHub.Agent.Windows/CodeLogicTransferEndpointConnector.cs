using StorageHub.Agent.Transfers;
using StorageHub.Application.Connections;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Abstractions;
using StorageHub.Storage.CodeLogic;

namespace StorageHub.Agent.Windows;

/// <summary>
/// Resolves the authoritative saved profile immediately before each attempt and owns the
/// runtime-only CL.Storage registration until that attempt disposes its connection.
/// </summary>
internal sealed class CodeLogicTransferEndpointConnector(
    IConnectionProfileRepository profiles,
    Func<CodeLogicConnectionProfileConnector> connectorProvider) : ITransferEndpointConnector
{
    private readonly IConnectionProfileRepository _profiles =
        profiles ?? throw new ArgumentNullException(nameof(profiles));
    private readonly Func<CodeLogicConnectionProfileConnector> _connectorProvider =
        connectorProvider ?? throw new ArgumentNullException(nameof(connectorProvider));

    public async ValueTask<StorageResult<ITransferEndpointConnection>> OpenAsync(
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
            var opened = await _connectorProvider().OpenAsync(profile, cancellationToken).ConfigureAwait(false);
            return opened.IsSuccess
                ? StorageResult<ITransferEndpointConnection>.Success(
                    new RuntimeTransferEndpointConnection(opened.Value))
                : StorageResult<ITransferEndpointConnection>.Fail(opened.Error);
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

    public ValueTask<StorageResult<ITransferEndpointConnection>> OpenAsync(
        StorageAddress address,
        CancellationToken cancellationToken = default) =>
        LocalFilesystemTransferEndpoint.IsLocalSource(address)
            ? ValueTask.FromResult(LocalFilesystemTransferEndpoint.Open(address))
            : LocalStagingTransferEndpoint.IsLocalDestination(address)
                ? ValueTask.FromResult(LocalStagingTransferEndpoint.Open(address))
            : OpenAsync(address.ProfileId, cancellationToken);

    private static StorageResult<ITransferEndpointConnection> Fail(
        string code,
        StorageFailureKind kind,
        string message,
        bool isTransient = false) => StorageResult<ITransferEndpointConnection>.Fail(
        new StorageFailure(code, kind, message, isTransient));

    private sealed class RuntimeTransferEndpointConnection(RuntimeStorageConnection connection)
        : ITransferEndpointConnection
    {
        private readonly RuntimeStorageConnection _connection =
            connection ?? throw new ArgumentNullException(nameof(connection));

        public IStorageEndpointSession Session => _connection.Session;

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }
}
