using System.Security.Cryptography;
using System.Text;
using StorageHub.Agent.Transfers;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Capabilities;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Abstractions;
using StorageHub.Storage.Models;

namespace StorageHub.Agent.Windows;

/// <summary>A write-only transfer destination rooted in an agent-created Explorer export folder.</summary>
internal static class LocalStagingTransferEndpoint
{
    private const string StagingPrefix = "localstage:v1:";
    private const string DestinationPrefix = "localdest:v1:";
    private static readonly Guid Namespace = new("BF65DA41-21F9-48CE-AEAA-651185B97D62");

    public static bool IsStagingDestination(StorageAddress address) =>
        address.RootIdentity.StartsWith(StagingPrefix, StringComparison.Ordinal);

    public static bool IsLocalDestination(StorageAddress address) =>
        IsStagingDestination(address) || address.RootIdentity.StartsWith(DestinationPrefix, StringComparison.Ordinal);

    public static StorageResult<StorageAddress> CreateAddress(string stagingRoot, string relativePath)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingRoot));
            if (!IsApprovedRoot(root) || !Directory.Exists(root))
                return Fail<StorageAddress>("local-stage.root", "The Explorer staging root is not approved.");
            var identity = StagingPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(root));
            return StorageAddress.Create(
                new ConnectionProfileId(CreateDeterministicGuid(root)), identity, relativePath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Fail<StorageAddress>("local-stage.invalid", "The Explorer staging destination is invalid.");
        }
    }

    public static StorageResult<StorageAddress> CreateDestinationAddress(string destinationRoot, string relativePath)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot));
            if (!Directory.Exists(root))
                return Fail<StorageAddress>("local-destination.root", "The Explorer destination folder is unavailable.");
            var identity = DestinationPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(root));
            return StorageAddress.Create(new ConnectionProfileId(CreateDeterministicGuid(root)), identity, relativePath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Fail<StorageAddress>("local-destination.invalid", "The Explorer destination is invalid.");
        }
    }

    public static StorageResult<ITransferEndpointConnection> Open(StorageAddress address)
    {
        if (!TryDecodeRoot(address, out var root, out var staging) ||
            (staging && !IsApprovedRoot(root)) || !Directory.Exists(root))
            return Fail<ITransferEndpointConnection>("local-stage.invalid", "The Explorer staging destination is unavailable.");
        return StorageResult<ITransferEndpointConnection>.Success(
            new Connection(new Session(address.ProfileId, root, address.RootIdentity)));
    }

    private static bool TryDecodeRoot(StorageAddress address, out string root, out bool staging)
    {
        root = string.Empty;
        staging = IsStagingDestination(address);
        var prefix = staging ? StagingPrefix : DestinationPrefix;
        if (!IsLocalDestination(address)) return false;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                Encoding.UTF8.GetString(Convert.FromBase64String(address.RootIdentity[prefix.Length..]))));
            return address.ProfileId == new ConnectionProfileId(CreateDeterministicGuid(root));
        }
        catch (Exception) { return false; }
    }

    private static bool IsApprovedRoot(string root)
    {
        var exports = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StorageHub", "ShellExports")));
        return string.Equals(Path.GetDirectoryName(root), exports, StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(Path.GetFileName(root));
    }

    private static Guid CreateDeterministicGuid(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Namespace + "|" + value.ToUpperInvariant()));
        return new Guid(hash[..16]);
    }

    private static StorageResult<T> Fail<T>(string code, string message, bool transient = false) =>
        StorageResult<T>.Fail(new StorageFailure(code, StorageFailureKind.Validation, message, transient));

    private sealed class Connection(Session session) : ITransferEndpointConnection
    {
        public IStorageEndpointSession Session { get; } = session;
        public ValueTask DisposeAsync() => session.DisposeAsync();
    }

    private sealed class Session(ConnectionProfileId profileId, string root, string rootIdentity) : IStorageEndpointSession
    {
        public ConnectionProfileId ProfileId { get; } = profileId;
        public string RootIdentity { get; } = rootIdentity;
        public EffectiveStorageCapabilities Capabilities { get; } = new([
            new KeyValuePair<StorageFeature, FeatureSupport>(StorageFeature.WriteStream, FeatureSupport.Native()),
            new KeyValuePair<StorageFeature, FeatureSupport>(StorageFeature.ConditionalCreate, FeatureSupport.Native())]);

        public ValueTask<StorageResult> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Directory.Exists(root)
                ? StorageResult.Success()
                : StorageResult.Fail(new StorageFailure("local-stage.unavailable", StorageFailureKind.NotFound, "The Explorer staging root no longer exists.")));

        public ValueTask<StorageResult<StorageEntry>> GetEntryAsync(StorageAddress address, CancellationToken cancellationToken = default)
        {
            var resolved = Resolve(address);
            if (resolved.IsFailure) return ValueTask.FromResult(StorageResult<StorageEntry>.Fail(resolved.Error));
            var file = new FileInfo(resolved.Value);
            if (!file.Exists)
                return ValueTask.FromResult(StorageResult<StorageEntry>.Fail(new StorageFailure("local-stage.not-found", StorageFailureKind.NotFound, "The staged file does not exist.")));
            return ValueTask.FromResult(StorageEntry.Create(address, StorageEntryKind.File, file.Length, file.LastWriteTimeUtc));
        }

        public ValueTask<StorageResult<IStorageWriteHandle>> OpenWriteAsync(StorageWriteRequest request, CancellationToken cancellationToken = default)
        {
            var validation = request.Validate();
            if (validation.IsFailure) return ValueTask.FromResult(StorageResult<IStorageWriteHandle>.Fail(validation.Error));
            if (request.Mode != StorageWriteMode.CreateNew)
                return ValueTask.FromResult(Fail<IStorageWriteHandle>("local-stage.create-only", "Explorer staging files must be created once."));
            var resolved = Resolve(request.Destination);
            if (resolved.IsFailure) return ValueTask.FromResult(StorageResult<IStorageWriteHandle>.Fail(resolved.Error));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(resolved.Value)!);
                IStorageWriteHandle handle = new WriteHandle(request.Destination, resolved.Value, request.ExpectedLength);
                return ValueTask.FromResult(StorageResult<IStorageWriteHandle>.Success(handle));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return ValueTask.FromResult(Fail<IStorageWriteHandle>("local-stage.open", "The staged file could not be created.", true));
            }
        }

        public ValueTask<StorageResult<StoragePage>> ListAsync(StorageAddress address, StorageListRequest? request = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Fail<StoragePage>("local-stage.write-only", "Explorer staging destinations cannot be listed by the transfer worker."));
        public ValueTask<StorageResult<Stream>> OpenReadAsync(StorageReadRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Fail<Stream>("local-stage.write-only", "Explorer staging destinations are write-only."));
        public ValueTask<StorageResult<StorageEntry>> CreateDirectoryAsync(StorageAddress address, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Fail<StorageEntry>("local-stage.unsupported", "Directories are created by the export planner."));
        public ValueTask<StorageResult> DeleteAsync(StorageDeleteRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageResult.Fail(new StorageFailure("local-stage.unsupported", StorageFailureKind.Unsupported, "Deletion is unavailable.")));
        public ValueTask<StorageResult<StorageEntry>> CopyAsync(StorageCopyRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Fail<StorageEntry>("local-stage.unsupported", "Server-side copy is unavailable."));
        public ValueTask<StorageResult<StorageEntry>> MoveAsync(StorageMoveRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Fail<StorageEntry>("local-stage.unsupported", "Move is unavailable."));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private StorageResult<string> Resolve(StorageAddress address)
        {
            if (address.ProfileId != ProfileId || !string.Equals(address.RootIdentity, RootIdentity, StringComparison.Ordinal))
                return Fail<string>("local-stage.address", "The staged address is outside its approved root.");
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(root, address.CanonicalRelativePath.Replace('/', Path.DirectorySeparatorChar)));
                var prefix = root + Path.DirectorySeparatorChar;
                if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return Fail<string>("local-stage.escape", "The staged path escapes its approved root.");
                return StorageResult<string>.Success(candidate);
            }
            catch (Exception error) when (error is IOException or ArgumentException or NotSupportedException)
            {
                return Fail<string>("local-stage.address", "The staged path is invalid.");
            }
        }
    }

    private sealed class WriteHandle : IStorageWriteHandle
    {
        private readonly string _path;
        private readonly long? _expectedLength;
        private FileStream? _stream;

        public WriteHandle(StorageAddress destination, string path, long? expectedLength)
        {
            Destination = destination;
            _path = path;
            _expectedLength = expectedLength;
            _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        public StorageAddress Destination { get; }
        public Stream Content => _stream ?? throw new ObjectDisposedException(nameof(WriteHandle));
        public long AcceptedOffset => 0;
        public string? ResumeToken => null;
        public StorageWriteHandleState State { get; private set; } = StorageWriteHandleState.Open;

        public async ValueTask<StorageResult<StorageEntry>> CommitAsync(CancellationToken cancellationToken = default)
        {
            if (State != StorageWriteHandleState.Open)
                return Fail<StorageEntry>("local-stage.state", "The staged write is no longer open.");
            State = StorageWriteHandleState.Committing;
            try
            {
                await _stream!.FlushAsync(cancellationToken).ConfigureAwait(false);
                await _stream.DisposeAsync().ConfigureAwait(false);
                _stream = null;
                var file = new FileInfo(_path);
                if (_expectedLength is { } length && file.Length != length)
                    throw new IOException("The staged file length did not match the transfer plan.");
                State = StorageWriteHandleState.Committed;
                return StorageEntry.Create(Destination, StorageEntryKind.File, file.Length, file.LastWriteTimeUtc);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                State = StorageWriteHandleState.Faulted;
                TryDelete();
                return Fail<StorageEntry>("local-stage.commit", "The staged file could not be committed.", true);
            }
        }

        public async ValueTask<StorageResult> AbortAsync(CancellationToken cancellationToken = default)
        {
            if (State is StorageWriteHandleState.Committed or StorageWriteHandleState.Aborted) return StorageResult.Success();
            State = StorageWriteHandleState.Aborting;
            if (_stream is not null) { await _stream.DisposeAsync().ConfigureAwait(false); _stream = null; }
            TryDelete();
            State = StorageWriteHandleState.Aborted;
            return StorageResult.Success();
        }

        public async ValueTask DisposeAsync()
        {
            if (State == StorageWriteHandleState.Open) await AbortAsync().ConfigureAwait(false);
            else if (_stream is not null) { await _stream.DisposeAsync().ConfigureAwait(false); _stream = null; }
        }

        private void TryDelete() { try { if (File.Exists(_path)) File.Delete(_path); } catch { } }
    }
}
