using System.Text.Json;
using StorageHub.Agent.Ipc;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

public sealed record AgentMonitorStatus(
    AgentConnectionState State,
    int ActiveTransfers,
    int ActiveSyncRuns,
    string Detail,
    DateTimeOffset ObservedAtUtc);

public sealed class AgentMonitorStatusEventArgs(AgentMonitorStatus status) : EventArgs
{
    public AgentMonitorStatus Status { get; } = status;
}

public sealed class AgentStatusMonitor : IAsyncDisposable
{
    public const string DefaultPipeName = "StorageHub.Agent.v1";

    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _connectTimeout;
    private readonly CancellationTokenSource _lifetime;
    private Task? _monitorTask;
    private bool _disposed;

    public AgentStatusMonitor(
        TimeSpan? pollInterval = null,
        TimeSpan? connectTimeout = null)
    {
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(8);
        _connectTimeout = connectTimeout ?? TimeSpan.FromMilliseconds(450);
        if (_pollInterval < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), "The poll interval must be at least one second.");
        }

        if (_connectTimeout <= TimeSpan.Zero || _connectTimeout > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(nameof(connectTimeout), "The local IPC timeout must be between zero and five seconds.");
        }

        _lifetime = new CancellationTokenSource();
    }

    public event EventHandler<AgentMonitorStatusEventArgs>? StatusChanged;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _monitorTask ??= Task.Run(() => MonitorAsync(_lifetime.Token));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_monitorTask is not null)
        {
            try
            {
                await _monitorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is the expected shutdown path.
            }
        }

        _lifetime.Dispose();
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_pollInterval);
        do
        {
            await PollOnceAsync(cancellationToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var client = new NamedPipeIpcClient(new NamedPipeIpcClientOptions
            {
                PipeName = DefaultPipeName,
                ClientName = "StorageHub.Desktop",
                ClientVersion = typeof(AgentStatusMonitor).Assembly.GetName().Version?.ToString() ?? "0.1.0",
                ConnectTimeout = _connectTimeout,
                MaxConnectAttempts = 1,
                InitialReconnectDelay = TimeSpan.Zero,
                MaximumReconnectDelay = TimeSpan.Zero
            });
            _ = await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var requestId = Guid.NewGuid();
            await client.SendAsync(
                IpcEnvelope.Create(IpcProtocol.AgentStatusRequestMessageType, requestId, 1, new AgentStatusRequest()),
                cancellationToken).ConfigureAwait(false);
            var envelope = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (envelope.RequestId != requestId ||
                !string.Equals(envelope.MessageType, IpcProtocol.AgentStatusResponseMessageType, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The agent returned an unexpected status response.");
            }

            var snapshot = envelope.DeserializePayload<AgentStatusSnapshot>();
            RaiseStatus(Map(snapshot));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or JsonException)
        {
            RaiseStatus(new AgentMonitorStatus(
                AgentConnectionState.Disconnected,
                0,
                0,
                "Background agent is offline; browsing remains available.",
                DateTimeOffset.UtcNow));
        }
    }

    private void RaiseStatus(AgentMonitorStatus status) =>
        StatusChanged?.Invoke(this, new AgentMonitorStatusEventArgs(status));

    private static AgentMonitorStatus Map(AgentStatusSnapshot snapshot)
    {
        var state = snapshot.State switch
        {
            AgentLifecycleState.Ready => AgentConnectionState.Connected,
            AgentLifecycleState.Degraded or AgentLifecycleState.Faulted => AgentConnectionState.RecoveryOnly,
            AgentLifecycleState.Starting => AgentConnectionState.Starting,
            _ => AgentConnectionState.Disconnected
        };
        return new AgentMonitorStatus(
            state,
            snapshot.ActiveTransfers,
            snapshot.ActiveSyncRuns,
            snapshot.Detail ?? string.Empty,
            snapshot.ObservedAtUtc);
    }
}
