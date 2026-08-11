using System.Security.Cryptography;
using System.Text.Json;
using StorageHub.Agent.Ipc;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

public interface IRemoteConnectionProfileClient : IAsyncDisposable
{
    Task<ConnectionProfileGetResponse> GetAsync(
        ConnectionProfileGetRequest request,
        CancellationToken cancellationToken = default);

    Task<ConnectionProfileWriteResponse> CreateAsync(
        ConnectionProfileCreateRequest request,
        CancellationToken cancellationToken = default);

    Task<ConnectionProfileWriteResponse> UpdateAsync(
        ConnectionProfileUpdateRequest request,
        CancellationToken cancellationToken = default);

    Task<ConnectionProfileWriteResponse> DeleteAsync(
        ConnectionProfileDeleteRequest request,
        CancellationToken cancellationToken = default);

    Task<ConnectionTrustGetResponse> GetTrustAsync(
        ConnectionTrustGetRequest request,
        CancellationToken cancellationToken = default);

    Task<ConnectionSshHostKeyDiscoveryResponse> DiscoverSshHostKeyAsync(
        ConnectionSshHostKeyDiscoveryRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This profile client does not support SSH host-key discovery.");

    Task<ConnectionTrustMutationResponse> DecideTrustAsync(
        ConnectionTrustDecisionRequest request,
        CancellationToken cancellationToken = default);

    Task<ConnectionTrustMutationResponse> RolloverTrustAsync(
        ConnectionTrustRolloverRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class NamedPipeRemoteConnectionProfileClient : IRemoteConnectionProfileClient
{
    private readonly IStorageIpcTransport _transport;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private long _sendSequence;
    private bool _disposed;

    public NamedPipeRemoteConnectionProfileClient(RemoteStorageAgentClientOptions? options = null)
        : this(CreateTransport(options ?? new RemoteStorageAgentClientOptions()), options?.RequestTimeout)
    {
    }

    public NamedPipeRemoteConnectionProfileClient(
        IStorageIpcTransport transport,
        TimeSpan? requestTimeout = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(15);
        ValidateTimeout(_requestTimeout);
    }

    public Task<ConnectionProfileGetResponse> GetAsync(
        ConnectionProfileGetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureRequest(request.ContractVersion, request.HasValidBounds, nameof(request));
        return ExecuteAsync<ConnectionProfileGetRequest, ConnectionProfileGetResponse>(
            ConnectionProfileIpcMessageTypes.GetRequest,
            ConnectionProfileIpcMessageTypes.GetResponse,
            request,
            response => ValidateGetResponse(request, response),
            cancellationToken);
    }

    public Task<ConnectionProfileWriteResponse> CreateAsync(
        ConnectionProfileCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureRequest(request.ContractVersion, request.HasValidBounds, nameof(request));
        return ExecuteAsync<ConnectionProfileCreateRequest, ConnectionProfileWriteResponse>(
            ConnectionProfileIpcMessageTypes.CreateRequest,
            ConnectionProfileIpcMessageTypes.CreateResponse,
            request,
            response => ValidateWriteResponse(response, expectedId: null, expectedVersion: 1, requireProfile: true),
            cancellationToken);
    }

    public Task<ConnectionProfileWriteResponse> UpdateAsync(
        ConnectionProfileUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureRequest(request.ContractVersion, request.HasValidBounds, nameof(request));
        return ExecuteAsync<ConnectionProfileUpdateRequest, ConnectionProfileWriteResponse>(
            ConnectionProfileIpcMessageTypes.UpdateRequest,
            ConnectionProfileIpcMessageTypes.UpdateResponse,
            request,
            response => ValidateWriteResponse(
                response,
                request.ConnectionId,
                checked(request.ExpectedVersion + 1),
                requireProfile: true),
            cancellationToken);
    }

    public Task<ConnectionProfileWriteResponse> DeleteAsync(
        ConnectionProfileDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureRequest(request.ContractVersion, request.HasValidBounds, nameof(request));
        return ExecuteAsync<ConnectionProfileDeleteRequest, ConnectionProfileWriteResponse>(
            ConnectionProfileIpcMessageTypes.DeleteRequest,
            ConnectionProfileIpcMessageTypes.DeleteResponse,
            request,
            response => ValidateWriteResponse(
                response,
                request.ConnectionId,
                checked(request.ExpectedVersion + 1),
                requireProfile: false),
            cancellationToken);
    }

    public Task<ConnectionTrustGetResponse> GetTrustAsync(
        ConnectionTrustGetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureTrustRequest(request.ContractVersion, request.HasValidBounds, nameof(request));
        return ExecuteAsync<ConnectionTrustGetRequest, ConnectionTrustGetResponse>(
            ConnectionTrustIpcMessageTypes.GetRequest,
            ConnectionTrustIpcMessageTypes.GetResponse,
            request,
            response => ValidateTrustGetResponse(request, response),
            cancellationToken);
    }

    public Task<ConnectionSshHostKeyDiscoveryResponse> DiscoverSshHostKeyAsync(
        ConnectionSshHostKeyDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureTrustRequest(request.ContractVersion, request.HasValidBounds, nameof(request));
        return ExecuteAsync<ConnectionSshHostKeyDiscoveryRequest, ConnectionSshHostKeyDiscoveryResponse>(
            ConnectionTrustIpcMessageTypes.DiscoverSshHostKeyRequest,
            ConnectionTrustIpcMessageTypes.DiscoverSshHostKeyResponse,
            request,
            response => ValidateDiscoveryResponse(request, response),
            cancellationToken);
    }

    public Task<ConnectionTrustMutationResponse> DecideTrustAsync(
        ConnectionTrustDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureTrustRequest(request.ContractVersion, request.HasValidBounds, nameof(request));
        return ExecuteAsync<ConnectionTrustDecisionRequest, ConnectionTrustMutationResponse>(
            ConnectionTrustIpcMessageTypes.DecideRequest,
            ConnectionTrustIpcMessageTypes.DecideResponse,
            request,
            response => ValidateTrustMutationResponse(
                request.ConnectionId,
                request.ExpectedProfileVersion,
                response),
            cancellationToken);
    }

    public Task<ConnectionTrustMutationResponse> RolloverTrustAsync(
        ConnectionTrustRolloverRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureTrustRequest(request.ContractVersion, request.HasValidBounds, nameof(request));
        return ExecuteAsync<ConnectionTrustRolloverRequest, ConnectionTrustMutationResponse>(
            ConnectionTrustIpcMessageTypes.RolloverRequest,
            ConnectionTrustIpcMessageTypes.RolloverResponse,
            request,
            response => ValidateTrustMutationResponse(
                request.ConnectionId,
                request.ExpectedProfileVersion,
                response),
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
        string requestType,
        string responseType,
        TRequest request,
        Action<TResponse> validate,
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
            throw new TimeoutException("The profile request timed out before it could start.", error);
        }

        try
        {
            if (!_transport.IsConnected)
            {
                await _transport.ConnectAsync(deadline.Token).ConfigureAwait(false);
            }

            var requestId = Guid.NewGuid();
            var sequence = checked(Interlocked.Increment(ref _sendSequence));
            await _transport.SendAsync(
                IpcEnvelope.Create(requestType, requestId, sequence, request),
                deadline.Token).ConfigureAwait(false);
            var envelope = await _transport.ReceiveAsync(deadline.Token).ConfigureAwait(false);
            ValidateEnvelope(envelope, requestId, responseType);
            var response = envelope.DeserializePayload<TResponse>();
            validate(response);
            return response;
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            await DisconnectAfterFailureAsync().ConfigureAwait(false);
            throw new TimeoutException("The profile request timed out.", error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DisconnectAfterFailureAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException or
            InvalidDataException or InvalidOperationException or JsonException)
        {
            await DisconnectAfterFailureAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private static void ValidateGetResponse(
        ConnectionProfileGetRequest request,
        ConnectionProfileGetResponse response)
    {
        ValidateContract(response.ContractVersion);
        if (!IsValidFailure(response.Failure) ||
            response.Failure is null != (response.Profile is not null) ||
            response.Profile is { } profile &&
                (!profile.HasValidBounds || profile.ConnectionId != request.ConnectionId))
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateWriteResponse(
        ConnectionProfileWriteResponse response,
        Guid? expectedId,
        long expectedVersion,
        bool requireProfile)
    {
        ValidateContract(response.ContractVersion);
        if (!Enum.IsDefined(response.Status) || !IsValidFailure(response.Failure))
        {
            throw InvalidResponse();
        }

        if (response.Status == ConnectionProfileWriteStatus.Succeeded)
        {
            if (response.Failure is not null || response.ActualVersion != expectedVersion ||
                requireProfile && response.Profile is null ||
                response.Profile is { } profile &&
                    (!profile.HasValidBounds || profile.Version != expectedVersion ||
                     expectedId is { } id && profile.ConnectionId != id))
            {
                throw InvalidResponse();
            }
        }
        else if (response.Failure is null || response.Profile is not null ||
                 response.ActualVersion is { } actualVersion && actualVersion <= 0)
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateTrustGetResponse(
        ConnectionTrustGetRequest request,
        ConnectionTrustGetResponse response)
    {
        ValidateTrustContract(response.ContractVersion);
        if (!IsValidFailure(response.Failure) ||
            response.Failure is null != (response.Snapshot is not null) ||
            response.Snapshot is { } snapshot &&
                (!snapshot.HasValidBounds ||
                 snapshot.ConnectionId != request.ConnectionId ||
                 snapshot.ProfileVersion != request.ExpectedProfileVersion))
        {
            throw InvalidTrustResponse();
        }
    }

    private static void ValidateDiscoveryResponse(
        ConnectionSshHostKeyDiscoveryRequest request,
        ConnectionSshHostKeyDiscoveryResponse response)
    {
        if (!response.HasValidBounds || !IsValidFailure(response.Failure) ||
            response.Target is not { } target ||
            !string.Equals(target.CanonicalHost, request.Host, StringComparison.OrdinalIgnoreCase) ||
            target.Port != request.Port)
        {
            throw InvalidTrustResponse();
        }
    }

    private static void ValidateTrustMutationResponse(
        Guid expectedConnectionId,
        long expectedProfileVersion,
        ConnectionTrustMutationResponse response)
    {
        ValidateTrustContract(response.ContractVersion);
        if (!Enum.IsDefined(response.Status) || !IsValidFailure(response.Failure))
        {
            throw InvalidTrustResponse();
        }

        if (response.Status == ConnectionTrustMutationStatus.Succeeded)
        {
            if (response.Failure is not null || response.Snapshot is not { HasValidBounds: true } snapshot ||
                snapshot.ConnectionId != expectedConnectionId ||
                snapshot.ProfileVersion != expectedProfileVersion)
            {
                throw InvalidTrustResponse();
            }
        }
        else if (response.Failure is null || response.Snapshot is not null)
        {
            throw InvalidTrustResponse();
        }
    }

    private static void EnsureRequest(int contractVersion, bool hasValidBounds, string parameterName)
    {
        if (!ConnectionProfileIpcContract.IsSupported(contractVersion) || !hasValidBounds)
        {
            throw new ArgumentException(
                "The profile request is outside the connection-management contract bounds.",
                parameterName);
        }
    }

    private static void EnsureTrustRequest(int contractVersion, bool hasValidBounds, string parameterName)
    {
        if (!ConnectionTrustIpcContract.IsSupported(contractVersion) || !hasValidBounds)
        {
            throw new ArgumentException(
                "The trust request is outside the connection-trust contract bounds.",
                parameterName);
        }
    }

    private static void ValidateEnvelope(IpcEnvelope envelope, Guid requestId, string responseType)
    {
        if (envelope.RequestId != requestId || envelope.Sequence <= 0 ||
            !string.Equals(envelope.MessageType, responseType, StringComparison.Ordinal))
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateContract(int version)
    {
        if (!ConnectionProfileIpcContract.IsSupported(version))
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateTrustContract(int version)
    {
        if (!ConnectionTrustIpcContract.IsSupported(version))
        {
            throw InvalidTrustResponse();
        }
    }

    private static bool IsValidFailure(StorageIpcFailure? failure) => failure is null ||
        !string.IsNullOrWhiteSpace(failure.Code) &&
        failure.Code.Length <= StorageIpcLimits.MaximumFailureCodeLength &&
        !failure.Code.Any(char.IsControl) &&
        !string.IsNullOrWhiteSpace(failure.Message) &&
        failure.Message.Length <= StorageIpcLimits.MaximumFailureMessageLength &&
        !failure.Message.Any(char.IsControl) &&
        Enum.IsDefined(failure.Category);

    private async Task DisconnectAfterFailureAsync()
    {
        try
        {
            await _transport.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static InvalidDataException InvalidResponse() =>
        new("The local agent returned a profile response outside the negotiated bounds.");

    private static InvalidDataException InvalidTrustResponse() =>
        new("The local agent returned a trust response outside the negotiated bounds.");

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }

    private static ProfileNamedPipeTransport CreateTransport(RemoteStorageAgentClientOptions options)
    {
        var version = typeof(NamedPipeRemoteConnectionProfileClient).Assembly.GetName().Version?.ToString() ?? "0.1.0";
        return new ProfileNamedPipeTransport(new NamedPipeIpcClient(new NamedPipeIpcClientOptions
        {
            PipeName = options.PipeName,
            ClientName = "StorageHub.Desktop.ConnectionManager",
            ClientVersion = version,
            ConnectTimeout = options.ConnectTimeout,
            MaxConnectAttempts = 3,
            InitialReconnectDelay = TimeSpan.FromMilliseconds(100),
            MaximumReconnectDelay = TimeSpan.FromMilliseconds(400)
        }));
    }

    private sealed class ProfileNamedPipeTransport(NamedPipeIpcClient client) : IStorageIpcTransport
    {
        public bool IsConnected => client.IsConnected;
        public async Task ConnectAsync(CancellationToken cancellationToken = default) =>
            _ = await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        public ValueTask SendAsync(IpcEnvelope envelope, CancellationToken cancellationToken = default) =>
            client.SendAsync(envelope, cancellationToken);
        public ValueTask<IpcEnvelope> ReceiveAsync(CancellationToken cancellationToken = default) =>
            client.ReceiveAsync(cancellationToken);
        public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
            client.DisconnectAsync(cancellationToken);
        public ValueTask DisposeAsync() => client.DisposeAsync();
    }
}

public interface IRemoteSecretVaultClient : IAsyncDisposable
{
    Task<SecretVaultResponse> EnrollAsync(
        SecretMaterialPurpose purpose,
        ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken = default);

    Task<SecretVaultResponse> UpdateAsync(
        string reference,
        SecretMaterialPurpose purpose,
        ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken = default);

    Task<SecretVaultResponse> DeleteAsync(
        string reference,
        SecretMaterialPurpose purpose,
        CancellationToken cancellationToken = default);
}

public interface ISecretIpcTransport : IAsyncDisposable
{
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken cancellationToken = default);
    ValueTask SendAsync(SecretIpcRequestEnvelope envelope, CancellationToken cancellationToken = default);
    ValueTask<SecretIpcResponseEnvelope> ReceiveAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

public sealed record RemoteSecretVaultClientOptions
{
    public string PipeName { get; init; } = StorageHubIpcPipeNames.Secret;
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed class NamedPipeRemoteSecretVaultClient : IRemoteSecretVaultClient
{
    private readonly ISecretIpcTransport _transport;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private long _sendSequence;
    private bool _disposed;

    public NamedPipeRemoteSecretVaultClient(RemoteSecretVaultClientOptions? options = null)
        : this(CreateTransport(options ?? new RemoteSecretVaultClientOptions()), options?.RequestTimeout)
    {
    }

    public NamedPipeRemoteSecretVaultClient(
        ISecretIpcTransport transport,
        TimeSpan? requestTimeout = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        if (_requestTimeout <= TimeSpan.Zero || _requestTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }
    }

    public Task<SecretVaultResponse> EnrollAsync(
        SecretMaterialPurpose purpose,
        ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken = default) => ExecuteWithMaterialAsync(
        SecretVaultIpcMessageTypes.EnrollRequest,
        SecretVaultIpcMessageTypes.EnrollResponse,
        SecretVaultOperation.Enroll,
        purpose,
        reference: null,
        secret,
        cancellationToken);

    public Task<SecretVaultResponse> UpdateAsync(
        string reference,
        SecretMaterialPurpose purpose,
        ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken = default) => ExecuteWithMaterialAsync(
        SecretVaultIpcMessageTypes.UpdateRequest,
        SecretVaultIpcMessageTypes.UpdateResponse,
        SecretVaultOperation.Update,
        purpose,
        reference,
        secret,
        cancellationToken);

    public Task<SecretVaultResponse> DeleteAsync(
        string reference,
        SecretMaterialPurpose purpose,
        CancellationToken cancellationToken = default) => ExecuteAsync(
        SecretVaultIpcMessageTypes.DeleteRequest,
        SecretVaultIpcMessageTypes.DeleteResponse,
        new SecretVaultRequest(
            SecretVaultIpcContract.CurrentVersion,
            SecretVaultOperation.Delete,
            purpose,
            reference,
            SecretMaterial: null),
        cancellationToken);

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

    private async Task<SecretVaultResponse> ExecuteWithMaterialAsync(
        string requestType,
        string responseType,
        SecretVaultOperation operation,
        SecretMaterialPurpose purpose,
        string? reference,
        ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken)
    {
        if (secret.Length is <= 0 or > SecretVaultIpcContract.MaximumSecretBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(secret));
        }

        var copy = secret.ToArray();
        try
        {
            return await ExecuteAsync(
                requestType,
                responseType,
                new SecretVaultRequest(
                    SecretVaultIpcContract.CurrentVersion,
                    operation,
                    purpose,
                    reference,
                    copy),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    private async Task<SecretVaultResponse> ExecuteAsync(
        string requestType,
        string responseType,
        SecretVaultRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!SecretVaultIpcContract.IsSupported(request.ContractVersion) || !request.HasValidBounds)
        {
            throw new ArgumentException("The secret request is outside the secret IPC contract bounds.", nameof(request));
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_requestTimeout);
        try
        {
            await _requestGate.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The secret request timed out before it could start.", error);
        }

        try
        {
            if (!_transport.IsConnected)
            {
                await _transport.ConnectAsync(deadline.Token).ConfigureAwait(false);
            }

            var requestId = Guid.NewGuid();
            await _transport.SendAsync(
                new SecretIpcRequestEnvelope(
                    requestType,
                    requestId,
                    checked(Interlocked.Increment(ref _sendSequence)),
                    request),
                deadline.Token).ConfigureAwait(false);
            var response = await _transport.ReceiveAsync(deadline.Token).ConfigureAwait(false);
            ValidateResponse(response, requestId, responseType, request.Operation, request.Reference);
            return response.Payload;
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            await DisconnectAfterFailureAsync().ConfigureAwait(false);
            throw new TimeoutException("The secret request timed out.", error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DisconnectAfterFailureAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException or
            InvalidDataException or InvalidOperationException or JsonException)
        {
            await DisconnectAfterFailureAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private static void ValidateResponse(
        SecretIpcResponseEnvelope response,
        Guid requestId,
        string expectedType,
        SecretVaultOperation operation,
        string? expectedReference)
    {
        if (response.Payload is null || response.RequestId != requestId || response.Sequence <= 0 ||
            !string.Equals(response.MessageType, expectedType, StringComparison.Ordinal) ||
            !SecretVaultIpcContract.IsSupported(response.Payload.ContractVersion) ||
            response.Payload.Operation != operation ||
            !IsValidFailure(response.Payload.Failure))
        {
            throw InvalidResponse();
        }

        if (response.Payload.Succeeded)
        {
            var validSuccess = response.Payload.Failure is null && (operation switch
            {
                SecretVaultOperation.Enroll or SecretVaultOperation.Update =>
                    ConnectionEndpointDocument.IsOpaqueSecretReference(response.Payload.Reference) &&
                    response.Payload.Reference is not null && response.Payload.Version is > 0 &&
                    (operation != SecretVaultOperation.Update ||
                     string.Equals(response.Payload.Reference, expectedReference, StringComparison.Ordinal)),
                SecretVaultOperation.Delete => response.Payload.Reference is null && response.Payload.Version is null,
                _ => false
            });
            if (!validSuccess)
            {
                throw InvalidResponse();
            }
        }
        else if (response.Payload.Failure is null ||
                 response.Payload.Reference is not null || response.Payload.Version is not null)
        {
            throw InvalidResponse();
        }
    }

    private static bool IsValidFailure(StorageIpcFailure? failure) => failure is null ||
        !string.IsNullOrWhiteSpace(failure.Code) &&
        failure.Code.Length <= StorageIpcLimits.MaximumFailureCodeLength &&
        !failure.Code.Any(char.IsControl) &&
        !string.IsNullOrWhiteSpace(failure.Message) &&
        failure.Message.Length <= StorageIpcLimits.MaximumFailureMessageLength &&
        !failure.Message.Any(char.IsControl) &&
        Enum.IsDefined(failure.Category);

    private async Task DisconnectAfterFailureAsync()
    {
        try
        {
            await _transport.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static InvalidDataException InvalidResponse() =>
        new("The local agent returned a secret response outside the negotiated bounds.");

    private static SecretNamedPipeTransport CreateTransport(RemoteSecretVaultClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ConnectTimeout <= TimeSpan.Zero || options.ConnectTimeout > TimeSpan.FromSeconds(15) ||
            options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        var version = typeof(NamedPipeRemoteSecretVaultClient).Assembly.GetName().Version?.ToString() ?? "0.1.0";
        return new SecretNamedPipeTransport(new NamedPipeIpcClient(new NamedPipeIpcClientOptions
        {
            PipeName = options.PipeName,
            ClientName = "StorageHub.Desktop.SecretEnrollment",
            ClientVersion = version,
            ConnectTimeout = options.ConnectTimeout,
            MaxConnectAttempts = 3,
            InitialReconnectDelay = TimeSpan.FromMilliseconds(100),
            MaximumReconnectDelay = TimeSpan.FromMilliseconds(400),
            FrameKind = IpcFrameKind.Secret
        }));
    }

    private sealed class SecretNamedPipeTransport(NamedPipeIpcClient client) : ISecretIpcTransport
    {
        public bool IsConnected => client.IsConnected;
        public async Task ConnectAsync(CancellationToken cancellationToken = default) =>
            _ = await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        public ValueTask SendAsync(
            SecretIpcRequestEnvelope envelope,
            CancellationToken cancellationToken = default) => client.SendSecretAsync(envelope, cancellationToken);
        public ValueTask<SecretIpcResponseEnvelope> ReceiveAsync(CancellationToken cancellationToken = default) =>
            client.ReceiveSecretAsync(cancellationToken);
        public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
            client.DisconnectAsync(cancellationToken);
        public ValueTask DisposeAsync() => client.DisposeAsync();
    }
}
