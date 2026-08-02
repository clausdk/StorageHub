using System.Text.Json;
using StorageHub.Agent.Ipc;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

public sealed class NamedPipePackagedAgentLifecycleClient : IPackagedAgentLifecycleClient
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(125);
    private readonly string _clientVersion;
    private readonly string _agentExecutablePath;
    private readonly IPackagedAgentProcessMonitor _processMonitor;
    private readonly TimeProvider _timeProvider;

    public NamedPipePackagedAgentLifecycleClient(
        string clientVersion,
        string agentExecutablePath,
        IPackagedAgentProcessMonitor processMonitor,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientVersion);
        if (clientVersion.Length > 128 || clientVersion.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The desktop version must be at most 128 non-control characters.",
                nameof(clientVersion));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(agentExecutablePath);
        if (!Path.IsPathFullyQualified(agentExecutablePath))
        {
            throw new ArgumentException(
                "An absolute packaged Agent executable path is required.",
                nameof(agentExecutablePath));
        }

        _clientVersion = clientVersion;
        _agentExecutablePath = Path.GetFullPath(agentExecutablePath);
        _processMonitor = processMonitor ?? throw new ArgumentNullException(nameof(processMonitor));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var client = CreateClient();
            _ = await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (IsRecoverableTransportFailure(error))
        {
            return false;
        }
    }

    public ValueTask<bool> WaitUntilAvailableAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        WaitForAvailabilityAsync(expectedAvailability: true, timeout, cancellationToken);

    public async ValueTask<bool> RequestShutdownAndWaitAsync(
        AgentShutdownReason reason,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeout);
        var startedAt = _timeProvider.GetTimestamp();
        using var requestTimeout = new CancellationTokenSource(timeout, _timeProvider);
        using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            requestTimeout.Token);
        var requestToken = requestLifetime.Token;
        int processId;
        while (true)
        {
            try
            {
                await using var client = CreateClient();
                _ = await client.ConnectAsync(requestToken).ConfigureAwait(false);
                var requestId = Guid.NewGuid();
                await client.SendAsync(
                    IpcEnvelope.Create(
                        AgentControlIpcMessageTypes.ShutdownRequest,
                        requestId,
                        sequence: 1,
                        new AgentShutdownRequest(
                            AgentControlIpcContract.CurrentVersion,
                            MapReason(reason))),
                    requestToken).ConfigureAwait(false);
                var responseEnvelope = await client.ReceiveAsync(requestToken).ConfigureAwait(false);
                if (responseEnvelope.RequestId != requestId ||
                    !string.Equals(
                        responseEnvelope.MessageType,
                        AgentControlIpcMessageTypes.ShutdownResponse,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                var response = responseEnvelope.DeserializePayload<AgentShutdownResponse>();
                if (!response.Accepted || !response.HasValidBounds)
                {
                    return false;
                }

                processId = response.ProcessId;
                break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (requestTimeout.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception error) when (IsRecoverableTransportFailure(error))
            {
                if (!_processMonitor.IsRunning(_agentExecutablePath))
                {
                    return true;
                }

                var retryRemaining = Remaining(timeout, startedAt);
                if (retryRemaining <= TimeSpan.Zero)
                {
                    return false;
                }

                await Task.Delay(
                    retryRemaining < ProbeInterval ? retryRemaining : ProbeInterval,
                    _timeProvider,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        var remaining = Remaining(timeout, startedAt);
        return remaining > TimeSpan.Zero &&
            await _processMonitor.WaitForExitAsync(
                processId,
                _agentExecutablePath,
                remaining,
                cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> WaitForAvailabilityAsync(
        bool expectedAvailability,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ValidateTimeout(timeout);
        var startedAt = _timeProvider.GetTimestamp();
        while (true)
        {
            if (await IsAvailableAsync(cancellationToken).ConfigureAwait(false) == expectedAvailability)
            {
                return true;
            }

            var remaining = Remaining(timeout, startedAt);
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            await Task.Delay(
                remaining < ProbeInterval ? remaining : ProbeInterval,
                _timeProvider,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private NamedPipeIpcClient CreateClient() => new(new NamedPipeIpcClientOptions
    {
        PipeName = AgentStatusMonitor.DefaultPipeName,
        ClientName = "StorageHub.Desktop.Lifecycle",
        ClientVersion = _clientVersion,
        ConnectTimeout = TimeSpan.FromMilliseconds(350),
        MaxConnectAttempts = 1,
        InitialReconnectDelay = TimeSpan.Zero,
        MaximumReconnectDelay = TimeSpan.Zero
    });

    private TimeSpan Remaining(TimeSpan timeout, long startedAt)
    {
        var elapsed = _timeProvider.GetElapsedTime(startedAt, _timeProvider.GetTimestamp());
        return elapsed >= timeout ? TimeSpan.Zero : timeout - elapsed;
    }

    private static string MapReason(AgentShutdownReason reason) => reason switch
    {
        AgentShutdownReason.Update => AgentShutdownReasons.Update,
        AgentShutdownReason.Uninstall => AgentShutdownReasons.Uninstall,
        AgentShutdownReason.Restart => AgentShutdownReasons.Restart,
        _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(12))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The Agent lifecycle timeout must be between zero and twelve seconds.");
        }
    }

    private static bool IsRecoverableTransportFailure(Exception error) => error is
        IOException or
        TimeoutException or
        UnauthorizedAccessException or
        InvalidDataException or
        InvalidOperationException or
        JsonException;
}
