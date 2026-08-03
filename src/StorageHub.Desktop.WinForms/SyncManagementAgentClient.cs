using System.Text.Json;
using StorageHub.Agent.Ipc;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

public sealed record SyncManagementAgentClientOptions
{
    public string PipeName { get; init; } = AgentStatusMonitor.DefaultPipeName;
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(2);
}

public interface ISyncManagementAgentClient : IAsyncDisposable
{
    Task<SyncProfileListResponse> ListProfilesAsync(
        SyncProfileListRequest request,
        CancellationToken cancellationToken = default);

    Task<SyncProfileGetResponse> GetProfileAsync(
        SyncProfileGetRequest request,
        CancellationToken cancellationToken = default);

    Task<SyncProfileMutationResponse> CreateProfileAsync(
        SyncProfileCreateRequest request,
        CancellationToken cancellationToken = default);

    Task<SyncProfileMutationResponse> UpdateProfileAsync(
        SyncProfileUpdateRequest request,
        CancellationToken cancellationToken = default);

    Task<SyncPreviewGenerateResponse> GeneratePreviewAsync(
        SyncPreviewGenerateRequest request,
        CancellationToken cancellationToken = default);

    Task<SyncRunStatusResponse> GetRunStatusAsync(
        SyncRunStatusRequest request,
        CancellationToken cancellationToken = default);

    Task<SyncRunListResponse> ListRunsAsync(
        SyncRunListRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new SyncRunListResponse(
            SyncManagementIpcContract.CurrentVersion,
            [],
            null));

    Task<SyncPlanPageResponse> GetPlanPageAsync(
        SyncPlanPageRequest request,
        CancellationToken cancellationToken = default);

    Task<SyncConflictPageResponse> GetConflictPageAsync(
        SyncConflictPageRequest request,
        CancellationToken cancellationToken = default);

    Task<SyncApproveDispatchResponse> ApproveAndDispatchAsync(
        SyncApproveDispatchRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Strict correlated client for preview-first sync management over the normal pipe.</summary>
public sealed class NamedPipeSyncManagementAgentClient : ISyncManagementAgentClient
{
    private readonly IStorageIpcTransport _transport;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private long _sendSequence;
    private bool _disposed;

    public NamedPipeSyncManagementAgentClient(SyncManagementAgentClientOptions? options = null)
        : this(CreateTransport(options ?? new SyncManagementAgentClientOptions()), options)
    {
    }

    public NamedPipeSyncManagementAgentClient(
        IStorageIpcTransport transport,
        SyncManagementAgentClientOptions? options = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        var effective = options ?? new SyncManagementAgentClientOptions();
        ValidateOptions(effective);
        _requestTimeout = effective.RequestTimeout;
    }

    public Task<SyncProfileListResponse> ListProfilesAsync(
        SyncProfileListRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request, request?.HasValidBounds ?? false, nameof(request));
        return ExecuteAsync<SyncProfileListRequest, SyncProfileListResponse>(
            SyncManagementIpcMessageTypes.ProfileListRequest,
            SyncManagementIpcMessageTypes.ProfileListResponse,
            request!,
            response => ValidateProfileListResponse(request!, response),
            cancellationToken);
    }

    public Task<SyncProfileGetResponse> GetProfileAsync(
        SyncProfileGetRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request, request?.HasValidBounds ?? false, nameof(request));
        return ExecuteAsync<SyncProfileGetRequest, SyncProfileGetResponse>(
            SyncManagementIpcMessageTypes.ProfileGetRequest,
            SyncManagementIpcMessageTypes.ProfileGetResponse,
            request!,
            response => ValidateProfileGetResponse(request!, response),
            cancellationToken);
    }

    public Task<SyncProfileMutationResponse> CreateProfileAsync(
        SyncProfileCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request, request?.HasValidBounds ?? false, nameof(request));
        return ExecuteAsync<SyncProfileCreateRequest, SyncProfileMutationResponse>(
            SyncManagementIpcMessageTypes.ProfileCreateRequest,
            SyncManagementIpcMessageTypes.ProfileCreateResponse,
            request!,
            response => ValidateProfileMutationResponse(request!.ProfileId, response),
            cancellationToken);
    }

    public Task<SyncProfileMutationResponse> UpdateProfileAsync(
        SyncProfileUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request, request?.HasValidBounds ?? false, nameof(request));
        return ExecuteAsync<SyncProfileUpdateRequest, SyncProfileMutationResponse>(
            SyncManagementIpcMessageTypes.ProfileUpdateRequest,
            SyncManagementIpcMessageTypes.ProfileUpdateResponse,
            request!,
            response => ValidateProfileMutationResponse(request!.ProfileId, response),
            cancellationToken);
    }

    public Task<SyncPreviewGenerateResponse> GeneratePreviewAsync(
        SyncPreviewGenerateRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request, request?.HasValidBounds ?? false, nameof(request));
        return ExecuteAsync<SyncPreviewGenerateRequest, SyncPreviewGenerateResponse>(
            SyncManagementIpcMessageTypes.PreviewGenerateRequest,
            SyncManagementIpcMessageTypes.PreviewGenerateResponse,
            request!,
            response => ValidatePreviewResponse(request!, response),
            cancellationToken);
    }

    public Task<SyncRunStatusResponse> GetRunStatusAsync(
        SyncRunStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request, request?.HasValidBounds ?? false, nameof(request));
        return ExecuteAsync<SyncRunStatusRequest, SyncRunStatusResponse>(
            SyncManagementIpcMessageTypes.RunStatusRequest,
            SyncManagementIpcMessageTypes.RunStatusResponse,
            request!,
            response => ValidateRunStatusResponse(request!, response),
            cancellationToken);
    }

    public Task<SyncRunListResponse> ListRunsAsync(
        SyncRunListRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request, request?.HasValidBounds ?? false, nameof(request));
        return ExecuteAsync<SyncRunListRequest, SyncRunListResponse>(
            SyncManagementIpcMessageTypes.RunListRequest,
            SyncManagementIpcMessageTypes.RunListResponse,
            request!,
            response => ValidateRunListResponse(request!, response),
            cancellationToken);
    }

    public Task<SyncPlanPageResponse> GetPlanPageAsync(
        SyncPlanPageRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request, request?.HasValidBounds ?? false, nameof(request));
        return ExecuteAsync<SyncPlanPageRequest, SyncPlanPageResponse>(
            SyncManagementIpcMessageTypes.PlanPageRequest,
            SyncManagementIpcMessageTypes.PlanPageResponse,
            request!,
            response => ValidatePlanPageResponse(request!, response),
            cancellationToken);
    }

    public Task<SyncConflictPageResponse> GetConflictPageAsync(
        SyncConflictPageRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request, request?.HasValidBounds ?? false, nameof(request));
        return ExecuteAsync<SyncConflictPageRequest, SyncConflictPageResponse>(
            SyncManagementIpcMessageTypes.ConflictPageRequest,
            SyncManagementIpcMessageTypes.ConflictPageResponse,
            request!,
            response => ValidateConflictPageResponse(request!, response),
            cancellationToken);
    }

    public Task<SyncApproveDispatchResponse> ApproveAndDispatchAsync(
        SyncApproveDispatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request, request?.HasValidBounds ?? false, nameof(request));
        return ExecuteAsync<SyncApproveDispatchRequest, SyncApproveDispatchResponse>(
            SyncManagementIpcMessageTypes.ApproveDispatchRequest,
            SyncManagementIpcMessageTypes.ApproveDispatchResponse,
            request!,
            response => ValidateApproveResponse(request!, response),
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
                throw new InvalidDataException("The local agent returned invalid sync data.", error);
            }

            validateResponse(response);
            return response;
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            await DisconnectAfterFailureAsync().ConfigureAwait(false);
            throw new TimeoutException("The local agent sync request timed out.", error);
        }
        catch (OperationCanceledException)
        {
            // A cancelled framed exchange cannot safely leave its response queued for the next request.
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
        if (envelope is null)
        {
            throw InvalidResponse();
        }
        if (envelope.RequestId != requestId || envelope.Sequence <= 0)
        {
            throw InvalidResponse();
        }

        if (string.Equals(envelope.MessageType, IpcProtocol.ErrorResponseMessageType, StringComparison.Ordinal))
        {
            IpcErrorResponse error;
            try
            {
                error = envelope.DeserializePayload<IpcErrorResponse>();
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The local agent returned an invalid sync error response.", exception);
            }

            if (string.IsNullOrWhiteSpace(error.Code) || string.IsNullOrWhiteSpace(error.Message) ||
                error.Code.Length > StorageIpcLimits.MaximumFailureCodeLength ||
                error.Message.Length > StorageIpcLimits.MaximumFailureMessageLength ||
                error.Code.Any(char.IsControl) || error.Message.Any(char.IsControl))
            {
                throw new InvalidDataException("The local agent returned an invalid sync error response.");
            }

            throw new InvalidOperationException(error.Message);
        }

        if (!string.Equals(envelope.MessageType, responseType, StringComparison.Ordinal))
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateProfileListResponse(
        SyncProfileListRequest request,
        SyncProfileListResponse response)
    {
        ValidateContract(response.ContractVersion);
        if (response.Profiles is null ||
            response.Profiles.Length > request.MaximumCount ||
            response.Profiles.Length > SyncManagementIpcLimits.MaximumProfileResults ||
            !IsValidFailure(response.Failure) ||
            response.Failure is not null && response.Profiles.Length != 0 ||
            response.Profiles.Any(profile => profile is null || !IsValidProfileSummary(profile)) ||
            response.Profiles.Select(static profile => profile.ProfileId).Distinct().Count() !=
                response.Profiles.Length ||
            !request.IncludeDisabled && response.Profiles.Any(static profile => !profile.Enabled))
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateProfileGetResponse(
        SyncProfileGetRequest request,
        SyncProfileGetResponse response)
    {
        ValidateContract(response.ContractVersion);
        if (response.ProfileId != request.ProfileId ||
            !IsValidFailure(response.Failure) ||
            (response.Profile is null) == (response.Failure is null) ||
            response.Profile is not null &&
                (response.Profile.ProfileId != request.ProfileId || !IsValidProfile(response.Profile)))
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateProfileMutationResponse(
        Guid profileId,
        SyncProfileMutationResponse response)
    {
        ValidateContract(response.ContractVersion);
        if (response.ProfileId != profileId ||
            !Enum.IsDefined(response.Outcome) ||
            !IsValidFailure(response.Failure) ||
            response.ActualRevision is <= 0 ||
            response.Profile is not null &&
                (response.Profile.ProfileId != profileId ||
                 !IsValidProfile(response.Profile) ||
                 response.ActualRevision != response.Profile.Revision))
        {
            throw InvalidResponse();
        }

        var succeeded = response.Outcome is
            SyncProfileMutationOutcome.Succeeded or SyncProfileMutationOutcome.AlreadyApplied;
        if (succeeded
            ? response.Profile is null || response.Failure is not null
            : response.Failure is null)
        {
            throw InvalidResponse();
        }
    }

    private static void ValidatePreviewResponse(
        SyncPreviewGenerateRequest request,
        SyncPreviewGenerateResponse response)
    {
        ValidateContract(response.ContractVersion);
        if (response.ProfileId != request.ProfileId ||
            !IsValidFailure(response.Failure) ||
            (response.Run is null || response.Plan is null) != (response.Failure is not null) ||
            response.Run is not null &&
                (!IsValidRun(response.Run) || response.Run.ProfileId != request.ProfileId) ||
            response.Plan is not null &&
                (!IsValidPlanOverview(response.Plan) ||
                 response.Run is null ||
                 response.Plan.SyncRunId != response.Run.SyncRunId ||
                 response.Plan.PlanId != response.Run.PlanId ||
                 !string.Equals(response.Plan.PlanSha256, response.Run.PlanSha256, StringComparison.OrdinalIgnoreCase)))
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateRunStatusResponse(
        SyncRunStatusRequest request,
        SyncRunStatusResponse response)
    {
        ValidateContract(response.ContractVersion);
        if (response.SyncRunId != request.SyncRunId ||
            !IsValidFailure(response.Failure) ||
            (response.Run is null) == (response.Failure is null) ||
            response.Run is not null &&
                (response.Run.SyncRunId != request.SyncRunId || !IsValidRun(response.Run)))
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateRunListResponse(
        SyncRunListRequest request,
        SyncRunListResponse response)
    {
        ValidateContract(response.ContractVersion);
        if (response.Runs is null || response.Runs.Length > request.PageSize ||
            !IsValidFailure(response.Failure) ||
            response.Failure is not null && response.Runs.Length != 0 ||
            response.Runs.Any(run => !IsValidRun(run) ||
                request.ProfileId is { } profileId && run.ProfileId != profileId) ||
            response.ContinuationToken is not null &&
                (response.ContinuationToken.Length > SyncManagementIpcLimits.MaximumContinuationTokenLength ||
                 !response.ContinuationToken.All(char.IsAsciiDigit)))
        {
            throw InvalidResponse();
        }
    }

    private static void ValidatePlanPageResponse(
        SyncPlanPageRequest request,
        SyncPlanPageResponse response)
    {
        ValidateContract(response.ContractVersion);
        if (response.SyncRunId != request.SyncRunId ||
            response.Operations is null ||
            response.Operations.Length > request.PageSize ||
            response.TotalOperations is < 0 or > SyncManagementIpcLimits.MaximumPlanOperationCount ||
            !IsValidFailure(response.Failure) ||
            !IsSafeText(response.ContinuationToken, SyncManagementIpcLimits.MaximumContinuationTokenLength) ||
            response.Failure is not null &&
                (response.PlanId != Guid.Empty ||
                 !string.IsNullOrEmpty(response.PlanSha256) ||
                 response.TotalOperations != 0 ||
                 response.Operations.Length != 0 ||
                 response.ContinuationToken is not null) ||
            response.Failure is null &&
                (response.PlanId == Guid.Empty ||
                 !IsSha256(response.PlanSha256) ||
                 response.TotalOperations < response.Operations.Length) ||
            response.ContinuationToken is not null && response.Operations.Length == 0 ||
            response.Operations.Any(operation => operation is null || !IsValidOperation(operation)) ||
            response.Operations.Select(static operation => operation.Sequence).Distinct().Count() !=
                response.Operations.Length)
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateConflictPageResponse(
        SyncConflictPageRequest request,
        SyncConflictPageResponse response)
    {
        ValidateContract(response.ContractVersion);
        if (response.SyncRunId != request.SyncRunId ||
            response.Conflicts is null ||
            response.Conflicts.Length > request.PageSize ||
            response.ReportedConflictCount < 0 ||
            response.ReportedConflictCount < response.Conflicts.Length ||
            !IsValidFailure(response.Failure) ||
            !IsSafeText(response.ContinuationToken, SyncManagementIpcLimits.MaximumContinuationTokenLength) ||
            response.Failure is not null &&
                (response.ReportedConflictCount != 0 ||
                 response.Conflicts.Length != 0 ||
                 response.ContinuationToken is not null ||
                 response.IsTruncatedAtSource) ||
            response.Conflicts.Any(conflict => conflict is null || !IsValidConflict(conflict)) ||
            response.ContinuationToken is not null && response.Conflicts.Length == 0 ||
            response.Conflicts.Select(static conflict => conflict.ConflictId).Distinct().Count() !=
                response.Conflicts.Length)
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateApproveResponse(
        SyncApproveDispatchRequest request,
        SyncApproveDispatchResponse response)
    {
        ValidateContract(response.ContractVersion);
        if (response.SyncRunId != request.SyncRunId ||
            !IsValidFailure(response.Failure) ||
            (response.Failure is null
                ? !response.DurablyDispatched || response.Run is null
                : response.DurablyDispatched || response.Run is not null) ||
            response.Run is not null &&
                (response.Run.SyncRunId != request.SyncRunId ||
                 response.Run.Revision <= request.ExpectedRevision ||
                 response.Run.DispatchState != SyncIpcDispatchState.DurablyDispatched ||
                 !string.Equals(response.Run.ApprovalSha256, request.ApprovalSha256, StringComparison.OrdinalIgnoreCase) ||
                 !IsValidRun(response.Run)))
        {
            throw InvalidResponse();
        }
    }

    private static bool IsValidProfileSummary(SyncProfileSummary profile) =>
        profile.ProfileId != Guid.Empty &&
        IsSafeText(profile.DisplayName, SyncManagementIpcLimits.MaximumDisplayNameLength, required: true) &&
        profile.LocationAConnectionId != Guid.Empty &&
        profile.LocationBConnectionId != Guid.Empty &&
        Enum.IsDefined(profile.Direction) &&
        Enum.IsDefined(profile.DeletionMode) &&
        profile.Revision >= 1 &&
        profile.UpdatedUtc.Offset == TimeSpan.Zero;

    private static bool IsValidProfile(SyncProfileDocument profile) =>
        profile.ProfileId != Guid.Empty &&
        profile.Draft is not null && profile.Draft.HasValidBounds &&
        profile.Revision >= 1 &&
        profile.CreatedUtc.Offset == TimeSpan.Zero &&
        profile.UpdatedUtc.Offset == TimeSpan.Zero &&
        profile.UpdatedUtc >= profile.CreatedUtc;

    private static bool IsValidRun(SyncRunSummary run) =>
        run.SyncRunId != Guid.Empty &&
        run.ProfileId != Guid.Empty &&
        run.Generation >= 1 &&
        Enum.IsDefined(run.Phase) &&
        Enum.IsDefined(run.StatusCode) &&
        run.Revision >= 0 &&
        run.UpdatedUtc.Offset == TimeSpan.Zero &&
        run.PlanId != Guid.Empty &&
        IsSha256(run.PlanSha256) &&
        IsSha256(run.ApprovalSha256) &&
        run.ConflictCount >= 0 &&
        Enum.IsDefined(run.DispatchState) &&
        run.CreatedUtc.Offset == TimeSpan.Zero &&
        run.BaselineItemCount >= 0 &&
        run.LeftItemCount >= 0 &&
        run.RightItemCount >= 0 &&
        (run.DispatchedUtc is null || run.DispatchedUtc.Value.Offset == TimeSpan.Zero) &&
        (run.DispatchState == SyncIpcDispatchState.DurablyDispatched) == run.DispatchedUtc.HasValue;

    private static bool IsValidPlanOverview(SyncPlanOverview plan) =>
        plan.SyncRunId != Guid.Empty &&
        plan.PlanId != Guid.Empty &&
        IsSha256(plan.PlanSha256) &&
        plan.BaselineGeneration >= 0 &&
        plan.OperationCount >= 0 &&
        plan.CopyCount >= 0 &&
        plan.DeleteCount >= 0 &&
        plan.CreateDirectoryCount >= 0 &&
        plan.CopyCount + plan.DeleteCount + plan.CreateDirectoryCount == plan.OperationCount &&
        plan.CreatedUtc.Offset == TimeSpan.Zero;

    private static bool IsValidOperation(SyncPlanOperationSummary operation) =>
        operation.Sequence >= 0 &&
        Enum.IsDefined(operation.Kind) &&
        operation.SourceConnectionId != Guid.Empty &&
        IsSafeText(
            operation.SourcePath,
            SyncManagementIpcLimits.MaximumRelativePathLength,
            allowEmpty: true) &&
        (operation.DestinationConnectionId is null) == (operation.DestinationPath is null) &&
        (operation.DestinationConnectionId is null || operation.DestinationConnectionId != Guid.Empty) &&
        IsSafeText(
            operation.DestinationPath,
            SyncManagementIpcLimits.MaximumRelativePathLength,
            allowEmpty: true) &&
        operation.ExpectedLength is null or >= 0 &&
        operation.IsDestructive == (operation.Kind == SyncIpcPlanOperationKind.Delete);

    private static bool IsValidConflict(SyncConflictSummary conflict) =>
        conflict.ConflictId != Guid.Empty &&
        IsSafeText(
            conflict.RelativePath,
            SyncManagementIpcLimits.MaximumRelativePathLength,
            allowEmpty: true) &&
        IsSafeText(
            conflict.ConflictKind,
            SyncManagementIpcLimits.MaximumConflictKindLength,
            required: true) &&
        Enum.IsDefined(conflict.State) &&
        IsSafeText(
            conflict.SafeReason,
            SyncManagementIpcLimits.MaximumConflictReasonLength,
            required: true) &&
        conflict.DetectedUtc.Offset == TimeSpan.Zero &&
        (conflict.ResolvedUtc is null || conflict.ResolvedUtc.Value.Offset == TimeSpan.Zero) &&
        conflict.Revision >= 1;

    private static bool IsValidFailure(StorageIpcFailure? failure) => failure is null ||
        IsSafeText(failure.Code, StorageIpcLimits.MaximumFailureCodeLength, required: true) &&
        IsSafeText(failure.Message, StorageIpcLimits.MaximumFailureMessageLength, required: true) &&
        Enum.IsDefined(failure.Category);

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

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

    private static void ValidateRequest<TRequest>(TRequest? request, bool validBounds, string parameterName)
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(request, parameterName);
        var version = request switch
        {
            SyncProfileListRequest value => value.ContractVersion,
            SyncProfileGetRequest value => value.ContractVersion,
            SyncProfileCreateRequest value => value.ContractVersion,
            SyncProfileUpdateRequest value => value.ContractVersion,
            SyncPreviewGenerateRequest value => value.ContractVersion,
            SyncRunStatusRequest value => value.ContractVersion,
            SyncRunListRequest value => value.ContractVersion,
            SyncPlanPageRequest value => value.ContractVersion,
            SyncConflictPageRequest value => value.ContractVersion,
            SyncApproveDispatchRequest value => value.ContractVersion,
            _ => 0
        };
        if (!SyncManagementIpcContract.IsSupported(version) || !validBounds)
        {
            throw new ArgumentException(
                "The sync request is outside the negotiated IPC contract bounds.",
                parameterName);
        }
    }

    private static void ValidateContract(int version)
    {
        if (!SyncManagementIpcContract.IsSupported(version))
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
        new("The local agent returned sync data outside the negotiated bounds.");

    private static NamedPipeSyncIpcTransport CreateTransport(SyncManagementAgentClientOptions options)
    {
        ValidateOptions(options);
        var version = typeof(NamedPipeSyncManagementAgentClient).Assembly.GetName().Version?.ToString() ?? "0.1.0";
        return new NamedPipeSyncIpcTransport(new NamedPipeIpcClient(new NamedPipeIpcClientOptions
        {
            PipeName = options.PipeName,
            ClientName = "StorageHub.Desktop.SyncManagement",
            ClientVersion = version,
            ConnectTimeout = options.ConnectTimeout,
            MaxConnectAttempts = 1,
            InitialReconnectDelay = TimeSpan.Zero,
            MaximumReconnectDelay = TimeSpan.Zero
        }));
    }

    private static void ValidateOptions(SyncManagementAgentClientOptions options)
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

        if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The request timeout must be at most ten minutes.");
        }
    }

    private sealed class NamedPipeSyncIpcTransport(NamedPipeIpcClient client) : IStorageIpcTransport
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
