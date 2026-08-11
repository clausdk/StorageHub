using StorageHub.Contracts.Results;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Abstractions;

namespace StorageHub.Agent.Transfers;

/// <summary>Owns one root-scoped runtime connection for a transfer attempt.</summary>
public interface ITransferEndpointConnection : IAsyncDisposable
{
    IStorageEndpointSession Session { get; }
}

/// <summary>Resolves a saved profile and opens a non-persisted runtime provider session.</summary>
public interface ITransferEndpointConnector
{
    ValueTask<StorageResult<ITransferEndpointConnection>> OpenAsync(
        ConnectionProfileId profileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens an endpoint from the complete durable address.  Implementations which only support
    /// saved connections can retain the profile-only implementation; source-only endpoints use
    /// the root identity as their immutable configuration.
    /// </summary>
    ValueTask<StorageResult<ITransferEndpointConnection>> OpenAsync(
        StorageAddress address,
        CancellationToken cancellationToken = default) => OpenAsync(address.ProfileId, cancellationToken);
}
