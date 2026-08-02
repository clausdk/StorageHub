using System.Diagnostics.CodeAnalysis;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace StorageHub.Storage.CodeLogic.Tests;

internal class FakeStorageService : IStorageService
{
    public string ConnectionId { get; init; } = "fake";

    public StorageProvider Provider { get; init; } = StorageProvider.S3;

    public string Root { get; init; } = "fake-root";

    public StorageCapabilities Capabilities { get; init; } = new(
        Directories: true,
        NativeCopy: true,
        NativeMove: true,
        RangeReads: true,
        Metadata: true,
        ServerPagination: true);

    public int GetInfoCallCount { get; private set; }

    public int ListCallCount { get; private set; }

    public int UploadCallCount { get; private set; }

    public int DeleteCallCount { get; private set; }

    public string? LastPath { get; private set; }

    public StorageListOptions? LastListOptions { get; private set; }

    public StorageUploadOptions? LastUploadOptions { get; private set; }

    public StorageDownloadOptions? LastDownloadOptions { get; private set; }

    public StorageDeleteOptions? LastDeleteOptions { get; private set; }

    public Func<string, CancellationToken, Task<Result<StorageItem>>>? GetInfoHandler { get; init; }

    public Func<string, StorageListOptions?, CancellationToken, Task<Result<StoragePage>>>? ListHandler { get; init; }

    public Func<string, Stream, StorageUploadOptions?, CancellationToken, Task<Result<StorageItem>>>? UploadHandler { get; init; }

    public Func<string, StorageDownloadOptions?, CancellationToken, Task<Result<Stream>>>? DownloadHandler { get; init; }

    public Func<string, StorageDeleteOptions?, CancellationToken, Task<Result>>? DeleteHandler { get; init; }

    public Task<Result<StorageItem>> GetInfoAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        GetInfoCallCount++;
        LastPath = path;
        return GetInfoHandler?.Invoke(path, cancellationToken) ??
            Task.FromResult(Result<StorageItem>.Failure(StorageErrors.NotFound("missing")));
    }

    public Task<Result<bool>> ExistsAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<bool>.Success(false));

    public Task<Result<StoragePage>> ListAsync(
        string path,
        StorageListOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ListCallCount++;
        LastPath = path;
        LastListOptions = options;
        return ListHandler?.Invoke(path, options, cancellationToken) ??
            Task.FromResult(Result<StoragePage>.Success(new StoragePage([])));
    }

    public Task<Result> CreateDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());

    public Task<Result<StorageItem>> UploadAsync(
        string path,
        Stream source,
        StorageUploadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        UploadCallCount++;
        LastPath = path;
        LastUploadOptions = options;
        return UploadHandler?.Invoke(path, source, options, cancellationToken) ??
            Task.FromResult(Result<StorageItem>.Failure(StorageErrors.Unsupported("Upload was not configured.")));
    }

    public Task<Result<StorageItem>> UploadBytesAsync(
        string path,
        byte[] content,
        StorageUploadOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<StorageItem>.Failure(StorageErrors.Unsupported("UploadBytes is unused by the adapter.")));

    public Task<Result<Stream>> DownloadAsync(
        string path,
        StorageDownloadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        LastPath = path;
        LastDownloadOptions = options;
        return DownloadHandler?.Invoke(path, options, cancellationToken) ??
            Task.FromResult(Result<Stream>.Failure(StorageErrors.Unsupported("Download was not configured.")));
    }

    public Task<Result<byte[]>> DownloadBytesAsync(
        string path,
        StorageDownloadOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<byte[]>.Failure(StorageErrors.Unsupported("DownloadBytes is unused by the adapter.")));

    public Task<Result> DeleteAsync(
        string path,
        StorageDeleteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        DeleteCallCount++;
        LastPath = path;
        LastDeleteOptions = options;
        return DeleteHandler?.Invoke(path, options, cancellationToken) ??
            Task.FromResult(Result.Success());
    }

    public Task<Result> CopyAsync(
        string sourcePath,
        string destinationPath,
        StorageTransferOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());

    public Task<Result> MoveAsync(
        string sourcePath,
        string destinationPath,
        StorageTransferOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());
}

internal sealed class FakeStorageBackend : FakeStorageService, IStorageBackend
{
    public int HealthCallCount { get; private set; }

    public Func<CancellationToken, Task<Result>>? HealthHandler { get; init; }

    public Task<Result> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        HealthCallCount++;
        return HealthHandler?.Invoke(cancellationToken) ?? Task.FromResult(Result.Success());
    }

    public bool TryGetNativeClient<TClient>([NotNullWhen(true)] out TClient? client)
        where TClient : class
    {
        client = null;
        return false;
    }

    public Task<Result<NativeConnectionLease<TClient>>> OpenNativeConnectionAsync<TClient>(
        CancellationToken cancellationToken = default)
        where TClient : class => Task.FromResult(Result<NativeConnectionLease<TClient>>.Failure(
            StorageErrors.Unsupported("Native access is not configured for this test backend.")));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeAdvancedStorageService : FakeStorageService,
    IStorageMetadataService,
    IStorageTagService,
    IStorageSignedUrlService,
    IStorageVersionService
{
    public int GetMetadataCallCount { get; private set; }
    public int SetMetadataCallCount { get; private set; }
    public int GetTagsCallCount { get; private set; }
    public int SetTagsCallCount { get; private set; }
    public int CreateSignedUrlCallCount { get; private set; }
    public int ListVersionsCallCount { get; private set; }
    public int DeleteVersionCallCount { get; private set; }
    public string? LastAdvancedPath { get; private set; }
    public string? LastDeletedVersionId { get; private set; }
    public IReadOnlyDictionary<string, string>? LastMetadata { get; private set; }
    public IReadOnlyDictionary<string, string>? LastTags { get; private set; }
    public StorageMetadataUpdateOptions? LastMetadataOptions { get; private set; }
    public StorageTagUpdateOptions? LastTagOptions { get; private set; }
    public StorageSignedUrlOptions? LastSignedUrlOptions { get; private set; }
    public StorageVersionListOptions? LastVersionListOptions { get; private set; }

    public Func<string, CancellationToken, Task<Result<IReadOnlyDictionary<string, string>>>>?
        GetMetadataHandler
    { get; init; }

    public Func<string, IReadOnlyDictionary<string, string>, StorageMetadataUpdateOptions?,
        CancellationToken, Task<Result<StorageItem>>>? SetMetadataHandler
    { get; init; }

    public Func<string, CancellationToken, Task<Result<IReadOnlyDictionary<string, string>>>>?
        GetTagsHandler
    { get; init; }

    public Func<string, IReadOnlyDictionary<string, string>, StorageTagUpdateOptions?,
        CancellationToken, Task<Result<StorageItem>>>? SetTagsHandler
    { get; init; }

    public Func<string, StorageSignedUrlOptions?, CancellationToken, Task<Result<StorageSignedUrl>>>?
        CreateSignedUrlHandler
    { get; init; }

    public Func<string, StorageVersionListOptions?, CancellationToken, Task<Result<StorageVersionPage>>>?
        ListVersionsHandler
    { get; init; }

    public Func<string, string, CancellationToken, Task<Result>>? DeleteVersionHandler { get; init; }

    public Task<Result<IReadOnlyDictionary<string, string>>> GetMetadataAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        GetMetadataCallCount++;
        LastAdvancedPath = path;
        return GetMetadataHandler?.Invoke(path, cancellationToken) ??
            Task.FromResult(Result<IReadOnlyDictionary<string, string>>.Success(
                new Dictionary<string, string>()));
    }

    public Task<Result<StorageItem>> SetMetadataAsync(
        string path,
        IReadOnlyDictionary<string, string> metadata,
        StorageMetadataUpdateOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        SetMetadataCallCount++;
        LastAdvancedPath = path;
        LastMetadata = new Dictionary<string, string>(metadata, StringComparer.Ordinal);
        LastMetadataOptions = options;
        return SetMetadataHandler?.Invoke(path, metadata, options, cancellationToken) ??
            Task.FromResult(Result<StorageItem>.Failure(
                StorageErrors.Unsupported("Metadata update was not configured.")));
    }

    public Task<Result<IReadOnlyDictionary<string, string>>> GetTagsAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        GetTagsCallCount++;
        LastAdvancedPath = path;
        return GetTagsHandler?.Invoke(path, cancellationToken) ??
            Task.FromResult(Result<IReadOnlyDictionary<string, string>>.Success(
                new Dictionary<string, string>()));
    }

    public Task<Result<StorageItem>> SetTagsAsync(
        string path,
        IReadOnlyDictionary<string, string> tags,
        StorageTagUpdateOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        SetTagsCallCount++;
        LastAdvancedPath = path;
        LastTags = new Dictionary<string, string>(tags, StringComparer.Ordinal);
        LastTagOptions = options;
        return SetTagsHandler?.Invoke(path, tags, options, cancellationToken) ??
            Task.FromResult(Result<StorageItem>.Failure(
                StorageErrors.Unsupported("Tag update was not configured.")));
    }

    public Task<Result<StorageSignedUrl>> CreateSignedUrlAsync(
        string path,
        StorageSignedUrlOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        CreateSignedUrlCallCount++;
        LastAdvancedPath = path;
        LastSignedUrlOptions = options;
        return CreateSignedUrlHandler?.Invoke(path, options, cancellationToken) ??
            Task.FromResult(Result<StorageSignedUrl>.Failure(
                StorageErrors.Unsupported("Signed URL creation was not configured.")));
    }

    public Task<Result<StorageVersionPage>> ListVersionsAsync(
        string path,
        StorageVersionListOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ListVersionsCallCount++;
        LastAdvancedPath = path;
        LastVersionListOptions = options;
        return ListVersionsHandler?.Invoke(path, options, cancellationToken) ??
            Task.FromResult(Result<StorageVersionPage>.Success(new StorageVersionPage([], null)));
    }

    public Task<Result> DeleteVersionAsync(
        string path,
        string versionId,
        CancellationToken cancellationToken = default)
    {
        DeleteVersionCallCount++;
        LastAdvancedPath = path;
        LastDeletedVersionId = versionId;
        return DeleteVersionHandler?.Invoke(path, versionId, cancellationToken) ??
            Task.FromResult(Result.Success());
    }
}
