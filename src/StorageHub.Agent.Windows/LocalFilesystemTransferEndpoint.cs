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

/// <summary>
/// A read-only, source-only endpoint for paths explicitly approved from Explorer.  The root is
/// serialized into the durable address, and every open rechecks containment, reparse points and
/// the captured file evidence. It deliberately exposes no write/delete/move surface.
/// </summary>
internal sealed class LocalFilesystemTransferEndpoint
{
    private const string Prefix = "localfs:v1:";
    private static readonly Guid Namespace = new("2E330960-289A-4C9F-A7BE-062BA2B29517");

    public static bool IsLocalSource(StorageAddress address) => address.RootIdentity.StartsWith(Prefix, StringComparison.Ordinal);

    public static StorageResult<StorageAddress> CreateAddress(string root, string relativePath, FileInfo file)
    {
        try
        {
            file.Refresh();
            if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return Fail<StorageAddress>("local-source.changed", "The dropped filesystem source no longer exists or is a link.");
            }
            var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var identity = Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(canonicalRoot));
            var id = new ConnectionProfileId(CreateDeterministicGuid(canonicalRoot));
            return StorageAddress.Create(id, identity, relativePath, null, Evidence(file), null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Fail<StorageAddress>("local-source.invalid", "The dropped filesystem source is not available.");
        }
    }

    public static StorageResult<ITransferEndpointConnection> Open(StorageAddress address)
    {
        if (!TryDecodeRoot(address, out var root))
        {
            return Fail<ITransferEndpointConnection>("local-source.invalid", "The approved local source is invalid.");
        }

        try
        {
            if (new DirectoryInfo(root).LinkTarget is not null)
            {
                return Fail<ITransferEndpointConnection>("local-source.reparse-point", "The dropped source root is a link or reparse point.");
            }

            return StorageResult<ITransferEndpointConnection>.Success(new Connection(new Session(address.ProfileId, root, address.RootIdentity)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Fail<ITransferEndpointConnection>("local-source.unavailable", "The approved local source is no longer available.", transient: true);
        }
    }

    private static bool TryDecodeRoot(StorageAddress address, out string root)
    {
        root = string.Empty;
        if (!IsLocalSource(address)) return false;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Encoding.UTF8.GetString(Convert.FromBase64String(address.RootIdentity[Prefix.Length..]))));
            return address.ProfileId == new ConnectionProfileId(CreateDeterministicGuid(root));
        }
        catch (Exception) { return false; }
    }

    private static string Evidence(FileInfo info) => $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";

    private static Guid CreateDeterministicGuid(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Namespace + "|" + value.ToUpperInvariant()));
        return new Guid(hash[..16]);
    }

    private static StorageResult<T> Fail<T>(string code, string message, bool transient = false) => StorageResult<T>.Fail(
        new StorageFailure(code, StorageFailureKind.Validation, message, transient));

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
            new KeyValuePair<StorageFeature, FeatureSupport>(StorageFeature.ReadStream, FeatureSupport.Native()),
            new KeyValuePair<StorageFeature, FeatureSupport>(StorageFeature.List, FeatureSupport.Native())]);

        public ValueTask<StorageResult> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Directory.Exists(root) ? StorageResult.Success() : StorageResult.Fail(new StorageFailure("local-source.unavailable", StorageFailureKind.NotFound, "The approved local source root no longer exists.")));

        public ValueTask<StorageResult<StorageEntry>> GetEntryAsync(StorageAddress address, CancellationToken cancellationToken = default)
        {
            var path = Resolve(address);
            if (path.IsFailure) return ValueTask.FromResult(StorageResult<StorageEntry>.Fail(path.Error));
            var file = new FileInfo(path.Value);
            if (!file.Exists || IsReparse(file)) return ValueTask.FromResult(Fail<StorageEntry>("local-source.changed", "The approved source file no longer exists or is a link."));
            if (!string.Equals(address.VersionId, Evidence(file), StringComparison.Ordinal)) return ValueTask.FromResult(Fail<StorageEntry>("local-source.changed", "The approved source file changed after it was dropped."));
            return ValueTask.FromResult(StorageEntry.Create(address, StorageEntryKind.File, file.Length, file.LastWriteTimeUtc, eTag: Evidence(file)));
        }

        public ValueTask<StorageResult<StoragePage>> ListAsync(StorageAddress address, StorageListRequest? request = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Fail<StoragePage>("local-source.unsupported", "Approved local sources cannot be listed by the transfer worker."));

        public ValueTask<StorageResult<Stream>> OpenReadAsync(StorageReadRequest request, CancellationToken cancellationToken = default)
        {
            var path = Resolve(request.Address);
            if (path.IsFailure) return ValueTask.FromResult(StorageResult<Stream>.Fail(path.Error));
            var file = new FileInfo(path.Value);
            if (!file.Exists || IsReparse(file) || !string.Equals(request.Address.VersionId, Evidence(file), StringComparison.Ordinal))
                return ValueTask.FromResult(Fail<Stream>("local-source.changed", "The approved source file changed after it was dropped."));
            try
            {
                var stream = new FileStream(path.Value, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (request.Offset > stream.Length) { stream.Dispose(); return ValueTask.FromResult(Fail<Stream>("local-source.range", "The requested local source range is invalid.")); }
                stream.Position = request.Offset;
                Stream bounded = request.Length is { } length ? new BoundedReadStream(stream, length) : stream;
                return ValueTask.FromResult(StorageResult<Stream>.Success(bounded));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { return ValueTask.FromResult(Fail<Stream>("local-source.unavailable", "The approved source file could not be opened.", transient: true)); }
        }

        public ValueTask<StorageResult<IStorageWriteHandle>> OpenWriteAsync(StorageWriteRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(Fail<IStorageWriteHandle>("local-source.read-only", "Local shell sources are read-only."));
        public ValueTask<StorageResult<StorageEntry>> CreateDirectoryAsync(StorageAddress address, CancellationToken cancellationToken = default) => ValueTask.FromResult(Fail<StorageEntry>("local-source.read-only", "Local shell sources are read-only."));
        public ValueTask<StorageResult> DeleteAsync(StorageDeleteRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(StorageResult.Fail(new StorageFailure("local-source.read-only", StorageFailureKind.Validation, "Local shell sources are read-only.")));
        public ValueTask<StorageResult<StorageEntry>> CopyAsync(StorageCopyRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(Fail<StorageEntry>("local-source.read-only", "Local shell sources are read-only."));
        public ValueTask<StorageResult<StorageEntry>> MoveAsync(StorageMoveRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(Fail<StorageEntry>("local-source.read-only", "Local shell sources are read-only."));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private StorageResult<string> Resolve(StorageAddress address)
        {
            if (address.ProfileId != ProfileId || !string.Equals(address.RootIdentity, RootIdentity, StringComparison.Ordinal)) return Fail<string>("local-source.address", "The source address is outside its approved root.");
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(root, address.CanonicalRelativePath.Replace('/', Path.DirectorySeparatorChar)));
                var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
                if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return Fail<string>("local-source.escape", "The source path escapes its approved root.");
                return StorageResult<string>.Success(candidate);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return Fail<string>("local-source.address", "The source path is invalid."); }
        }

        private static bool IsReparse(FileSystemInfo item) => (item.Attributes & FileAttributes.ReparsePoint) != 0;
    }

    private sealed class BoundedReadStream(Stream inner, long remaining) : Stream
    {
        public override bool CanRead => inner.CanRead; public override bool CanSeek => inner.CanSeek; public override bool CanWrite => false; public override long Length => Math.Min(inner.Length - inner.Position + Position, remaining); public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override int Read(byte[] buffer, int offset, int count) { var read = inner.Read(buffer, offset, (int)Math.Min(count, remaining)); remaining -= read; return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) { var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, remaining)], token); remaining -= read; return read; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(); protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
