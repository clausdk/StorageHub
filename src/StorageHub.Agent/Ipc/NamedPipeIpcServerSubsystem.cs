using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Agent.Ipc;

public sealed class NamedPipeIpcServerSubsystem : IAgentSubsystem, IAsyncDisposable
{
    private const int MaximumPipeInstances = 254;
    private readonly NamedPipeIpcServerOptions _options;
    private readonly Func<NamedPipeIpcSession, CancellationToken, Task>? _sessionHandler;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _clientSlots;
    private readonly object _pendingListenerGate = new();
    private readonly ConcurrentDictionary<int, NamedPipeServerStream> _activePipes = new();
    private readonly ConcurrentDictionary<int, Task> _sessionTasks = new();
    private CancellationTokenSource? _lifetime;
    private NamedPipeServerStream? _pendingListener;
    private Task? _acceptLoop;
    private Exception? _lastFailure;
    private int _nextSessionId;
    private int _activeClientCount;
    private int _peakClientCount;
    private bool _initialized;
    private bool _disposed;

    public NamedPipeIpcServerSubsystem(
        NamedPipeIpcServerOptions options,
        Func<NamedPipeIpcSession, CancellationToken, Task>? sessionHandler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        _options = options;
        _sessionHandler = sessionHandler;
        _clientSlots = new SemaphoreSlim(options.MaxConcurrentClients, options.MaxConcurrentClients);
    }

    public string Name => _options.FrameKind == IpcFrameKind.Secret
        ? "Secret IPC"
        : "Local IPC";

    public bool CanRunInRecoveryMode => true;

    public bool IsRunning { get; private set; }

    public int ActiveClientCount => Volatile.Read(ref _activeClientCount);

    public int PeakClientCount => Volatile.Read(ref _peakClientCount);

    public static bool UsesCurrentUserOnlySecurity => OperatingSystem.IsWindows();

    public async Task<SubsystemInitializationResult> InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _initialized = true;
            return SubsystemInitializationResult.Ready();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_initialized)
            {
                throw new InvalidOperationException("The local IPC subsystem must be initialized before it starts.");
            }

            if (IsRunning)
            {
                return;
            }

            _lastFailure = null;
            _lifetime = new CancellationTokenSource();
            IsRunning = true;
            _acceptLoop = AcceptConnectionsAsync(_lifetime.Token);
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
            if (!IsRunning)
            {
                return;
            }

            IsRunning = false;
            _lifetime?.Cancel();
            DisposePendingListener();
            foreach (var pipe in _activePipes.Values)
            {
                pipe.Dispose();
            }

            if (_acceptLoop is not null)
            {
                await IgnoreExpectedShutdownExceptionAsync(_acceptLoop).ConfigureAwait(false);
            }

            var sessions = _sessionTasks.Values.ToArray();
            if (sessions.Length != 0)
            {
                await Task.WhenAll(sessions).ConfigureAwait(false);
            }

            _acceptLoop = null;
            _lifetime?.Dispose();
            _lifetime = null;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task<SubsystemHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_lastFailure is not null)
        {
            return Task.FromResult(SubsystemHealth.Unhealthy(
                $"Local IPC failed: {_lastFailure.GetType().Name}."));
        }

        if (IsRunning)
        {
            return Task.FromResult(SubsystemHealth.Healthy(
                $"Local IPC is accepting connections ({ActiveClientCount}/{_options.MaxConcurrentClients} active)."));
        }

        return Task.FromResult(SubsystemHealth.Degraded("Local IPC is stopped."));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _disposed = true;
        _lifetime?.Dispose();
        _clientSlots.Dispose();
        _lifecycleGate.Dispose();
    }

    private static void ValidateOptions(NamedPipeIpcServerOptions options)
    {
        IpcProtocolValidation.ValidatePipeName(options.PipeName);
        if (!Enum.IsDefined(options.FrameKind))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The IPC frame kind is invalid.");
        }

        if (options.FrameKind == IpcFrameKind.Secret && !OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Secret IPC requires Windows current-user-only named-pipe security.");
        }

        IpcProtocolValidation.ValidateIdentity(options.AgentVersion, nameof(options.AgentVersion));
        if (options.AgentInstanceId == Guid.Empty)
        {
            throw new ArgumentException("The agent instance ID must not be empty.", nameof(options));
        }

        if (options.MaxConcurrentClients is < 1 or > MaximumPipeInstances)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxConcurrentClients,
                $"The maximum concurrent client count must be between 1 and {MaximumPipeInstances}.");
        }

        IpcProtocolValidation.ValidatePositiveTimeout(options.HandshakeTimeout, nameof(options.HandshakeTimeout));
        IpcProtocolValidation.ValidatePositiveOperationTimeout(
            options.SessionIdleTimeout,
            nameof(options.SessionIdleTimeout));
        IpcProtocolValidation.ValidatePositiveOperationTimeout(
            options.RequestTimeout,
            nameof(options.RequestTimeout));
    }

    private async Task AcceptConnectionsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var ownsSlot = false;
            var handedOff = false;
            NamedPipeServerStream? listener = null;
            try
            {
                await _clientSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                ownsSlot = true;
                listener = CreateServerStream();
                SetPendingListener(listener);
                await listener.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                ClearPendingListener(listener);

                var sessionId = Interlocked.Increment(ref _nextSessionId);
                _activePipes[sessionId] = listener;
                var sessionTask = RunSessionAsync(sessionId, listener, cancellationToken);
                _sessionTasks[sessionId] = sessionTask;
                _ = ObserveSessionAsync(sessionId, sessionTask);
                handedOff = true;
                listener = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                _lastFailure = error;
                return;
            }
            finally
            {
                if (listener is not null)
                {
                    ClearPendingListener(listener);
                    listener.Dispose();
                }

                if (ownsSlot && !handedOff)
                {
                    _clientSlots.Release();
                }
            }
        }
    }

    private NamedPipeServerStream CreateServerStream()
    {
        var pipeOptions = PipeOptions.Asynchronous;
        if (OperatingSystem.IsWindows())
        {
            pipeOptions |= PipeOptions.CurrentUserOnly;
        }

        return new NamedPipeServerStream(
            _options.PipeName,
            PipeDirection.InOut,
            _options.MaxConcurrentClients,
            PipeTransmissionMode.Byte,
            pipeOptions);
    }

    private async Task RunSessionAsync(
        int sessionId,
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        try
        {
            using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var session = await NegotiateSessionAsync(pipe, sessionCancellation).ConfigureAwait(false);
            if (session is null)
            {
                return;
            }

            await using (session.ConfigureAwait(false))
            {
                RecordClientStarted();
                try
                {
                    if (_sessionHandler is not null)
                    {
                        await _sessionHandler(session, sessionCancellation.Token).ConfigureAwait(false);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _activeClientCount);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Lifetime cancellation and unauthenticated handshake timeouts are both session-local.
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (JsonException)
        {
            // Malformed or incompatible client payloads are rejected by closing only that session.
        }
        catch (InvalidDataException)
        {
            // Invalid lengths, envelopes, and sequence values are untrusted client input.
        }
        catch (IpcProtocolNegotiationException)
        {
            // Protocol failures are isolated to the untrusted client connection.
        }
        catch (IOException)
        {
            // A local client can disconnect at any point. This is session-local, not subsystem failure.
        }
        catch (Exception error)
        {
            _lastFailure = error;
        }
        finally
        {
            _activePipes.TryRemove(sessionId, out _);
            pipe.Dispose();
            _clientSlots.Release();
        }
    }

    private async Task<NamedPipeIpcSession?> NegotiateSessionAsync(
        NamedPipeServerStream pipe,
        CancellationTokenSource sessionCancellation)
    {
        using var handshakeCancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation.Token);
        handshakeCancellation.CancelAfter(_options.HandshakeTimeout);
        var envelope = await LengthPrefixedJsonChannel.ReadAsync<IpcEnvelope>(
            pipe,
            IpcFrameLimits.NormalMaxBytes,
            cancellationToken: handshakeCancellation.Token).ConfigureAwait(false);
        IpcProtocolValidation.ValidateHandshakeEnvelope(envelope, IpcProtocol.HelloRequestMessageType);
        HelloRequest hello;
        try
        {
            hello = envelope.DeserializePayload<HelloRequest>();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or NotSupportedException)
        {
            throw new IpcProtocolNegotiationException("The client returned an invalid handshake payload.", error);
        }

        string? rejectionReason = null;
        if (string.IsNullOrWhiteSpace(hello.ClientName) || string.IsNullOrWhiteSpace(hello.ClientVersion))
        {
            rejectionReason = "Client name and version are required.";
        }
        else if (hello.ClientName.Length > 256 || hello.ClientVersion.Length > 256)
        {
            rejectionReason = "Client name and version cannot exceed 256 characters.";
        }
        else if (hello.ClientInstanceId == Guid.Empty)
        {
            rejectionReason = "Client instance ID must not be empty.";
        }
        else if (!_options.ProtocolVersion.IsCompatibleWith(hello.ProtocolVersion))
        {
            rejectionReason =
                $"Client protocol {hello.ProtocolVersion} is incompatible with agent protocol {_options.ProtocolVersion}.";
        }

        var accepted = rejectionReason is null;
        var negotiatedVersion = accepted
            ? new ProtocolVersion(
                _options.ProtocolVersion.Major,
                Math.Min(_options.ProtocolVersion.Minor, hello.ProtocolVersion.Minor))
            : _options.ProtocolVersion;
        var response = new HelloResponse(
            negotiatedVersion,
            accepted,
            _options.AgentVersion,
            _options.AgentInstanceId,
            rejectionReason);
        var responseEnvelope = IpcEnvelope.Create(
            IpcProtocol.HelloResponseMessageType,
            envelope.RequestId,
            0,
            response);
        await LengthPrefixedJsonChannel.WriteAsync(
            pipe,
            responseEnvelope,
            IpcFrameLimits.NormalMaxBytes,
            cancellationToken: handshakeCancellation.Token).ConfigureAwait(false);

        return accepted
            ? new NamedPipeIpcSession(
                pipe,
                hello,
                negotiatedVersion,
                sessionCancellation,
                _options.SessionIdleTimeout,
                _options.RequestTimeout,
                _options.FrameKind)
            : null;
    }

    private void RecordClientStarted()
    {
        var current = Interlocked.Increment(ref _activeClientCount);
        var observedPeak = Volatile.Read(ref _peakClientCount);
        while (current > observedPeak)
        {
            var priorPeak = Interlocked.CompareExchange(ref _peakClientCount, current, observedPeak);
            if (priorPeak == observedPeak)
            {
                break;
            }

            observedPeak = priorPeak;
        }
    }

    private async Task ObserveSessionAsync(int sessionId, Task sessionTask)
    {
        await sessionTask.ConfigureAwait(false);
        _sessionTasks.TryRemove(sessionId, out _);
    }

    private void SetPendingListener(NamedPipeServerStream listener)
    {
        lock (_pendingListenerGate)
        {
            _pendingListener = listener;
        }
    }

    private void ClearPendingListener(NamedPipeServerStream listener)
    {
        lock (_pendingListenerGate)
        {
            if (ReferenceEquals(_pendingListener, listener))
            {
                _pendingListener = null;
            }
        }
    }

    private void DisposePendingListener()
    {
        lock (_pendingListenerGate)
        {
            _pendingListener?.Dispose();
            _pendingListener = null;
        }
    }

    private static async Task IgnoreExpectedShutdownExceptionAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
