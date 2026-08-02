using System.Text.Json;
using StorageHub.Agent.Ipc;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

public sealed record RemoteStorageAgentClientOptions
{
    public string PipeName { get; init; } = AgentStatusMonitor.DefaultPipeName;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>The read-only storage operations exposed by the local StorageHub agent.</summary>
public interface IRemoteStorageAgentClient : IAsyncDisposable
{
    Task<ConnectionListResponse> ListConnectionsAsync(
        ConnectionListRequest request,
        CancellationToken cancellationToken = default);

    Task<ConnectionTestResponse> TestConnectionAsync(
        ConnectionTestRequest request,
        CancellationToken cancellationToken = default);

    Task<StorageListPageResponse> ListStorageAsync(
        StorageListPageRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Small injectable seam around the framed named-pipe client. It exists so request/response
/// correlation and timeout behavior can be verified without depending on a live agent.
/// </summary>
public interface IStorageIpcTransport : IAsyncDisposable
{
    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);

    ValueTask SendAsync(IpcEnvelope envelope, CancellationToken cancellationToken = default);

    ValueTask<IpcEnvelope> ReceiveAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Serializes read-only requests over one authenticated current-user named pipe. A request is
/// accepted only when its ID, response type, contract identity, and resource identity all match.
/// </summary>
public sealed class NamedPipeRemoteStorageAgentClient : IRemoteStorageAgentClient
{
    private readonly IStorageIpcTransport _transport;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private long _sendSequence;
    private bool _disposed;

    public NamedPipeRemoteStorageAgentClient(RemoteStorageAgentClientOptions? options = null)
        : this(CreateTransport(options ?? new RemoteStorageAgentClientOptions()), options ?? new RemoteStorageAgentClientOptions())
    {
    }

    public NamedPipeRemoteStorageAgentClient(
        IStorageIpcTransport transport,
        RemoteStorageAgentClientOptions? options = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        var effectiveOptions = options ?? new RemoteStorageAgentClientOptions();
        ValidateOptions(effectiveOptions);
        _requestTimeout = effectiveOptions.RequestTimeout;
    }

    public Task<ConnectionListResponse> ListConnectionsAsync(
        ConnectionListRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!StorageIpcContract.IsSupported(request.ContractVersion) || !request.HasValidBounds)
        {
            throw new ArgumentException("The connection-list request is outside the storage IPC contract bounds.", nameof(request));
        }

        return ExecuteAsync<ConnectionListRequest, ConnectionListResponse>(
            StorageIpcMessageTypes.ConnectionListRequest,
            StorageIpcMessageTypes.ConnectionListResponse,
            request,
            response => ValidateConnectionListResponse(request, response),
            cancellationToken);
    }

    public Task<ConnectionTestResponse> TestConnectionAsync(
        ConnectionTestRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!StorageIpcContract.IsSupported(request.ContractVersion) || !request.HasValidBounds)
        {
            throw new ArgumentException("The connection-test request is outside the storage IPC contract bounds.", nameof(request));
        }

        return ExecuteAsync<ConnectionTestRequest, ConnectionTestResponse>(
            StorageIpcMessageTypes.ConnectionTestRequest,
            StorageIpcMessageTypes.ConnectionTestResponse,
            request,
            response => ValidateConnectionTestResponse(request, response),
            cancellationToken);
    }

    public Task<StorageListPageResponse> ListStorageAsync(
        StorageListPageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!StorageIpcContract.IsSupported(request.ContractVersion) || !request.HasValidBounds)
        {
            throw new ArgumentException("The storage-list request is outside the storage IPC contract bounds.", nameof(request));
        }

        return ExecuteAsync<StorageListPageRequest, StorageListPageResponse>(
            StorageIpcMessageTypes.StorageListRequest,
            StorageIpcMessageTypes.StorageListResponse,
            request,
            response => ValidateStorageListResponse(request, response),
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
            throw new TimeoutException("The local agent request timed out before it could start.", error);
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
                throw new InvalidDataException("The local agent returned an invalid response payload.", error);
            }

            validateResponse(response);
            return response;
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            await DisconnectAfterFailureAsync().ConfigureAwait(false);
            throw new TimeoutException("The local agent request timed out.", error);
        }
        catch (OperationCanceledException)
        {
            // Once this request owns the session, cancellation can leave an unread response in
            // the pipe. Retire the connection so it cannot be mistaken for the next request.
            await DisconnectAfterFailureAsync().ConfigureAwait(false);
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            await DisconnectAfterFailureAsync().ConfigureAwait(false);
            throw new UnauthorizedAccessException(
                "StorageHub could not authenticate to the local background agent.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or JsonException)
        {
            await DisconnectAfterFailureAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private static void ValidateEnvelope(
        IpcEnvelope envelope,
        Guid expectedRequestId,
        string expectedMessageType)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.RequestId != expectedRequestId || envelope.Sequence <= 0)
        {
            throw new InvalidDataException("The local agent response did not match the active request.");
        }

        if (string.Equals(envelope.MessageType, IpcProtocol.ErrorResponseMessageType, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The local agent rejected the storage request.");
        }

        if (!string.Equals(envelope.MessageType, expectedMessageType, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The local agent returned an unexpected response type.");
        }
    }

    private static void ValidateConnectionListResponse(
        ConnectionListRequest request,
        ConnectionListResponse response)
    {
        ValidateContract(response.ContractVersion);
        if (response.ContractVersion != request.ContractVersion ||
            response.Connections is null ||
            response.Connections.Length > request.Limit ||
            response.Connections.Length > StorageIpcLimits.MaximumConnectionResults ||
            !IsValidFailure(response.Failure) ||
            response.Failure is not null && response.Connections.Length != 0)
        {
            throw InvalidResponse();
        }

        foreach (var connection in response.Connections)
        {
            if (connection is null ||
                connection.ConnectionId == Guid.Empty ||
                !IsSafeText(connection.DisplayName, 512, required: true) ||
                !Enum.IsDefined(connection.Provider) ||
                !IsSafeText(connection.FolderPath, StorageIpcLimits.MaximumRelativePathLength) ||
                connection.Tags is null ||
                connection.Tags.Length > 100 ||
                connection.Tags.Any(tag => !IsSafeText(tag, 256, required: true)) ||
                !IsSafeText(connection.IconKey, 128) ||
                !IsValidAccent(connection.AccentColor) ||
                connection.Version < 1)
            {
                throw InvalidResponse();
            }
        }

        if (response.Connections.Select(static connection => connection.ConnectionId).Distinct().Count() !=
            response.Connections.Length ||
            !request.IncludeDisabled && response.Connections.Any(static connection => !connection.IsEnabled))
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateConnectionTestResponse(
        ConnectionTestRequest request,
        ConnectionTestResponse response)
    {
        ValidateContract(response.ContractVersion);
        if (response.ContractVersion != request.ContractVersion ||
            response.ConnectionId != request.ConnectionId ||
            response.ElapsedMilliseconds < 0 ||
            !IsValidFailure(response.Failure) ||
            response.Succeeded == (response.Failure is not null))
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateStorageListResponse(
        StorageListPageRequest request,
        StorageListPageResponse response)
    {
        ValidateContract(response.ContractVersion);
        var carriesStableIdentities = StorageIpcContract.SupportsStableItemIdentities(
            response.ContractVersion);
        if (response.ContractVersion != request.ContractVersion ||
            response.ConnectionId != request.ConnectionId ||
            !string.Equals(response.RelativePath, request.RelativePath, StringComparison.Ordinal) ||
            response.Entries is null ||
            response.Entries.Length > request.PageSize ||
            response.Entries.Length > StorageIpcLimits.MaximumStoragePageSize ||
            !IsSafeText(response.RelativePath, StorageIpcLimits.MaximumRelativePathLength, allowEmpty: true) ||
            !IsSafeText(response.ContinuationToken, StorageIpcLimits.MaximumContinuationTokenLength) ||
            !IsValidFailure(response.Failure) ||
            response.Failure is not null &&
                (response.Entries.Length != 0 ||
                 response.ContinuationToken is not null ||
                 response.RootIdentity is not null) ||
            response.Failure is null && carriesStableIdentities != response.HasValidRootIdentity ||
            !carriesStableIdentities && response.RootIdentity is not null)
        {
            throw InvalidResponse();
        }

        foreach (var entry in response.Entries)
        {
            if (entry is null ||
                !IsSafeText(entry.Name, StorageIpcLimits.MaximumItemNameLength, required: true) ||
                !IsSafeText(entry.RelativePath, StorageIpcLimits.MaximumRelativePathLength, required: true) ||
                !Enum.IsDefined(entry.Kind) ||
                entry.Size is < 0 ||
                !IsSafeText(entry.ContentType, StorageIpcLimits.MaximumContentTypeLength) ||
                !entry.HasValidIdentityBounds ||
                !carriesStableIdentities &&
                    (entry.NativeItemId is not null ||
                     entry.VersionId is not null ||
                     entry.EntityTag is not null) ||
                !IsWithinPage(entry.RelativePath, response.RelativePath) ||
                entry.Kind is StorageItemKind.Directory or StorageItemKind.Prefix && !entry.IsContainer ||
                entry.Kind == StorageItemKind.File && entry.IsContainer)
            {
                throw InvalidResponse();
            }
        }

        if (response.Entries.Select(static entry => entry.RelativePath).Distinct(StringComparer.Ordinal).Count() !=
            response.Entries.Length)
        {
            throw InvalidResponse();
        }
    }

    private static bool IsWithinPage(string itemPath, string parentPath)
    {
        if (string.Equals(itemPath, parentPath, StringComparison.Ordinal))
        {
            return true;
        }

        var prefix = parentPath.Length == 0 ? string.Empty : parentPath + "/";
        if (!itemPath.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = itemPath[prefix.Length..];
        return remainder.Length > 0 && !remainder.Contains('/', StringComparison.Ordinal);
    }

    private static void ValidateContract(int version)
    {
        if (!StorageIpcContract.IsSupported(version))
        {
            throw InvalidResponse();
        }
    }

    private static bool IsValidFailure(StorageIpcFailure? failure) => failure is null ||
        IsSafeText(failure.Code, StorageIpcLimits.MaximumFailureCodeLength, required: true) &&
        IsSafeText(failure.Message, StorageIpcLimits.MaximumFailureMessageLength, required: true) &&
        Enum.IsDefined(failure.Category);

    private static bool IsValidAccent(string? value)
    {
        if (value is null)
        {
            return true;
        }

        if (value.Length != 7 || value[0] != '#')
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            if (!Uri.IsHexDigit(value[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSafeText(
        string? value,
        int maximumLength,
        bool required = false,
        bool allowEmpty = false)
    {
        if (value is null)
        {
            return !required;
        }

        if (required && string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return (allowEmpty || value.Length > 0 || !required) &&
            value.Length <= maximumLength &&
            !value.Any(char.IsControl);
    }

    private async Task DisconnectAfterFailureAsync()
    {
        try
        {
            await _transport.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the protocol/transport error that made this connection unusable.
        }
    }

    private static InvalidDataException InvalidResponse() =>
        new("The local agent returned a storage response outside the negotiated bounds.");

    private static NamedPipeStorageIpcTransport CreateTransport(RemoteStorageAgentClientOptions options)
    {
        ValidateOptions(options);
        var version = typeof(NamedPipeRemoteStorageAgentClient).Assembly.GetName().Version?.ToString() ?? "0.1.0";
        return new NamedPipeStorageIpcTransport(new NamedPipeIpcClient(new NamedPipeIpcClientOptions
        {
            PipeName = options.PipeName,
            ClientName = "StorageHub.Desktop.StorageBrowser",
            ClientVersion = version,
            ConnectTimeout = options.ConnectTimeout,
            MaxConnectAttempts = 1,
            InitialReconnectDelay = TimeSpan.Zero,
            MaximumReconnectDelay = TimeSpan.Zero
        }));
    }

    private static void ValidateOptions(RemoteStorageAgentClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.PipeName) || options.PipeName.Length > 180)
        {
            throw new ArgumentException("A valid local agent pipe name is required.", nameof(options));
        }

        if (options.ConnectTimeout <= TimeSpan.Zero || options.ConnectTimeout > TimeSpan.FromSeconds(15))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The connect timeout must be at most 15 seconds.");
        }

        if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The request timeout must be at most one minute.");
        }
    }

    private sealed class NamedPipeStorageIpcTransport(NamedPipeIpcClient client) : IStorageIpcTransport
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
