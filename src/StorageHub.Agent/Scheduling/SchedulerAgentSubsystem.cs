using System.Collections.Concurrent;
using StorageHub.Domain.Identifiers;

namespace StorageHub.Agent.Scheduling;

/// <summary>
/// Polls durable schedule snapshots and dispatches fenced, non-overlapping sync runs. SQLite or
/// another persistence implementation owns compare-and-swap revisions and cross-process leases.
/// </summary>
public sealed class SchedulerAgentSubsystem : IAgentSubsystem, IAsyncDisposable
{
    private readonly IScheduledSyncJobStore _store;
    private readonly IScheduledSyncJobRunner _runner;
    private readonly SchedulerAgentOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ISchedulerDelay _delay;
    private readonly SemaphoreSlim _executionSlots;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly ConcurrentDictionary<SyncProfileId, byte> _activeProfiles = new();
    private CancellationTokenSource? _lifetime;
    private Task? _pollLoop;
    private string? _lastInfrastructureFailure;
    private string? _lastLeaseLoss;
    private long _completedRuns;
    private long _failedRuns;
    private long _cancelledRuns;
    private long _leaseLosses;
    private long _queuedOccurrences;
    private long _skippedOccurrences;
    private int _activeExecutionCount;
    private bool _initialized;
    private bool _started;
    private bool _disposed;

    public SchedulerAgentSubsystem(
        IScheduledSyncJobStore store,
        IScheduledSyncJobRunner runner,
        SchedulerAgentOptions? options = null,
        TimeProvider? timeProvider = null,
        ISchedulerDelay? delay = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _options = options ?? new SchedulerAgentOptions();
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _delay = delay ?? new TimeProviderSchedulerDelay(_timeProvider);
        _executionSlots = new SemaphoreSlim(
            _options.MaximumConcurrency,
            _options.MaximumConcurrency);
    }

    public string Name => "scheduler";

    public bool CanRunInRecoveryMode => false;

    public int ActiveExecutionCount => Volatile.Read(ref _activeExecutionCount);

    public Task<SubsystemInitializationResult> InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
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
                throw new InvalidOperationException("The scheduler must be initialized before it is started.");
            }

            if (_started)
            {
                throw new InvalidOperationException("The scheduler is already started.");
            }

            _lifetime = new CancellationTokenSource();
            _started = true;
            _pollLoop = Task.Run(
                () => PollLoopAsync(_lifetime.Token),
                CancellationToken.None);
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
            if (_pollLoop is not null)
            {
                await _pollLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            _started = false;
            _pollLoop = null;
            _lifetime.Dispose();
            _lifetime = null;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Executes one deterministic scheduler poll. It awaits every job selected by this snapshot;
    /// callers can use it for tests, manual run-now checks, and controlled host orchestration.
    /// </summary>
    public async ValueTask RunDueOnceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            throw new InvalidOperationException("The scheduler must be initialized before polling.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observedAtUtc = _timeProvider.GetUtcNow();
            var snapshots = await _store.GetJobsAsync(cancellationToken).ConfigureAwait(false) ??
                throw new InvalidOperationException("The scheduled job store returned a null snapshot collection.");
            var snapshotArray = snapshots
                .Select(job => job ?? throw new InvalidOperationException(
                    "The scheduled job store returned a null job snapshot."))
                .ToArray();
            if (snapshotArray.Select(job => job.JobId).Distinct().Count() != snapshotArray.Length)
            {
                throw new InvalidOperationException(
                    "The scheduled job store returned duplicate job snapshots.");
            }

            var dueJobs = snapshotArray
                .Where(job => IsDue(job, observedAtUtc))
                .OrderBy(job => job.DueOccurrenceUtc)
                .ThenBy(job => job.JobId.Value)
                .ToArray();
            await ProcessDueJobsAsync(
                dueJobs,
                cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _lastInfrastructureFailure, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            Volatile.Write(
                ref _lastInfrastructureFailure,
                "The durable scheduler store or dispatch loop failed.");
            throw;
        }
    }

    public Task<SubsystemHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_initialized)
        {
            return Task.FromResult(SubsystemHealth.Unhealthy("Scheduler is not initialized."));
        }

        var infrastructureFailure = Volatile.Read(ref _lastInfrastructureFailure);
        if (infrastructureFailure is not null)
        {
            return Task.FromResult(SubsystemHealth.Degraded(infrastructureFailure));
        }

        var leaseLoss = Volatile.Read(ref _lastLeaseLoss);
        if (leaseLoss is not null)
        {
            return Task.FromResult(SubsystemHealth.Degraded(leaseLoss));
        }

        var active = ActiveExecutionCount;
        var message = string.Create(
            provider: null,
            $"Scheduler healthy; active={active}, completed={Interlocked.Read(ref _completedRuns)}, failed={Interlocked.Read(ref _failedRuns)}, cancelled={Interlocked.Read(ref _cancelledRuns)}, lease-lost={Interlocked.Read(ref _leaseLosses)}, queued={Interlocked.Read(ref _queuedOccurrences)}, skipped={Interlocked.Read(ref _skippedOccurrences)}.");
        return Task.FromResult(SubsystemHealth.Healthy(message));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _disposed = true;
        _executionSlots.Dispose();
        _lifecycleGate.Dispose();
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunDueOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // RunDueOnceAsync already records a safe health diagnostic. The next tick retries.
            }

            try
            {
                await _delay.DelayAsync(
                    _options.PollInterval,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task ProcessJobAsync(
        ScheduledSyncJobSnapshot job,
        CancellationToken cancellationToken)
    {
        await _executionSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var observedAtUtc = _timeProvider.GetUtcNow();
            var scheduledForUtc = job.DueOccurrenceUtc;
            if (!job.Enabled || scheduledForUtc is null || scheduledForUtc > observedAtUtc)
            {
                return;
            }

            var queuedOccurrence = job.QueuedOccurrenceUtc.HasValue;
            var nextOccurrenceUtc = ScheduleCalculator.GetNextOccurrence(
                job.Schedule,
                observedAtUtc,
                inclusive: false);
            if (!queuedOccurrence &&
                ScheduleCalculator.EvaluateMisfire(job.Schedule, scheduledForUtc.Value, observedAtUtc) ==
                MisfireAction.SkipExpiredOccurrence)
            {
                await RecordDispositionAsync(
                    job,
                    scheduledForUtc.Value,
                    observedAtUtc,
                    nextOccurrenceUtc,
                    ScheduledOccurrenceDispositionKind.ExpiredMisfireSkipped,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!_activeProfiles.TryAdd(job.ProfileId, 0))
            {
                await HandleOverlapAsync(
                    job,
                    scheduledForUtc.Value,
                    queuedOccurrence,
                    observedAtUtc,
                    nextOccurrenceUtc,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            try
            {
                // A worker may have waited behind a long-running job. Lease time must be sampled
                // at acquisition, never inherited from the poll snapshot, or the new lease could
                // already be expired by the time execution starts.
                var leaseObservedAtUtc = _timeProvider.GetUtcNow();
                nextOccurrenceUtc = ScheduleCalculator.GetNextOccurrence(
                    job.Schedule,
                    leaseObservedAtUtc,
                    inclusive: false);
                var leaseRequest = new ScheduledSyncLeaseRequest(
                    job.JobId,
                    job.ProfileId,
                    job.Revision,
                    scheduledForUtc.Value,
                    queuedOccurrence,
                    leaseObservedAtUtc,
                    nextOccurrenceUtc,
                    _options.LeaseDuration);
                var acquisition = await _store.TryAcquireLeaseAsync(
                    leaseRequest,
                    cancellationToken).ConfigureAwait(false);
                if (acquisition.Status == ScheduledSyncLeaseAcquisitionStatus.ProfileBusy)
                {
                    await HandleOverlapAsync(
                        job,
                        scheduledForUtc.Value,
                        queuedOccurrence,
                        observedAtUtc,
                        nextOccurrenceUtc,
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (acquisition.Status != ScheduledSyncLeaseAcquisitionStatus.Acquired)
                {
                    return;
                }

                var lease = acquisition.Lease ?? throw new InvalidOperationException(
                    "The scheduler store reported an acquired lease without returning the lease.");
                ValidateLease(leaseRequest, lease);
                await RunLeasedJobAsync(job, lease, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _ = _activeProfiles.TryRemove(job.ProfileId, out _);
            }
        }
        finally
        {
            _executionSlots.Release();
        }
    }

    private async Task ProcessDueJobsAsync(
        ScheduledSyncJobSnapshot[] dueJobs,
        CancellationToken cancellationToken)
    {
        if (dueJobs.Length == 0)
        {
            return;
        }

        var cursor = -1;
        var workerCount = Math.Min(_options.MaximumConcurrency, dueJobs.Length);
        var workers = new Task[workerCount];
        for (var workerIndex = 0; workerIndex < workerCount; workerIndex++)
        {
            workers[workerIndex] = ProcessWorkerAsync();
        }

        await Task.WhenAll(workers).ConfigureAwait(false);

        async Task ProcessWorkerAsync()
        {
            while (true)
            {
                var jobIndex = Interlocked.Increment(ref cursor);
                if (jobIndex >= dueJobs.Length)
                {
                    return;
                }

                await ProcessJobAsync(
                    dueJobs[jobIndex],
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleOverlapAsync(
        ScheduledSyncJobSnapshot job,
        DateTimeOffset scheduledForUtc,
        bool queuedOccurrence,
        DateTimeOffset observedAtUtc,
        DateTimeOffset? nextOccurrenceUtc,
        CancellationToken cancellationToken)
    {
        if (queuedOccurrence)
        {
            return;
        }

        var disposition = job.QueueOneWhileRunning
            ? ScheduledOccurrenceDispositionKind.OverlapQueued
            : ScheduledOccurrenceDispositionKind.OverlapSkipped;
        await RecordDispositionAsync(
            job,
            scheduledForUtc,
            observedAtUtc,
            nextOccurrenceUtc,
            disposition,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordDispositionAsync(
        ScheduledSyncJobSnapshot job,
        DateTimeOffset scheduledForUtc,
        DateTimeOffset observedAtUtc,
        DateTimeOffset? nextOccurrenceUtc,
        ScheduledOccurrenceDispositionKind disposition,
        CancellationToken cancellationToken)
    {
        var update = new ScheduledOccurrenceDisposition(
            job.JobId,
            job.ProfileId,
            job.Revision,
            scheduledForUtc,
            observedAtUtc,
            nextOccurrenceUtc,
            disposition);
        if (!await _store.TryRecordOccurrenceDispositionAsync(
                update,
                cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (disposition == ScheduledOccurrenceDispositionKind.OverlapQueued)
        {
            _ = Interlocked.Increment(ref _queuedOccurrences);
        }
        else
        {
            _ = Interlocked.Increment(ref _skippedOccurrences);
        }
    }

    private async Task RunLeasedJobAsync(
        ScheduledSyncJobSnapshot job,
        ScheduledSyncJobLease lease,
        CancellationToken cancellationToken)
    {
        var startedAtUtc = _timeProvider.GetUtcNow();
        _ = Interlocked.Increment(ref _activeExecutionCount);
        using var runnerLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leaseMonitor = MonitorLeaseAsync(
            lease,
            runnerLifetime);
        var leaseMonitorOutcome = LeaseMonitorOutcome.Stopped;
        ScheduledSyncJobRunResult result;
        try
        {
            result = await _runner.RunAsync(
                new ScheduledSyncJobExecution(job, lease),
                runnerLifetime.Token).ConfigureAwait(false) ??
                ScheduledSyncJobRunResult.Failed(
                    "scheduler.runner.invalid_result",
                    "The scheduled sync runner returned no result.");
        }
        catch (OperationCanceledException) when (runnerLifetime.IsCancellationRequested)
        {
            result = ScheduledSyncJobRunResult.Cancelled();
        }
        catch (Exception)
        {
            result = ScheduledSyncJobRunResult.Failed(
                "scheduler.runner.unexpected",
                "The scheduled sync runner failed unexpectedly.");
        }
        finally
        {
            runnerLifetime.Cancel();
            leaseMonitorOutcome = await leaseMonitor.ConfigureAwait(false);
            _ = Interlocked.Decrement(ref _activeExecutionCount);
        }

        if (leaseMonitorOutcome == LeaseMonitorOutcome.LeaseLost)
        {
            _ = Interlocked.Increment(ref _failedRuns);
            _ = Interlocked.Increment(ref _leaseLosses);
            Volatile.Write(
                ref _lastLeaseLoss,
                "A scheduled sync execution lost its durable lease; only the current lease owner may record completion.");
            return;
        }

        if (leaseMonitorOutcome == LeaseMonitorOutcome.InfrastructureFailure)
        {
            _ = Interlocked.Increment(ref _failedRuns);
            throw new InvalidOperationException(
                "The scheduler could not renew its durable execution lease.");
        }

        var completion = new ScheduledSyncJobCompletion(
            lease,
            result,
            startedAtUtc,
            _timeProvider.GetUtcNow());
        using var completionTimeout = new CancellationTokenSource(_options.StoreWriteTimeout);
        await _store.RecordCompletionAsync(completion, completionTimeout.Token).ConfigureAwait(false);
        Volatile.Write(ref _lastLeaseLoss, null);
        switch (result.Outcome)
        {
            case ScheduledSyncRunOutcome.Completed:
                _ = Interlocked.Increment(ref _completedRuns);
                break;
            case ScheduledSyncRunOutcome.Failed:
                _ = Interlocked.Increment(ref _failedRuns);
                break;
            case ScheduledSyncRunOutcome.Cancelled:
                _ = Interlocked.Increment(ref _cancelledRuns);
                break;
            default:
                throw new InvalidOperationException("The scheduled sync runner returned an invalid outcome.");
        }
    }

    private static bool IsDue(ScheduledSyncJobSnapshot job, DateTimeOffset observedAtUtc) =>
        job.Enabled && job.DueOccurrenceUtc is { } due && due <= observedAtUtc;

    private void ValidateLease(
        ScheduledSyncLeaseRequest request,
        ScheduledSyncJobLease lease)
    {
        if (lease.JobId != request.JobId ||
            lease.ProfileId != request.ProfileId ||
            lease.ScheduledForUtc != request.ScheduledForUtc ||
            lease.AcquiredAtUtc < request.ObservedAtUtc ||
            lease.ExpiresAtUtc <= _timeProvider.GetUtcNow())
        {
            throw new InvalidOperationException(
                "The scheduler store returned a lease that does not match the requested occurrence.");
        }
    }

    private async Task<LeaseMonitorOutcome> MonitorLeaseAsync(
        ScheduledSyncJobLease lease,
        CancellationTokenSource runnerLifetime)
    {
        var currentExpiryUtc = lease.ExpiresAtUtc;
        try
        {
            while (!runnerLifetime.IsCancellationRequested)
            {
                await _delay.DelayAsync(
                    _options.LeaseRenewalInterval,
                    runnerLifetime.Token).ConfigureAwait(false);
                var renewedAtUtc = _timeProvider.GetUtcNow();
                var remainingLease = currentExpiryUtc - renewedAtUtc;
                if (remainingLease <= TimeSpan.Zero)
                {
                    runnerLifetime.Cancel();
                    return LeaseMonitorOutcome.LeaseLost;
                }

                var renewal = new ScheduledSyncLeaseRenewal(
                    lease,
                    renewedAtUtc,
                    renewedAtUtc.Add(_options.LeaseDuration));
                var attemptTimeout = remainingLease < _options.StoreWriteTimeout
                    ? remainingLease
                    : _options.StoreWriteTimeout;
                using var renewalLifetime = CancellationTokenSource.CreateLinkedTokenSource(
                    runnerLifetime.Token);
                renewalLifetime.CancelAfter(attemptTimeout);
                var renewalTask = _store.TryRenewLeaseAsync(
                        renewal,
                        renewalLifetime.Token)
                    .AsTask();
                bool renewed;
                try
                {
                    renewed = await renewalTask
                        .WaitAsync(attemptTimeout, runnerLifetime.Token)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    renewalLifetime.Cancel();
                    ObserveAbandonedStoreWrite(renewalTask);
                    runnerLifetime.Cancel();
                    return LeaseMonitorOutcome.InfrastructureFailure;
                }
                catch (OperationCanceledException) when (
                    !runnerLifetime.IsCancellationRequested && renewalLifetime.IsCancellationRequested)
                {
                    ObserveAbandonedStoreWrite(renewalTask);
                    runnerLifetime.Cancel();
                    return LeaseMonitorOutcome.InfrastructureFailure;
                }

                if (renewed)
                {
                    currentExpiryUtc = renewal.ExpiresAtUtc;
                    continue;
                }

                runnerLifetime.Cancel();
                return LeaseMonitorOutcome.LeaseLost;
            }

            return LeaseMonitorOutcome.Stopped;
        }
        catch (OperationCanceledException) when (runnerLifetime.IsCancellationRequested)
        {
            return LeaseMonitorOutcome.Stopped;
        }
        catch (Exception)
        {
            runnerLifetime.Cancel();
            return LeaseMonitorOutcome.InfrastructureFailure;
        }
    }

    private static void ObserveAbandonedStoreWrite(Task storeWrite)
    {
        _ = storeWrite.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private enum LeaseMonitorOutcome
    {
        Stopped = 0,
        LeaseLost = 1,
        InfrastructureFailure = 2,
    }
}
