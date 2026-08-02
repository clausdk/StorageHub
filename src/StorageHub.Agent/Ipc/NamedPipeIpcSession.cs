using System.IO.Pipes;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Agent.Ipc;

public sealed class NamedPipeIpcSession : IAsyncDisposable
{
    private readonly NamedPipeServerStream _stream;
    private readonly CancellationTokenSource _sessionCancellation;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _requestTimeout;
    private readonly IpcFrameKind _frameKind;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private long _lastReceivedSequence;
    private long _lastSentSequence;
    private bool _disposed;

    internal NamedPipeIpcSession(
        NamedPipeServerStream stream,
        HelloRequest clientHello,
        ProtocolVersion negotiatedProtocolVersion,
        CancellationTokenSource sessionCancellation,
        TimeSpan idleTimeout,
        TimeSpan requestTimeout,
        IpcFrameKind frameKind)
    {
        _stream = stream;
        _sessionCancellation = sessionCancellation;
        _idleTimeout = idleTimeout;
        _requestTimeout = requestTimeout;
        _frameKind = frameKind;
        ClientHello = clientHello;
        NegotiatedProtocolVersion = negotiatedProtocolVersion;
        _sessionCancellation.CancelAfter(_idleTimeout);
    }

    public HelloRequest ClientHello { get; }

    public ProtocolVersion NegotiatedProtocolVersion { get; }

    public bool IsConnected => !_disposed && _stream.IsConnected;

    public async ValueTask<IpcEnvelope> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureFrameKind(IpcFrameKind.Normal);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _sessionCancellation.Token);
        await _readGate.WaitAsync(operationCancellation.Token).ConfigureAwait(false);
        try
        {
            var envelope = await LengthPrefixedJsonChannel.ReadAsync<IpcEnvelope>(
                _stream,
                IpcFrameLimits.NormalMaxBytes,
                cancellationToken: operationCancellation.Token).ConfigureAwait(false);
            IpcProtocolValidation.ValidateNormalEnvelope(envelope, _lastReceivedSequence);
            _lastReceivedSequence = envelope.Sequence;
            _sessionCancellation.CancelAfter(_requestTimeout);
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
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _sessionCancellation.Token);
        await _writeGate.WaitAsync(operationCancellation.Token).ConfigureAwait(false);
        try
        {
            IpcProtocolValidation.ValidateNormalEnvelope(envelope, _lastSentSequence);
            await LengthPrefixedJsonChannel.WriteAsync(
                _stream,
                envelope,
                IpcFrameLimits.NormalMaxBytes,
                cancellationToken: operationCancellation.Token).ConfigureAwait(false);
            _lastSentSequence = envelope.Sequence;
            _sessionCancellation.CancelAfter(_idleTimeout);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<SecretIpcRequestEnvelope> ReceiveSecretAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureFrameKind(IpcFrameKind.Secret);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _sessionCancellation.Token);
        await _readGate.WaitAsync(operationCancellation.Token).ConfigureAwait(false);
        try
        {
            var envelope = await LengthPrefixedJsonChannel.ReadAsync<SecretIpcRequestEnvelope>(
                _stream,
                IpcFrameLimits.SecretMaxBytes,
                cancellationToken: operationCancellation.Token).ConfigureAwait(false);
            IpcProtocolValidation.ValidateSecretEnvelope(
                envelope.MessageType,
                envelope.RequestId,
                envelope.Sequence,
                _lastReceivedSequence);
            _lastReceivedSequence = envelope.Sequence;
            _sessionCancellation.CancelAfter(_requestTimeout);
            return envelope;
        }
        finally
        {
            _readGate.Release();
        }
    }

    public async ValueTask SendSecretAsync(
        SecretIpcResponseEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureFrameKind(IpcFrameKind.Secret);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _sessionCancellation.Token);
        await _writeGate.WaitAsync(operationCancellation.Token).ConfigureAwait(false);
        try
        {
            IpcProtocolValidation.ValidateSecretEnvelope(
                envelope.MessageType,
                envelope.RequestId,
                envelope.Sequence,
                _lastSentSequence);
            await LengthPrefixedJsonChannel.WriteAsync(
                _stream,
                envelope,
                IpcFrameLimits.SecretMaxBytes,
                cancellationToken: operationCancellation.Token).ConfigureAwait(false);
            _lastSentSequence = envelope.Sequence;
            _sessionCancellation.CancelAfter(_idleTimeout);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _stream.Dispose();
        _readGate.Dispose();
        _writeGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private void EnsureFrameKind(IpcFrameKind required)
    {
        if (_frameKind != required)
        {
            throw new InvalidOperationException(
                $"This named-pipe session is configured for {_frameKind} frames, not {required} frames.");
        }
    }
}
