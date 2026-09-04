using System.Globalization;
using System.Text;
using StorageHub.Agent.Ipc;
using StorageHub.Agent.Transfers;
using StorageHub.Contracts.Ipc;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Transfers;

namespace StorageHub.Agent.Windows;

/// <summary>
/// Versioned, bounded control surface for the durable transfer queue. The wire model contains
/// stable storage identities and user-safe errors only; leases, resume IDs, provider exceptions,
/// and credential material never cross this normal IPC boundary.
/// </summary>
public sealed class TransferQueueIpcCommandService : IAgentIpcCommandHandler
{
    private const string InvalidRequestCode = "transfer.request.invalid";
    private readonly ITransferJobStore _store;
    private readonly ITransferQueueQueryStore _queries;
    private readonly IActiveTransferCancellation? _activeCancellation;
    private readonly TimeProvider _timeProvider;

    public TransferQueueIpcCommandService(
        ITransferJobStore store,
        ITransferQueueQueryStore queries,
        IActiveTransferCancellation? activeCancellation = null,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _queries = queries ?? throw new ArgumentNullException(nameof(queries));
        _activeCancellation = activeCancellation;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool CanHandle(string messageType) => messageType is
        TransferQueueIpcMessageTypes.EnqueueRequest or
        TransferQueueIpcMessageTypes.ListRequest or
        TransferQueueIpcMessageTypes.StatusRequest or
        TransferQueueIpcMessageTypes.CancelRequest or
        TransferQueueIpcMessageTypes.RetryRequest or
        TransferQueueIpcMessageTypes.ReconcileRequest or
        TransferQueueIpcMessageTypes.ClearHistoryRequest;

    public ValueTask<AgentIpcCommandResponse> HandleAsync(
        IpcEnvelope request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.MessageType switch
        {
            TransferQueueIpcMessageTypes.EnqueueRequest => EnqueueAsync(request, cancellationToken),
            TransferQueueIpcMessageTypes.ListRequest => ListAsync(request, cancellationToken),
            TransferQueueIpcMessageTypes.StatusRequest => StatusAsync(request, cancellationToken),
            TransferQueueIpcMessageTypes.CancelRequest => CancelAsync(request, cancellationToken),
            TransferQueueIpcMessageTypes.RetryRequest => RetryAsync(request, cancellationToken),
            TransferQueueIpcMessageTypes.ReconcileRequest => ReconcileAsync(request, cancellationToken),
            TransferQueueIpcMessageTypes.ClearHistoryRequest => ClearHistoryAsync(request, cancellationToken),
            _ => ValueTask.FromResult(AgentIpcCommandResponse.Error(
                "ipc.message.unsupported",
                "The requested IPC operation is not supported by this agent version."))
        };
    }

    private async ValueTask<AgentIpcCommandResponse> EnqueueAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<TransferEnqueueRequest>();
        var validation = ValidateRequest(request.ContractVersion, request.HasValidBounds);
        if (validation is not null)
        {
            return validation;
        }

        if (!TryCreateAddress(request.Source, out var source) ||
            !TryCreateAddress(request.Destination, out var destination))
        {
            return EnqueueFailure(request.TransferId, ValidationFailure(
                "Source and destination must use canonical root-relative storage addresses."));
        }

        if (request.Operation == TransferQueueOperation.Move && !HasStableIdentity(source) ||
            HasOverwritePrecondition(request, destination) &&
            (!HasStableIdentity(source) || !HasExactDestinationIdentity(request, destination)))
        {
            return EnqueueFailure(request.TransferId, ValidationFailure(
                "Move and overwrite operations require immutable source and destination identity evidence."));
        }

        TransferIntent intent;
        try
        {
            intent = new TransferIntent(
                new TransferJobId(request.TransferId),
                Map(request.Operation),
                source,
                destination,
                request.ExpectedLength,
                Map(request.Verification),
                _timeProvider.GetUtcNow(),
                request.ExpectedDestinationVersionId,
                request.ExpectedDestinationEntityTag);
        }
        catch (ArgumentException)
        {
            return EnqueueFailure(request.TransferId, ValidationFailure(
                "The transfer intent is not valid for the selected source and destination."));
        }

        try
        {
            var inserted = await _store.TryEnqueueAsync(intent, request.Priority, cancellationToken)
                .ConfigureAwait(false);
            var job = await _store.FindAsync(intent.TransferJobId, cancellationToken).ConfigureAwait(false);
            if (job is null)
            {
                return EnqueueFailure(request.TransferId, UnavailableFailure());
            }

            if (!inserted && !IsSameIntent(job, intent, request.Priority))
            {
                return EnqueueFailure(request.TransferId, new StorageIpcFailure(
                    "transfer.id.conflict",
                    StorageIpcFailureCategory.Conflict,
                    "The transfer ID is already assigned to a different operation.",
                    IsTransient: false));
            }

            var summary = await MapSummaryAsync(job, cancellationToken).ConfigureAwait(false);
            return AgentIpcCommandResponse.Create(
                TransferQueueIpcMessageTypes.EnqueueResponse,
                new TransferEnqueueResponse(
                    TransferQueueIpcContract.CurrentVersion,
                    request.TransferId,
                    Accepted: true,
                    AlreadyExisted: !inserted,
                    summary));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return EnqueueFailure(request.TransferId, UnavailableFailure());
        }
    }

    private async ValueTask<AgentIpcCommandResponse> ListAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<TransferListRequest>();
        var validation = ValidateRequest(request.ContractVersion, request.HasValidBounds);
        if (validation is not null)
        {
            return validation;
        }

        if (!TryDecodeCursor(request.ContinuationToken, out var cursor))
        {
            return ListFailure(ValidationFailure("The transfer queue continuation token is invalid."));
        }

        try
        {
            var page = await _queries.ListAsync(
                new TransferQueueQuery(request.States.Select(Map), request.PageSize, cursor),
                cancellationToken).ConfigureAwait(false);
            if (page.Jobs.Count > request.PageSize)
            {
                return ListFailure(new StorageIpcFailure(
                    "transfer.response.invalid",
                    StorageIpcFailureCategory.Integrity,
                    "The transfer queue returned more items than requested.",
                    IsTransient: false));
            }

            var transfers = new TransferQueueSummary[page.Jobs.Count];
            for (var index = 0; index < page.Jobs.Count; index++)
            {
                transfers[index] = await MapSummaryAsync(page.Jobs[index], cancellationToken)
                    .ConfigureAwait(false);
            }

            var counts = await _queries.CountByStateAsync(cancellationToken).ConfigureAwait(false);
            return AgentIpcCommandResponse.Create(
                TransferQueueIpcMessageTypes.ListResponse,
                new TransferListResponse(
                    TransferQueueIpcContract.CurrentVersion,
                    transfers,
                    EncodeCursor(page.Continuation),
                    StateCounts: counts.ToDictionary(pair => Map(pair.Key), pair => pair.Value)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ListFailure(UnavailableFailure());
        }
    }

    private async ValueTask<AgentIpcCommandResponse> ClearHistoryAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<TransferHistoryClearRequest>();
        var validation = ValidateRequest(request.ContractVersion, request.HasValidBounds);
        if (validation is not null) return validation;
        if (_store is not ITransferHistoryStore history)
        {
            return AgentIpcCommandResponse.Create(
                TransferQueueIpcMessageTypes.ClearHistoryResponse,
                new TransferHistoryClearResponse(TransferQueueIpcContract.CurrentVersion, 0, UnavailableFailure()));
        }
        try
        {
            var ids = request.ClearAll
                ? null
                : request.TransferIds.Select(id => new TransferJobId(id)).ToArray();
            var cleared = await history.ClearTerminalHistoryAsync(ids, cancellationToken).ConfigureAwait(false);
            return AgentIpcCommandResponse.Create(
                TransferQueueIpcMessageTypes.ClearHistoryResponse,
                new TransferHistoryClearResponse(TransferQueueIpcContract.CurrentVersion, cleared));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return AgentIpcCommandResponse.Create(
                TransferQueueIpcMessageTypes.ClearHistoryResponse,
                new TransferHistoryClearResponse(TransferQueueIpcContract.CurrentVersion, 0, UnavailableFailure()));
        }
    }

    private async ValueTask<AgentIpcCommandResponse> StatusAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<TransferStatusRequest>();
        var validation = ValidateRequest(request.ContractVersion, request.HasValidBounds);
        if (validation is not null)
        {
            return validation;
        }

        try
        {
            var job = await _store.FindAsync(new TransferJobId(request.TransferId), cancellationToken)
                .ConfigureAwait(false);
            return job is null
                ? StatusFailure(request.TransferId, NotFoundFailure())
                : AgentIpcCommandResponse.Create(
                    TransferQueueIpcMessageTypes.StatusResponse,
                    new TransferStatusResponse(
                        TransferQueueIpcContract.CurrentVersion,
                        request.TransferId,
                        await MapSummaryAsync(job, cancellationToken).ConfigureAwait(false)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return StatusFailure(request.TransferId, UnavailableFailure());
        }
    }

    private async ValueTask<AgentIpcCommandResponse> CancelAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<TransferCancelRequest>();
        var validation = ValidateRequest(request.ContractVersion, request.HasValidBounds);
        if (validation is not null)
        {
            return validation;
        }

        var transferId = new TransferJobId(request.TransferId);
        try
        {
            var current = await _store.FindAsync(transferId, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                return MutationResponse(
                    TransferQueueIpcMessageTypes.CancelResponse,
                    request.TransferId,
                    TransferQueueMutationOutcome.NotFound,
                    failure: NotFoundFailure());
            }

            if (current.State.Revision != request.ExpectedRevision)
            {
                return await RevisionConflictAsync(
                    TransferQueueIpcMessageTypes.CancelResponse,
                    current,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!TransferStateMachine.CanTransition(current.State.State, TransferState.Cancelled))
            {
                return await InvalidStateAsync(
                    TransferQueueIpcMessageTypes.CancelResponse,
                    current,
                    cancellationToken).ConfigureAwait(false);
            }

            if (current.ActiveLease is not null)
            {
                var activeResult = _activeCancellation?.TryRequestActiveCancellation(
                    transferId,
                    request.ExpectedRevision) ?? ActiveTransferCancellationResult.NotActive;
                return activeResult switch
                {
                    ActiveTransferCancellationResult.Accepted or
                    ActiveTransferCancellationResult.AlreadyRequested => MutationResponse(
                        TransferQueueIpcMessageTypes.CancelResponse,
                        request.TransferId,
                        TransferQueueMutationOutcome.Accepted,
                        await MapSummaryAsync(current, cancellationToken).ConfigureAwait(false)),
                    ActiveTransferCancellationResult.RevisionConflict => await RevisionConflictAsync(
                        TransferQueueIpcMessageTypes.CancelResponse,
                        current,
                        cancellationToken).ConfigureAwait(false),
                    _ => MutationResponse(
                        TransferQueueIpcMessageTypes.CancelResponse,
                        request.TransferId,
                        TransferQueueMutationOutcome.RevisionConflict,
                        await MapSummaryAsync(current, cancellationToken).ConfigureAwait(false),
                        ConflictFailure())
                };
            }

            return await ApplyControlTransitionAsync(
                TransferQueueIpcMessageTypes.CancelResponse,
                current,
                request.ExpectedRevision,
                TransferState.Cancelled,
                TransferStatusCode.None,
                error: null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return MutationResponse(
                TransferQueueIpcMessageTypes.CancelResponse,
                request.TransferId,
                TransferQueueMutationOutcome.InvalidState,
                failure: UnavailableFailure());
        }
    }

    private async ValueTask<AgentIpcCommandResponse> RetryAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<TransferRetryRequest>();
        var validation = ValidateRequest(request.ContractVersion, request.HasValidBounds);
        if (validation is not null)
        {
            return validation;
        }

        return await ExecuteInactiveControlAsync(
            TransferQueueIpcMessageTypes.RetryResponse,
            request.TransferId,
            request.ExpectedRevision,
            TransferState.Pending,
            TransferStatusCode.None,
            error: null,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<AgentIpcCommandResponse> ReconcileAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<TransferReconcileRequest>();
        var validation = ValidateRequest(request.ContractVersion, request.HasValidBounds);
        if (validation is not null)
        {
            return validation;
        }

        var (nextState, status, error) = request.Action switch
        {
            TransferReconciliationAction.Review => (
                TransferState.NeedsReconciliation,
                TransferStatusCode.StateUncertain,
                (TransferSafeError?)null),
            TransferReconciliationAction.Restart => (
                TransferState.RestartRequired,
                TransferStatusCode.ResumeNotSupported,
                null),
            TransferReconciliationAction.MarkCompleted => (
                TransferState.Completed,
                TransferStatusCode.None,
                null),
            TransferReconciliationAction.MarkFailed => (
                TransferState.Failed,
                TransferStatusCode.ProviderFailure,
                new TransferSafeError(
                    "transfer.reconciliation.failed",
                    "The operator marked the transfer as failed during reconciliation.")),
            TransferReconciliationAction.Cancel => (
                TransferState.Cancelled,
                TransferStatusCode.None,
                null),
            _ => throw new InvalidOperationException("Unsupported reconciliation action.")
        };

        return await ExecuteInactiveControlAsync(
            TransferQueueIpcMessageTypes.ReconcileResponse,
            request.TransferId,
            request.ExpectedRevision,
            nextState,
            status,
            error,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<AgentIpcCommandResponse> ExecuteInactiveControlAsync(
        string responseType,
        Guid requestTransferId,
        long expectedRevision,
        TransferState nextState,
        TransferStatusCode statusCode,
        TransferSafeError? error,
        CancellationToken cancellationToken)
    {
        try
        {
            var transferId = new TransferJobId(requestTransferId);
            var current = await _store.FindAsync(transferId, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                return MutationResponse(
                    responseType,
                    requestTransferId,
                    TransferQueueMutationOutcome.NotFound,
                    failure: NotFoundFailure());
            }

            if (current.State.Revision != expectedRevision)
            {
                return await RevisionConflictAsync(responseType, current, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (current.ActiveLease is not null ||
                !TransferStateMachine.CanTransition(current.State.State, nextState))
            {
                return await InvalidStateAsync(responseType, current, cancellationToken)
                    .ConfigureAwait(false);
            }

            return await ApplyControlTransitionAsync(
                responseType,
                current,
                expectedRevision,
                nextState,
                statusCode,
                error,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return MutationResponse(
                responseType,
                requestTransferId,
                TransferQueueMutationOutcome.InvalidState,
                failure: UnavailableFailure());
        }
    }

    private async ValueTask<AgentIpcCommandResponse> ApplyControlTransitionAsync(
        string responseType,
        DurableTransferJob current,
        long expectedRevision,
        TransferState nextState,
        TransferStatusCode statusCode,
        TransferSafeError? error,
        CancellationToken cancellationToken)
    {
        var result = await _store.TryTransitionControlStateAsync(
            new TransferControlStateTransitionRequest(
                current.Intent.TransferJobId,
                expectedRevision,
                nextState,
                _timeProvider.GetUtcNow(),
                statusCode,
                error),
            cancellationToken).ConfigureAwait(false);
        if (result.Status == TransferStoreMutationStatus.Applied)
        {
            return MutationResponse(
                responseType,
                current.Intent.TransferJobId.Value,
                TransferQueueMutationOutcome.Applied,
                await MapSummaryAsync(result.Value!, cancellationToken).ConfigureAwait(false));
        }

        var latest = await _store.FindAsync(current.Intent.TransferJobId, cancellationToken)
            .ConfigureAwait(false);
        return result.Status == TransferStoreMutationStatus.NotFound || latest is null
            ? MutationResponse(
                responseType,
                current.Intent.TransferJobId.Value,
                TransferQueueMutationOutcome.NotFound,
                failure: NotFoundFailure())
            : await RevisionConflictAsync(responseType, latest, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<AgentIpcCommandResponse> RevisionConflictAsync(
        string responseType,
        DurableTransferJob current,
        CancellationToken cancellationToken) => MutationResponse(
            responseType,
            current.Intent.TransferJobId.Value,
            TransferQueueMutationOutcome.RevisionConflict,
            await MapSummaryAsync(current, cancellationToken).ConfigureAwait(false),
            ConflictFailure());

    private async ValueTask<AgentIpcCommandResponse> InvalidStateAsync(
        string responseType,
        DurableTransferJob current,
        CancellationToken cancellationToken) => MutationResponse(
            responseType,
            current.Intent.TransferJobId.Value,
            TransferQueueMutationOutcome.InvalidState,
            await MapSummaryAsync(current, cancellationToken).ConfigureAwait(false),
            new StorageIpcFailure(
                "transfer.state.invalid",
                StorageIpcFailureCategory.Conflict,
                "The requested action is not valid in the transfer's current state.",
                IsTransient: false));

    private async ValueTask<TransferQueueSummary> MapSummaryAsync(
        DurableTransferJob job,
        CancellationToken cancellationToken)
    {
        var checkpoint = await _store.FindCheckpointAsync(job.Intent.TransferJobId, cancellationToken)
            .ConfigureAwait(false);
        var expectedBytes = checkpoint?.Checkpoint.ExpectedLength ?? job.Intent.ExpectedLength;
        var progressBytes = checkpoint?.Checkpoint.VerifiedBytes ?? 0;
        if (expectedBytes is long length && progressBytes > length)
        {
            progressBytes = length;
        }

        return new TransferQueueSummary(
            job.Intent.TransferJobId.Value,
            Map(job.Intent.Operation),
            job.Intent.Source.ProfileId.Value,
            job.Intent.Source.CanonicalRelativePath,
            job.Intent.Destination.ProfileId.Value,
            job.Intent.Destination.CanonicalRelativePath,
            Map(job.State.State),
            job.State.Revision,
            job.State.Attempt,
            job.Priority,
            expectedBytes,
            progressBytes,
            job.State.TransitionedAtUtc,
            job.RetryAvailableAtUtc,
            job.LastError?.Code,
            job.LastError?.Summary,
            TransferStateMachine.CanTransition(job.State.State, TransferState.Cancelled),
            CanRetry(job.State.State) && job.ActiveLease is null,
            job.State.State is TransferState.Interrupted or TransferState.NeedsReconciliation);
    }

    private static bool TryCreateAddress(TransferQueueAddress wire, out StorageAddress address)
    {
        var result = StorageAddress.Create(
            new ConnectionProfileId(wire.ConnectionId),
            wire.RootIdentity,
            wire.RelativePath,
            wire.NativeItemId,
            wire.VersionId,
            wire.EntityTag);
        if (result.IsFailure ||
            !string.Equals(result.Value.CanonicalRelativePath, wire.RelativePath, StringComparison.Ordinal))
        {
            address = null!;
            return false;
        }

        address = result.Value;
        return true;
    }

    private static bool HasStableIdentity(StorageAddress address) =>
        address.VersionId is not null || address.EntityTag is not null;

    private static bool HasOverwritePrecondition(
        TransferEnqueueRequest request,
        StorageAddress destination) =>
        request.ExpectedDestinationVersionId is not null ||
        request.ExpectedDestinationEntityTag is not null ||
        destination.VersionId is not null ||
        destination.EntityTag is not null;

    private static bool HasExactDestinationIdentity(
        TransferEnqueueRequest request,
        StorageAddress destination)
    {
        var version = request.ExpectedDestinationVersionId ?? destination.VersionId;
        var entityTag = request.ExpectedDestinationEntityTag ?? destination.EntityTag;
        return (version is not null || entityTag is not null) &&
               (request.ExpectedDestinationVersionId is null || destination.VersionId is null ||
                string.Equals(request.ExpectedDestinationVersionId, destination.VersionId, StringComparison.Ordinal)) &&
               (request.ExpectedDestinationEntityTag is null || destination.EntityTag is null ||
                string.Equals(request.ExpectedDestinationEntityTag, destination.EntityTag, StringComparison.Ordinal));
    }

    private static bool IsSameIntent(DurableTransferJob job, TransferIntent intent, int priority) =>
        job.Priority == priority &&
        job.Intent.Operation == intent.Operation &&
        job.Intent.Source == intent.Source &&
        job.Intent.Destination == intent.Destination &&
        job.Intent.ExpectedLength == intent.ExpectedLength &&
        job.Intent.VerificationPolicy == intent.VerificationPolicy &&
        string.Equals(
            job.Intent.ExpectedDestinationVersionId,
            intent.ExpectedDestinationVersionId,
            StringComparison.Ordinal) &&
        string.Equals(
            job.Intent.ExpectedDestinationEntityTag,
            intent.ExpectedDestinationEntityTag,
            StringComparison.Ordinal);

    private static bool CanRetry(TransferState state) => state is
        TransferState.Paused or
        TransferState.BlockedCredential or
        TransferState.BlockedTrust or
        TransferState.RestartRequired or
        TransferState.Failed;

    private static bool TryDecodeCursor(string? encoded, out TransferQueueCursor? cursor)
    {
        cursor = null;
        if (encoded is null)
        {
            return true;
        }

        try
        {
            var value = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var separator = value.IndexOf('|');
            if (separator <= 0 || separator == value.Length - 1 ||
                !DateTimeOffset.TryParseExact(
                    value[..separator],
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var transitionedAtUtc) ||
                transitionedAtUtc.Offset != TimeSpan.Zero ||
                !Guid.TryParseExact(value[(separator + 1)..], "D", out var transferId) ||
                transferId == Guid.Empty)
            {
                return false;
            }

            cursor = new TransferQueueCursor(transitionedAtUtc, new TransferJobId(transferId));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string? EncodeCursor(TransferQueueCursor? cursor) => cursor is null
        ? null
        : Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $"{cursor.TransitionedAtUtc:O}|{cursor.TransferJobId.Value:D}")));

    private static TransferOperationKind Map(TransferQueueOperation operation) => operation switch
    {
        TransferQueueOperation.Copy => TransferOperationKind.Copy,
        TransferQueueOperation.Move => TransferOperationKind.Move,
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static TransferQueueOperation Map(TransferOperationKind operation) => operation switch
    {
        TransferOperationKind.Copy => TransferQueueOperation.Copy,
        TransferOperationKind.Move => TransferQueueOperation.Move,
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static TransferVerificationPolicy Map(TransferQueueVerification verification) => verification switch
    {
        TransferQueueVerification.Size => TransferVerificationPolicy.Size,
        TransferQueueVerification.StrongHashWhenAvailable => TransferVerificationPolicy.StrongHashWhenAvailable,
        TransferQueueVerification.StrongHashRequired => TransferVerificationPolicy.StrongHashRequired,
        _ => throw new ArgumentOutOfRangeException(nameof(verification))
    };

    private static TransferState Map(TransferQueueState state) => state switch
    {
        TransferQueueState.Pending => TransferState.Pending,
        TransferQueueState.Preparing => TransferState.Preparing,
        TransferQueueState.Connecting => TransferState.Connecting,
        TransferQueueState.Transferring => TransferState.Transferring,
        TransferQueueState.Verifying => TransferState.Verifying,
        TransferQueueState.Finalizing => TransferState.Finalizing,
        TransferQueueState.Paused => TransferState.Paused,
        TransferQueueState.Retrying => TransferState.Retrying,
        TransferQueueState.BlockedCredential => TransferState.BlockedCredential,
        TransferQueueState.BlockedTrust => TransferState.BlockedTrust,
        TransferQueueState.Interrupted => TransferState.Interrupted,
        TransferQueueState.NeedsReconciliation => TransferState.NeedsReconciliation,
        TransferQueueState.RestartRequired => TransferState.RestartRequired,
        TransferQueueState.CleanupPending => TransferState.CleanupPending,
        TransferQueueState.Completed => TransferState.Completed,
        TransferQueueState.Failed => TransferState.Failed,
        TransferQueueState.Cancelled => TransferState.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    private static TransferQueueState Map(TransferState state) => state switch
    {
        TransferState.Pending => TransferQueueState.Pending,
        TransferState.Preparing => TransferQueueState.Preparing,
        TransferState.Connecting => TransferQueueState.Connecting,
        TransferState.Transferring => TransferQueueState.Transferring,
        TransferState.Verifying => TransferQueueState.Verifying,
        TransferState.Finalizing => TransferQueueState.Finalizing,
        TransferState.Paused => TransferQueueState.Paused,
        TransferState.Retrying => TransferQueueState.Retrying,
        TransferState.BlockedCredential => TransferQueueState.BlockedCredential,
        TransferState.BlockedTrust => TransferQueueState.BlockedTrust,
        TransferState.Interrupted => TransferQueueState.Interrupted,
        TransferState.NeedsReconciliation => TransferQueueState.NeedsReconciliation,
        TransferState.RestartRequired => TransferQueueState.RestartRequired,
        TransferState.CleanupPending => TransferQueueState.CleanupPending,
        TransferState.Completed => TransferQueueState.Completed,
        TransferState.Failed => TransferQueueState.Failed,
        TransferState.Cancelled => TransferQueueState.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    private static AgentIpcCommandResponse? ValidateRequest(int version, bool hasValidBounds)
    {
        if (!TransferQueueIpcContract.IsSupported(version))
        {
            return AgentIpcCommandResponse.Error(
                "transfer.contract.unsupported",
                "The transfer queue contract version is not supported.");
        }

        return hasValidBounds
            ? null
            : AgentIpcCommandResponse.Error(InvalidRequestCode, "The transfer queue request is invalid.");
    }

    private static AgentIpcCommandResponse EnqueueFailure(Guid transferId, StorageIpcFailure failure) =>
        AgentIpcCommandResponse.Create(
            TransferQueueIpcMessageTypes.EnqueueResponse,
            new TransferEnqueueResponse(
                TransferQueueIpcContract.CurrentVersion,
                transferId,
                Accepted: false,
                AlreadyExisted: false,
                Failure: failure));

    private static AgentIpcCommandResponse ListFailure(StorageIpcFailure failure) =>
        AgentIpcCommandResponse.Create(
            TransferQueueIpcMessageTypes.ListResponse,
            new TransferListResponse(
                TransferQueueIpcContract.CurrentVersion,
                [],
                ContinuationToken: null,
                failure));

    private static AgentIpcCommandResponse StatusFailure(Guid transferId, StorageIpcFailure failure) =>
        AgentIpcCommandResponse.Create(
            TransferQueueIpcMessageTypes.StatusResponse,
            new TransferStatusResponse(
                TransferQueueIpcContract.CurrentVersion,
                transferId,
                Transfer: null,
                failure));

    private static AgentIpcCommandResponse MutationResponse(
        string messageType,
        Guid transferId,
        TransferQueueMutationOutcome outcome,
        TransferQueueSummary? transfer = null,
        StorageIpcFailure? failure = null) => AgentIpcCommandResponse.Create(
            messageType,
            new TransferMutationResponse(
                TransferQueueIpcContract.CurrentVersion,
                transferId,
                outcome,
                transfer,
                failure));

    private static StorageIpcFailure ValidationFailure(string message) => new(
        InvalidRequestCode,
        StorageIpcFailureCategory.Validation,
        message,
        IsTransient: false);

    private static StorageIpcFailure NotFoundFailure() => new(
        "transfer.not_found",
        StorageIpcFailureCategory.NotFound,
        "The requested transfer was not found.",
        IsTransient: false);

    private static StorageIpcFailure ConflictFailure() => new(
        "transfer.revision.conflict",
        StorageIpcFailureCategory.Conflict,
        "The transfer changed before the requested action was applied.",
        IsTransient: true);

    private static StorageIpcFailure UnavailableFailure() => new(
        "transfer.queue.unavailable",
        StorageIpcFailureCategory.Unavailable,
        "The transfer queue is temporarily unavailable.",
        IsTransient: true);
}
