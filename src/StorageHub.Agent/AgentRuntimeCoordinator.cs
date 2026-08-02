using System.Runtime.ExceptionServices;
using StorageHub.Application;

namespace StorageHub.Agent;

public sealed class AgentRuntimeCoordinator : IApplicationRuntimeCoordinator, IAsyncDisposable
{
    private readonly IAgentSubsystem[] _subsystems;
    private readonly List<IAgentSubsystem> _started = [];
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private CancellationTokenSource? _lifetime;
    private bool _disposed;

    public AgentRuntimeCoordinator(IEnumerable<IAgentSubsystem> subsystems)
    {
        ArgumentNullException.ThrowIfNull(subsystems);
        _subsystems = subsystems.ToArray();
        if (_subsystems.Select(static subsystem => subsystem.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _subsystems.Length)
        {
            throw new ArgumentException("Agent subsystem names must be unique.", nameof(subsystems));
        }
    }

    public ApplicationOperationalState State { get; private set; } = ApplicationOperationalState.Created;

    public string? HealthMessage { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State != ApplicationOperationalState.Created)
            {
                throw new InvalidOperationException($"Cannot initialize the agent from state {State}.");
            }

            State = ApplicationOperationalState.Initializing;
            _lifetime = new CancellationTokenSource();
            var recoveryOnly = false;
            foreach (var subsystem in _subsystems)
            {
                if (recoveryOnly && !subsystem.CanRunInRecoveryMode)
                {
                    continue;
                }

                var result = await subsystem.InitializeAsync(cancellationToken).ConfigureAwait(false);
                if (result.RequiresRecoveryMode)
                {
                    recoveryOnly = true;
                    HealthMessage = result.Message ?? $"{subsystem.Name} requires recovery mode.";
                    continue;
                }

                if (!result.IsReady)
                {
                    State = ApplicationOperationalState.Faulted;
                    HealthMessage = result.Message ?? $"{subsystem.Name} failed to initialize.";
                    throw new InvalidOperationException(HealthMessage);
                }
            }

            State = recoveryOnly
                ? ApplicationOperationalState.RecoveryOnly
                : ApplicationOperationalState.Starting;
        }
        catch
        {
            if (State != ApplicationOperationalState.RecoveryOnly)
            {
                State = ApplicationOperationalState.Faulted;
            }

            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IAgentSubsystem? startingSubsystem = null;
        try
        {
            if (State is not (ApplicationOperationalState.Starting or ApplicationOperationalState.RecoveryOnly))
            {
                throw new InvalidOperationException($"Cannot start the agent from state {State}.");
            }

            var recoveryOnly = State == ApplicationOperationalState.RecoveryOnly;
            foreach (var subsystem in _subsystems)
            {
                if (recoveryOnly && !subsystem.CanRunInRecoveryMode)
                {
                    continue;
                }

                startingSubsystem = subsystem;
                await subsystem.StartAsync(cancellationToken).ConfigureAwait(false);
                _started.Add(subsystem);
            }

            State = recoveryOnly ? ApplicationOperationalState.RecoveryOnly : ApplicationOperationalState.Ready;
            HealthMessage ??= recoveryOnly ? "Agent is available for recovery operations only." : "Agent is ready.";
        }
        catch (Exception startError)
        {
            State = ApplicationOperationalState.Faulted;
            HealthMessage = startingSubsystem is null
                ? "The agent failed to start."
                : $"{startingSubsystem.Name} failed to start.";
            var rollbackFailures = await StopStartedSubsystemsAsync(CancellationToken.None).ConfigureAwait(false);
            if (rollbackFailures.Count != 0)
            {
                throw new AggregateException(
                    "The agent failed to start and one or more started subsystems failed to roll back.",
                    new[] { startError }.Concat(rollbackFailures));
            }

            ExceptionDispatchInfo.Capture(startError).Throw();
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || State == ApplicationOperationalState.Stopped)
        {
            return;
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            State = ApplicationOperationalState.Stopping;
            _lifetime?.Cancel();
            var failures = await StopStartedSubsystemsAsync(cancellationToken).ConfigureAwait(false);
            if (failures.Count == 0)
            {
                State = ApplicationOperationalState.Stopped;
                HealthMessage = "Agent stopped.";
            }
            else
            {
                State = ApplicationOperationalState.Faulted;
                HealthMessage = "One or more agent subsystems failed to stop.";
                throw new AggregateException(HealthMessage, failures);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<IReadOnlyDictionary<string, SubsystemHealth>> CheckSubsystemHealthAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, SubsystemHealth>(StringComparer.OrdinalIgnoreCase);
        foreach (var subsystem in _subsystems)
        {
            results[subsystem.Name] = await subsystem.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    public async Task<ApplicationRuntimeHealth> CheckHealthAsync(
        CancellationToken cancellationToken = default)
    {
        var subsystemHealth = await CheckSubsystemHealthAsync(cancellationToken).ConfigureAwait(false);
        var components = subsystemHealth.ToDictionary(
            static item => item.Key,
            static item => item.Value.Message,
            StringComparer.OrdinalIgnoreCase);
        if (State == ApplicationOperationalState.Faulted ||
            subsystemHealth.Values.Any(static health => health.Level == SubsystemHealthLevel.Unhealthy))
        {
            return ApplicationRuntimeHealth.Unhealthy(
                HealthMessage ?? "One or more agent subsystems are unhealthy.",
                components);
        }

        if (State != ApplicationOperationalState.Ready ||
            subsystemHealth.Values.Any(static health => health.Level == SubsystemHealthLevel.Degraded))
        {
            return ApplicationRuntimeHealth.Degraded(
                HealthMessage ?? "One or more agent subsystems are degraded.",
                components);
        }

        return ApplicationRuntimeHealth.Healthy(
            HealthMessage ?? "The StorageHub agent is ready.",
            components);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        Exception? stopFailure = null;
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            stopFailure = error;
        }
        finally
        {
            _disposed = true;
            _lifetime?.Dispose();
            _lifecycleGate.Dispose();
        }

        if (stopFailure is not null)
        {
            ExceptionDispatchInfo.Capture(stopFailure).Throw();
        }
    }

    private async Task<IReadOnlyList<Exception>> StopStartedSubsystemsAsync(CancellationToken cancellationToken)
    {
        var failures = new List<Exception>();
        var stopToken = cancellationToken;
        var startedSnapshot = _started.ToArray();
        for (var index = startedSnapshot.Length - 1; index >= 0; index--)
        {
            var subsystem = startedSnapshot[index];
            try
            {
                await subsystem.StopAsync(stopToken).ConfigureAwait(false);
                _started.Remove(subsystem);
            }
            catch (Exception error)
            {
                failures.Add(new InvalidOperationException(
                    $"Agent subsystem '{subsystem.Name}' failed to stop.",
                    error));
                if (error is OperationCanceledException)
                {
                    // Once shutdown has begun, still give every remaining subsystem a cleanup attempt.
                    stopToken = CancellationToken.None;
                }
            }
        }

        return failures;
    }
}
