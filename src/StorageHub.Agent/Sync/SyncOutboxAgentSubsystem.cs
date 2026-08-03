using StorageHub.Sync;
using StorageHub.Sync.Persistence;
using StorageHub.Agent;

namespace StorageHub.Agent.Sync;

/// <summary>
/// Separately leased consumer for StorageHub sync command events. It selectively claims only the
/// two owned kinds and cancels provider work as soon as renewal proves the claim is no longer live.
/// </summary>
public sealed class SyncOutboxAgentSubsystem : IAgentSubsystem, IAsyncDisposable
{
    public static IReadOnlyList<string> OwnedEventKinds { get; } = Array.AsReadOnly(
        new[]
        {
            SyncOutboxEventKinds.PreviewRequested,
            SyncOutboxEventKinds.ApplyRequested,
        });

    private readonly IReliableOutboxStore _outbox;
    private readonly ISyncOutboxEventProcessor _processor;
    private readonly SyncOutboxWorkerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly string _ownerId;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly AdaptiveConcurrencyController _adaptiveConcurrency;
    private CancellationTokenSource? _lifetime;
    private Task[] _workers = [];
    private string? _lastInfrastructureFailure;
    private string? _lastLeaseLoss;
    private long _completed;
    private long _retried;
    private long _deadLettered;
    private long _leaseLosses;
    private int _activeCount;
    private bool _initialized;
    private bool _started;
    private bool _disposed;

    public SyncOutboxAgentSubsystem(
        IReliableOutboxStore outbox,
        ISyncOutboxEventProcessor processor,
        SyncOutboxWorkerOptions? options = null,
        TimeProvider? timeProvider = null,
        string? ownerId = null)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _options = options ?? new SyncOutboxWorkerOptions();
        _options.Validate();
        _adaptiveConcurrency = new AdaptiveConcurrencyController(
            _options.AdaptiveConcurrency,
            _options.MinimumConcurrency,
            _options.MaximumConcurrency);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ownerId = ownerId ?? $"storagehub-sync-{Guid.NewGuid():N}";
        if (string.IsNullOrWhiteSpace(_ownerId) || _ownerId.Length > 256 || _ownerId.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The sync outbox owner ID must be at most 256 safe characters.",
                nameof(ownerId));
        }
    }

    public string Name => "sync outbox";

    public bool CanRunInRecoveryMode => false;

    public int ActiveCount => Volatile.Read(ref _activeCount);

    public int CurrentConcurrencyLimit => _adaptiveConcurrency.CurrentLimit;

    public long CompletedCount => Interlocked.Read(ref _completed);

    public long RetryCount => Interlocked.Read(ref _retried);

    public long DeadLetterCount => Interlocked.Read(ref _deadLettered);

    public long LeaseLossCount => Interlocked.Read(ref _leaseLosses);

    public Task<SubsystemInitializationResult> InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_initialized)
        {
            throw new InvalidOperationException("The sync outbox worker is already initialized.");
        }

        _initialized = true;
        return Task.FromResult(SubsystemInitializationResult.Ready());
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_initialized)
            {
                throw new InvalidOperationException("The sync outbox worker must be initialized before start.");
            }

            if (_started)
            {
                throw new InvalidOperationException("The sync outbox worker is already started.");
            }

            _lifetime = new CancellationTokenSource();
            _workers = Enumerable.Range(0, _options.MaximumConcurrency)
                .Select(index => Task.Run(() => WorkerLoopAsync(index, _lifetime.Token), CancellationToken.None))
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

    /// <summary>Claims and fully handles at most one owned outbox event.</summary>
    public async ValueTask<bool> RunClaimOnceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            throw new InvalidOperationException("The sync outbox worker must be initialized before claiming work.");
        }

        IReadOnlyList<OutboxDeliveryLease> leases;
        try
        {
            leases = await _outbox.ClaimPendingByKindsAsync(
                _ownerId,
                OwnedEventKinds,
                maximumCount: 1,
                _timeProvider.GetUtcNow(),
                _options.LeaseDuration,
                cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _lastInfrastructureFailure, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            Volatile.Write(ref _lastInfrastructureFailure, "The sync outbox could not claim durable work.");
            throw;
        }

        if (leases.Count == 0)
        {
            return false;
        }

        _ = Interlocked.Increment(ref _activeCount);
        var startedAt = _timeProvider.GetTimestamp();
        var succeeded = false;
        try
        {
            try
            {
                succeeded = await ProcessLeaseAsync(leases[0], cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                Volatile.Write(
                    ref _lastInfrastructureFailure,
                    "The sync outbox could not safely finalize a claimed event.");
                throw;
            }

            return true;
        }
        finally
        {
            var elapsed = _timeProvider.GetElapsedTime(startedAt, _timeProvider.GetTimestamp());
            if (succeeded)
            {
                _adaptiveConcurrency.ReportSuccess(1, elapsed);
            }
            else if (!cancellationToken.IsCancellationRequested)
            {
                _adaptiveConcurrency.ReportFailure();
            }

            _ = Interlocked.Decrement(ref _activeCount);
        }
    }

    public Task<SubsystemHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_initialized)
        {
            return Task.FromResult(SubsystemHealth.Unhealthy("The sync outbox worker is not initialized."));
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

        return Task.FromResult(SubsystemHealth.Healthy(
            $"Sync outbox healthy; active={ActiveCount}, concurrency={CurrentConcurrencyLimit}/{_options.MaximumConcurrency}, completed={CompletedCount}, retries={RetryCount}, dead-lettered={DeadLetterCount}, lease-lost={LeaseLossCount}."));
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

    private async Task WorkerLoopAsync(int workerIndex, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (workerIndex >= _adaptiveConcurrency.CurrentLimit)
            {
                await DelayForNextPollAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

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
                // Health contains the safe infrastructure diagnostic; polling continues.
            }

            if (claimed)
            {
                continue;
            }

            await DelayForNextPollAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DelayForNextPollAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_options.PollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> ProcessLeaseAsync(
        OutboxDeliveryLease initialLease,
        CancellationToken hostCancellationToken)
    {
        var leaseContext = new LeaseContext(initialLease);
        using var executionLifetime = CancellationTokenSource.CreateLinkedTokenSource(hostCancellationToken);
        var monitor = MonitorLeaseAsync(leaseContext, executionLifetime);
        SyncOutboxProcessingResult? result = null;
        var processingCancelled = false;
        try
        {
            try
            {
                result = await _processor.ProcessAsync(initialLease, executionLifetime.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (executionLifetime.IsCancellationRequested)
            {
                processingCancelled = true;
            }
            catch (Exception)
            {
                Volatile.Write(
                    ref _lastInfrastructureFailure,
                    "The sync outbox processor stopped unexpectedly; its claim was left unfinished.");
                result = SyncOutboxProcessingResult.LostLease();
            }
        }
        finally
        {
            executionLifetime.Cancel();
        }

        var monitorOutcome = await monitor.ConfigureAwait(false);
        if (monitorOutcome != LeaseMonitorOutcome.Stopped ||
            result?.Outcome == SyncOutboxProcessingOutcome.LeaseLost)
        {
            RecordLeaseLoss(monitorOutcome == LeaseMonitorOutcome.InfrastructureFailure);
            return false;
        }

        if (processingCancelled || hostCancellationToken.IsCancellationRequested || result is null)
        {
            return false;
        }

        var currentLease = leaseContext.Current;
        var now = _timeProvider.GetUtcNow();
        SyncPersistenceMutationStatus terminal;
        switch (result.Outcome)
        {
            case SyncOutboxProcessingOutcome.Completed:
                terminal = await _outbox.CompleteAsync(currentLease, now, CancellationToken.None)
                    .ConfigureAwait(false);
                if (terminal is SyncPersistenceMutationStatus.Applied or
                    SyncPersistenceMutationStatus.AlreadyApplied)
                {
                    _ = Interlocked.Increment(ref _completed);
                    return true;
                }

                break;

            case SyncOutboxProcessingOutcome.Retry:
                var deadLetter = currentLease.Event.AttemptCount >= _options.MaximumAttempts;
                var retryDelay = result.RetryDelay ?? _options.DefaultRetryDelay;
                terminal = await _outbox.FailAsync(
                    currentLease,
                    now,
                    now.Add(retryDelay),
                    result.ErrorCode ?? "sync.outbox.retry",
                    result.SafeErrorSummary ?? "The sync event will be retried.",
                    deadLetter,
                    CancellationToken.None).ConfigureAwait(false);
                if (terminal == SyncPersistenceMutationStatus.Applied)
                {
                    _ = deadLetter
                        ? Interlocked.Increment(ref _deadLettered)
                        : Interlocked.Increment(ref _retried);
                    return false;
                }

                break;

            case SyncOutboxProcessingOutcome.DeadLetter:
                terminal = await _outbox.FailAsync(
                    currentLease,
                    now,
                    now,
                    result.ErrorCode ?? "sync.outbox.rejected",
                    result.SafeErrorSummary ?? "The sync event was rejected safely.",
                    deadLetter: true,
                    CancellationToken.None).ConfigureAwait(false);
                if (terminal is SyncPersistenceMutationStatus.Applied or
                    SyncPersistenceMutationStatus.AlreadyApplied)
                {
                    _ = Interlocked.Increment(ref _deadLettered);
                    return false;
                }

                break;

            default:
                return false;
        }

        RecordLeaseLoss(infrastructureFailure: false);
        return false;
    }

    private async Task<LeaseMonitorOutcome> MonitorLeaseAsync(
        LeaseContext context,
        CancellationTokenSource executionLifetime)
    {
        try
        {
            while (!executionLifetime.IsCancellationRequested)
            {
                await Task.Delay(
                    _options.LeaseRenewalInterval,
                    _timeProvider,
                    executionLifetime.Token).ConfigureAwait(false);
                var renewedAtUtc = _timeProvider.GetUtcNow();
                if (renewedAtUtc >= context.Current.ExpiresAtUtc)
                {
                    executionLifetime.Cancel();
                    return LeaseMonitorOutcome.LeaseLost;
                }

                var renewal = await _outbox.RenewAsync(
                    context.Current,
                    renewedAtUtc,
                    _options.LeaseDuration,
                    executionLifetime.Token).ConfigureAwait(false);
                if (renewal.Status != SyncPersistenceMutationStatus.Applied || renewal.Value is null)
                {
                    executionLifetime.Cancel();
                    return LeaseMonitorOutcome.LeaseLost;
                }

                context.Current = renewal.Value;
            }

            return LeaseMonitorOutcome.Stopped;
        }
        catch (OperationCanceledException) when (executionLifetime.IsCancellationRequested)
        {
            return LeaseMonitorOutcome.Stopped;
        }
        catch (Exception)
        {
            executionLifetime.Cancel();
            return LeaseMonitorOutcome.InfrastructureFailure;
        }
    }

    private void RecordLeaseLoss(bool infrastructureFailure)
    {
        _ = Interlocked.Increment(ref _leaseLosses);
        Volatile.Write(
            ref _lastLeaseLoss,
            infrastructureFailure
                ? "The sync worker could not renew its outbox lease; provider work was cancelled."
                : "A sync outbox lease was superseded; the stale worker did not record completion.");
    }

    private sealed class LeaseContext(OutboxDeliveryLease lease)
    {
        private OutboxDeliveryLease _current = lease;

        public OutboxDeliveryLease Current
        {
            get => Volatile.Read(ref _current);
            set => Volatile.Write(ref _current, value);
        }
    }

    private enum LeaseMonitorOutcome
    {
        Stopped,
        LeaseLost,
        InfrastructureFailure,
    }
}
