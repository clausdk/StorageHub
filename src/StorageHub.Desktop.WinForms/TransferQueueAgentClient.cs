using System.Text.Json;
using StorageHub.Agent.Ipc;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

public interface ITransferQueueAgentClient : IAsyncDisposable
{
    Task<TransferEnqueueResponse> EnqueueAsync(
        TransferEnqueueRequest request,
        CancellationToken cancellationToken = default);

    Task<TransferListResponse> ListAsync(
        TransferListRequest request,
        CancellationToken cancellationToken = default);

    Task<TransferStatusResponse> GetStatusAsync(
        TransferStatusRequest request,
        CancellationToken cancellationToken = default);

    Task<TransferMutationResponse> CancelAsync(
        TransferCancelRequest request,
        CancellationToken cancellationToken = default);

    Task<TransferMutationResponse> RetryAsync(
        TransferRetryRequest request,
        CancellationToken cancellationToken = default);

    Task<TransferMutationResponse> ReconcileAsync(
        TransferReconcileRequest request,
        CancellationToken cancellationToken = default);

    Task<ShellImportPlanResponse> PlanShellImportAsync(
        ShellImportPlanRequest request,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("This transfer client does not support shell imports.");

    Task<ShellImportCommitResponse> CommitShellImportAsync(
        ShellImportCommitRequest request,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("This transfer client does not support shell imports.");

    Task<ShellExportPrepareResponse> PrepareShellExportAsync(
        ShellExportPrepareRequest request,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("This transfer client does not support shell exports.");

    Task<ExplorerDropBeginResponse> BeginExplorerDropAsync(
        ShellExportPrepareRequest request,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("This transfer client does not support Explorer drops.");

    Task<ExplorerDropCommitResponse> CommitExplorerDropAsync(
        ExplorerDropCommitRequest request,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("This transfer client does not support Explorer drops.");
}

/// <summary>
/// Bounded, strictly correlated client for the ordinary (non-secret) transfer queue IPC surface.
/// One request is in flight per connection so a response can never be attributed to another UI
/// action.
/// </summary>
public sealed class NamedPipeTransferQueueAgentClient : ITransferQueueAgentClient
{
    private readonly IStorageIpcTransport _transport;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private long _sendSequence;
    private bool _disposed;

    public NamedPipeTransferQueueAgentClient(RemoteStorageAgentClientOptions? options = null)
        : this(CreateTransport(options ?? new RemoteStorageAgentClientOptions()), options)
    {
    }

    public NamedPipeTransferQueueAgentClient(
        IStorageIpcTransport transport,
        RemoteStorageAgentClientOptions? options = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        var effectiveOptions = options ?? new RemoteStorageAgentClientOptions();
        ValidateOptions(effectiveOptions);
        _requestTimeout = effectiveOptions.RequestTimeout;
    }

    public Task<TransferEnqueueResponse> EnqueueAsync(
        TransferEnqueueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request.ContractVersion, request.HasValidBounds, nameof(request));
        return ExecuteAsync<TransferEnqueueRequest, TransferEnqueueResponse>(
            TransferQueueIpcMessageTypes.EnqueueRequest,
            TransferQueueIpcMessageTypes.EnqueueResponse,
            request,
            response => ValidateEnqueueResponse(request, response),
            cancellationToken);
    }

    public Task<TransferListResponse> ListAsync(
        TransferListRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request.ContractVersion, request.HasValidBounds, nameof(request));
        return ExecuteAsync<TransferListRequest, TransferListResponse>(
            TransferQueueIpcMessageTypes.ListRequest,
            TransferQueueIpcMessageTypes.ListResponse,
            request,
            response => ValidateListResponse(request, response),
            cancellationToken);
    }

    public Task<TransferStatusResponse> GetStatusAsync(
        TransferStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request.ContractVersion, request.HasValidBounds, nameof(request));
        return ExecuteAsync<TransferStatusRequest, TransferStatusResponse>(
            TransferQueueIpcMessageTypes.StatusRequest,
            TransferQueueIpcMessageTypes.StatusResponse,
            request,
            response => ValidateStatusResponse(request, response),
            cancellationToken);
    }

    public Task<TransferMutationResponse> CancelAsync(
        TransferCancelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request.ContractVersion, request.HasValidBounds, nameof(request));
        return ExecuteMutationAsync(
            TransferQueueIpcMessageTypes.CancelRequest,
            TransferQueueIpcMessageTypes.CancelResponse,
            request,
            request.TransferId,
            cancellationToken);
    }

    public Task<TransferMutationResponse> RetryAsync(
        TransferRetryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request.ContractVersion, request.HasValidBounds, nameof(request));
        return ExecuteMutationAsync(
            TransferQueueIpcMessageTypes.RetryRequest,
            TransferQueueIpcMessageTypes.RetryResponse,
            request,
            request.TransferId,
            cancellationToken);
    }

    public Task<TransferMutationResponse> ReconcileAsync(
        TransferReconcileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request.ContractVersion, request.HasValidBounds, nameof(request));
        return ExecuteMutationAsync(
            TransferQueueIpcMessageTypes.ReconcileRequest,
            TransferQueueIpcMessageTypes.ReconcileResponse,
            request,
            request.TransferId,
            cancellationToken);
    }

    public Task<ShellImportPlanResponse> PlanShellImportAsync(ShellImportPlanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ShellTransferIpcContract.IsSupported(request.ContractVersion) || !request.HasValidBounds)
            throw new ArgumentException("The shell import plan is outside the negotiated IPC contract bounds.", nameof(request));
        return ExecuteAsync<ShellImportPlanRequest, ShellImportPlanResponse>(
            ShellTransferIpcMessageTypes.PlanImportRequest, ShellTransferIpcMessageTypes.PlanImportResponse, request,
            response =>
            {
                if (!ShellTransferIpcContract.IsSupported(response.ContractVersion) || response.Items is null || response.Items.Length > ShellTransferIpcLimits.MaximumEntries ||
                    response.Items.Any(item => item is null || string.IsNullOrWhiteSpace(item.RelativePath) || item.RelativePath.Length > TransferQueueIpcLimits.MaximumRelativePathLength || item.Length < 0) ||
                    response.ReviewToken is { Length: > ShellTransferIpcLimits.MaximumReviewTokenLength }) throw InvalidResponse();
            }, cancellationToken);
    }

    public Task<ShellImportCommitResponse> CommitShellImportAsync(ShellImportCommitRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ShellTransferIpcContract.IsSupported(request.ContractVersion) || !request.HasValidBounds)
            throw new ArgumentException("The shell import commit is outside the negotiated IPC contract bounds.", nameof(request));
        return ExecuteAsync<ShellImportCommitRequest, ShellImportCommitResponse>(
            ShellTransferIpcMessageTypes.CommitImportRequest, ShellTransferIpcMessageTypes.CommitImportResponse, request,
            response =>
            {
                if (!ShellTransferIpcContract.IsSupported(response.ContractVersion) || response.TransferIds is null || response.TransferIds.Any(id => id == Guid.Empty)) throw InvalidResponse();
            }, cancellationToken);
    }

    public Task<ShellExportPrepareResponse> PrepareShellExportAsync(ShellExportPrepareRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ShellTransferIpcContract.IsSupported(request.ContractVersion) || !request.HasValidBounds)
            throw new ArgumentException("The shell export request is outside the negotiated IPC contract bounds.", nameof(request));
        return PrepareShellExportJobAsync(request, cancellationToken);
    }

    public Task<ExplorerDropBeginResponse> BeginExplorerDropAsync(
        ShellExportPrepareRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ShellTransferIpcContract.IsSupported(request.ContractVersion) || !request.HasValidBounds)
            throw new ArgumentException("The Explorer drop request is outside the negotiated IPC contract bounds.", nameof(request));
        return ExecuteAsync<ShellExportPrepareRequest, ExplorerDropBeginResponse>(
            ShellTransferIpcMessageTypes.BeginExplorerDropRequest,
            ShellTransferIpcMessageTypes.BeginExplorerDropResponse,
            request,
            response =>
            {
                if (!ShellTransferIpcContract.IsSupported(response.ContractVersion) ||
                    (response.Failure is null &&
                     (response.DropToken is not { Length: 32 } || !response.DropToken.All(Uri.IsHexDigit) ||
                      string.IsNullOrWhiteSpace(response.MarkerPath) || !Path.IsPathFullyQualified(response.MarkerPath))))
                    throw InvalidResponse();
            }, cancellationToken);
    }

    public Task<ExplorerDropCommitResponse> CommitExplorerDropAsync(
        ExplorerDropCommitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ShellTransferIpcContract.IsSupported(request.ContractVersion) || !request.HasValidBounds)
            throw new ArgumentException("The Explorer drop commit is outside the negotiated IPC contract bounds.", nameof(request));
        return ExecuteAsync<ExplorerDropCommitRequest, ExplorerDropCommitResponse>(
            ShellTransferIpcMessageTypes.CommitExplorerDropRequest,
            ShellTransferIpcMessageTypes.CommitExplorerDropResponse,
            request,
            response =>
            {
                if (!ShellTransferIpcContract.IsSupported(response.ContractVersion) ||
                    (response.Accepted && (response.ExportId == Guid.Empty || string.IsNullOrWhiteSpace(response.DestinationPath) ||
                                           !Path.IsPathFullyQualified(response.DestinationPath))))
                    throw InvalidResponse();
            }, cancellationToken);
    }

    private async Task<ShellExportPrepareResponse> PrepareShellExportJobAsync(
        ShellExportPrepareRequest request,
        CancellationToken cancellationToken)
    {
        var started = await ExecuteAsync<ShellExportPrepareRequest, ShellExportStartResponse>(
            ShellTransferIpcMessageTypes.StartExportRequest,
            ShellTransferIpcMessageTypes.StartExportResponse,
            request,
            response =>
            {
                if (!ShellTransferIpcContract.IsSupported(response.ContractVersion) ||
                    (response.Failure is null && response.ExportId == Guid.Empty)) throw InvalidResponse();
            }, cancellationToken).ConfigureAwait(false);
        if (started.Failure is not null)
        {
            return new ShellExportPrepareResponse(ShellTransferIpcContract.CurrentVersion, [], started.Failure);
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await ExecuteAsync<ShellExportStatusRequest, ShellExportStatusResponse>(
                ShellTransferIpcMessageTypes.ExportStatusRequest,
                ShellTransferIpcMessageTypes.ExportStatusResponse,
                new ShellExportStatusRequest(ShellTransferIpcContract.CurrentVersion, started.ExportId),
                response =>
                {
                    if (!ShellTransferIpcContract.IsSupported(response.ContractVersion) ||
                        response.ExportId != started.ExportId || !Enum.IsDefined(response.State) ||
                        response.DiscoveredEntries < 0 || response.CompletedFiles < 0 || response.CompletedBytes < 0 ||
                        response.LocalPaths is null || response.LocalPaths.Length > ShellTransferIpcLimits.MaximumPaths ||
                        response.LocalPaths.Any(path => string.IsNullOrWhiteSpace(path) || path.Length > ShellTransferIpcLimits.MaximumPathLength || !Path.IsPathFullyQualified(path)))
                        throw InvalidResponse();
                }, cancellationToken).ConfigureAwait(false);
            if (status.State == ShellExportState.Completed)
            {
                return new ShellExportPrepareResponse(status.ContractVersion, status.LocalPaths);
            }
            if (status.State == ShellExportState.Failed || status.Failure is not null)
            {
                return new ShellExportPrepareResponse(status.ContractVersion, [], status.Failure ??
                    new StorageIpcFailure("shell-transfer.failed", StorageIpcFailureCategory.Unavailable,
                        "StorageHub could not prepare the selected items for Explorer.", true));
            }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
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

    private Task<TransferMutationResponse> ExecuteMutationAsync<TRequest>(
        string requestType,
        string responseType,
        TRequest request,
        Guid transferId,
        CancellationToken cancellationToken)
        where TRequest : class => ExecuteAsync<TRequest, TransferMutationResponse>(
            requestType,
            responseType,
            request,
            response => ValidateMutationResponse(transferId, response),
            cancellationToken);

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
                throw new InvalidDataException("The local agent returned an invalid transfer response.", error);
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
            // A response may still arrive after the caller abandons the request. Retire the
            // session so that response can never be consumed as the next command's response.
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
            error is IOException or TimeoutException or UnauthorizedAccessException or
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

    private static void ValidateEnvelope(
        IpcEnvelope envelope,
        Guid expectedRequestId,
        string expectedMessageType)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.RequestId != expectedRequestId || envelope.Sequence <= 0)
        {
            throw InvalidResponse();
        }

        if (string.Equals(envelope.MessageType, IpcProtocol.ErrorResponseMessageType, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The local agent rejected the transfer request.");
        }

        if (!string.Equals(envelope.MessageType, expectedMessageType, StringComparison.Ordinal))
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateEnqueueResponse(
        TransferEnqueueRequest request,
        TransferEnqueueResponse response)
    {
        ValidateContract(response.ContractVersion);
        if (response.TransferId != request.TransferId ||
            !IsValidFailure(response.Failure) ||
            response.AlreadyExisted && !response.Accepted ||
            response.Accepted != (response.Transfer is not null && response.Failure is null) ||
            response.Transfer is not null &&
                (response.Transfer.TransferId != request.TransferId || !IsValidSummary(response.Transfer)))
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateListResponse(TransferListRequest request, TransferListResponse response)
    {
        ValidateContract(response.ContractVersion);
        if (response.Transfers is null ||
            response.Transfers.Length > request.PageSize ||
            response.Transfers.Length > TransferQueueIpcLimits.MaximumPageSize ||
            !IsSafeText(
                response.ContinuationToken,
                TransferQueueIpcLimits.MaximumContinuationTokenLength) ||
            !IsValidFailure(response.Failure) ||
            response.Failure is not null &&
                (response.Transfers.Length != 0 || response.ContinuationToken is not null) ||
            response.Transfers.Any(transfer =>
                transfer is null ||
                !IsValidSummary(transfer) ||
                !request.States.Contains(transfer.State)) ||
            response.Transfers.Select(static transfer => transfer.TransferId).Distinct().Count() !=
                response.Transfers.Length)
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateStatusResponse(
        TransferStatusRequest request,
        TransferStatusResponse response)
    {
        ValidateContract(response.ContractVersion);
        if (response.TransferId != request.TransferId ||
            !IsValidFailure(response.Failure) ||
            (response.Transfer is null) == (response.Failure is null) ||
            response.Transfer is not null &&
                (response.Transfer.TransferId != request.TransferId || !IsValidSummary(response.Transfer)))
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateMutationResponse(Guid transferId, TransferMutationResponse response)
    {
        ValidateContract(response.ContractVersion);
        if (response.TransferId != transferId ||
            !Enum.IsDefined(response.Outcome) ||
            !IsValidFailure(response.Failure) ||
            response.Transfer is not null &&
                (response.Transfer.TransferId != transferId || !IsValidSummary(response.Transfer)))
        {
            throw InvalidResponse();
        }

        var isSuccessful = response.Outcome is
            TransferQueueMutationOutcome.Applied or TransferQueueMutationOutcome.Accepted;
        if (isSuccessful
            ? response.Transfer is null || response.Failure is not null
            : response.Failure is null)
        {
            throw InvalidResponse();
        }
    }

    private static bool IsValidSummary(TransferQueueSummary value) =>
        value.TransferId != Guid.Empty &&
        Enum.IsDefined(value.Operation) &&
        value.SourceConnectionId != Guid.Empty &&
        IsSafeText(value.SourcePath, TransferQueueIpcLimits.MaximumRelativePathLength, allowEmpty: true) &&
        value.DestinationConnectionId != Guid.Empty &&
        IsSafeText(value.DestinationPath, TransferQueueIpcLimits.MaximumRelativePathLength, allowEmpty: true) &&
        Enum.IsDefined(value.State) &&
        value.Revision >= 0 &&
        value.Attempt >= 0 &&
        value.Priority is >= TransferQueueIpcLimits.MinimumPriority and <= TransferQueueIpcLimits.MaximumPriority &&
        value.ExpectedBytes is null or >= 0 &&
        value.ProgressBytes >= 0 &&
        (value.ExpectedBytes is null || value.ProgressBytes <= value.ExpectedBytes) &&
        value.UpdatedUtc.Offset == TimeSpan.Zero &&
        (value.RetryAvailableUtc is null || value.RetryAvailableUtc.Value.Offset == TimeSpan.Zero) &&
        IsSafeText(value.ErrorCode, StorageIpcLimits.MaximumFailureCodeLength) &&
        IsSafeText(value.ErrorSummary, 1_024) &&
        (!value.NeedsReconciliation || value.State is
            TransferQueueState.Interrupted or TransferQueueState.NeedsReconciliation);

    private static bool IsValidFailure(StorageIpcFailure? failure) => failure is null ||
        IsSafeText(failure.Code, StorageIpcLimits.MaximumFailureCodeLength, required: true) &&
        IsSafeText(failure.Message, StorageIpcLimits.MaximumFailureMessageLength, required: true) &&
        Enum.IsDefined(failure.Category);

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

    private static void ValidateContract(int version)
    {
        if (!TransferQueueIpcContract.IsSupported(version))
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateRequest(int version, bool hasValidBounds, string parameterName)
    {
        if (!TransferQueueIpcContract.IsSupported(version) || !hasValidBounds)
        {
            throw new ArgumentException(
                "The transfer request is outside the negotiated IPC contract bounds.",
                parameterName);
        }
    }

    private async Task DisconnectAfterFailureAsync()
    {
        try
        {
            await _transport.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the protocol or transport failure that made the connection unusable.
        }
    }

    private static InvalidDataException InvalidResponse() =>
        new("The local agent returned a transfer response outside the negotiated bounds.");

    private static NamedPipeTransferIpcTransport CreateTransport(RemoteStorageAgentClientOptions options)
    {
        ValidateOptions(options);
        var version = typeof(NamedPipeTransferQueueAgentClient).Assembly.GetName().Version?.ToString() ?? "0.1.0";
        return new NamedPipeTransferIpcTransport(new NamedPipeIpcClient(new NamedPipeIpcClientOptions
        {
            PipeName = options.PipeName,
            ClientName = "StorageHub.Desktop.TransferQueue",
            ClientVersion = version,
            ConnectTimeout = options.ConnectTimeout,
            MaxConnectAttempts = 3,
            InitialReconnectDelay = TimeSpan.FromMilliseconds(100),
            MaximumReconnectDelay = TimeSpan.FromMilliseconds(400)
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

    private sealed class NamedPipeTransferIpcTransport(NamedPipeIpcClient client) : IStorageIpcTransport
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
