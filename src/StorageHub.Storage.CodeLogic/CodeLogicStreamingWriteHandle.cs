using System.IO.Pipelines;
using CL.Storage.Abstractions;
using CL.Storage.Models;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Abstractions;
using StorageHub.Storage.Models;

namespace StorageHub.Storage.CodeLogic;

internal sealed class CodeLogicStreamingWriteHandle : IStorageWriteHandle
{
    private const long PauseWriterThreshold = 1024 * 1024;
    private const long ResumeWriterThreshold = 512 * 1024;
    private readonly object _gate = new();
    private readonly Pipe _pipe;
    private readonly Stream _readerStream;
    private readonly CancellationTokenSource _abortSource = new();
    private readonly IStorageService _storage;
    private readonly StorageWriteRequest _request;
    private readonly ConnectionProfileId _profileId;
    private readonly string _rootIdentity;
    private readonly string _providerUploadPath;
    private readonly bool _usesAtomicStaging;
    private readonly Task<global::CodeLogic.Core.Results.Result<StorageItem>> _upload;
    private Task<StorageResult>? _abortTask;
    private Task? _terminalTask;
    private Task? _disposeTask;
    private long _bytesWritten;
    private StorageWriteHandleState _state = StorageWriteHandleState.Open;
    private bool _disposed;

    public CodeLogicStreamingWriteHandle(
        IStorageService storage,
        StorageWriteRequest request,
        ConnectionProfileId profileId,
        string rootIdentity)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _profileId = profileId;
        _rootIdentity = rootIdentity;
        _usesAtomicStaging = storage.Provider == StorageProvider.Local;
        _providerUploadPath = _usesAtomicStaging
            ? CodeLogicLocalStaging.CreatePath()
            : request.Destination.CanonicalRelativePath;
        _pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: PauseWriterThreshold,
            resumeWriterThreshold: ResumeWriterThreshold,
            useSynchronizationContext: false));
        Content = new CountingPipeWriterStream(
            _pipe.Writer,
            count => Interlocked.Add(ref _bytesWritten, count));
        _readerStream = _pipe.Reader.AsStream();
        _upload = UploadAndDisposeReaderAsync(
            storage,
            _readerStream,
            request,
            _providerUploadPath,
            _usesAtomicStaging,
            _abortSource.Token);
    }

    public StorageAddress Destination => _request.Destination;

    public Stream Content { get; }

    public long AcceptedOffset => 0;

    public string? ResumeToken => null;

    public StorageWriteHandleState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public ValueTask<StorageResult<StorageEntry>> CommitAsync(
        CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<StorageResult<StorageEntry>>? completion = null;
        Task<StorageResult<StorageEntry>> resultTask;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Cancellation only has meaning before this caller becomes the terminal owner. Once
            // the provider may publish the object, its actual result must be determined without
            // turning a late caller cancellation into a false Aborted state.
            cancellationToken.ThrowIfCancellationRequested();

            if (_state != StorageWriteHandleState.Open)
            {
                return ValueTask.FromResult(StorageResult<StorageEntry>.Fail(InvalidState("commit")));
            }

            _state = StorageWriteHandleState.Committing;
            completion = new TaskCompletionSource<StorageResult<StorageEntry>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _terminalTask = completion.Task;
            resultTask = completion.Task;
        }

        _ = CompleteCommitOperationAsync(completion);
        return new ValueTask<StorageResult<StorageEntry>>(resultTask);
    }

    public ValueTask<StorageResult> AbortAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<StorageResult>? completion = null;
        Task<StorageResult> resultTask;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();

            if (_state == StorageWriteHandleState.Aborting && _abortTask is not null)
            {
                return new ValueTask<StorageResult>(_abortTask);
            }

            if (_state == StorageWriteHandleState.Aborted)
            {
                return ValueTask.FromResult(StorageResult.Success());
            }

            // In particular, an abort never cancels or changes an in-flight commit. The commit
            // owner is the only code allowed to publish its terminal state.
            if (_state != StorageWriteHandleState.Open)
            {
                return ValueTask.FromResult(StorageResult.Fail(InvalidState("abort")));
            }

            _state = StorageWriteHandleState.Aborting;
            completion = new TaskCompletionSource<StorageResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _abortTask = completion.Task;
            _terminalTask = completion.Task;
            resultTask = completion.Task;
        }

        _ = CompleteAbortOperationAsync(completion);
        return new ValueTask<StorageResult>(resultTask);
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource<StorageResult>? abortCompletion = null;
        TaskCompletionSource disposeCompletion;
        Task? terminalTask;
        Task disposeTask;

        lock (_gate)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _disposed = true;

            if (_state == StorageWriteHandleState.Open)
            {
                _state = StorageWriteHandleState.Aborting;
                var typedAbortCompletion = new TaskCompletionSource<StorageResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _abortTask = typedAbortCompletion.Task;
                _terminalTask = typedAbortCompletion.Task;
                abortCompletion = typedAbortCompletion;
            }

            terminalTask = _terminalTask;
            disposeCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = disposeCompletion.Task;
            disposeTask = disposeCompletion.Task;
        }

        if (abortCompletion is not null)
        {
            _ = CompleteAbortOperationAsync(abortCompletion);
        }

        _ = CompleteDisposeOperationAsync(terminalTask, disposeCompletion);
        return new ValueTask(disposeTask);
    }

    private async Task CompleteCommitOperationAsync(
        TaskCompletionSource<StorageResult<StorageEntry>> completion)
    {
        StorageResult<StorageEntry> outcome;

        try
        {
            outcome = await ExecuteCommitAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            TryCancelProvider();
            await CompleteWriterBestEffortAsync(exception).ConfigureAwait(false);
            await ObserveUploadAsync().ConfigureAwait(false);
            TransitionFromOwner(StorageWriteHandleState.Committing, StorageWriteHandleState.Faulted);
            outcome = !await CleanupStagingAsync().ConfigureAwait(false)
                ? StorageResult<StorageEntry>.Fail(StagingCleanupFailure())
                : StorageResult<StorageEntry>.Fail(new StorageFailure(
                    "storage.write.provider_indeterminate",
                    StorageFailureKind.Integrity,
                    "The provider outcome could not be confirmed."));
        }

        completion.TrySetResult(outcome);
    }

    private async Task<StorageResult<StorageEntry>> ExecuteCommitAsync()
    {
        if (_request.ExpectedLength is { } expectedLength &&
            expectedLength != Interlocked.Read(ref _bytesWritten))
        {
            await CompleteWriterAsync(new InvalidDataException(
                    "The supplied content length did not match the declared length."))
                .ConfigureAwait(false);
            TryCancelProvider();
            await ObserveUploadAsync().ConfigureAwait(false);
            if (!await CleanupStagingAsync().ConfigureAwait(false))
            {
                TransitionFromOwner(StorageWriteHandleState.Committing, StorageWriteHandleState.Faulted);
                return StorageResult<StorageEntry>.Fail(StagingCleanupFailure());
            }

            TransitionFromOwner(StorageWriteHandleState.Committing, StorageWriteHandleState.Faulted);
            return StorageResult<StorageEntry>.Fail(new StorageFailure(
                "storage.write.length_mismatch",
                StorageFailureKind.Integrity,
                "The supplied content length did not match the declared length."));
        }

        await CompleteWriterAsync().ConfigureAwait(false);

        // Deliberately do not WaitAsync with the caller's token. Cancellation was checked before
        // Committing became visible; from here the provider result is authoritative.
        var result = await _upload.ConfigureAwait(false);
        if (result.IsFailure)
        {
            if (!await CleanupStagingAsync().ConfigureAwait(false))
            {
                TransitionFromOwner(StorageWriteHandleState.Committing, StorageWriteHandleState.Faulted);
                return StorageResult<StorageEntry>.Fail(StagingCleanupFailure());
            }

            TransitionFromOwner(StorageWriteHandleState.Committing, StorageWriteHandleState.Faulted);
            return StorageResult<StorageEntry>.Fail(CodeLogicStorageMapper.MapFailure(
                result.Error,
                "storage.write.failed",
                "The item could not be written."));
        }

        var providerItem = result.Value!;
        if (_usesAtomicStaging)
        {
            var publish = await _storage.MoveAsync(
                    _providerUploadPath,
                    _request.Destination.CanonicalRelativePath,
                    new StorageTransferOptions
                    {
                        Overwrite = _request.Mode == StorageWriteMode.Overwrite,
                        CreateParents = true
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (publish.IsFailure)
            {
                if (!await CleanupStagingAsync().ConfigureAwait(false))
                {
                    TransitionFromOwner(StorageWriteHandleState.Committing, StorageWriteHandleState.Faulted);
                    return StorageResult<StorageEntry>.Fail(StagingCleanupFailure());
                }

                TransitionFromOwner(StorageWriteHandleState.Committing, StorageWriteHandleState.Faulted);
                return StorageResult<StorageEntry>.Fail(CodeLogicStorageMapper.MapFailure(
                    publish.Error,
                    "storage.write.publish_failed",
                    "The completed item could not be published."));
            }

            var destinationPath = _request.Destination.CanonicalRelativePath;
            providerItem = providerItem with
            {
                Path = destinationPath,
                Name = GetName(destinationPath)
            };
        }

        var entry = CodeLogicStorageMapper.MapEntry(providerItem, _profileId, _rootIdentity);
        TransitionFromOwner(
            StorageWriteHandleState.Committing,
            entry.IsSuccess ? StorageWriteHandleState.Committed : StorageWriteHandleState.Faulted);
        return entry;
    }

    private async Task CompleteAbortOperationAsync(TaskCompletionSource<StorageResult> completion)
    {
        StorageResult outcome;

        try
        {
            TryCancelProvider();
            await CompleteWriterAsync(new OperationCanceledException("The write was aborted."))
                .ConfigureAwait(false);

            try
            {
                var providerResult = await _upload.ConfigureAwait(false);
                if (_usesAtomicStaging)
                {
                    var cleanupSucceeded = await CleanupStagingAsync().ConfigureAwait(false);
                    if (cleanupSucceeded)
                    {
                        TransitionFromOwner(StorageWriteHandleState.Aborting, StorageWriteHandleState.Aborted);
                        outcome = StorageResult.Success();
                    }
                    else
                    {
                        TransitionFromOwner(StorageWriteHandleState.Aborting, StorageWriteHandleState.Faulted);
                        outcome = StorageResult.Fail(StagingCleanupFailure());
                    }
                }
                else if (providerResult.IsSuccess)
                {
                    // The provider says it published an item despite cancellation. Calling this
                    // Aborted would permit unsafe retries and duplicate/corrupt data.
                    TransitionFromOwner(StorageWriteHandleState.Aborting, StorageWriteHandleState.Faulted);
                    outcome = StorageResult.Fail(new StorageFailure(
                        "storage.write.abort_indeterminate",
                        StorageFailureKind.Integrity,
                        "The provider reported a completed write while the abort was in progress."));
                }
                else
                {
                    TransitionFromOwner(StorageWriteHandleState.Aborting, StorageWriteHandleState.Aborted);
                    outcome = StorageResult.Success();
                }
            }
            catch (OperationCanceledException) when (_abortSource.IsCancellationRequested)
            {
                if (await CleanupStagingAsync().ConfigureAwait(false))
                {
                    TransitionFromOwner(StorageWriteHandleState.Aborting, StorageWriteHandleState.Aborted);
                    outcome = StorageResult.Success();
                }
                else
                {
                    TransitionFromOwner(StorageWriteHandleState.Aborting, StorageWriteHandleState.Faulted);
                    outcome = StorageResult.Fail(StagingCleanupFailure());
                }
            }
            catch (Exception)
            {
                if (_usesAtomicStaging && await CleanupStagingAsync().ConfigureAwait(false))
                {
                    TransitionFromOwner(StorageWriteHandleState.Aborting, StorageWriteHandleState.Aborted);
                    outcome = StorageResult.Success();
                }
                else
                {
                    TransitionFromOwner(StorageWriteHandleState.Aborting, StorageWriteHandleState.Faulted);
                    outcome = StorageResult.Fail(CodeLogicStorageMapper.Unexpected("abort write"));
                }
            }
        }
        catch (Exception)
        {
            TryCancelProvider();
            await CompleteWriterBestEffortAsync(new IOException(
                    "The abort cleanup could not complete the content writer."))
                .ConfigureAwait(false);
            await ObserveUploadAsync().ConfigureAwait(false);
            TransitionFromOwner(StorageWriteHandleState.Aborting, StorageWriteHandleState.Faulted);
            outcome = !await CleanupStagingAsync().ConfigureAwait(false)
                ? StorageResult.Fail(StagingCleanupFailure())
                : StorageResult.Fail(CodeLogicStorageMapper.Unexpected("abort write"));
        }

        completion.TrySetResult(outcome);
    }

    private async Task CompleteDisposeOperationAsync(
        Task? terminalTask,
        TaskCompletionSource completion)
    {
        try
        {
            if (terminalTask is not null)
            {
                await terminalTask.ConfigureAwait(false);
            }

            await Content.DisposeAsync().ConfigureAwait(false);
            _abortSource.Dispose();
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private static async Task<global::CodeLogic.Core.Results.Result<StorageItem>> UploadAndDisposeReaderAsync(
        IStorageService storage,
        Stream source,
        StorageWriteRequest request,
        string providerUploadPath,
        bool usesAtomicStaging,
        CancellationToken cancellationToken)
    {
        try
        {
            return await storage.UploadAsync(
                    providerUploadPath,
                    source,
                    new StorageUploadOptions
                    {
                        Overwrite = !usesAtomicStaging && request.Mode == StorageWriteMode.Overwrite,
                        CreateParents = true,
                        ContentType = request.ContentType,
                        Metadata = request.Metadata,
                        Condition = request.ExpectedDestinationVersionId is null &&
                            request.ExpectedDestinationEntityTag is null
                                ? null
                                : new StorageMutationCondition
                                {
                                    ExpectedETag = request.ExpectedDestinationEntityTag,
                                    ExpectedVersionId = request.ExpectedDestinationVersionId
                                }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask CompleteWriterAsync(Exception? error = null)
    {
        try
        {
            await _pipe.Writer.CompleteAsync(error).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The writer may already be completed after an early provider failure.
        }
    }

    private async Task ObserveUploadAsync()
    {
        try
        {
            await _upload.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // This path exists only to finish provider cleanup after a locally detected failure.
        }
    }

    private async Task<bool> CleanupStagingAsync()
    {
        if (!_usesAtomicStaging)
        {
            return true;
        }

        try
        {
            var cleanup = await _storage.DeleteAsync(
                    _providerUploadPath,
                    new StorageDeleteOptions { IgnoreMissing = true },
                    CancellationToken.None)
                .ConfigureAwait(false);
            return cleanup.IsSuccess;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task CompleteWriterBestEffortAsync(Exception error)
    {
        try
        {
            await CompleteWriterAsync(error).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The provider outcome remains Faulted; terminal completion must not be stranded by
            // a secondary pipe-cleanup failure.
        }
    }

    private void TryCancelProvider()
    {
        try
        {
            _abortSource.Cancel();
        }
        catch (Exception)
        {
            // Provider cancellation callbacks are outside StorageHub's control. The terminal
            // operation still has to complete and report a safe failure if one misbehaves.
        }
    }

    private void TransitionFromOwner(
        StorageWriteHandleState ownerState,
        StorageWriteHandleState terminalState)
    {
        lock (_gate)
        {
            if (_state == ownerState)
            {
                _state = terminalState;
            }
        }
    }

    private static StorageFailure InvalidState(string operation) => new(
        "storage.write.invalid_state",
        StorageFailureKind.Conflict,
        $"The write handle cannot {operation} from its current state.");

    private static StorageFailure StagingCleanupFailure() => new(
        "storage.write.staging_cleanup_failed",
        StorageFailureKind.Integrity,
        "The private staging item could not be removed, so the write outcome requires attention.");

    private static string GetName(string path)
    {
        var separatorIndex = path.LastIndexOf('/');
        return separatorIndex < 0 ? path : path[(separatorIndex + 1)..];
    }

    private sealed class CountingPipeWriterStream(PipeWriter writer, Action<int> countBytes) : Stream
    {
        private const int MaximumWriteChunk = 64 * 1024;
        private int _disposed;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => Volatile.Read(ref _disposed) == 0;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            ThrowIfDisposed();
            EnsureFlushSucceeded(writer.FlushAsync().AsTask().GetAwaiter().GetResult());
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            EnsureFlushSucceeded(await writer.FlushAsync(cancellationToken).ConfigureAwait(false));
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ThrowIfDisposed();

            while (!buffer.IsEmpty)
            {
                var chunkLength = Math.Min(buffer.Length, MaximumWriteChunk);
                buffer[..chunkLength].CopyTo(writer.GetSpan(chunkLength));
                writer.Advance(chunkLength);
                EnsureFlushSucceeded(writer.FlushAsync().AsTask().GetAwaiter().GetResult());
                countBytes(chunkLength);
                buffer = buffer[chunkLength..];
            }
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            while (!buffer.IsEmpty)
            {
                var chunkLength = Math.Min(buffer.Length, MaximumWriteChunk);
                buffer[..chunkLength].CopyTo(writer.GetMemory(chunkLength));
                writer.Advance(chunkLength);
                var flushResult = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                EnsureFlushSucceeded(flushResult);
                countBytes(chunkLength);
                buffer = buffer[chunkLength..];
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Interlocked.Exchange(ref _disposed, 1);
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            await base.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }

        private void ThrowIfDisposed() =>
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        private static void EnsureFlushSucceeded(FlushResult result)
        {
            if (result.IsCanceled)
            {
                throw new OperationCanceledException("The write was canceled before the provider accepted it.");
            }

            if (result.IsCompleted)
            {
                throw new IOException("The provider stopped accepting content before the write completed.");
            }
        }
    }
}
