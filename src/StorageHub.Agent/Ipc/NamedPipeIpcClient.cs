using System.IO.Pipes;
using System.Text.Json;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Agent.Ipc;

public sealed class NamedPipeIpcClient : IAsyncDisposable
{
    private readonly NamedPipeIpcClientOptions _options;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private NamedPipeClientStream? _stream;
    private HelloResponse? _serverHello;
    private long _lastReceivedSequence;
    private long _lastSentSequence;
    private int _lastConnectionAttemptCount;
    private bool _disposed;

    public NamedPipeIpcClient(NamedPipeIpcClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        _options = options;
    }

    public bool IsConnected => !_disposed && _stream is { IsConnected: true };

    public static bool UsesCurrentUserOnlySecurity => OperatingSystem.IsWindows();

    public int LastConnectionAttemptCount => Volatile.Read(ref _lastConnectionAttemptCount);

    public HelloResponse? ServerHello => _serverHello;

    public async Task<HelloResponse> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected && _serverHello is not null)
            {
                return _serverHello;
            }

            DisconnectCore();
            return await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<HelloResponse> ReconnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DisconnectCore();
            return await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async ValueTask<IpcEnvelope> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureFrameKind(IpcFrameKind.Normal);
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = GetConnectedStream();
            var envelope = await LengthPrefixedJsonChannel.ReadAsync<IpcEnvelope>(
                stream,
                IpcFrameLimits.NormalMaxBytes,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            IpcProtocolValidation.ValidateNormalEnvelope(envelope, _lastReceivedSequence);
            _lastReceivedSequence = envelope.Sequence;
            return envelope;
        }
        finally
        {
            _readGate.Release();
        }
    }

    public async ValueTask SendAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureFrameKind(IpcFrameKind.Normal);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = GetConnectedStream();
            IpcProtocolValidation.ValidateNormalEnvelope(envelope, _lastSentSequence);
            await LengthPrefixedJsonChannel.WriteAsync(
                stream,
                envelope,
                IpcFrameLimits.NormalMaxBytes,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            _lastSentSequence = envelope.Sequence;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<SecretIpcResponseEnvelope> ReceiveSecretAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureFrameKind(IpcFrameKind.Secret);
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = GetConnectedStream();
            var envelope = await LengthPrefixedJsonChannel.ReadAsync<SecretIpcResponseEnvelope>(
                stream,
                IpcFrameLimits.SecretMaxBytes,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            IpcProtocolValidation.ValidateSecretEnvelope(
                envelope.MessageType,
                envelope.RequestId,
                envelope.Sequence,
                _lastReceivedSequence);
            _lastReceivedSequence = envelope.Sequence;
            return envelope;
        }
        finally
        {
            _readGate.Release();
        }
    }

    public async ValueTask SendSecretAsync(
        SecretIpcRequestEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureFrameKind(IpcFrameKind.Secret);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = GetConnectedStream();
            IpcProtocolValidation.ValidateSecretEnvelope(
                envelope.MessageType,
                envelope.RequestId,
                envelope.Sequence,
                _lastSentSequence);
            await LengthPrefixedJsonChannel.WriteAsync(
                stream,
                envelope,
                IpcFrameLimits.SecretMaxBytes,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            _lastSentSequence = envelope.Sequence;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DisconnectCore();
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _connectionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            DisconnectCore();
        }
        finally
        {
            _connectionGate.Release();
        }

        await _readGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        await _writeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        _connectionGate.Dispose();
        _readGate.Dispose();
        _writeGate.Dispose();
    }

    private static void ValidateOptions(NamedPipeIpcClientOptions options)
    {
        IpcProtocolValidation.ValidatePipeName(options.PipeName);
        IpcProtocolValidation.ValidateIdentity(options.ClientName, nameof(options.ClientName));
        IpcProtocolValidation.ValidateIdentity(options.ClientVersion, nameof(options.ClientVersion));
        if (options.ClientInstanceId == Guid.Empty)
        {
            throw new ArgumentException("The client instance ID must not be empty.", nameof(options));
        }

        IpcProtocolValidation.ValidatePositiveTimeout(options.ConnectTimeout, nameof(options.ConnectTimeout));
        if (!Enum.IsDefined(options.FrameKind))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The IPC frame kind is invalid.");
        }

        if (options.FrameKind == IpcFrameKind.Secret && !OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Secret IPC requires Windows current-user-only named-pipe security.");
        }

        if (options.MaxConnectAttempts is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxConnectAttempts,
                "The maximum connect attempt count must be between 1 and 100.");
        }

        if (options.InitialReconnectDelay < TimeSpan.Zero ||
            options.MaximumReconnectDelay < options.InitialReconnectDelay ||
            options.MaximumReconnectDelay > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Reconnect delays must be non-negative, ordered, and no longer than one minute.");
        }
    }

    private async Task<HelloResponse> ConnectCoreAsync(CancellationToken cancellationToken)
    {
        Exception? finalTransientError = null;
        var reconnectDelay = _options.InitialReconnectDelay;
        Volatile.Write(ref _lastConnectionAttemptCount, 0);

        for (var attempt = 1; attempt <= _options.MaxConnectAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref _lastConnectionAttemptCount, attempt);
            var candidate = CreateClientStream();
            using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCancellation.CancelAfter(_options.ConnectTimeout);
            try
            {
                await candidate.ConnectAsync(attemptCancellation.Token).ConfigureAwait(false);
                var hello = await NegotiateAsync(candidate, attemptCancellation.Token).ConfigureAwait(false);
                _stream = candidate;
                _serverHello = hello;
                _lastReceivedSequence = 0;
                _lastSentSequence = 0;
                return hello;
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                candidate.Dispose();
                finalTransientError = new TimeoutException(
                    $"Timed out connecting to local pipe '{_options.PipeName}'.",
                    error);
            }
            catch (IOException error) when (error is not IpcProtocolNegotiationException)
            {
                candidate.Dispose();
                finalTransientError = error;
            }
            catch
            {
                candidate.Dispose();
                throw;
            }

            if (attempt == _options.MaxConnectAttempts)
            {
                break;
            }

            if (reconnectDelay > TimeSpan.Zero)
            {
                await Task.Delay(reconnectDelay, cancellationToken).ConfigureAwait(false);
            }

            var doubledTicks = Math.Min(
                reconnectDelay.Ticks * 2,
                _options.MaximumReconnectDelay.Ticks);
            reconnectDelay = TimeSpan.FromTicks(doubledTicks);
        }

        throw finalTransientError ?? new IOException(
            $"Could not connect to local pipe '{_options.PipeName}'.");
    }

    private NamedPipeClientStream CreateClientStream()
    {
        var pipeOptions = PipeOptions.Asynchronous;
        if (OperatingSystem.IsWindows())
        {
            pipeOptions |= PipeOptions.CurrentUserOnly;
        }

        return new NamedPipeClientStream(
            ".",
            _options.PipeName,
            PipeDirection.InOut,
            pipeOptions);
    }

    private async Task<HelloResponse> NegotiateAsync(
        NamedPipeClientStream stream,
        CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid();
        var hello = new HelloRequest(
            _options.ProtocolVersion,
            _options.ClientName,
            _options.ClientVersion,
            _options.ClientInstanceId);
        var requestEnvelope = IpcEnvelope.Create(
            IpcProtocol.HelloRequestMessageType,
            requestId,
            0,
            hello);
        await LengthPrefixedJsonChannel.WriteAsync(
            stream,
            requestEnvelope,
            IpcFrameLimits.NormalMaxBytes,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var responseEnvelope = await LengthPrefixedJsonChannel.ReadAsync<IpcEnvelope>(
            stream,
            IpcFrameLimits.NormalMaxBytes,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        IpcProtocolValidation.ValidateHandshakeEnvelope(
            responseEnvelope,
            IpcProtocol.HelloResponseMessageType);
        if (responseEnvelope.RequestId != requestId)
        {
            throw new IpcProtocolNegotiationException("The handshake response request ID did not match the request.");
        }

        HelloResponse response;
        try
        {
            response = responseEnvelope.DeserializePayload<HelloResponse>();
        }
        catch (JsonException error)
        {
            throw new IpcProtocolNegotiationException(
                $"The agent returned an invalid handshake payload: {error.Message}",
                error);
        }

        if (!response.Accepted)
        {
            throw new IpcProtocolNegotiationException(
                response.RejectionReason ?? "The agent rejected the IPC protocol handshake.");
        }

        if (!_options.ProtocolVersion.IsCompatibleWith(response.ProtocolVersion))
        {
            throw new IpcProtocolNegotiationException(
                $"Agent protocol {response.ProtocolVersion} is incompatible with client protocol {_options.ProtocolVersion}.");
        }

        if (response.ProtocolVersion.Minor > _options.ProtocolVersion.Minor ||
            response.AgentInstanceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(response.AgentVersion))
        {
            throw new IpcProtocolNegotiationException("The agent returned invalid negotiated protocol metadata.");
        }

        return response;
    }

    private NamedPipeClientStream GetConnectedStream() =>
        IsConnected && _stream is not null
            ? _stream
            : throw new InvalidOperationException("The local IPC client is not connected.");

    private void EnsureFrameKind(IpcFrameKind required)
    {
        if (_options.FrameKind != required)
        {
            throw new InvalidOperationException(
                $"This named-pipe client is configured for {_options.FrameKind} frames, not {required} frames.");
        }
    }

    private void DisconnectCore()
    {
        _stream?.Dispose();
        _stream = null;
        _serverHello = null;
        _lastReceivedSequence = 0;
        _lastSentSequence = 0;
    }
}
