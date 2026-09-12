namespace StorageHub.Desktop;

/// <summary>
/// Drives the background agent using the lifecycle primitives the desktop already owns: the
/// current-user shutdown command the agent exposes, and the guarded launch the desktop performs at
/// startup. Nothing new is asked of the agent - this only gives the existing operations a caller.
/// </summary>
public sealed class PackagedAgentLifecycleController(PackagedDesktopLifecycle lifecycle)
    : IAgentLifecycleController
{
    private readonly PackagedDesktopLifecycle _lifecycle =
        lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));

    public async Task<AgentLifecycleResult> ExecuteAsync(
        AgentLifecycleAction action,
        CancellationToken cancellationToken = default)
    {
        return action switch
        {
            AgentLifecycleAction.Start => await StartAsync(cancellationToken).ConfigureAwait(false),
            AgentLifecycleAction.Stop => await StopAsync(cancellationToken).ConfigureAwait(false),
            AgentLifecycleAction.Restart => await RestartAsync(cancellationToken).ConfigureAwait(false),
            _ => new AgentLifecycleResult(false, "That agent action is not supported.")
        };
    }

    private async Task<AgentLifecycleResult> StartAsync(CancellationToken cancellationToken)
    {
        var ensured = await _lifecycle.EnsureAgentAsync(cancellationToken).ConfigureAwait(false);
        return ensured.Status switch
        {
            AgentEnsureStatus.AlreadyRunning => new(true, "The agent was already running."),
            AgentEnsureStatus.Started => new(true, "The agent started."),
            AgentEnsureStatus.MissingExecutable =>
                new(false, "The agent executable is missing. Repair or reinstall StorageHub."),
            AgentEnsureStatus.StartupTimedOut =>
                new(false, "The agent did not become available in time."),
            _ => new(false, "The agent could not be launched.")
        };
    }

    private async Task<AgentLifecycleResult> StopAsync(CancellationToken cancellationToken)
    {
        // A graceful shutdown lets in-flight work checkpoint; the durable queue recovers an
        // interrupted owner on the next start either way.
        var stopped = await _lifecycle
            .TryStopAgentAsync(AgentShutdownReason.Restart, cancellationToken)
            .ConfigureAwait(false);
        return stopped
            ? new(true, "The agent stopped.")
            : new(false, "The agent did not confirm shutdown within the timeout.");
    }

    private async Task<AgentLifecycleResult> RestartAsync(CancellationToken cancellationToken)
    {
        var stopped = await _lifecycle
            .TryStopAgentAsync(AgentShutdownReason.Restart, cancellationToken)
            .ConfigureAwait(false);

        // Start regardless: a stop that timed out may still have left no agent running, and an
        // agent that was already down is exactly what a restart should fix.
        var started = await StartAsync(cancellationToken).ConfigureAwait(false);
        if (!started.Succeeded)
        {
            return started;
        }

        return stopped
            ? new(true, "The agent restarted.")
            : new(true, "The agent restarted, but the previous instance did not confirm shutdown.");
    }
}
