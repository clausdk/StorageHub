using System.Text.Json;
using StorageHub.Agent.Ipc;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

public sealed record KeyStoreAgentClientOptions
{
    public string PipeName { get; init; } = AgentStatusMonitor.DefaultPipeName;
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Reads and manages key store entries over the ordinary agent pipe. Key material is never carried
/// here: importing material is a separate enrollment on the dedicated secret pipe, and this client
/// only ever sees the opaque references that enrollment returns.
/// </summary>
public interface IKeyStoreAgentClient : IAsyncDisposable
{
    Task<KeyStoreListResponse> ListAsync(
        KeyStoreListRequest request,
        CancellationToken cancellationToken = default);

    Task<KeyStoreWriteResponse> CreateAsync(
        KeyStoreCreateRequest request,
        CancellationToken cancellationToken = default);

    Task<KeyStoreWriteResponse> UpdateAsync(
        KeyStoreUpdateRequest request,
        CancellationToken cancellationToken = default);

    Task<KeyStoreWriteResponse> DeleteAsync(
        KeyStoreDeleteRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class NamedPipeKeyStoreAgentClient : IKeyStoreAgentClient
{
    private readonly IStorageIpcTransport _transport;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private long _sendSequence;
    private bool _disposed;

    public NamedPipeKeyStoreAgentClient(KeyStoreAgentClientOptions? options = null)
        : this(
            CreateTransport(options ?? new KeyStoreAgentClientOptions()),
            options ?? new KeyStoreAgentClientOptions())
    {
    }

    public NamedPipeKeyStoreAgentClient(
        IStorageIpcTransport transport,
        KeyStoreAgentClientOptions? options = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        var effective = options ?? new KeyStoreAgentClientOptions();
        ValidateOptions(effective);
        _requestTimeout = effective.RequestTimeout;
    }

    public Task<KeyStoreListResponse> ListAsync(
        KeyStoreListRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request, request?.HasValidBounds == true);
        return ExecuteAsync<KeyStoreListRequest, KeyStoreListResponse>(
            KeyStoreIpcMessageTypes.ListRequest,
            KeyStoreIpcMessageTypes.ListResponse,
            request!,
            ValidateListResponse,
            cancellationToken);
    }

    public Task<KeyStoreWriteResponse> CreateAsync(
        KeyStoreCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request, request?.HasValidBounds == true);
        return ExecuteAsync<KeyStoreCreateRequest, KeyStoreWriteResponse>(
            KeyStoreIpcMessageTypes.CreateRequest,
            KeyStoreIpcMessageTypes.CreateResponse,
            request!,
            ValidateWriteResponse,
            cancellationToken);
    }

    public Task<KeyStoreWriteResponse> UpdateAsync(
        KeyStoreUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request, request?.HasValidBounds == true);
        return ExecuteAsync<KeyStoreUpdateRequest, KeyStoreWriteResponse>(
            KeyStoreIpcMessageTypes.UpdateRequest,
            KeyStoreIpcMessageTypes.UpdateResponse,
            request!,
            ValidateWriteResponse,
            cancellationToken);
    }

    public Task<KeyStoreWriteResponse> DeleteAsync(
        KeyStoreDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request, request?.HasValidBounds == true);
        return ExecuteAsync<KeyStoreDeleteRequest, KeyStoreWriteResponse>(
            KeyStoreIpcMessageTypes.DeleteRequest,
            KeyStoreIpcMessageTypes.DeleteResponse,
            request!,
            ValidateWriteResponse,
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _requestGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _requestGate.Release();
            _requestGate.Dispose();
        }
    }

    private async Task<TResponse> ExecuteAsync<TRequest, TResponse>(
        string requestMessageType,
        string responseMessageType,
        TRequest request,
        Action<TResponse> validateResponse,
        CancellationToken cancellationToken)
        where TRequest : class
        where TResponse : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_requestTimeout);
        try
        {
            await _requestGate.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The local agent key store request timed out before it could start.", error);
        }

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_transport.IsConnected)
            {
                await _transport.ConnectAsync(deadline.Token).ConfigureAwait(false);
            }

            var requestId = Guid.NewGuid();
            var sequence = checked(Interlocked.Increment(ref _sendSequence));
            await _transport.SendAsync(
                IpcEnvelope.Create(requestMessageType, requestId, sequence, request),
                deadline.Token).ConfigureAwait(false);
            var envelope = await _transport.ReceiveAsync(deadline.Token).ConfigureAwait(false);
            ValidateEnvelope(envelope, requestId, responseMessageType);

            TResponse response;
            try
            {
                response = envelope.DeserializePayload<TResponse>();
            }
            catch (JsonException error)
            {
                throw new InvalidDataException("The local agent returned invalid key store data.", error);
            }

            validateResponse(response);
            return response;
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            await DisconnectAfterFailureAsync().ConfigureAwait(false);
            throw new TimeoutException("The local agent key store request timed out.", error);
        }
        catch (OperationCanceledException)
        {
            await DisconnectAfterFailureAsync().ConfigureAwait(false);
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            await DisconnectAfterFailureAsync().ConfigureAwait(false);
            throw new UnauthorizedAccessException(
                "StorageHub could not authenticate to the local background agent.");
        }
        catch (Exception error) when (
            error is IOException or TimeoutException or InvalidDataException or
                InvalidOperationException or JsonException)
        {
            await DisconnectAfterFailureAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    /// <summary>
    /// Rejects a response that steps outside the negotiated bounds. This is also the last line of
    /// defence against an agent that tried to hand back anything other than metadata.
    /// </summary>
    private static void ValidateListResponse(KeyStoreListResponse response)
    {
        if (!KeyStoreIpcContract.IsSupported(response.ContractVersion) ||
            response.Entries is not { Length: <= KeyStoreIpcLimits.MaximumEntriesPerPage } entries ||
            entries.Any(entry => entry is null || !entry.HasValidBounds))
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateWriteResponse(KeyStoreWriteResponse response)
    {
        if (!KeyStoreIpcContract.IsSupported(response.ContractVersion) ||
            !Enum.IsDefined(response.Outcome) ||
            response.Entry is { } entry && !entry.HasValidBounds ||
            response.ActualVersion is <= 0 ||
            response.ReferencedByProfiles is { Length: > KeyStoreIpcLimits.MaximumReferencedProfiles })
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateEnvelope(
        IpcEnvelope? envelope,
        Guid expectedRequestId,
        string expectedMessageType)
    {
        if (envelope is null || envelope.RequestId != expectedRequestId || envelope.Sequence <= 0)
        {
            throw InvalidResponse();
        }

        if (string.Equals(envelope.MessageType, IpcProtocol.ErrorResponseMessageType, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The local agent rejected the key store request.");
        }

        if (!string.Equals(envelope.MessageType, expectedMessageType, StringComparison.Ordinal))
        {
            throw InvalidResponse();
        }
    }

    private async ValueTask DisconnectAfterFailureAsync()
    {
        try
        {
            await _transport.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // The next request reconnects; a failed teardown must not mask the original fault.
        }
    }

    private static void Validate(object? request, bool withinBounds)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!withinBounds)
        {
            throw new ArgumentException(
                "The key store request is outside the negotiated IPC contract bounds.",
                nameof(request));
        }
    }

    private static InvalidDataException InvalidResponse() =>
        new("The local agent returned key store data outside the negotiated bounds.");

    private static NamedPipeKeyStoreIpcTransport CreateTransport(KeyStoreAgentClientOptions options)
    {
        ValidateOptions(options);
        return new NamedPipeKeyStoreIpcTransport(new NamedPipeIpcClient(
            DesktopAgentIpcOptions.Create(
                options.PipeName,
                "StorageHub.Desktop.KeyStore",
                options.ConnectTimeout)));
    }

    private static void ValidateOptions(KeyStoreAgentClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.PipeName) || options.PipeName.Length > 180)
        {
            throw new ArgumentException("A valid local agent pipe name is required.", nameof(options));
        }

        if (options.ConnectTimeout <= TimeSpan.Zero || options.ConnectTimeout > TimeSpan.FromSeconds(15))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The connect timeout must be at most 15 seconds.");
        }

        if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The request timeout must be at most two minutes.");
        }
    }

    private sealed class NamedPipeKeyStoreIpcTransport(NamedPipeIpcClient client) : IStorageIpcTransport
    {
        private readonly NamedPipeIpcClient _client = client ?? throw new ArgumentNullException(nameof(client));

        public bool IsConnected => _client.IsConnected;

        public async Task ConnectAsync(CancellationToken cancellationToken = default) =>
            _ = await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);

        public ValueTask SendAsync(IpcEnvelope envelope, CancellationToken cancellationToken = default) =>
            _client.SendAsync(envelope, cancellationToken);

        public ValueTask<IpcEnvelope> ReceiveAsync(CancellationToken cancellationToken = default) =>
            _client.ReceiveAsync(cancellationToken);

        public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
            _client.DisconnectAsync(cancellationToken);

        public ValueTask DisposeAsync() => _client.DisposeAsync();
    }
}
