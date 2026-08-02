using System.Collections.Concurrent;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Storage;
using StorageHub.Transfers;

namespace StorageHub.Agent.Transfers;

/// <summary>
/// Claims and executes the durable transfer queue with bounded concurrency. All state,
/// checkpoint, and renewal writes retain the store-issued lease fence and attempt identity.
/// </summary>
public sealed class TransferQueueAgentSubsystem : IAgentSubsystem, IActiveTransferCancellation, IAsyncDisposable
{
    private readonly ITransferJobStore _store;
    private readonly ITransferEndpointConnector _connector;
    private readonly TransferQueueWorkerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly string _ownerId;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly ConcurrentDictionary<Domain.Identifiers.TransferJobId, ActiveExecutionControl> _activeExecutions = new();
    private CancellationTokenSource? _lifetime;
    private Task[] _workers = [];
    private string? _lastInfrastructureFailure;
    private string? _lastLeaseLoss;
    private long _completed;
    private long _failed;
    private long _retried;
    private long _interrupted;
    private long _cancelled;
    private long _leaseLosses;
    private int _recoveredInterruptedCount;
    private int _activeExecutionCount;
    private bool _initialized;
    private bool _started;
    private bool _disposed;

    public TransferQueueAgentSubsystem(
        ITransferJobStore store,
        ITransferEndpointConnector connector,
        TransferQueueWorkerOptions? options = null,
        TimeProvider? timeProvider = null,
        string? ownerId = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _options = options ?? new TransferQueueWorkerOptions();
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ownerId = ownerId ?? $"storagehub-agent-{Guid.NewGuid():N}";
        if (string.IsNullOrWhiteSpace(_ownerId) || _ownerId.Length > 256 || _ownerId.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The transfer worker owner ID must be at most 256 characters and contain no control characters.",
                nameof(ownerId));
        }
    }

    public string Name => "transfer queue";

    public bool CanRunInRecoveryMode => false;

    public int ActiveExecutionCount => Volatile.Read(ref _activeExecutionCount);

    public int RecoveredInterruptedCount => Volatile.Read(ref _recoveredInterruptedCount);

    /// <summary>
    /// Requests cancellation of a currently streaming attempt. The durable transition remains
    /// owned by that attempt and therefore retains its lease fence and revision CAS.
    /// </summary>
    public ActiveTransferCancellationResult TryRequestActiveCancellation(
        Domain.Identifiers.TransferJobId transferJobId,
        long expectedRevision)
    {
        if (transferJobId.IsEmpty)
        {
            throw new ArgumentException("A transfer job ID is required.", nameof(transferJobId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        if (!_activeExecutions.TryGetValue(transferJobId, out var active))
        {
            return ActiveTransferCancellationResult.NotActive;
        }

        if (active.ExpectedRevision != expectedRevision)
        {
            return ActiveTransferCancellationResult.RevisionConflict;
        }

        return active.RequestCancellation()
            ? ActiveTransferCancellationResult.Accepted
            : ActiveTransferCancellationResult.AlreadyRequested;
    }

    public async Task<SubsystemInitializationResult> InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
        {
            throw new InvalidOperationException("The transfer queue is already initialized.");
        }

        var recovered = await _store
            .RecoverInterruptedAsync(_timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        _ = Interlocked.Add(ref _recoveredInterruptedCount, recovered);
        _initialized = true;
        return SubsystemInitializationResult.Ready();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_initialized)
            {
                throw new InvalidOperationException("The transfer queue must be initialized before it is started.");
            }

            if (_started)
            {
                throw new InvalidOperationException("The transfer queue is already started.");
            }

            _lifetime = new CancellationTokenSource();
            _workers = Enumerable.Range(0, _options.MaximumConcurrency)
                .Select(_ => Task.Run(() => WorkerLoopAsync(_lifetime.Token), CancellationToken.None))
                .ToArray();
            _started = true;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_started)
            {
                return;
            }

            _lifetime!.Cancel();
            await Task.WhenAll(_workers).WaitAsync(cancellationToken).ConfigureAwait(false);
            _workers = [];
            _lifetime.Dispose();
            _lifetime = null;
            _started = false;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>Claims and fully processes at most one due transfer.</summary>
    public async ValueTask<bool> RunClaimOnceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            throw new InvalidOperationException("The transfer queue must be initialized before claiming work.");
        }

        TransferJobClaim? claim;
        try
        {
            var recoveryObservedAt = _timeProvider.GetUtcNow();
            var recovered = await _store.RecoverInterruptedAsync(recoveryObservedAt, cancellationToken)
                .ConfigureAwait(false);
            _ = Interlocked.Add(ref _recoveredInterruptedCount, recovered);
            // Recovery can wait behind durable-store work. A claim must start from a fresh
            // observation or a slow recovery could issue a lease that is already expired.
            var claimObservedAt = _timeProvider.GetUtcNow();
            claim = await _store.TryClaimNextAsync(
                new TransferClaimRequest(_ownerId, claimObservedAt, _options.LeaseDuration),
                cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _lastInfrastructureFailure, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            Volatile.Write(ref _lastInfrastructureFailure, "The durable transfer queue could not claim work.");
            throw;
        }

        if (claim is null)
        {
            return false;
        }

        _ = Interlocked.Increment(ref _activeExecutionCount);
        try
        {
            try
            {
                await ExecuteClaimAsync(claim, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                Volatile.Write(
                    ref _lastInfrastructureFailure,
                    "The transfer worker could not persist or execute a claimed job.");
                throw;
            }

            return true;
        }
        finally
        {
            _ = Interlocked.Decrement(ref _activeExecutionCount);
        }
    }

    public Task<SubsystemHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_initialized)
        {
            return Task.FromResult(SubsystemHealth.Unhealthy("The transfer queue is not initialized."));
        }

        var infrastructure = Volatile.Read(ref _lastInfrastructureFailure);
        if (infrastructure is not null)
        {
            return Task.FromResult(SubsystemHealth.Degraded(infrastructure));
        }

        var leaseLoss = Volatile.Read(ref _lastLeaseLoss);
        if (leaseLoss is not null)
        {
            return Task.FromResult(SubsystemHealth.Degraded(leaseLoss));
        }

        return Task.FromResult(SubsystemHealth.Healthy(string.Create(
            provider: null,
            $"Transfer queue healthy; active={ActiveExecutionCount}, completed={Interlocked.Read(ref _completed)}, failed={Interlocked.Read(ref _failed)}, retried={Interlocked.Read(ref _retried)}, interrupted={Interlocked.Read(ref _interrupted)}, cancelled={Interlocked.Read(ref _cancelled)}, lease-lost={Interlocked.Read(ref _leaseLosses)}, recovered={RecoveredInterruptedCount}.")));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _disposed = true;
        _lifecycleGate.Dispose();
    }

    private async Task WorkerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var claimed = false;
            try
            {
                claimed = await RunClaimOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // A safe health diagnostic has already been recorded. Polling retries.
            }

            if (claimed)
            {
                continue;
            }

            try
            {
                await Task.Delay(_options.PollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task ExecuteClaimAsync(TransferJobClaim claim, CancellationToken hostCancellationToken)
    {
        var context = new ClaimContext(claim);
        if (!await ClearPriorCheckpointAsync(context, hostCancellationToken).ConfigureAwait(false))
        {
            RecordLeaseLoss();
            return;
        }

        if (!await TryTransitionAsync(context, TransferState.Connecting, cancellationToken: hostCancellationToken)
                .ConfigureAwait(false))
        {
            RecordLeaseLoss();
            return;
        }

        ITransferEndpointConnection? sourceConnection = null;
        ITransferEndpointConnection? destinationConnection = null;
        try
        {
            var source = await OpenEndpointAsync(
                context,
                context.Job.Intent.Source.ProfileId,
                hostCancellationToken).ConfigureAwait(false);
            if (source is null)
            {
                return;
            }

            sourceConnection = source;
            var destination = await OpenEndpointAsync(
                context,
                context.Job.Intent.Destination.ProfileId,
                hostCancellationToken).ConfigureAwait(false);
            if (destination is null)
            {
                return;
            }

            destinationConnection = destination;
            if (!await TryTransitionAsync(context, TransferState.Transferring, cancellationToken: hostCancellationToken)
                    .ConfigureAwait(false))
            {
                RecordLeaseLoss();
                return;
            }

            using var executionLifetime = CancellationTokenSource.CreateLinkedTokenSource(hostCancellationToken);
            var activeControl = new ActiveExecutionControl(context.Job.State.Revision, executionLifetime);
            if (!_activeExecutions.TryAdd(context.Lease.TransferJobId, activeControl))
            {
                throw new InvalidOperationException("The transfer already has an active local execution.");
            }

            var progress = new LatestTransferProgress();
            var leaseMonitor = MonitorLeaseAsync(context.Lease, executionLifetime);
            var checkpointMonitor = MonitorCheckpointAsync(context, progress, executionLifetime);
            StorageResult<TransferExecutionReport>? executionResult = null;
            Exception? unexpectedFailure = null;
            MonitorOutcome leaseOutcome;
            MonitorOutcome checkpointOutcome;
            try
            {
                try
                {
                    executionResult = await TransferExecutor.ExecuteAsync(
                        context.Job.Intent,
                        sourceConnection.Session,
                        destinationConnection.Session,
                        new TransferExecutionOptions(
                            Overwrite: context.Job.Intent.ExpectedDestinationVersionId is not null ||
                                       context.Job.Intent.ExpectedDestinationEntityTag is not null,
                            BufferSize: _options.BufferSize),
                        progress,
                        executionLifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (executionLifetime.IsCancellationRequested)
                {
                }
                catch (Exception)
                {
                    unexpectedFailure = new InvalidOperationException("The transfer executor failed unexpectedly.");
                }
                finally
                {
                    executionLifetime.Cancel();
                }

                leaseOutcome = await leaseMonitor.ConfigureAwait(false);
                checkpointOutcome = await checkpointMonitor.ConfigureAwait(false);
            }
            finally
            {
                _ = _activeExecutions.TryRemove(context.Lease.TransferJobId, out _);
            }

            if (leaseOutcome == MonitorOutcome.LeaseLost || checkpointOutcome == MonitorOutcome.LeaseLost)
            {
                RecordLeaseLoss();
                return;
            }

            if (leaseOutcome == MonitorOutcome.InfrastructureFailure ||
                checkpointOutcome == MonitorOutcome.InfrastructureFailure)
            {
                Volatile.Write(
                    ref _lastInfrastructureFailure,
                    "The transfer worker could not maintain its durable execution lease or checkpoint.");
                return;
            }

            if (executionResult is null)
            {
                if (activeControl.IsCancellationRequestedByUser)
                {
                    await TransitionCancelledAsync(context).ConfigureAwait(false);
                    return;
                }

                if (hostCancellationToken.IsCancellationRequested)
                {
                    await TransitionInterruptedAsync(context).ConfigureAwait(false);
                    return;
                }

                await TransitionUncertainAsync(context, unexpectedFailure is null
                    ? "Transfer execution was cancelled after its ownership monitor stopped."
                    : "The transfer executor stopped unexpectedly after provider I/O began.").ConfigureAwait(false);
                return;
            }

            if (executionResult.IsFailure)
            {
                await HandleExecutionFailureAsync(context, executionResult.Error).ConfigureAwait(false);
                return;
            }

            if (!await SaveLatestCheckpointAsync(context, progress.BytesTransferred, CancellationToken.None)
                    .ConfigureAwait(false))
            {
                RecordLeaseLoss();
                return;
            }

            if (!await TryTransitionAsync(
                    context,
                    TransferState.Verifying,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false) ||
                !await TryTransitionAsync(
                    context,
                    TransferState.Finalizing,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false) ||
                !await TryTransitionAsync(
                    context,
                    TransferState.Completed,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false))
            {
                RecordLeaseLoss();
                return;
            }

            _ = Interlocked.Increment(ref _completed);
            Volatile.Write(ref _lastLeaseLoss, null);
        }
        finally
        {
            if (destinationConnection is not null)
            {
                await destinationConnection.DisposeAsync().ConfigureAwait(false);
            }

            if (sourceConnection is not null)
            {
                await sourceConnection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<ITransferEndpointConnection?> OpenEndpointAsync(
        ClaimContext context,
        Domain.Identifiers.ConnectionProfileId profileId,
        CancellationToken cancellationToken)
    {
        StorageResult<ITransferEndpointConnection> opened;
        try
        {
            opened = await _connector.OpenAsync(profileId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TransitionInterruptedAsync(context).ConfigureAwait(false);
            return null;
        }
        catch (Exception)
        {
            await HandleConnectionFailureAsync(context, new StorageFailure(
                "transfer.connection.unexpected",
                StorageFailureKind.Unexpected,
                "The runtime connection could not be opened.")).ConfigureAwait(false);
            return null;
        }

        if (opened.IsSuccess)
        {
            return opened.Value;
        }

        await HandleConnectionFailureAsync(context, opened.Error).ConfigureAwait(false);
        return null;
    }

    private async Task HandleConnectionFailureAsync(ClaimContext context, StorageFailure failure)
    {
        if (failure.Code.StartsWith("storage.trust.", StringComparison.Ordinal))
        {
            if (!await TryTransitionAsync(
                context,
                TransferState.BlockedTrust,
                TransferStatusCode.TrustRequired,
                SafeError("transfer.trust.required", "Endpoint trust approval is required."))
                .ConfigureAwait(false))
            {
                RecordLeaseLoss();
            }

            return;
        }

        if (failure.Kind == StorageFailureKind.Unauthorized ||
            failure.Code.StartsWith("storage.credential.", StringComparison.Ordinal))
        {
            if (!await TryTransitionAsync(
                context,
                TransferState.BlockedCredential,
                TransferStatusCode.CredentialUnavailable,
                SafeError("transfer.credential.unavailable", "A required endpoint credential is unavailable."))
                .ConfigureAwait(false))
            {
                RecordLeaseLoss();
            }

            return;
        }

        await RetryOrFailAsync(context, failure.IsTransient).ConfigureAwait(false);
    }

    private async Task HandleExecutionFailureAsync(ClaimContext context, StorageFailure failure)
    {
        if (failure.Code is "transfer.move.cleanup_failed" or "transfer.promote.failed" ||
            failure.Kind is StorageFailureKind.Integrity or StorageFailureKind.Conflict)
        {
            await TransitionUncertainAsync(
                context,
                "Provider side effects require reconciliation before this transfer can continue.")
                .ConfigureAwait(false);
            return;
        }

        await RetryOrFailAsync(context, failure.IsTransient).ConfigureAwait(false);
    }

    private async Task RetryOrFailAsync(ClaimContext context, bool transient)
    {
        if (transient && context.Lease.Attempt < _options.MaximumAttempts)
        {
            var now = NextTransitionTime(context.Job.State);
            var retryAt = now.Add(CalculateRetryDelay(context.Lease.Attempt));
            if (await TryTransitionAsync(
                    context,
                    TransferState.Retrying,
                    error: SafeError("transfer.retry.transient", "A transient endpoint failure will be retried."),
                    retryAt: retryAt,
                    transitionedAtUtc: now).ConfigureAwait(false))
            {
                _ = Interlocked.Increment(ref _retried);
            }
            else
            {
                RecordLeaseLoss();
            }

            return;
        }

        if (await TryTransitionAsync(
                context,
                TransferState.Failed,
                TransferStatusCode.ProviderFailure,
                SafeError("transfer.provider.failed", "The transfer could not be completed safely."))
                .ConfigureAwait(false))
        {
            _ = Interlocked.Increment(ref _failed);
        }
        else
        {
            RecordLeaseLoss();
        }
    }

    private async Task TransitionInterruptedAsync(ClaimContext context)
    {
        if (await TryTransitionAsync(
                context,
                TransferState.Interrupted,
                TransferStatusCode.Interrupted,
                SafeError("transfer.worker.stopped", "The transfer owner stopped before completion."),
                cancellationToken: CancellationToken.None).ConfigureAwait(false))
        {
            _ = Interlocked.Increment(ref _interrupted);
        }
        else
        {
            RecordLeaseLoss();
        }
    }

    private async Task TransitionCancelledAsync(ClaimContext context)
    {
        if (await TryTransitionAsync(
                context,
                TransferState.Cancelled,
                cancellationToken: CancellationToken.None).ConfigureAwait(false))
        {
            _ = Interlocked.Increment(ref _cancelled);
        }
        else
        {
            RecordLeaseLoss();
        }
    }

    private async Task TransitionUncertainAsync(ClaimContext context, string summary)
    {
        if (await TryTransitionAsync(
                context,
                TransferState.NeedsReconciliation,
                TransferStatusCode.StateUncertain,
                SafeError("transfer.state.uncertain", summary),
                cancellationToken: CancellationToken.None).ConfigureAwait(false))
        {
            _ = Interlocked.Increment(ref _failed);
        }
        else
        {
            RecordLeaseLoss();
        }
    }

    private async ValueTask<bool> TryTransitionAsync(
        ClaimContext context,
        TransferState nextState,
        TransferStatusCode statusCode = TransferStatusCode.None,
        TransferSafeError? error = null,
        DateTimeOffset? retryAt = null,
        DateTimeOffset? transitionedAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        var observedAt = transitionedAtUtc ?? NextTransitionTime(context.Job.State);
        var result = await _store.TryTransitionAsync(
            new TransferStateTransitionRequest(
                context.Lease,
                context.Job.State.Revision,
                nextState,
                observedAt,
                statusCode,
                error,
                retryAt),
            cancellationToken).ConfigureAwait(false);
        if (result.Status != TransferStoreMutationStatus.Applied)
        {
            return false;
        }

        context.Job = result.Value!;
        return true;
    }

    private async ValueTask<bool> ClearPriorCheckpointAsync(
        ClaimContext context,
        CancellationToken cancellationToken)
    {
        var existing = await _store.FindCheckpointAsync(
            context.Lease.TransferJobId,
            cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return true;
        }

        var result = await _store.TryClearCheckpointAsync(
            new TransferCheckpointClearRequest(
                context.Lease,
                existing.Version,
                NextTransitionTime(context.Job.State)),
            cancellationToken).ConfigureAwait(false);
        return result == TransferStoreMutationStatus.Applied;
    }

    private async Task<MonitorOutcome> MonitorLeaseAsync(
        TransferJobLease lease,
        CancellationTokenSource executionLifetime)
    {
        var currentExpiry = lease.ExpiresAtUtc;
        try
        {
            while (!executionLifetime.IsCancellationRequested)
            {
                await Task.Delay(
                    _options.LeaseRenewalInterval,
                    _timeProvider,
                    executionLifetime.Token).ConfigureAwait(false);
                var renewedAt = _timeProvider.GetUtcNow();
                if (renewedAt >= currentExpiry)
                {
                    executionLifetime.Cancel();
                    return MonitorOutcome.LeaseLost;
                }

                var renewal = await _store.TryRenewLeaseAsync(
                    new TransferLeaseRenewal(lease, renewedAt, renewedAt.Add(_options.LeaseDuration)),
                    executionLifetime.Token).ConfigureAwait(false);
                if (renewal.Status != TransferStoreMutationStatus.Applied)
                {
                    executionLifetime.Cancel();
                    return MonitorOutcome.LeaseLost;
                }

                currentExpiry = renewal.Value!.ExpiresAtUtc;
            }

            return MonitorOutcome.Stopped;
        }
        catch (OperationCanceledException) when (executionLifetime.IsCancellationRequested)
        {
            return MonitorOutcome.Stopped;
        }
        catch (Exception)
        {
            executionLifetime.Cancel();
            return MonitorOutcome.InfrastructureFailure;
        }
    }

    private async Task<MonitorOutcome> MonitorCheckpointAsync(
        ClaimContext context,
        LatestTransferProgress progress,
        CancellationTokenSource executionLifetime)
    {
        long lastSaved = -1;
        try
        {
            while (!executionLifetime.IsCancellationRequested)
            {
                await Task.Delay(
                    _options.CheckpointInterval,
                    _timeProvider,
                    executionLifetime.Token).ConfigureAwait(false);
                var latest = progress.BytesTransferred;
                if (latest == lastSaved)
                {
                    continue;
                }

                if (!await SaveLatestCheckpointAsync(context, latest, executionLifetime.Token)
                        .ConfigureAwait(false))
                {
                    executionLifetime.Cancel();
                    return MonitorOutcome.LeaseLost;
                }

                lastSaved = latest;
            }

            return MonitorOutcome.Stopped;
        }
        catch (OperationCanceledException) when (executionLifetime.IsCancellationRequested)
        {
            return MonitorOutcome.Stopped;
        }
        catch (Exception)
        {
            executionLifetime.Cancel();
            return MonitorOutcome.InfrastructureFailure;
        }
    }

    private async ValueTask<bool> SaveLatestCheckpointAsync(
        ClaimContext context,
        long bytesTransferred,
        CancellationToken cancellationToken)
    {
        var expectedLength = context.Job.Intent.ExpectedLength;
        if (expectedLength is { } length)
        {
            bytesTransferred = Math.Min(bytesTransferred, length);
        }

        var checkpoint = TransferCheckpoint.Create(
            context.Lease.TransferJobId,
            context.Lease.Attempt,
            Math.Max(0, bytesTransferred),
            expectedLength,
            context.Job.Intent.Source,
            CreateCheckpointDestination(context.Job.Intent),
            TransferResumeMode.None,
            sourceDigest: context.Job.Intent.ExpectedSourceDigest is null
                ? null
                : new TransferContentDigest(
                    context.Job.Intent.ExpectedSourceDigest.AlgorithmName,
                    context.Job.Intent.ExpectedSourceDigest.Value),
            providerResumeId: null,
            completedParts: [],
            NextTransitionTime(context.Job.State));
        var saved = await _store.TrySaveCheckpointAsync(
            new TransferCheckpointWriteRequest(context.Lease, checkpoint, context.CheckpointVersion),
            cancellationToken).ConfigureAwait(false);
        if (saved.Status != TransferStoreMutationStatus.Applied)
        {
            return false;
        }

        context.CheckpointVersion = saved.Value!.Version;
        return true;
    }

    private static StorageAddress CreateCheckpointDestination(TransferIntent intent)
    {
        if (intent.ExpectedDestinationVersionId is null &&
            intent.ExpectedDestinationEntityTag is null)
        {
            return intent.Destination;
        }

        var staging = intent.Destination.Parent.Append($".storagehub-{intent.TransferJobId.Value:N}.staging");
        return staging.IsSuccess ? staging.Value : intent.Destination;
    }

    private TimeSpan CalculateRetryDelay(int attempt)
    {
        var exponent = Math.Min(30, Math.Max(0, attempt - 1));
        var multiplier = 1L << exponent;
        var ticks = _options.InitialRetryDelay.Ticks > long.MaxValue / multiplier
            ? long.MaxValue
            : _options.InitialRetryDelay.Ticks * multiplier;
        return TimeSpan.FromTicks(Math.Min(ticks, _options.MaximumRetryDelay.Ticks));
    }

    private DateTimeOffset NextTransitionTime(TransferStateSnapshot current)
    {
        var now = _timeProvider.GetUtcNow();
        return now < current.TransitionedAtUtc ? current.TransitionedAtUtc : now;
    }

    private void RecordLeaseLoss()
    {
        _ = Interlocked.Increment(ref _leaseLosses);
        Volatile.Write(
            ref _lastLeaseLoss,
            "A transfer lost its durable lease; the stale worker did not record completion.");
    }

    private static TransferSafeError SafeError(string code, string summary) => new(code, summary);

    private sealed class ClaimContext(TransferJobClaim claim)
    {
        public TransferJobLease Lease { get; } = claim.Lease;
        public DurableTransferJob Job { get; set; } = claim.Job;
        public long? CheckpointVersion { get; set; }
    }

    private sealed class LatestTransferProgress : IProgress<TransferProgress>
    {
        private long _bytesTransferred;

        public long BytesTransferred => Interlocked.Read(ref _bytesTransferred);

        public void Report(TransferProgress value)
        {
            if (value.BytesTransferred < 0)
            {
                return;
            }

            while (true)
            {
                var current = Interlocked.Read(ref _bytesTransferred);
                if (value.BytesTransferred <= current ||
                    Interlocked.CompareExchange(
                        ref _bytesTransferred,
                        value.BytesTransferred,
                        current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class ActiveExecutionControl(
        long expectedRevision,
        CancellationTokenSource executionLifetime)
    {
        private int _cancellationRequestedByUser;

        public long ExpectedRevision { get; } = expectedRevision;

        public bool IsCancellationRequestedByUser =>
            Volatile.Read(ref _cancellationRequestedByUser) != 0;

        public bool RequestCancellation()
        {
            if (Interlocked.Exchange(ref _cancellationRequestedByUser, 1) != 0)
            {
                return false;
            }

            executionLifetime.Cancel();
            return true;
        }
    }

    private enum MonitorOutcome
    {
        Stopped,
        LeaseLost,
        InfrastructureFailure
    }
}

public enum ActiveTransferCancellationResult
{
    NotActive,
    RevisionConflict,
    Accepted,
    AlreadyRequested
}

public interface IActiveTransferCancellation
{
    ActiveTransferCancellationResult TryRequestActiveCancellation(
        Domain.Identifiers.TransferJobId transferJobId,
        long expectedRevision);
}
