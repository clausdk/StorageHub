using StorageHub.Application;

namespace StorageHub.Agent.IntegrationTests;

public sealed class AgentRuntimeCoordinatorTests
{
    [Fact]
    public async Task StartsInOrderAndStopsInReverseOrder()
    {
        var calls = new List<string>();
        var first = new RecordingSubsystem("database", calls);
        var second = new RecordingSubsystem("ipc", calls);
        await using var coordinator = new AgentRuntimeCoordinator([first, second]);

        await coordinator.InitializeAsync();
        await coordinator.StartAsync();
        await coordinator.StopAsync();

        Assert.Equal(
            ["database.initialize", "ipc.initialize", "database.start", "ipc.start", "ipc.stop", "database.stop"],
            calls);
        Assert.Equal(ApplicationOperationalState.Stopped, coordinator.State);
    }

    [Fact]
    public async Task RecoveryModeStartsOnlyRecoveryCapableSubsystems()
    {
        var calls = new List<string>();
        var database = new RecordingSubsystem("database", calls)
        {
            InitializationResult = SubsystemInitializationResult.RecoveryOnly("integrity check failed")
        };
        var ipc = new RecordingSubsystem("ipc", calls) { CanRunInRecoveryMode = true };
        var scheduler = new RecordingSubsystem("scheduler", calls);
        await using var coordinator = new AgentRuntimeCoordinator([database, ipc, scheduler]);

        await coordinator.InitializeAsync();
        await coordinator.StartAsync();

        Assert.Equal(ApplicationOperationalState.RecoveryOnly, coordinator.State);
        Assert.Contains("integrity check failed", coordinator.HealthMessage, StringComparison.Ordinal);
        Assert.Contains("ipc.initialize", calls);
        Assert.DoesNotContain("scheduler.initialize", calls);
        Assert.DoesNotContain("scheduler.start", calls);
    }

    [Fact]
    public void DuplicateSubsystemNamesAreRejected()
    {
        var calls = new List<string>();

        Assert.Throws<ArgumentException>(() =>
            new AgentRuntimeCoordinator([
                new RecordingSubsystem("queue", calls),
                new RecordingSubsystem("QUEUE", calls)
            ]));
    }

    [Fact]
    public async Task StartFailureRollsBackEveryStartedSubsystemAndPreservesAllFailures()
    {
        var calls = new List<string>();
        var first = new RecordingSubsystem("database", calls);
        var second = new RecordingSubsystem("vault", calls)
        {
            StopFailures = new Queue<Exception>([new IOException("vault stop failed")])
        };
        var third = new RecordingSubsystem("scheduler", calls)
        {
            StartFailure = new InvalidOperationException("scheduler start failed")
        };
        await using var coordinator = new AgentRuntimeCoordinator([first, second, third]);
        await coordinator.InitializeAsync();

        var error = await Assert.ThrowsAsync<AggregateException>(() => coordinator.StartAsync());

        Assert.Equal(ApplicationOperationalState.Faulted, coordinator.State);
        Assert.Collection(
            error.InnerExceptions,
            failure => Assert.Equal("scheduler start failed", failure.Message),
            failure =>
            {
                Assert.Contains("vault", failure.Message, StringComparison.Ordinal);
                Assert.Equal("vault stop failed", failure.InnerException?.Message);
            });
        Assert.Equal(
            ["database.initialize", "vault.initialize", "scheduler.initialize",
             "database.start", "vault.start", "scheduler.start", "vault.stop", "database.stop"],
            calls);

        // Dispose retries only the subsystem whose rollback failed.
        await coordinator.DisposeAsync();
        Assert.Equal(2, calls.Count(call => call == "vault.stop"));
    }

    [Fact]
    public async Task StopAttemptsEverySubsystemAndCanRetryOnlyFailures()
    {
        var calls = new List<string>();
        var first = new RecordingSubsystem("database", calls);
        var second = new RecordingSubsystem("ipc", calls)
        {
            StopFailures = new Queue<Exception>([new IOException("transient")])
        };
        var coordinator = new AgentRuntimeCoordinator([first, second]);
        await coordinator.InitializeAsync();
        await coordinator.StartAsync();

        await Assert.ThrowsAsync<AggregateException>(() => coordinator.StopAsync());

        Assert.Equal(ApplicationOperationalState.Faulted, coordinator.State);
        Assert.Contains("database.stop", calls);
        await coordinator.StopAsync();
        Assert.Equal(ApplicationOperationalState.Stopped, coordinator.State);
        Assert.Equal(1, calls.Count(call => call == "database.stop"));
        Assert.Equal(2, calls.Count(call => call == "ipc.stop"));
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task CancellationFromOneStopDoesNotPreventRemainingCleanup()
    {
        var calls = new List<string>();
        var first = new RecordingSubsystem("database", calls);
        var second = new RecordingSubsystem("ipc", calls)
        {
            StopFailures = new Queue<Exception>([new OperationCanceledException("deadline")])
        };
        var coordinator = new AgentRuntimeCoordinator([first, second]);
        await coordinator.InitializeAsync();
        await coordinator.StartAsync();

        await Assert.ThrowsAsync<AggregateException>(() => coordinator.StopAsync());

        Assert.Contains("ipc.stop", calls);
        Assert.Contains("database.stop", calls);
        await coordinator.StopAsync();
        await coordinator.DisposeAsync();
    }

    private sealed class RecordingSubsystem(string name, List<string> calls) : IAgentSubsystem
    {
        public string Name { get; } = name;
        public bool CanRunInRecoveryMode { get; set; }
        public SubsystemInitializationResult InitializationResult { get; set; } = SubsystemInitializationResult.Ready();
        public Exception? StartFailure { get; set; }
        public Queue<Exception> StopFailures { get; set; } = new();

        public Task<SubsystemInitializationResult> InitializeAsync(CancellationToken cancellationToken)
        {
            calls.Add($"{Name}.initialize");
            return Task.FromResult(InitializationResult);
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            calls.Add($"{Name}.start");
            if (StartFailure is not null)
            {
                throw StartFailure;
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            calls.Add($"{Name}.stop");
            if (StopFailures.TryDequeue(out var failure))
            {
                throw failure;
            }

            return Task.CompletedTask;
        }

        public Task<SubsystemHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SubsystemHealth.Healthy());
    }
}
