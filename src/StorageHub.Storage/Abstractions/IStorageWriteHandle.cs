using StorageHub.Contracts.Results;
using StorageHub.Domain.Storage;

namespace StorageHub.Storage.Abstractions;

public enum StorageWriteHandleState
{
    Open,
    Committing,
    Committed,
    Aborting,
    Aborted,
    Faulted
}

/// <summary>
/// Owns a provider write stream and its commit/abort lifecycle. Disposing an uncommitted
/// handle must perform best-effort abort and must never silently commit partial content.
/// </summary>
public interface IStorageWriteHandle : IAsyncDisposable
{
    StorageAddress Destination { get; }

    /// <summary>
    /// The handle-owned writable stream, valid until commit, abort, or handle disposal.
    /// Consumers dispose the handle rather than disposing this stream independently.
    /// </summary>
    Stream Content { get; }

    /// <summary>The provider-confirmed starting offset for this handle.</summary>
    long AcceptedOffset { get; }

    /// <summary>An opaque checkpoint that may be persisted when resume upload is supported.</summary>
    string? ResumeToken { get; }

    StorageWriteHandleState State { get; }

    /// <summary>Closes content and makes it visible. Implementations permit at most one terminal transition.</summary>
    ValueTask<StorageResult<StorageEntry>> CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes staged content when possible. Implementations permit at most one terminal transition.</summary>
    ValueTask<StorageResult> AbortAsync(CancellationToken cancellationToken = default);
}
