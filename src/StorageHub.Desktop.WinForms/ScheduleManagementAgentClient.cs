using System.Text.Json;
using StorageHub.Agent.Ipc;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

public sealed record ScheduleManagementAgentClientOptions
{
    public string PipeName { get; init; } = AgentStatusMonitor.DefaultPipeName;
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

public interface IScheduleManagementAgentClient : IAsyncDisposable
{
    Task<ScheduleListResponse> ListAsync(
        ScheduleListRequest request,
        CancellationToken cancellationToken = default);

    Task<ScheduleGetResponse> GetAsync(
        ScheduleGetRequest request,
        CancellationToken cancellationToken = default);

    Task<ScheduleMutationResponse> CreateAsync(
        ScheduleCreateRequest request,
        CancellationToken cancellationToken = default);

    Task<ScheduleMutationResponse> UpdateAsync(
        ScheduleUpdateRequest request,
        CancellationToken cancellationToken = default);

    Task<ScheduleMutationResponse> SetEnabledAsync(
        ScheduleSetEnabledRequest request,
        CancellationToken cancellationToken = default);

    Task<ScheduleMutationResponse> DeleteAsync(
        ScheduleDeleteRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Strict correlated client for preview-only schedule management.</summary>
public sealed class NamedPipeScheduleManagementAgentClient : IScheduleManagementAgentClient
{
    private readonly IStorageIpcTransport _transport;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private long _sendSequence;
    private bool _disposed;

    public NamedPipeScheduleManagementAgentClient(ScheduleManagementAgentClientOptions? options = null)
        : this(CreateTransport(options ?? new ScheduleManagementAgentClientOptions()), options)
    {
    }

    public NamedPipeScheduleManagementAgentClient(
        IStorageIpcTransport transport,
        ScheduleManagementAgentClientOptions? options = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        var effective = options ?? new ScheduleManagementAgentClientOptions();
        ValidateOptions(effective);
        _requestTimeout = effective.RequestTimeout;
    }

    public Task<ScheduleListResponse> ListAsync(
        ScheduleListRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request, request?.HasValidBounds ?? false, nameof(request));
        return ExecuteAsync<ScheduleListRequest, ScheduleListResponse>(
            ScheduleManagementIpcMessageTypes.ListRequest,
            ScheduleManagementIpcMessageTypes.ListResponse,
            request!,
            response => ValidateListResponse(request!, response),
            cancellationToken);
    }

    public Task<ScheduleGetResponse> GetAsync(
        ScheduleGetRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request, request?.HasValidBounds ?? false, nameof(request));
        return ExecuteAsync<ScheduleGetRequest, ScheduleGetResponse>(
            ScheduleManagementIpcMessageTypes.GetRequest,
            ScheduleManagementIpcMessageTypes.GetResponse,
            request!,
            response => ValidateGetResponse(request!, response),
            cancellationToken);
    }

    public Task<ScheduleMutationResponse> CreateAsync(
        ScheduleCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request, request?.HasValidBounds ?? false, nameof(request));
        return ExecuteAsync<ScheduleCreateRequest, ScheduleMutationResponse>(
            ScheduleManagementIpcMessageTypes.CreateRequest,
            ScheduleManagementIpcMessageTypes.CreateResponse,
            request!,
            response => ValidateMutationResponse(
                request!.ScheduleId,
                expectedRevision: null,
                delete: false,
                response),
            cancellationToken);
    }

    public Task<ScheduleMutationResponse> UpdateAsync(
        ScheduleUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request, request?.HasValidBounds ?? false, nameof(request));
        return ExecuteAsync<ScheduleUpdateRequest, ScheduleMutationResponse>(
            ScheduleManagementIpcMessageTypes.UpdateRequest,
            ScheduleManagementIpcMessageTypes.UpdateResponse,
            request!,
            response => ValidateMutationResponse(
                request!.ScheduleId,
                request.ExpectedRevision,
                delete: false,
                response),
            cancellationToken);
    }

    public Task<ScheduleMutationResponse> SetEnabledAsync(
        ScheduleSetEnabledRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request, request?.HasValidBounds ?? false, nameof(request));
        return ExecuteAsync<ScheduleSetEnabledRequest, ScheduleMutationResponse>(
            ScheduleManagementIpcMessageTypes.SetEnabledRequest,
            ScheduleManagementIpcMessageTypes.SetEnabledResponse,
            request!,
            response => ValidateMutationResponse(
                request!.ScheduleId,
                request.ExpectedRevision,
                delete: false,
                response),
            cancellationToken);
    }

    public Task<ScheduleMutationResponse> DeleteAsync(
        ScheduleDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request, request?.HasValidBounds ?? false, nameof(request));
        return ExecuteAsync<ScheduleDeleteRequest, ScheduleMutationResponse>(
            ScheduleManagementIpcMessageTypes.DeleteRequest,
            ScheduleManagementIpcMessageTypes.DeleteResponse,
            request!,
            response => ValidateMutationResponse(
                request!.ScheduleId,
                request.ExpectedRevision,
                delete: true,
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
                IpcEnvelope.Create(requestType, requestId, sequence, request),
                deadline.Token).ConfigureAwait(false);
            var envelope = await _transport.ReceiveAsync(deadline.Token).ConfigureAwait(false);
            ValidateEnvelope(envelope, requestId, responseType);
            TResponse response;
            try
            {
                response = envelope.DeserializePayload<TResponse>() ?? throw InvalidResponse();
            }
            catch (JsonException error)
            {
                throw new InvalidDataException("The local agent returned invalid schedule data.", error);
            }

            validateResponse(response);
            return response;
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            await DisconnectAfterFailureAsync().ConfigureAwait(false);
            throw new TimeoutException("The local agent schedule request timed out.", error);
        }
        catch (OperationCanceledException)
        {
            await DisconnectAfterFailureAsync().ConfigureAwait(false);
            throw;
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

    private static void ValidateEnvelope(IpcEnvelope envelope, Guid requestId, string responseType)
    {
        if (envelope is null || envelope.RequestId != requestId || envelope.Sequence <= 0)
        {
            throw InvalidResponse();
        }

        if (string.Equals(envelope.MessageType, IpcProtocol.ErrorResponseMessageType, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The local agent rejected the schedule request.");
        }

        if (!string.Equals(envelope.MessageType, responseType, StringComparison.Ordinal))
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateListResponse(ScheduleListRequest request, ScheduleListResponse response)
    {
        ValidateContract(response.ContractVersion);
        if (response.Schedules is null ||
            response.Schedules.Length > request.MaximumCount ||
            response.Schedules.Length > ScheduleManagementIpcLimits.MaximumScheduleResults ||
            !IsValidFailure(response.Failure) ||
            response.Failure is not null && response.Schedules.Length != 0 ||
            response.Schedules.Any(schedule => schedule is null || !IsValidSchedule(schedule)) ||
            response.Schedules.Select(static schedule => schedule.ScheduleId).Distinct().Count() !=
                response.Schedules.Length ||
            !request.IncludeDisabled && response.Schedules.Any(static schedule => !schedule.Enabled))
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateGetResponse(ScheduleGetRequest request, ScheduleGetResponse response)
    {
        ValidateContract(response.ContractVersion);
        if (response.ScheduleId != request.ScheduleId ||
            !IsValidFailure(response.Failure) ||
            (response.Schedule is null) == (response.Failure is null) ||
            response.Schedule is not null &&
                (response.Schedule.ScheduleId != request.ScheduleId || !IsValidSchedule(response.Schedule)))
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateMutationResponse(
        Guid scheduleId,
        long? expectedRevision,
        bool delete,
        ScheduleMutationResponse response)
    {
        ValidateContract(response.ContractVersion);
        if (response.ScheduleId != scheduleId ||
            !Enum.IsDefined(response.Outcome) ||
            !IsValidFailure(response.Failure) ||
            response.ActualRevision is < 0 ||
            response.Schedule is not null &&
                (response.Schedule.ScheduleId != scheduleId ||
                 !IsValidSchedule(response.Schedule) ||
                 response.ActualRevision != response.Schedule.Revision))
        {
            throw InvalidResponse();
        }

        var succeeded = response.Outcome is
            ScheduleMutationOutcome.Succeeded or ScheduleMutationOutcome.AlreadyApplied;
        if (succeeded
            ? response.Failure is not null || delete == (response.Schedule is not null)
            : response.Failure is null || response.Schedule is not null)
        {
            throw InvalidResponse();
        }

        if (succeeded && expectedRevision is { } revision)
        {
            if (delete)
            {
                if (response.ActualRevision != revision)
                {
                    throw InvalidResponse();
                }
            }
            else if (response.Outcome == ScheduleMutationOutcome.Succeeded &&
                     response.Schedule!.Revision <= revision)
            {
                throw InvalidResponse();
            }
        }
    }

    private static bool IsValidSchedule(ScheduleDocument schedule) =>
        schedule.ScheduleId != Guid.Empty &&
        schedule.ProfileId != Guid.Empty &&
        IsSafeText(
            schedule.ProfileDisplayName,
            ScheduleManagementIpcLimits.MaximumProfileDisplayNameLength,
            required: true) &&
        IsSafeText(
            schedule.CronExpression,
            ScheduleManagementIpcLimits.MaximumCronExpressionLength,
            required: true) &&
        IsSafeText(
            schedule.TimeZoneId,
            ScheduleManagementIpcLimits.MaximumTimeZoneIdLength,
            required: true) &&
        schedule.MisfireGraceSeconds is >= ScheduleManagementIpcLimits.MinimumMisfireGraceSeconds and
            <= ScheduleManagementIpcLimits.MaximumMisfireGraceSeconds &&
        (schedule.NextOccurrenceUtc is null || schedule.NextOccurrenceUtc.Value.Offset == TimeSpan.Zero) &&
        (schedule.QueuedOccurrenceUtc is null || schedule.QueuedOccurrenceUtc.Value.Offset == TimeSpan.Zero) &&
        (schedule.Enabled || schedule.NextOccurrenceUtc is null && schedule.QueuedOccurrenceUtc is null) &&
        (schedule.QueueOneWhileRunning || schedule.QueuedOccurrenceUtc is null) &&
        IsSafeText(schedule.LastRunOutcome, ScheduleManagementIpcLimits.MaximumOutcomeLength) &&
        IsSafeText(schedule.LastErrorCode, ScheduleManagementIpcLimits.MaximumErrorCodeLength) &&
        schedule.Revision >= 0 &&
        schedule.ExecutionMode == ScheduleIpcExecutionMode.PreviewOnly;

    private static bool IsValidFailure(StorageIpcFailure? failure) => failure is null ||
        IsSafeText(failure.Code, StorageIpcLimits.MaximumFailureCodeLength, required: true) &&
        IsSafeText(failure.Message, StorageIpcLimits.MaximumFailureMessageLength, required: true) &&
        Enum.IsDefined(failure.Category);

    private static bool IsSafeText(string? value, int maximumLength, bool required = false)
    {
        if (value is null)
        {
            return !required;
        }

        return (!required || !string.IsNullOrWhiteSpace(value)) &&
            value.Length <= maximumLength &&
            !value.Any(char.IsControl);
    }

    private static void ValidateRequest<TRequest>(
        TRequest? request,
        bool validBounds,
        string parameterName)
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(request, parameterName);
        var version = request switch
        {
            ScheduleListRequest value => value.ContractVersion,
            ScheduleGetRequest value => value.ContractVersion,
            ScheduleCreateRequest value => value.ContractVersion,
            ScheduleUpdateRequest value => value.ContractVersion,
            ScheduleSetEnabledRequest value => value.ContractVersion,
            ScheduleDeleteRequest value => value.ContractVersion,
            _ => 0
        };
        if (!ScheduleManagementIpcContract.IsSupported(version) || !validBounds)
        {
            throw new ArgumentException(
                "The schedule request is outside the negotiated IPC contract bounds.",
                parameterName);
        }
    }

    private static void ValidateContract(int version)
    {
        if (!ScheduleManagementIpcContract.IsSupported(version))
        {
            throw InvalidResponse();
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
            // Preserve the protocol or transport failure that made this connection unusable.
        }
    }

    private static InvalidDataException InvalidResponse() =>
        new("The local agent returned schedule data outside the negotiated bounds.");

    private static NamedPipeScheduleIpcTransport CreateTransport(ScheduleManagementAgentClientOptions options)
    {
        ValidateOptions(options);
        var version = typeof(NamedPipeScheduleManagementAgentClient).Assembly.GetName().Version?.ToString() ?? "0.1.0";
        return new NamedPipeScheduleIpcTransport(new NamedPipeIpcClient(new NamedPipeIpcClientOptions
        {
            PipeName = options.PipeName,
            ClientName = "StorageHub.Desktop.ScheduleManagement",
            ClientVersion = version,
            ConnectTimeout = options.ConnectTimeout,
            MaxConnectAttempts = 1,
            InitialReconnectDelay = TimeSpan.Zero,
            MaximumReconnectDelay = TimeSpan.Zero
        }));
    }

    private static void ValidateOptions(ScheduleManagementAgentClientOptions options)
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

    private sealed class NamedPipeScheduleIpcTransport(NamedPipeIpcClient client) : IStorageIpcTransport
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
