using StorageHub.Contracts.Results;
using StorageHub.Domain.Capabilities;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Models;

namespace StorageHub.Storage.Abstractions;

/// <summary>A root-scoped, provider-neutral live storage session.</summary>
public interface IStorageEndpointSession : IAsyncDisposable
{
    ConnectionProfileId ProfileId { get; }

    /// <summary>The immutable root identity captured when this session was opened.</summary>
    string RootIdentity { get; }

    EffectiveStorageCapabilities Capabilities { get; }

    ValueTask<StorageResult> CheckHealthAsync(CancellationToken cancellationToken = default);

    ValueTask<StorageResult<StorageEntry>> GetEntryAsync(
        StorageAddress address,
        CancellationToken cancellationToken = default);

    ValueTask<StorageResult<StoragePage>> ListAsync(
        StorageAddress address,
        StorageListRequest? request = null,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a caller-owned stream that must be disposed before the session is disposed.</summary>
    ValueTask<StorageResult<Stream>> OpenReadAsync(
        StorageReadRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<StorageResult<IStorageWriteHandle>> OpenWriteAsync(
        StorageWriteRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<StorageResult<StorageEntry>> CreateDirectoryAsync(
        StorageAddress address,
        CancellationToken cancellationToken = default);

    ValueTask<StorageResult> DeleteAsync(
        StorageDeleteRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Copies within this session only. Cross-session transfer belongs to the transfer engine.</summary>
    ValueTask<StorageResult<StorageEntry>> CopyAsync(
        StorageCopyRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Moves within this session only. Cross-session move is copy, verify, then delete.</summary>
    ValueTask<StorageResult<StorageEntry>> MoveAsync(
        StorageMoveRequest request,
        CancellationToken cancellationToken = default);
}
