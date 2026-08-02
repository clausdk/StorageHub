using System.Text.Json;
using System.Text.Json.Serialization;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Abstractions;
using StorageHub.Sync.Persistence;

namespace StorageHub.Sync;

public enum SyncOutboxProcessingOutcome
{
    Completed,
    Retry,
    DeadLetter,
    LeaseLost,
}

public sealed record SyncOutboxProcessingResult(
    SyncOutboxProcessingOutcome Outcome,
    string? ErrorCode = null,
    string? SafeErrorSummary = null,
    TimeSpan? RetryDelay = null)
{
    public static SyncOutboxProcessingResult Complete() => new(SyncOutboxProcessingOutcome.Completed);

    public static SyncOutboxProcessingResult Retry(
        string code,
        string summary,
        TimeSpan? delay = null) => new(
        SyncOutboxProcessingOutcome.Retry,
        code,
        summary,
        delay ?? TimeSpan.FromSeconds(30));

    public static SyncOutboxProcessingResult DeadLetter(string code, string summary) => new(
        SyncOutboxProcessingOutcome.DeadLetter,
        code,
        summary);

    public static SyncOutboxProcessingResult LostLease() => new(SyncOutboxProcessingOutcome.LeaseLost);
}

public interface ISyncOutboxEventProcessor
{
    ValueTask<SyncOutboxProcessingResult> ProcessAsync(
        OutboxDeliveryLease lease,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Processes only StorageHub's two sync command events. Preview is idempotent and read-only;
/// apply is a fenced durable state machine and never resumes an interrupted mutation.
/// </summary>
public sealed class SyncOutboxEventProcessor : ISyncOutboxEventProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly ISyncOrchestrationService _orchestration;
    private readonly ISyncProfileRepository _profiles;
    private readonly ISyncPlanStore _plans;
    private readonly ISyncExecutionStore _executions;
    private readonly ISyncEndpointConnector _connector;
    private readonly SyncSnapshotScanOptions _scanOptions;
    private readonly TimeProvider _timeProvider;

    public SyncOutboxEventProcessor(
        ISyncOrchestrationService orchestration,
        ISyncProfileRepository profiles,
        ISyncPlanStore plans,
        ISyncExecutionStore executions,
        ISyncEndpointConnector connector,
        SyncSnapshotScanOptions? scanOptions = null,
        TimeProvider? timeProvider = null)
    {
        _orchestration = orchestration ?? throw new ArgumentNullException(nameof(orchestration));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _plans = plans ?? throw new ArgumentNullException(nameof(plans));
        _executions = executions ?? throw new ArgumentNullException(nameof(executions));
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _scanOptions = scanOptions ?? SyncSnapshotScanOptions.SynchronizationDefault;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask<SyncOutboxProcessingResult> ProcessAsync(
        OutboxDeliveryLease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(lease.Event);
        return lease.Event.EventKind switch
        {
            SyncOutboxEventKinds.PreviewRequested => ProcessPreviewAsync(lease, cancellationToken),
            SyncOutboxEventKinds.ApplyRequested => ProcessApplyAsync(lease, cancellationToken),
            _ => ValueTask.FromResult(SyncOutboxProcessingResult.DeadLetter(
                "sync.outbox.kind_not_owned",
                "The sync worker does not own this outbox event kind.")),
        };
    }

    private async ValueTask<SyncOutboxProcessingResult> ProcessPreviewAsync(
        OutboxDeliveryLease lease,
        CancellationToken cancellationToken)
    {
        if (!TryDeserialize(lease.Event.SafePayloadJson, out ScheduledSyncPreviewOutboxPayload? payload) ||
            payload is null ||
            !Guid.TryParse(payload.SyncScheduleId, out var scheduleId) || scheduleId == Guid.Empty ||
            !Guid.TryParse(payload.LeaseId, out var schedulerLeaseId) || schedulerLeaseId != lease.Event.EventId ||
            !SyncProfileId.TryParse(payload.SyncProfileId, out var profileId) ||
            payload.FencingToken != lease.Event.SequenceNumber ||
            payload.FencingToken <= 0 ||
            payload.ScheduledForUtc.Offset != TimeSpan.Zero ||
            !StringComparer.Ordinal.Equals(
                lease.Event.AggregateId,
                $"sync-schedule:{scheduleId:D}"))
        {
            return SyncOutboxProcessingResult.DeadLetter(
                "sync.preview.event_invalid",
                "The scheduled preview event does not match its durable dispatch identity.");
        }

        var preview = await _orchestration.GeneratePreviewAsync(
            profileId,
            SyncPreviewTrigger.Scheduled,
            $"outbox:{lease.Event.EventId:D}",
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return preview.IsSuccess
            ? SyncOutboxProcessingResult.Complete()
            : preview.Error.IsTransient
                ? SyncOutboxProcessingResult.Retry(
                    preview.Error.Code,
                    "The scheduled preview could not be generated yet.")
                : SyncOutboxProcessingResult.DeadLetter(
                    preview.Error.Code,
                    "The scheduled preview was rejected safely.");
    }

    private async ValueTask<SyncOutboxProcessingResult> ProcessApplyAsync(
        OutboxDeliveryLease lease,
        CancellationToken cancellationToken)
    {
        if (!TryParseApply(lease, out var payload, out var runId, out var profileId, out var planId,
                out var planDigest))
        {
            return SyncOutboxProcessingResult.DeadLetter(
                "sync.apply.event_invalid",
                "The apply event does not match a complete immutable execution binding.");
        }

        var begin = await _executions.BeginAsync(
            new SyncExecutionBeginRequest(
                lease,
                runId,
                profileId,
                planId,
                planDigest,
                lease.Event.SequenceNumber,
                payload.ProfileRevision,
                payload.ProfilePolicySha256,
                payload.ApprovalSha256,
                _timeProvider.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);
        switch (begin.Status)
        {
            case SyncExecutionBeginStatus.AlreadyCompleted:
                return SyncOutboxProcessingResult.Complete();
            case SyncExecutionBeginStatus.ReconciliationRequired:
                return SyncOutboxProcessingResult.DeadLetter(
                    "sync.apply.interrupted",
                    "A prior apply attempt may have changed provider state and requires reconciliation.");
            case SyncExecutionBeginStatus.StaleLease:
                return SyncOutboxProcessingResult.LostLease();
            case SyncExecutionBeginStatus.NotFound:
                return SyncOutboxProcessingResult.DeadLetter(
                    "sync.apply.run_not_found",
                    "The sync run referenced by the apply event no longer exists.");
            case SyncExecutionBeginStatus.Conflict:
                return SyncOutboxProcessingResult.DeadLetter(
                    "sync.apply.binding_conflict",
                    "The sync run, profile, plan, baseline, or approval no longer matches the dispatch.");
            case SyncExecutionBeginStatus.Acquired:
                break;
            default:
                throw new InvalidOperationException("The execution store returned an unknown begin status.");
        }

        var context = begin.Context ?? throw new InvalidDataException(
            "An acquired sync execution did not return its durable context.");
        var current = context.Preview;
        var profile = await _profiles.GetAsync(profileId, cancellationToken).ConfigureAwait(false);
        var persistedPlan = await _plans.GetAsync(planId, cancellationToken).ConfigureAwait(false);
        if (profile is null || persistedPlan is null || !ProfileAndPlanMatch(
                profile,
                persistedPlan.Plan,
                payload,
                planDigest,
                context.Baseline))
        {
            return await FailBeforeMutationAsync(
                lease,
                current,
                "The execution binding could not be reloaded exactly.").ConfigureAwait(false);
        }

        var leftOpen = await _connector.OpenAsync(
            profile.LeftConnectionProfileId,
            cancellationToken).ConfigureAwait(false);
        if (leftOpen.IsFailure)
        {
            return await HandleConnectionFailureAsync(lease, current, leftOpen.Error).ConfigureAwait(false);
        }

        await using var leftConnection = leftOpen.Value;
        var rightOpen = await _connector.OpenAsync(
            profile.RightConnectionProfileId,
            cancellationToken).ConfigureAwait(false);
        if (rightOpen.IsFailure)
        {
            return await HandleConnectionFailureAsync(lease, current, rightOpen.Error).ConfigureAwait(false);
        }

        await using var rightConnection = rightOpen.Value;
        var left = leftConnection.Session;
        var right = rightConnection.Session;
        if (left.ProfileId != profile.LeftConnectionProfileId ||
            right.ProfileId != profile.RightConnectionProfileId ||
            left.ProfileId == right.ProfileId)
        {
            return await FailBeforeMutationAsync(
                lease,
                current,
                "A connector returned a session for the wrong profile.").ConfigureAwait(false);
        }

        var leftRoot = StorageAddress.Create(left.ProfileId, left.RootIdentity, profile.LeftRoot);
        var rightRoot = StorageAddress.Create(right.ProfileId, right.RootIdentity, profile.RightRoot);
        var sessions = new Dictionary<ConnectionProfileId, IStorageEndpointSession>
        {
            [left.ProfileId] = left,
            [right.ProfileId] = right,
        };
        var approved = SyncExecutionApproval.Parse(payload.ApprovalSha256);
        if (leftRoot.IsFailure || rightRoot.IsFailure ||
            !current.Snapshots.Left.IsComplete || !current.Snapshots.Right.IsComplete ||
            SyncExecutionApproval.Create(
                persistedPlan.Plan,
                sessions,
                current.Snapshots,
                SyncPlanExecutionMode.Execute,
                profile.DeletionSafetyPolicy,
                profile.TransferOptions) != approved)
        {
            return await FailBeforeMutationAsync(
                lease,
                current,
                "Live roots or capabilities no longer match the approved execution.").ConfigureAwait(false);
        }

        var armed = await _executions.ArmProviderMutationAsync(
            lease,
            runId,
            current.State.Revision,
            _timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        if (armed == SyncPersistenceMutationStatus.StaleLease)
        {
            return SyncOutboxProcessingResult.LostLease();
        }

        if (armed != SyncPersistenceMutationStatus.Applied)
        {
            return await ReconcileAsync(
                lease,
                current,
                "Execution authorization changed before provider I/O.").ConfigureAwait(false);
        }

        var progress = new MutationProgress();
        var execution = await SyncPlanExecutor.ExecuteAsync(
            new SyncPlanExecutionRequest(
                persistedPlan.Plan,
                approved,
                sessions,
                current.Snapshots,
                SyncPlanExecutionMode.Execute,
                profile.DeletionSafetyPolicy,
                profile.TransferOptions),
            progress,
            _timeProvider,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (execution.IsFailure && execution.Error.Kind == StorageFailureKind.Cancelled)
        {
            return await ReconcileAsync(
                lease,
                current,
                "Execution was cancelled after its provider-mutation fence was armed.")
                .ConfigureAwait(false);
        }

        if (execution.IsFailure)
        {
            return await ReconcileAsync(
                lease,
                current,
                progress.OperationStarted
                    ? "Provider I/O failed after at least one operation began."
                    : "Execution was armed and stopped before completion; provider state is treated as uncertain.")
                .ConfigureAwait(false);
        }

        var verifying = await TransitionAsync(
            lease,
            current,
            SyncRunPhase.Verifying,
            SyncStatusCode.None,
            safeErrorSummary: null).ConfigureAwait(false);
        if (verifying.Status == SyncPersistenceMutationStatus.StaleLease)
        {
            return SyncOutboxProcessingResult.LostLease();
        }

        if (verifying.Value is null || verifying.Status != SyncPersistenceMutationStatus.Applied)
        {
            return await ReconcileAsync(
                lease,
                current,
                "The execution result could not be durably fenced before verification.").ConfigureAwait(false);
        }

        current = verifying.Value;
        var leftScanTask = SyncSnapshotScanner.ScanAsync(
            left,
            leftRoot.Value,
            _scanOptions,
            cancellationToken).AsTask();
        var rightScanTask = SyncSnapshotScanner.ScanAsync(
            right,
            rightRoot.Value,
            _scanOptions,
            cancellationToken).AsTask();
        await Task.WhenAll(leftScanTask, rightScanTask).ConfigureAwait(false);
        var leftScan = await leftScanTask.ConfigureAwait(false);
        var rightScan = await rightScanTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (leftScan.IsFailure || rightScan.IsFailure)
        {
            return await ReconcileAsync(
                lease,
                current,
                "A complete post-apply endpoint verification scan could not be obtained.")
                .ConfigureAwait(false);
        }

        var currentProfile = await _profiles.GetAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (currentProfile is null || currentProfile.Revision != profile.Revision ||
            !currentProfile.Enabled ||
            !StringComparer.Ordinal.Equals(currentProfile.PolicySha256, profile.PolicySha256))
        {
            return await ReconcileAsync(
                lease,
                current,
                "The sync profile changed before post-apply verification completed.").ConfigureAwait(false);
        }

        var baseline = VerifiedSyncBaselineBuilder.Build(
            profile,
            persistedPlan.Plan,
            context.Baseline,
            leftScan.Value,
            rightScan.Value);
        if (baseline.IsFailure)
        {
            return await ReconcileAsync(lease, current, baseline.Error.Message).ConfigureAwait(false);
        }

        var committing = await TransitionAsync(
            lease,
            current,
            SyncRunPhase.CommittingBaseline,
            SyncStatusCode.None,
            safeErrorSummary: null).ConfigureAwait(false);
        if (committing.Status == SyncPersistenceMutationStatus.StaleLease)
        {
            return SyncOutboxProcessingResult.LostLease();
        }

        if (committing.Value is null || committing.Status != SyncPersistenceMutationStatus.Applied)
        {
            return await ReconcileAsync(
                lease,
                current,
                "The verified result could not enter its atomic baseline commit.").ConfigureAwait(false);
        }

        current = committing.Value;
        var committed = await _executions.CommitBaselineAndCompleteAsync(
            new SyncExecutionBaselineCommitRequest(
                lease,
                runId,
                current.State.Revision,
                profileId,
                profile.Revision,
                profile.PolicySha256,
                context.Baseline.Generation,
                context.Baseline.Revision,
                checked(context.Baseline.Generation + 1),
                baseline.Value,
                NextTransitionTime(current.State)),
            CancellationToken.None).ConfigureAwait(false);
        if (committed.Status == SyncPersistenceMutationStatus.StaleLease)
        {
            return SyncOutboxProcessingResult.LostLease();
        }

        if (committed.Value is null || committed.Status != SyncPersistenceMutationStatus.Applied)
        {
            return await ReconcileAsync(
                lease,
                current,
                "The verified baseline could not be atomically committed under the execution fence.")
                .ConfigureAwait(false);
        }

        return SyncOutboxProcessingResult.Complete();
    }

    private async ValueTask<SyncOutboxProcessingResult> HandleConnectionFailureAsync(
        OutboxDeliveryLease lease,
        SyncPreviewRecord current,
        StorageFailure failure)
    {
        var (phase, status, summary) = failure.Code.StartsWith("storage.trust.", StringComparison.Ordinal)
            ? (SyncRunPhase.BlockedTrust, SyncStatusCode.TrustRequired, "Endpoint trust approval is required.")
            : failure.Kind == StorageFailureKind.Unauthorized ||
              failure.Code.StartsWith("storage.credential.", StringComparison.Ordinal)
                ? (SyncRunPhase.BlockedCredential, SyncStatusCode.CredentialUnavailable,
                    "A required endpoint credential is unavailable.")
                : (SyncRunPhase.BlockedEndpoint, SyncStatusCode.EndpointUnavailable,
                    "A required endpoint could not be opened safely.");
        var transition = await TransitionAsync(lease, current, phase, status, summary).ConfigureAwait(false);
        return transition.Status == SyncPersistenceMutationStatus.Applied
            ? SyncOutboxProcessingResult.DeadLetter(failure.Code, summary)
            : SyncOutboxProcessingResult.LostLease();
    }

    private async ValueTask<SyncOutboxProcessingResult> FailBeforeMutationAsync(
        OutboxDeliveryLease lease,
        SyncPreviewRecord current,
        string summary)
    {
        var transition = await TransitionAsync(
            lease,
            current,
            SyncRunPhase.Failed,
            SyncStatusCode.ProviderFailure,
            summary).ConfigureAwait(false);
        return transition.Status == SyncPersistenceMutationStatus.Applied
            ? SyncOutboxProcessingResult.DeadLetter("sync.apply.preflight_failed", summary)
            : SyncOutboxProcessingResult.LostLease();
    }

    private async ValueTask<SyncOutboxProcessingResult> ReconcileAsync(
        OutboxDeliveryLease lease,
        SyncPreviewRecord current,
        string summary)
    {
        var transition = await TransitionAsync(
            lease,
            current,
            SyncRunPhase.NeedsReconciliation,
            SyncStatusCode.StateUncertain,
            summary).ConfigureAwait(false);
        return transition.Status == SyncPersistenceMutationStatus.Applied
            ? SyncOutboxProcessingResult.DeadLetter("sync.apply.reconciliation_required", summary)
            : SyncOutboxProcessingResult.LostLease();
    }

    private ValueTask<SyncPersistenceResult<SyncPreviewRecord>> TransitionAsync(
        OutboxDeliveryLease lease,
        SyncPreviewRecord current,
        SyncRunPhase next,
        SyncStatusCode status,
        string? safeErrorSummary) => _executions.TransitionAsync(
        new SyncExecutionTransitionRequest(
            lease,
            current.SyncRunId,
            current.State.Revision,
            current.State.Phase,
            next,
            NextTransitionTime(current.State),
            status,
            safeErrorSummary),
        CancellationToken.None);

    private DateTimeOffset NextTransitionTime(SyncRunState state)
    {
        var now = _timeProvider.GetUtcNow();
        return now < state.TransitionedAtUtc ? state.TransitionedAtUtc : now;
    }

    private static bool ProfileAndPlanMatch(
        SyncProfile profile,
        ImmutableSyncPlan plan,
        SyncApplyOutboxPayload payload,
        SyncPlanDigest planDigest,
        SyncBaselineSnapshot baseline) =>
        profile.Enabled &&
        profile.Revision == payload.ProfileRevision &&
        StringComparer.OrdinalIgnoreCase.Equals(profile.PolicySha256, payload.ProfilePolicySha256) &&
        plan.ProfileId == profile.ProfileId &&
        plan.PlanId.ToString() == payload.OperationPlanId &&
        plan.Digest == planDigest && plan.HasValidDigest &&
        plan.BaselineGeneration == baseline.Generation;

    private static bool TryParseApply(
        OutboxDeliveryLease lease,
        out SyncApplyOutboxPayload payload,
        out SyncRunId runId,
        out SyncProfileId profileId,
        out OperationPlanId planId,
        out SyncPlanDigest planDigest)
    {
        payload = null!;
        runId = default;
        profileId = default;
        planId = default;
        planDigest = default;
        if (!TryDeserialize(
                lease.Event.SafePayloadJson,
                out SyncApplyOutboxPayload? parsedPayload) || parsedPayload is null)
        {
            return false;
        }

        payload = parsedPayload;
        return
            SyncRunId.TryParse(payload.SyncRunId, out runId) &&
            SyncProfileId.TryParse(payload.SyncProfileId, out profileId) &&
            OperationPlanId.TryParse(payload.OperationPlanId, out planId) &&
            SyncPlanDigest.TryParse(payload.PlanSha256, out planDigest) &&
            SyncExecutionApproval.TryParse(payload.ApprovalSha256, out _) &&
            payload.ProfileRevision > 0 &&
            IsSha256(payload.ProfilePolicySha256) &&
            lease.Event.EventId == runId.Value &&
            lease.Event.SequenceNumber >= 0 &&
            StringComparer.Ordinal.Equals(lease.Event.AggregateId, $"sync-run:{runId}");
    }

    private static bool TryDeserialize<T>(string json, out T? value)
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(json, JsonOptions);
            return value is not null;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private sealed class MutationProgress : IProgress<SyncPlanExecutionEvent>
    {
        private int _operationStarted;

        public bool OperationStarted => Volatile.Read(ref _operationStarted) != 0;

        public void Report(SyncPlanExecutionEvent value)
        {
            if (value.Kind == SyncPlanExecutionEventKind.OperationStarted)
            {
                _ = Interlocked.Exchange(ref _operationStarted, 1);
            }
        }
    }
}
