using Microsoft.Data.Sqlite;
using StorageHub.Agent.Scheduling;
using StorageHub.Persistence;

namespace StorageHub.Agent.Windows;

/// <summary>
/// Verifies scheduler persistence without claiming that this host can execute sync profiles.
/// Replace this readiness-only subsystem with <see cref="SchedulerAgentSubsystem"/> once a real
/// <see cref="IScheduledSyncJobRunner"/> is composed with the sync engine.
/// </summary>
internal sealed class SchedulerStoreAgentSubsystem(SqliteScheduledSyncJobStore store) : IAgentSubsystem
{
    private readonly SqliteScheduledSyncJobStore _store =
        store ?? throw new ArgumentNullException(nameof(store));
    private bool _initialized;
    private bool _running;

    public string Name => "scheduler store";

    public bool CanRunInRecoveryMode => false;

    public async Task<SubsystemInitializationResult> InitializeAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await _store.GetJobsAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
            return SubsystemInitializationResult.Ready();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is SqliteException or InvalidDataException)
        {
            return new SubsystemInitializationResult(
                IsReady: false,
                RequiresRecoveryMode: false,
                "The durable scheduler store could not be read safely.");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_initialized)
        {
            throw new InvalidOperationException("The scheduler store has not been initialized.");
        }

        _running = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _running = false;
        return Task.CompletedTask;
    }

    public async Task<SubsystemHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        if (!_initialized || !_running)
        {
            return SubsystemHealth.Degraded("The scheduler store readiness check is stopped.");
        }

        try
        {
            var jobs = await _store.GetJobsAsync(cancellationToken).ConfigureAwait(false);
            var enabledJobs = jobs.Count(job => job.Enabled);
            return enabledJobs == 0
                ? SubsystemHealth.Healthy(
                    "Scheduler persistence is ready; no scheduled sync jobs are enabled.")
                : SubsystemHealth.Degraded(
                    $"{enabledJobs} scheduled sync job(s) are enabled, but this host has no sync runner registered.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is SqliteException or InvalidDataException)
        {
            return SubsystemHealth.Unhealthy("The durable scheduler store health query failed.");
        }
    }
}
