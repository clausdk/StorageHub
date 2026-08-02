using StorageHub.Contracts.Results;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Models;

namespace StorageHub.Storage.Abstractions;

/// <summary>
/// Optional capability-gated object APIs. Consumers must test for this interface and the matching
/// effective capability before invoking an operation.
/// </summary>
public interface IStorageAdvancedEndpointSession
{
    ValueTask<StorageResult<StorageObjectVersionPage>> ListObjectVersionsAsync(
        StorageAddress address,
        StorageVersionListRequest? request = null,
        CancellationToken cancellationToken = default);

    ValueTask<StorageResult> DeleteObjectVersionAsync(
        StorageDeleteVersionRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<StorageResult<StorageMetadata>> GetMetadataAsync(
        StorageAddress address,
        CancellationToken cancellationToken = default);

    ValueTask<StorageResult<StorageEntry>> SetMetadataAsync(
        StorageSetMetadataRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<StorageResult<StorageTags>> GetTagsAsync(
        StorageAddress address,
        CancellationToken cancellationToken = default);

    ValueTask<StorageResult<StorageEntry>> SetTagsAsync(
        StorageSetTagsRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<StorageResult<StorageSignedUrl>> CreateSignedUrlAsync(
        StorageSignedUrlRequest request,
        CancellationToken cancellationToken = default);
}
