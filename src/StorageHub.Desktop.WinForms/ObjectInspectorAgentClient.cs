using System.Text.Json;
using StorageHub.Agent.Ipc;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

public sealed record ObjectInspectorAgentClientOptions
{
    public string PipeName { get; init; } = AgentStatusMonitor.DefaultPipeName;
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

public interface IObjectInspectorAgentClient : IAsyncDisposable
{
    Task<ObjectVersionListResponse> ListVersionsAsync(
        ObjectVersionListRequest request,
        CancellationToken cancellationToken = default);

    Task<ObjectMetadataGetResponse> GetMetadataAsync(
        ObjectMetadataGetRequest request,
        CancellationToken cancellationToken = default);

    Task<ObjectTagsGetResponse> GetTagsAsync(
        ObjectTagsGetRequest request,
        CancellationToken cancellationToken = default);

    Task<EditableFileDownloadResponse> DownloadEditableFileAsync(
        EditableFileDownloadRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This inspector client does not support bounded editor downloads.");

    Task<EditableFileUploadResponse> UploadEditedFileAsync(
        EditableFileUploadRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This inspector client does not support bounded editor uploads.");

    Task<StorageDirectoryEnsureResponse> EnsureDirectoryAsync(
        StorageDirectoryEnsureRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This inspector client does not support directory creation.");
}

/// <summary>
/// Strict, correlated client for exact-object inspection and bounded external editing. A cancelled
/// framed exchange retires its pipe so a late response can never satisfy a subsequent request.
/// </summary>
public sealed class NamedPipeObjectInspectorAgentClient : IObjectInspectorAgentClient
{
    private readonly IStorageIpcTransport _transport;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private long _sendSequence;
    private bool _disposed;

    public NamedPipeObjectInspectorAgentClient(ObjectInspectorAgentClientOptions? options = null)
        : this(
            CreateTransport(options ?? new ObjectInspectorAgentClientOptions()),
            options ?? new ObjectInspectorAgentClientOptions())
    {
    }

    public NamedPipeObjectInspectorAgentClient(
        IStorageIpcTransport transport,
        ObjectInspectorAgentClientOptions? options = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        var effective = options ?? new ObjectInspectorAgentClientOptions();
        ValidateOptions(effective);
        _requestTimeout = effective.RequestTimeout;
    }

    public Task<ObjectVersionListResponse> ListVersionsAsync(
        ObjectVersionListRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request, request?.HasValidBounds == true, nameof(request));
        return ExecuteAsync<ObjectVersionListRequest, ObjectVersionListResponse>(
            ObjectInspectorIpcMessageTypes.VersionListRequest,
            ObjectInspectorIpcMessageTypes.VersionListResponse,
            request!,
            response => ValidateVersionResponse(request!, response),
            cancellationToken);
    }

    public Task<ObjectMetadataGetResponse> GetMetadataAsync(
        ObjectMetadataGetRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request, request?.HasValidBounds == true, nameof(request));
        return ExecuteAsync<ObjectMetadataGetRequest, ObjectMetadataGetResponse>(
            ObjectInspectorIpcMessageTypes.MetadataGetRequest,
            ObjectInspectorIpcMessageTypes.MetadataGetResponse,
            request!,
            response => ValidateMetadataResponse(request!, response),
            cancellationToken);
    }

    public Task<ObjectTagsGetResponse> GetTagsAsync(
        ObjectTagsGetRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request, request?.HasValidBounds == true, nameof(request));
        return ExecuteAsync<ObjectTagsGetRequest, ObjectTagsGetResponse>(
            ObjectInspectorIpcMessageTypes.TagsGetRequest,
            ObjectInspectorIpcMessageTypes.TagsGetResponse,
            request!,
            response => ValidateTagsResponse(request!, response),
            cancellationToken);
    }

    public Task<EditableFileDownloadResponse> DownloadEditableFileAsync(
        EditableFileDownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request, request?.HasValidBounds == true, nameof(request));
        return ExecuteAsync<EditableFileDownloadRequest, EditableFileDownloadResponse>(
            EditableFileIpcMessageTypes.DownloadRequest,
            EditableFileIpcMessageTypes.DownloadResponse,
            request!,
            response => ValidateDownloadResponse(request!, response),
            cancellationToken);
    }

    public Task<EditableFileUploadResponse> UploadEditedFileAsync(
        EditableFileUploadRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request, request?.HasValidBounds == true, nameof(request));
        return ExecuteAsync<EditableFileUploadRequest, EditableFileUploadResponse>(
            EditableFileIpcMessageTypes.UploadRequest,
            EditableFileIpcMessageTypes.UploadResponse,
            request!,
            response => ValidateUploadResponse(request!, response),
            cancellationToken);
    }

    public Task<StorageDirectoryEnsureResponse> EnsureDirectoryAsync(
        StorageDirectoryEnsureRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request, request?.HasValidBounds == true, nameof(request));
        return ExecuteAsync<StorageDirectoryEnsureRequest, StorageDirectoryEnsureResponse>(
            EditableFileIpcMessageTypes.DirectoryEnsureRequest,
            EditableFileIpcMessageTypes.DirectoryEnsureResponse,
            request!,
            response => ValidateDirectoryEnsureResponse(request!, response),
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
            throw new TimeoutException(
                "The local agent inspector request timed out before it could start.",
                error);
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
                throw new InvalidDataException(
                    "The local agent returned invalid object inspector data.",
                    error);
            }

            validateResponse(response);
            return response;
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            await DisconnectAfterFailureAsync().ConfigureAwait(false);
            throw new TimeoutException("The local agent inspector request timed out.", error);
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
            error is IOException or InvalidDataException or InvalidOperationException or JsonException)
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
        IpcEnvelope? envelope,
        Guid expectedRequestId,
        string expectedMessageType)
    {
        if (envelope is null || envelope.RequestId != expectedRequestId || envelope.Sequence <= 0)
        {
            throw InvalidResponse();
        }

        if (string.Equals(
                envelope.MessageType,
                IpcProtocol.ErrorResponseMessageType,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The local agent rejected the object inspector request.");
        }

        if (!string.Equals(envelope.MessageType, expectedMessageType, StringComparison.Ordinal))
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateVersionResponse(
        ObjectVersionListRequest request,
        ObjectVersionListResponse response)
    {
        if (response.ContractVersion != request.ContractVersion ||
            !ObjectInspectorIpcContract.IsSupported(response.ContractVersion) ||
            response.Address != request.Address ||
            response.Address?.HasValidBounds != true ||
            response.Versions is null ||
            response.Versions.Length > request.PageSize ||
            response.Versions.Length > ObjectInspectorIpcLimits.MaximumVersionPageSize ||
            !ObjectVersionListRequest.IsOptionalToken(response.ContinuationToken) ||
            !IsValidFailure(response.Failure) ||
            response.Failure is not null &&
                (response.Versions.Length != 0 || response.ContinuationToken is not null) ||
            response.Versions.Any(version =>
                version?.HasValidBounds != true ||
                version.LastModifiedUtc is { Offset: var offset } && offset != TimeSpan.Zero ||
                !request.IncludeDeleteMarkers && version.IsDeleteMarker) ||
            response.Versions.Select(static version => version.VersionId)
                .Distinct(StringComparer.Ordinal).Count() != response.Versions.Length ||
            response.Versions.Count(static version => version.IsLatest) > 1)
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateMetadataResponse(
        ObjectMetadataGetRequest request,
        ObjectMetadataGetResponse response)
    {
        if (response.ContractVersion != request.ContractVersion ||
            !ObjectInspectorIpcContract.IsSupported(response.ContractVersion) ||
            response.Address != request.Address ||
            response.Address?.HasValidBounds != true ||
            !response.HasValidMetadataBounds ||
            !IsValidFailure(response.Failure) ||
            response.Failure is not null && response.Metadata.Length != 0)
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateTagsResponse(
        ObjectTagsGetRequest request,
        ObjectTagsGetResponse response)
    {
        if (response.ContractVersion != request.ContractVersion ||
            !ObjectInspectorIpcContract.IsSupported(response.ContractVersion) ||
            response.Address != request.Address ||
            response.Address?.HasValidBounds != true ||
            !response.HasValidTagBounds ||
            !IsValidFailure(response.Failure) ||
            response.Failure is not null && response.Tags.Length != 0)
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateDownloadResponse(
        EditableFileDownloadRequest request,
        EditableFileDownloadResponse response)
    {
        if (response.ContractVersion != request.ContractVersion ||
            response.Address != request.Address ||
            response.Content is null ||
            response.Content.Length > request.MaximumBytes ||
            response.Content.Length > EditableFileIpcContract.MaximumContentBytes ||
            !IsSafeContentType(response.ContentType) ||
            !IsValidFailure(response.Failure) ||
            response.Failure is not null && response.Content.Length != 0)
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateUploadResponse(
        EditableFileUploadRequest request,
        EditableFileUploadResponse response)
    {
        if (response.ContractVersion != request.ContractVersion ||
            response.Address?.HasValidBounds != true ||
            response.Address.ConnectionId != request.Address.ConnectionId ||
            !string.Equals(response.Address.RootIdentity, request.Address.RootIdentity, StringComparison.Ordinal) ||
            !string.Equals(response.Address.RelativePath, request.Address.RelativePath, StringComparison.Ordinal) ||
            response.Size is < 0 or > EditableFileIpcContract.MaximumContentBytes ||
            response.LastModifiedUtc is { Offset: var offset } && offset != TimeSpan.Zero ||
            !IsValidFailure(response.Failure) ||
            response.Failure is not null && response.Size != 0)
        {
            throw InvalidResponse();
        }
    }

    private static bool IsSafeContentType(string? value) => value is null ||
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= StorageIpcLimits.MaximumContentTypeLength &&
        !value.Any(char.IsControl);

    private static void ValidateDirectoryEnsureResponse(
        StorageDirectoryEnsureRequest request,
        StorageDirectoryEnsureResponse response)
    {
        if (response.ContractVersion != request.ContractVersion ||
            response.Address != request.Address ||
            !IsValidFailure(response.Failure) ||
            response.Failure is not null && response.Created)
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateRequest<TRequest>(
        TRequest? request,
        bool validBounds,
        string parameterName)
        where TRequest : class
    {
        if (request is null || !validBounds)
        {
            throw new ArgumentException(
                "The object inspector request is outside the negotiated IPC contract bounds.",
                parameterName);
        }
    }

    private static bool IsValidFailure(StorageIpcFailure? failure) => failure is null ||
        !string.IsNullOrWhiteSpace(failure.Code) &&
        failure.Code.Length <= StorageIpcLimits.MaximumFailureCodeLength &&
        failure.Code.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-') &&
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
            // Preserve the protocol or transport failure that made this connection unusable.
        }
    }

    private static InvalidDataException InvalidResponse() =>
        new("The local agent returned object inspector data outside the negotiated bounds.");

    private static NamedPipeObjectInspectorIpcTransport CreateTransport(
        ObjectInspectorAgentClientOptions options)
    {
        ValidateOptions(options);
        var version = typeof(NamedPipeObjectInspectorAgentClient).Assembly
            .GetName().Version?.ToString() ?? "0.1.0";
        return new NamedPipeObjectInspectorIpcTransport(new NamedPipeIpcClient(
            new NamedPipeIpcClientOptions
            {
                PipeName = options.PipeName,
                ClientName = "StorageHub.Desktop.ObjectInspector",
                ClientVersion = version,
                ConnectTimeout = options.ConnectTimeout,
                MaxConnectAttempts = 1,
                InitialReconnectDelay = TimeSpan.Zero,
                MaximumReconnectDelay = TimeSpan.Zero
            }));
    }

    private static void ValidateOptions(ObjectInspectorAgentClientOptions options)
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

        if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The request timeout must be at most one minute.");
        }
    }

    private sealed class NamedPipeObjectInspectorIpcTransport(NamedPipeIpcClient client)
        : IStorageIpcTransport
    {
        private readonly NamedPipeIpcClient _client =
            client ?? throw new ArgumentNullException(nameof(client));

        public bool IsConnected => _client.IsConnected;

        public async Task ConnectAsync(CancellationToken cancellationToken = default) =>
            _ = await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);

        public ValueTask SendAsync(
            IpcEnvelope envelope,
            CancellationToken cancellationToken = default) =>
            _client.SendAsync(envelope, cancellationToken);

        public ValueTask<IpcEnvelope> ReceiveAsync(CancellationToken cancellationToken = default) =>
            _client.ReceiveAsync(cancellationToken);

        public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
            _client.DisconnectAsync(cancellationToken);

        public ValueTask DisposeAsync() => _client.DisposeAsync();
    }
}
