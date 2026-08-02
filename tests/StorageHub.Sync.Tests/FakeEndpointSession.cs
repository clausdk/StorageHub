using StorageHub.Contracts.Results;
using StorageHub.Domain.Capabilities;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Abstractions;
using StorageHub.Storage.Models;

namespace StorageHub.Sync.Tests;

internal sealed class FakeEndpointSession : IStorageEndpointSession, IStoragePortableChecksumSession
{
    private readonly IReadOnlyList<StorageEntry> _entries;
    private readonly bool _repeatContinuationToken;
    private readonly bool _returnEntriesOutsideRequestedDirectory;

    public FakeEndpointSession(
        ConnectionProfileId profileId,
        string rootIdentity,
        IEnumerable<StorageEntry> entries,
        StorageCaseSensitivity caseSensitivity = StorageCaseSensitivity.Sensitive,
        bool repeatContinuationToken = false,
        bool returnEntriesOutsideRequestedDirectory = false,
        Func<PortableChecksumRequest, CancellationToken, ValueTask<StorageResult<PortableChecksumResult>>>?
            checksumHandler = null)
    {
        ProfileId = profileId;
        RootIdentity = rootIdentity;
        _entries = entries.ToArray();
        _repeatContinuationToken = repeatContinuationToken;
        _returnEntriesOutsideRequestedDirectory = returnEntriesOutsideRequestedDirectory;
        ChecksumHandler = checksumHandler;
        Capabilities = new EffectiveStorageCapabilities(
            [
                new(StorageFeature.List, FeatureSupport.Native()),
                new(StorageFeature.PaginatedList, FeatureSupport.Native()),
                new(StorageFeature.ReadStream, FeatureSupport.Native()),
                new(StorageFeature.WriteStream, FeatureSupport.Native()),
                new(StorageFeature.CreateDirectory, FeatureSupport.Native()),
                new(StorageFeature.Delete, FeatureSupport.Native())
            ],
            caseSensitivity);
    }

    public ConnectionProfileId ProfileId { get; }

    public string RootIdentity { get; }

    public EffectiveStorageCapabilities Capabilities { get; }

    public Func<PortableChecksumRequest, CancellationToken, ValueTask<StorageResult<PortableChecksumResult>>>?
        ChecksumHandler
    { get; }

    public ValueTask<StorageResult> CheckHealthAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(StorageResult.Success());

    public ValueTask<StorageResult<StorageEntry>> GetEntryAsync(
        StorageAddress address,
        CancellationToken cancellationToken = default)
    {
        var entry = _entries.SingleOrDefault(candidate => candidate.Address == address);
        return ValueTask.FromResult(entry is null
            ? StorageResult<StorageEntry>.Fail(NotFound())
            : StorageResult<StorageEntry>.Success(entry));
    }

    public ValueTask<StorageResult<StoragePage>> ListAsync(
        StorageAddress address,
        StorageListRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        request ??= new StorageListRequest();
        var children = _entries
            .Where(entry => _returnEntriesOutsideRequestedDirectory ||
                            entry.Address.Parent.CanonicalRelativePath == address.CanonicalRelativePath)
            .OrderBy(entry => entry.Address.CanonicalRelativePath, StringComparer.Ordinal)
            .ToArray();
        var offset = request.ContinuationToken is null
            ? 0
            : int.Parse(request.ContinuationToken, System.Globalization.CultureInfo.InvariantCulture);
        var page = children.Skip(offset).Take(request.PageSize).ToArray();
        string? next = offset + page.Length < children.Length
            ? (offset + page.Length).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
        if (_repeatContinuationToken && next is not null)
        {
            next = request.ContinuationToken ?? next;
        }

        return ValueTask.FromResult(StorageResult<StoragePage>.Success(new StoragePage(page, next)));
    }

    public ValueTask<StorageResult<Stream>> OpenReadAsync(
        StorageReadRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(StorageResult<Stream>.Fail(Unsupported()));

    public ValueTask<StorageResult<IStorageWriteHandle>> OpenWriteAsync(
        StorageWriteRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(StorageResult<IStorageWriteHandle>.Fail(Unsupported()));

    public ValueTask<StorageResult<StorageEntry>> CreateDirectoryAsync(
        StorageAddress address,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(StorageResult<StorageEntry>.Fail(Unsupported()));

    public ValueTask<StorageResult> DeleteAsync(
        StorageDeleteRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(StorageResult.Fail(Unsupported()));

    public ValueTask<StorageResult<StorageEntry>> CopyAsync(
        StorageCopyRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(StorageResult<StorageEntry>.Fail(Unsupported()));

    public ValueTask<StorageResult<StorageEntry>> MoveAsync(
        StorageMoveRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(StorageResult<StorageEntry>.Fail(Unsupported()));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public ValueTask<StorageResult<PortableChecksumResult>> ComputePortableChecksumAsync(
        PortableChecksumRequest request,
        CancellationToken cancellationToken = default) => ChecksumHandler?.Invoke(request, cancellationToken) ??
        ValueTask.FromResult(StorageResult<PortableChecksumResult>.Fail(Unsupported()));

    private static StorageFailure Unsupported() => new(
        "test.unsupported",
        StorageFailureKind.Unsupported,
        "The fake session does not implement this operation.");

    private static StorageFailure NotFound() => new(
        "test.not_found",
        StorageFailureKind.NotFound,
        "The requested fake entry was not found.");
}

internal static class SyncTestEntries
{
    public static StorageAddress Address(
        ConnectionProfileId profileId,
        string rootIdentity,
        string path,
        string? versionId = null,
        string? entityTag = null) =>
        StorageAddress.Create(
            profileId,
            rootIdentity,
            path,
            versionId: versionId,
            entityTag: entityTag).Value;

    public static StorageEntry File(
        ConnectionProfileId profileId,
        string rootIdentity,
        string path,
        long length,
        string? checksum = null,
        string? versionId = null,
        string? entityTag = null) =>
        StorageEntry.Create(
            Address(profileId, rootIdentity, path, versionId, entityTag),
            StorageEntryKind.File,
            length,
            eTag: entityTag,
            checksum: checksum).Value;

    public static StorageEntry Directory(
        ConnectionProfileId profileId,
        string rootIdentity,
        string path) =>
        StorageEntry.Create(
            Address(profileId, rootIdentity, path),
            StorageEntryKind.Directory).Value;
}
