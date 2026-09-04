using StorageHub.Agent.Scheduling;
using StorageHub.Domain.Identifiers;

namespace StorageHub.Agent.IntegrationTests;

public sealed class SchedulerAgentSubsystemTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunDueOnce_ignores_disabled_and_future_jobs()
    {
        var store = new FakeStore();
        store.Jobs.Add(CreateJob(enabled: false));
        store.Jobs.Add(CreateJob(nextOccurrenceUtc: Now.AddMinutes(1)));
        var runner = new RecordingRunner();
        await using var scheduler = CreateScheduler(store, runner);
        await scheduler.InitializeAsync(CancellationToken.None);

        await scheduler.RunDueOnceAsync();

        Assert.Empty(store.LeaseRequests);
        Assert.Empty(runner.Jobs);
    }

    [Fact]
    public async Task RunDueOnce_acquires_atomic_lease_runs_and_records_completion()
    {
        var store = new FakeStore();
        var job = CreateJob(nextOccurrenceUtc: Now);
        store.Jobs.Add(job);
        var runner = new RecordingRunner();
        await using var scheduler = CreateScheduler(store, runner);
        await scheduler.InitializeAsync(CancellationToken.None);

        await scheduler.RunDueOnceAsync();

        var leaseRequest = Assert.Single(store.LeaseRequests);
        Assert.Equal(job.JobId, leaseRequest.JobId);
        Assert.Equal(job.ProfileId, leaseRequest.ProfileId);
        Assert.Equal(Now, leaseRequest.ScheduledForUtc);
        Assert.Equal(Now.AddMinutes(1), leaseRequest.NextOccurrenceUtc);
        Assert.Single(runner.Jobs);
        var completion = Assert.Single(store.Completions);
        Assert.Equal(ScheduledSyncRunOutcome.Completed, completion.Result.Outcome);
        Assert.Equal(leaseRequest.JobId, completion.Lease.JobId);
    }

    [Fact]
    public async Task Expired_misfire_is_advanced_without_lease_or_execution()
    {
        var store = new FakeStore();
        store.Jobs.Add(CreateJob(
            nextOccurrenceUtc: Now.AddHours(-1),
            misfireGrace: TimeSpan.FromMinutes(5)));
        var runner = new RecordingRunner();
        await using var scheduler = CreateScheduler(store, runner);
        await scheduler.InitializeAsync(CancellationToken.None);

        await scheduler.RunDueOnceAsync();

        Assert.Empty(store.LeaseRequests);
        Assert.Empty(runner.Jobs);
        var update = Assert.Single(store.OccurrenceUpdates);
        Assert.Equal(ScheduledOccurrenceDispositionKind.ExpiredMisfireSkipped, update.Disposition);
        Assert.Equal(Now.AddMinutes(1), update.NextOccurrenceUtc);
    }

    [Fact]
    public async Task Misfires_within_grace_are_coalesced_to_one_run()
    {
        var store = new FakeStore();
        store.Jobs.Add(CreateJob(
            nextOccurrenceUtc: Now.AddMinutes(-4),
            misfireGrace: TimeSpan.FromMinutes(5)));
        var runner = new RecordingRunner();
        await using var scheduler = CreateScheduler(store, runner);
        await scheduler.InitializeAsync(CancellationToken.None);

        await scheduler.RunDueOnceAsync();

        Assert.Single(runner.Jobs);
        Assert.Single(store.LeaseRequests);
        Assert.Equal(Now.AddMinutes(1), store.LeaseRequests[0].NextOccurrenceUtc);
    }

    [Fact]
    public async Task Busy_profile_persists_exactly_one_queued_occurrence_when_enabled()
    {
        var store = new FakeStore
        {
            LeaseAcquisition = request => ScheduledSyncLeaseAcquisition.ProfileBusy(),
        };
        store.Jobs.Add(CreateJob(nextOccurrenceUtc: Now, queueOneWhileRunning: true));
        var runner = new RecordingRunner();
        await using var scheduler = CreateScheduler(store, runner);
        await scheduler.InitializeAsync(CancellationToken.None);

        await scheduler.RunDueOnceAsync();

        Assert.Empty(runner.Jobs);
        var update = Assert.Single(store.OccurrenceUpdates);
        Assert.Equal(ScheduledOccurrenceDispositionKind.OverlapQueued, update.Disposition);
        Assert.Equal(Now, update.ScheduledForUtc);
        Assert.Equal(Now.AddMinutes(1), update.NextOccurrenceUtc);
    }

    [Fact]
    public async Task Queued_occurrence_runs_even_after_normal_misfire_grace_expires()
    {
        var store = new FakeStore();
        store.Jobs.Add(CreateJob(
            nextOccurrenceUtc: Now.AddMinutes(1),
            queuedOccurrenceUtc: Now.AddHours(-2),
            misfireGrace: TimeSpan.FromMinutes(5)));
        var runner = new RecordingRunner();
        await using var scheduler = CreateScheduler(store, runner);
        await scheduler.InitializeAsync(CancellationToken.None);

        await scheduler.RunDueOnceAsync();

        Assert.Single(runner.Jobs);
        Assert.True(Assert.Single(store.LeaseRequests).IsQueuedOccurrence);
    }

    [Fact]
    public async Task Busy_profile_skips_occurrence_when_queue_one_is_disabled()
    {
        var store = new FakeStore
        {
            LeaseAcquisition = request => ScheduledSyncLeaseAcquisition.ProfileBusy(),
        };
        store.Jobs.Add(CreateJob(nextOccurrenceUtc: Now, queueOneWhileRunning: false));
        var runner = new RecordingRunner();
        await using var scheduler = CreateScheduler(store, runner);
        await scheduler.InitializeAsync(CancellationToken.None);

        await scheduler.RunDueOnceAsync();

        Assert.Empty(runner.Jobs);
        Assert.Equal(
            ScheduledOccurrenceDispositionKind.OverlapSkipped,
            Assert.Single(store.OccurrenceUpdates).Disposition);
    }

    [Fact]
    public async Task Local_profile_guard_prevents_two_jobs_for_same_profile_overlapping()
    {
        var profileId = SyncProfileId.New();
        var store = new FakeStore();
        store.Jobs.Add(CreateJob(profileId: profileId, queueOneWhileRunning: false));
        store.Jobs.Add(CreateJob(profileId: profileId, queueOneWhileRunning: false));
        var runner = new GatedRunner(expectedStarts: 1);
        await using var scheduler = CreateScheduler(store, runner, maximumConcurrency: 2);
        await scheduler.InitializeAsync(CancellationToken.None);

        var polling = scheduler.RunDueOnceAsync().AsTask();
        await runner.ExpectedStartsReached.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, runner.CurrentConcurrency);
        Assert.Single(store.OccurrenceUpdates);
        runner.Release();
        await polling;
        Assert.Equal(1, runner.MaximumObservedConcurrency);
        Assert.Single(runner.Jobs);
    }

    [Fact]
    public async Task Global_execution_concurrency_is_bounded()
    {
        var store = new FakeStore();
        for (var index = 0; index < 4; index++)
        {
            store.Jobs.Add(CreateJob());
        }

        var runner = new GatedRunner(expectedStarts: 2);
        await using var scheduler = CreateScheduler(store, runner, maximumConcurrency: 2);
        await scheduler.InitializeAsync(CancellationToken.None);

        var polling = scheduler.RunDueOnceAsync().AsTask();
        await runner.ExpectedStartsReached.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, runner.CurrentConcurrency);
        Assert.Equal(2, runner.MaximumObservedConcurrency);
        runner.Release();
        await polling;
        Assert.Equal(4, runner.Jobs.Count);
        Assert.Equal(2, runner.MaximumObservedConcurrency);
    }

    [Fact]
    public async Task Worker_waiting_behind_prior_job_samples_fresh_lease_time()
    {
        var store = new FakeStore();
        store.Jobs.Add(CreateJob());
        store.Jobs.Add(CreateJob());
        var runner = new GatedRunner(expectedStarts: 1);
        var timeProvider = new MutableTimeProvider(Now);
        await using var scheduler = CreateScheduler(
            store,
            runner,
            maximumConcurrency: 1,
            timeProvider: timeProvider);
        await scheduler.InitializeAsync(CancellationToken.None);

        var polling = scheduler.RunDueOnceAsync().AsTask();
        await runner.ExpectedStartsReached.WaitAsync(TimeSpan.FromSeconds(5));
        timeProvider.Advance(TimeSpan.FromMinutes(31));
        runner.Release();
        await polling.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, store.LeaseRequests.Count);
        Assert.Equal(Now, store.LeaseRequests[0].ObservedAtUtc);
        Assert.Equal(Now.AddMinutes(31), store.LeaseRequests[1].ObservedAtUtc);
        Assert.True(
            store.Completions[1].Lease.ExpiresAtUtc > store.LeaseRequests[1].ObservedAtUtc);
    }

    [Fact]
    public async Task Runner_failure_and_unexpected_exception_are_recorded_without_escaping_poll()
    {
        var store = new FakeStore();
        store.Jobs.Add(CreateJob());
        store.Jobs.Add(CreateJob());
        var invocation = 0;
        var runner = new RecordingRunner((_, _) =>
        {
            invocation++;
            return invocation == 1
                ? ValueTask.FromResult(ScheduledSyncJobRunResult.Failed(
                    "sync.failed",
                    "The sync run failed."))
                : ValueTask.FromException<ScheduledSyncJobRunResult>(
                    new InvalidOperationException("secret provider detail"));
        });
        await using var scheduler = CreateScheduler(store, runner, maximumConcurrency: 1);
        await scheduler.InitializeAsync(CancellationToken.None);

        await scheduler.RunDueOnceAsync();

        Assert.Equal(2, store.Completions.Count);
        Assert.All(store.Completions, completion =>
            Assert.Equal(ScheduledSyncRunOutcome.Failed, completion.Result.Outcome));
        Assert.Contains(store.Completions, completion => completion.Result.Code == "sync.failed");
        var unexpected = Assert.Single(store.Completions, completion =>
            completion.Result.Code == "scheduler.runner.unexpected");
        Assert.DoesNotContain("secret", unexpected.Result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancellation_stops_runner_and_is_durably_recorded()
    {
        var store = new FakeStore();
        store.Jobs.Add(CreateJob());
        var runner = new CancellableRunner();
        await using var scheduler = CreateScheduler(store, runner);
        await scheduler.InitializeAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();

        var polling = scheduler.RunDueOnceAsync(cancellation.Token).AsTask();
        await runner.Started.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await polling;

        Assert.Equal(
            ScheduledSyncRunOutcome.Cancelled,
            Assert.Single(store.Completions).Result.Outcome);
    }

    [Fact]
    public async Task Lost_lease_renewal_cancels_runner_without_stale_completion()
    {
        var store = new FakeStore { RenewalAccepted = false };
        store.Jobs.Add(CreateJob());
        var runner = new CancellableRunner();
        var delay = new ManuallyReleasedDelay();
        await using var scheduler = CreateScheduler(store, runner, delay: delay);
        await scheduler.InitializeAsync(CancellationToken.None);

        var polling = scheduler.RunDueOnceAsync().AsTask();
        await runner.Started.WaitAsync(TimeSpan.FromSeconds(5));
        await delay.DelayRequested.WaitAsync(TimeSpan.FromSeconds(5));
        delay.Release();
        await polling;

        Assert.Single(store.LeaseRenewals);
        Assert.Empty(store.Completions);
        var health = await scheduler.CheckHealthAsync(CancellationToken.None);
        Assert.Equal(SubsystemHealthLevel.Degraded, health.Level);
        Assert.Contains("lost its durable lease", health.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Renewal_store_timeout_cancels_runner_and_degrades_dispatch()
    {
        var releaseRenewal = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeStore
        {
            RenewalHandler = async (_, _) => await releaseRenewal.Task
        };
        store.Jobs.Add(CreateJob());
        var runner = new CancellableRunner();
        var delay = new ManuallyReleasedDelay();
        await using var scheduler = CreateScheduler(
            store,
            runner,
            delay: delay,
            storeWriteTimeout: TimeSpan.FromMilliseconds(50));
        await scheduler.InitializeAsync(CancellationToken.None);

        var polling = scheduler.RunDueOnceAsync().AsTask();
        await runner.Started.WaitAsync(TimeSpan.FromSeconds(5));
        await delay.DelayRequested.WaitAsync(TimeSpan.FromSeconds(5));
        delay.Release();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => polling.WaitAsync(TimeSpan.FromSeconds(5)));
        releaseRenewal.TrySetResult(true);
        var health = await scheduler.CheckHealthAsync(CancellationToken.None);
        Assert.Equal(SubsystemHealthLevel.Degraded, health.Level);
        Assert.Empty(store.Completions);
    }

    [Fact]
    public async Task Store_cannot_substitute_a_lease_for_another_profile()
    {
        var store = new FakeStore
        {
            LeaseAcquisition = request => ScheduledSyncLeaseAcquisition.Acquired(
                new ScheduledSyncJobLease(
                    Guid.NewGuid(),
                    request.JobId,
                    SyncProfileId.New(),
                    request.ScheduledForUtc,
                    request.ObservedAtUtc,
                    request.ObservedAtUtc.Add(request.LeaseDuration),
                    fencingToken: 1)),
        };
        store.Jobs.Add(CreateJob());
        var runner = new RecordingRunner();
        await using var scheduler = CreateScheduler(store, runner);
        await scheduler.InitializeAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scheduler.RunDueOnceAsync().AsTask());

        Assert.Empty(runner.Jobs);
        Assert.Equal(
            SubsystemHealthLevel.Degraded,
            (await scheduler.CheckHealthAsync(CancellationToken.None)).Level);
    }

    [Fact]
    public async Task StopAsync_cancels_and_awaits_background_execution()
    {
        var store = new FakeStore();
        store.Jobs.Add(CreateJob(nextOccurrenceUtc: DateTimeOffset.UtcNow.AddMinutes(-1)));
        var runner = new CancellableRunner();
        await using var scheduler = new SchedulerAgentSubsystem(
            store,
            runner,
            new SchedulerAgentOptions
            {
                PollInterval = TimeSpan.FromHours(1),
                MaximumConcurrency = 1,
            });
        await scheduler.InitializeAsync(CancellationToken.None);
        await scheduler.StartAsync(CancellationToken.None);
        await runner.Started.WaitAsync(TimeSpan.FromSeconds(5));

        await scheduler.StopAsync(CancellationToken.None);

        Assert.Equal(
            ScheduledSyncRunOutcome.Cancelled,
            Assert.Single(store.Completions).Result.Outcome);
        Assert.Equal(0, scheduler.ActiveExecutionCount);
    }

    [Fact]
    public async Task Infrastructure_failure_degrades_health_and_a_successful_poll_recovers_it()
    {
        var store = new FakeStore { PollException = new IOException("database unavailable") };
        var runner = new RecordingRunner();
        await using var scheduler = CreateScheduler(store, runner);
        await scheduler.InitializeAsync(CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() => scheduler.RunDueOnceAsync().AsTask());
        var degraded = await scheduler.CheckHealthAsync(CancellationToken.None);
        store.PollException = null;
        await scheduler.RunDueOnceAsync();
        var recovered = await scheduler.CheckHealthAsync(CancellationToken.None);

        Assert.Equal(SubsystemHealthLevel.Degraded, degraded.Level);
        Assert.DoesNotContain("database unavailable", degraded.Message, StringComparison.Ordinal);
        Assert.Equal(SubsystemHealthLevel.Healthy, recovered.Level);
    }

    private static SchedulerAgentSubsystem CreateScheduler(
        FakeStore store,
        IScheduledSyncJobRunner runner,
        int maximumConcurrency = 2,
        ISchedulerDelay? delay = null,
        TimeProvider? timeProvider = null,
        TimeSpan? storeWriteTimeout = null) =>
        new(
            store,
            runner,
            new SchedulerAgentOptions
            {
                PollInterval = TimeSpan.FromMinutes(1),
                MaximumConcurrency = maximumConcurrency,
                LeaseDuration = TimeSpan.FromMinutes(30),
                LeaseRenewalInterval = TimeSpan.FromMinutes(5),
                StoreWriteTimeout = storeWriteTimeout ?? TimeSpan.FromSeconds(5),
            },
            timeProvider ?? new FixedTimeProvider(Now),
            delay);

    private static ScheduledSyncJobSnapshot CreateJob(
        SyncProfileId? profileId = null,
        bool enabled = true,
        bool queueOneWhileRunning = true,
        DateTimeOffset? nextOccurrenceUtc = null,
        DateTimeOffset? queuedOccurrenceUtc = null,
        TimeSpan? misfireGrace = null)
    {
        Assert.True(CronScheduleDefinition.TryCreate(
            "* * * * *",
            "UTC",
            out var schedule,
            out _,
            misfireGrace ?? TimeSpan.FromHours(1)));
        return new ScheduledSyncJobSnapshot(
            ScheduledSyncJobId.New(),
            profileId ?? SyncProfileId.New(),
            schedule!,
            enabled,
            queueOneWhileRunning,
            nextOccurrenceUtc ?? Now,
            queuedOccurrenceUtc,
            revision: 3);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private long _utcTicks = utcNow.UtcTicks;

        public override DateTimeOffset GetUtcNow() =>
            new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

        public void Advance(TimeSpan duration) =>
            _ = Interlocked.Add(ref _utcTicks, duration.Ticks);
    }

    private sealed class FakeStore : IScheduledSyncJobStore
    {
        private readonly object _sync = new();
        private long _fencingToken;

        public List<ScheduledSyncJobSnapshot> Jobs { get; } = [];
        public List<ScheduledSyncLeaseRequest> LeaseRequests { get; } = [];
        public List<ScheduledOccurrenceDisposition> OccurrenceUpdates { get; } = [];
        public List<ScheduledSyncJobCompletion> Completions { get; } = [];
        public List<ScheduledSyncLeaseRenewal> LeaseRenewals { get; } = [];
        public Func<ScheduledSyncLeaseRequest, ScheduledSyncLeaseAcquisition>? LeaseAcquisition { get; set; }
        public bool RenewalAccepted { get; set; } = true;
        public Func<ScheduledSyncLeaseRenewal, CancellationToken, ValueTask<bool>>? RenewalHandler { get; set; }
        public Exception? PollException { get; set; }

        public ValueTask<IReadOnlyList<ScheduledSyncJobSnapshot>> GetJobsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PollException is not null)
            {
                return ValueTask.FromException<IReadOnlyList<ScheduledSyncJobSnapshot>>(PollException);
            }

            return ValueTask.FromResult<IReadOnlyList<ScheduledSyncJobSnapshot>>(Jobs.ToArray());
        }

        public ValueTask<ScheduledSyncLeaseAcquisition> TryAcquireLeaseAsync(
            ScheduledSyncLeaseRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                LeaseRequests.Add(request);
            }

            var result = LeaseAcquisition?.Invoke(request) ?? ScheduledSyncLeaseAcquisition.Acquired(
                new ScheduledSyncJobLease(
                    Guid.NewGuid(),
                    request.JobId,
                    request.ProfileId,
                    request.ScheduledForUtc,
                    request.ObservedAtUtc,
                    request.ObservedAtUtc.Add(request.LeaseDuration),
                    Interlocked.Increment(ref _fencingToken)));
            return ValueTask.FromResult(result);
        }

        public ValueTask<bool> TryRecordOccurrenceDispositionAsync(
            ScheduledOccurrenceDisposition disposition,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                OccurrenceUpdates.Add(disposition);
            }

            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> TryRenewLeaseAsync(
            ScheduledSyncLeaseRenewal renewal,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                LeaseRenewals.Add(renewal);
            }

            return RenewalHandler?.Invoke(renewal, cancellationToken) ??
                ValueTask.FromResult(RenewalAccepted);
        }

        public ValueTask RecordCompletionAsync(
            ScheduledSyncJobCompletion completion,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                Completions.Add(completion);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingRunner(
        Func<ScheduledSyncJobExecution, CancellationToken, ValueTask<ScheduledSyncJobRunResult>>? run = null)
        : IScheduledSyncJobRunner
    {
        public List<ScheduledSyncJobExecution> Jobs { get; } = [];

        public ValueTask<ScheduledSyncJobRunResult> RunAsync(
            ScheduledSyncJobExecution execution,
            CancellationToken cancellationToken)
        {
            Jobs.Add(execution);
            return run?.Invoke(execution, cancellationToken) ??
                ValueTask.FromResult(ScheduledSyncJobRunResult.Completed());
        }
    }

    private sealed class GatedRunner(int expectedStarts) : IScheduledSyncJobRunner
    {
        private readonly TaskCompletionSource _expectedStartsReached = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _currentConcurrency;
        private int _maximumObservedConcurrency;
        private int _startedCount;

        public List<ScheduledSyncJobExecution> Jobs { get; } = [];
        public Task ExpectedStartsReached => _expectedStartsReached.Task;
        public int CurrentConcurrency => Volatile.Read(ref _currentConcurrency);
        public int MaximumObservedConcurrency => Volatile.Read(ref _maximumObservedConcurrency);

        public async ValueTask<ScheduledSyncJobRunResult> RunAsync(
            ScheduledSyncJobExecution execution,
            CancellationToken cancellationToken)
        {
            lock (Jobs)
            {
                Jobs.Add(execution);
            }

            var current = Interlocked.Increment(ref _currentConcurrency);
            UpdateMaximum(current);
            if (Interlocked.Increment(ref _startedCount) >= expectedStarts)
            {
                _expectedStartsReached.TrySetResult();
            }

            try
            {
                await _release.Task.WaitAsync(cancellationToken);
                return ScheduledSyncJobRunResult.Completed();
            }
            finally
            {
                _ = Interlocked.Decrement(ref _currentConcurrency);
            }
        }

        public void Release() => _release.TrySetResult();

        private void UpdateMaximum(int value)
        {
            var currentMaximum = Volatile.Read(ref _maximumObservedConcurrency);
            while (value > currentMaximum)
            {
                var prior = Interlocked.CompareExchange(
                    ref _maximumObservedConcurrency,
                    value,
                    currentMaximum);
                if (prior == currentMaximum)
                {
                    return;
                }

                currentMaximum = prior;
            }
        }
    }

    private sealed class CancellableRunner : IScheduledSyncJobRunner
    {
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public async ValueTask<ScheduledSyncJobRunResult> RunAsync(
            ScheduledSyncJobExecution execution,
            CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ScheduledSyncJobRunResult.Completed();
        }
    }

    private sealed class ManuallyReleasedDelay : ISchedulerDelay
    {
        private readonly TaskCompletionSource _delayRequested = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DelayRequested => _delayRequested.Task;

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            _delayRequested.TrySetResult();
            return new ValueTask(_release.Task.WaitAsync(cancellationToken));
        }

        public void Release() => _release.TrySetResult();
    }
}
