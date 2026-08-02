using StorageHub.Contracts.Results;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Abstractions;
using StorageHub.Sync.Persistence;

namespace StorageHub.Sync;

/// <summary>
/// Coordinates scans and immutable previews. Applying a preview only creates a durable outbox
/// command after revalidating the approval challenge; this service never mutates an endpoint.
/// </summary>
public sealed class SyncOrchestrationService : ISyncOrchestrationService
{
    private readonly ISyncProfileRepository _profiles;
    private readonly ISyncBaselineStore _baselines;
    private readonly ISyncPlanStore _plans;
    private readonly ISyncRunStore _runs;
    private readonly ISyncConflictStore _conflicts;
    private readonly ISyncEndpointConnector _connector;
    private readonly SyncSnapshotScanOptions _scanOptions;
    private readonly TimeProvider _timeProvider;

    public SyncOrchestrationService(
        ISyncProfileRepository profiles,
        ISyncBaselineStore baselines,
        ISyncPlanStore plans,
        ISyncRunStore runs,
        ISyncConflictStore conflicts,
        ISyncEndpointConnector connector,
        SyncSnapshotScanOptions? scanOptions = null,
        TimeProvider? timeProvider = null)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _baselines = baselines ?? throw new ArgumentNullException(nameof(baselines));
        _plans = plans ?? throw new ArgumentNullException(nameof(plans));
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _conflicts = conflicts ?? throw new ArgumentNullException(nameof(conflicts));
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _scanOptions = scanOptions ?? SyncSnapshotScanOptions.SynchronizationDefault;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<StorageResult<SyncPreviewResult>> GeneratePreviewAsync(
        SyncProfileId profileId,
        SyncPreviewTrigger trigger = SyncPreviewTrigger.Manual,
        string? triggerIdempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        if (profileId.IsEmpty)
        {
            throw new ArgumentException("A sync profile ID is required.", nameof(profileId));
        }

        if (!Enum.IsDefined(trigger))
        {
            throw new ArgumentOutOfRangeException(nameof(trigger));
        }

        if (trigger == SyncPreviewTrigger.Scheduled && string.IsNullOrWhiteSpace(triggerIdempotencyKey))
        {
            return Fail<SyncPreviewResult>(
                "sync.preview.idempotency_required",
                StorageFailureKind.Validation,
                "A scheduled preview requires its durable trigger idempotency key.");
        }

        var triggerKey = string.IsNullOrWhiteSpace(triggerIdempotencyKey)
            ? $"manual:{Guid.NewGuid():D}"
            : triggerIdempotencyKey.Trim();
        if (triggerKey.Length > 512 || triggerKey.Any(char.IsControl))
        {
            return Fail<SyncPreviewResult>(
                "sync.preview.invalid_idempotency_key",
                StorageFailureKind.Validation,
                "The preview idempotency key is invalid.");
        }

        var existing = await _runs.GetByTriggerAsync(profileId, triggerKey, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            var existingPlan = await _plans.GetAsync(existing.PlanId, cancellationToken).ConfigureAwait(false);
            if (existingPlan is null || existingPlan.Plan.Digest != existing.PlanDigest)
            {
                return Fail<SyncPreviewResult>(
                    "sync.preview.persisted_plan_invalid",
                    StorageFailureKind.Integrity,
                    "The durable preview no longer has its exact immutable plan.");
            }

            var existingConflicts = await _conflicts.ListForRunAsync(
                existing.SyncRunId,
                maximumCount: 1_000,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return StorageResult<SyncPreviewResult>.Success(new SyncPreviewResult(
                existing,
                existingPlan.Plan,
                existingConflicts));
        }

        var profile = await _profiles.GetAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return Fail<SyncPreviewResult>(
                "sync.profile.not_found",
                StorageFailureKind.NotFound,
                "The sync profile was not found.");
        }

        if (!profile.Enabled)
        {
            return Fail<SyncPreviewResult>(
                "sync.profile.disabled",
                StorageFailureKind.Unavailable,
                "The sync profile is disabled.");
        }

        var baseline = await _baselines.GetAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (baseline is null)
        {
            return Fail<SyncPreviewResult>(
                "sync.baseline.unavailable",
                StorageFailureKind.Integrity,
                "The sync profile baseline state is unavailable.");
        }

        var leftOpen = await _connector.OpenAsync(
            profile.LeftConnectionProfileId,
            cancellationToken).ConfigureAwait(false);
        if (leftOpen.IsFailure)
        {
            return StorageResult<SyncPreviewResult>.Fail(leftOpen.Error);
        }

        await using var leftConnection = leftOpen.Value;
        var left = leftConnection.Session;
        var rightOpen = await _connector.OpenAsync(
            profile.RightConnectionProfileId,
            cancellationToken).ConfigureAwait(false);
        if (rightOpen.IsFailure)
        {
            return StorageResult<SyncPreviewResult>.Fail(rightOpen.Error);
        }

        await using var rightConnection = rightOpen.Value;
        var right = rightConnection.Session;
        var sessionValidation = ValidateSessions(profile, left, right);
        if (sessionValidation is not null)
        {
            return StorageResult<SyncPreviewResult>.Fail(sessionValidation);
        }

        var leftRoot = CreateRoot(left, profile.LeftRoot);
        var rightRoot = CreateRoot(right, profile.RightRoot);
        if (leftRoot.IsFailure)
        {
            return StorageResult<SyncPreviewResult>.Fail(leftRoot.Error);
        }

        if (rightRoot.IsFailure)
        {
            return StorageResult<SyncPreviewResult>.Fail(rightRoot.Error);
        }

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
        if (leftScan.IsFailure)
        {
            return StorageResult<SyncPreviewResult>.Fail(leftScan.Error);
        }

        if (rightScan.IsFailure)
        {
            return StorageResult<SyncPreviewResult>.Fail(rightScan.Error);
        }

        var currentProfile = await _profiles.GetAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (currentProfile is null || !currentProfile.Enabled ||
            currentProfile.Revision != profile.Revision ||
            !StringComparer.Ordinal.Equals(currentProfile.PolicySha256, profile.PolicySha256))
        {
            return Fail<SyncPreviewResult>(
                "sync.profile.changed_during_scan",
                StorageFailureKind.Conflict,
                "The sync profile changed while its endpoints were being scanned.");
        }

        var createdAtUtc = _timeProvider.GetUtcNow();
        var planResult = SyncPlanBuilder.Build(new SyncPlanBuildRequest(
            OperationPlanId.New(),
            profileId,
            baseline.Generation,
            leftRoot.Value,
            rightRoot.Value,
            leftScan.Value,
            rightScan.Value,
            baseline.Items,
            profile.Direction,
            profile.DeletionMode,
            createdAtUtc));
        if (planResult.IsFailure)
        {
            return StorageResult<SyncPreviewResult>.Fail(planResult.Error);
        }

        var persistedPlan = await _plans.PutAsync(planResult.Value.Plan, cancellationToken)
            .ConfigureAwait(false);
        if (persistedPlan.Status is not (
                SyncPersistenceMutationStatus.Applied or
                SyncPersistenceMutationStatus.AlreadyApplied))
        {
            return Fail<SyncPreviewResult>(
                "sync.preview.plan_persistence_failed",
                StorageFailureKind.Conflict,
                "The immutable sync plan could not be durably persisted.");
        }

        var sessions = new Dictionary<ConnectionProfileId, IStorageEndpointSession>
        {
            [left.ProfileId] = left,
            [right.ProfileId] = right,
        };
        var approval = SyncExecutionApproval.Create(
            planResult.Value.Plan,
            sessions,
            planResult.Value.Snapshots,
            SyncPlanExecutionMode.Execute,
            profile.DeletionSafetyPolicy,
            profile.TransferOptions);
        var deletionCount = planResult.Value.Plan.Operations.LongCount(operation => operation.IsDestructive);
        var deletionDecision = profile.DeletionSafetyPolicy.Evaluate(
            deletionCount,
            planResult.Value.Snapshots.BaselineItemCount,
            planResult.Value.Snapshots.Left,
            planResult.Value.Snapshots.Right);
        var previewWrite = await _runs.CreatePreviewAsync(
            new SyncPreviewDraft(
                SyncRunId.New(),
                profileId,
                profile.Revision,
                profile.PolicySha256,
                planResult.Value.Plan.PlanId,
                planResult.Value.Plan.Digest,
                planResult.Value.Snapshots,
                approval.Sha256Hex!,
                trigger,
                triggerKey,
                planResult.Value.Conflicts,
                createdAtUtc,
                !deletionDecision.Allowed),
            cancellationToken).ConfigureAwait(false);
        if (previewWrite.Value is null || previewWrite.Status is not (
                SyncPersistenceMutationStatus.Applied or
                SyncPersistenceMutationStatus.AlreadyApplied))
        {
            return Fail<SyncPreviewResult>(
                "sync.preview.persistence_conflict",
                StorageFailureKind.Conflict,
                "The preview could not be persisted because its profile or trigger changed.");
        }

        var conflicts = await _conflicts.ListForRunAsync(
            previewWrite.Value.SyncRunId,
            maximumCount: 1_000,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return StorageResult<SyncPreviewResult>.Success(new SyncPreviewResult(
            previewWrite.Value,
            planResult.Value.Plan,
            conflicts));
    }

    public async ValueTask<StorageResult<SyncPreviewRecord>> ApproveAndDispatchAsync(
        SyncRunId syncRunId,
        long expectedRevision,
        string approvalSha256,
        CancellationToken cancellationToken = default)
    {
        if (syncRunId.IsEmpty)
        {
            throw new ArgumentException("A sync run ID is required.", nameof(syncRunId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        if (!SyncExecutionApproval.TryParse(approvalSha256, out var suppliedApproval))
        {
            return Fail<SyncPreviewRecord>(
                "sync.approval.invalid",
                StorageFailureKind.Validation,
                "The approval challenge is not a SHA-256 value.");
        }

        var preview = await _runs.GetAsync(syncRunId, cancellationToken).ConfigureAwait(false);
        if (preview is null)
        {
            return Fail<SyncPreviewRecord>(
                "sync.preview.not_found",
                StorageFailureKind.NotFound,
                "The sync preview was not found.");
        }

        if (preview.ApprovedForExecution &&
            preview.DispatchEventId == syncRunId.Value &&
            StringComparer.OrdinalIgnoreCase.Equals(
                preview.ApprovalChallengeSha256,
                approvalSha256))
        {
            return StorageResult<SyncPreviewRecord>.Success(preview);
        }

        if (preview.State.Phase != SyncRunPhase.AwaitingApproval ||
            preview.State.Revision != expectedRevision ||
            preview.ConflictCount != 0)
        {
            return Fail<SyncPreviewRecord>(
                "sync.approval.state_conflict",
                StorageFailureKind.Conflict,
                "The sync preview is not awaiting approval at the expected revision.");
        }

        var profile = await _profiles.GetAsync(preview.ProfileId, cancellationToken).ConfigureAwait(false);
        if (profile is null || !profile.Enabled || profile.Revision != preview.ProfileRevision ||
            !StringComparer.Ordinal.Equals(profile.PolicySha256, preview.ProfilePolicySha256))
        {
            return Fail<SyncPreviewRecord>(
                "sync.approval.profile_changed",
                StorageFailureKind.Conflict,
                "The sync profile changed after the preview was generated.");
        }

        var persistedPlan = await _plans.GetAsync(preview.PlanId, cancellationToken).ConfigureAwait(false);
        if (persistedPlan is null || persistedPlan.Plan.Digest != preview.PlanDigest ||
            !persistedPlan.Plan.HasValidDigest)
        {
            return Fail<SyncPreviewRecord>(
                "sync.approval.plan_invalid",
                StorageFailureKind.Integrity,
                "The immutable plan no longer matches the preview.");
        }

        var leftOpen = await _connector.OpenAsync(profile.LeftConnectionProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (leftOpen.IsFailure)
        {
            return StorageResult<SyncPreviewRecord>.Fail(leftOpen.Error);
        }

        await using var leftConnection = leftOpen.Value;
        var left = leftConnection.Session;
        var rightOpen = await _connector.OpenAsync(profile.RightConnectionProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (rightOpen.IsFailure)
        {
            return StorageResult<SyncPreviewRecord>.Fail(rightOpen.Error);
        }

        await using var rightConnection = rightOpen.Value;
        var right = rightConnection.Session;
        var sessionValidation = ValidateSessions(profile, left, right);
        if (sessionValidation is not null)
        {
            return StorageResult<SyncPreviewRecord>.Fail(sessionValidation);
        }

        var sessions = new Dictionary<ConnectionProfileId, IStorageEndpointSession>
        {
            [left.ProfileId] = left,
            [right.ProfileId] = right,
        };
        var currentApproval = SyncExecutionApproval.Create(
            persistedPlan.Plan,
            sessions,
            preview.Snapshots,
            SyncPlanExecutionMode.Execute,
            profile.DeletionSafetyPolicy,
            profile.TransferOptions);
        if (currentApproval != suppliedApproval ||
            !StringComparer.OrdinalIgnoreCase.Equals(
                preview.ApprovalChallengeSha256,
                approvalSha256))
        {
            return Fail<SyncPreviewRecord>(
                "sync.approval.mismatch",
                StorageFailureKind.Conflict,
                "The approved plan, roots, capabilities, or safety policy changed.");
        }

        var dispatched = await _runs.ApproveAndDispatchAsync(
            new SyncApplyDispatchRequest(
                syncRunId,
                expectedRevision,
                profile.Revision,
                profile.PolicySha256,
                currentApproval.Sha256Hex!,
                syncRunId.Value,
                _timeProvider.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);
        return dispatched.Value is not null && dispatched.Status is (
                SyncPersistenceMutationStatus.Applied or
                SyncPersistenceMutationStatus.AlreadyApplied)
            ? StorageResult<SyncPreviewRecord>.Success(dispatched.Value)
            : Fail<SyncPreviewRecord>(
                "sync.approval.persistence_conflict",
                StorageFailureKind.Conflict,
                "The sync preview changed before approval could be durably dispatched.");
    }

    private static StorageFailure? ValidateSessions(
        SyncProfile profile,
        IStorageEndpointSession left,
        IStorageEndpointSession right)
    {
        if (left.ProfileId != profile.LeftConnectionProfileId ||
            right.ProfileId != profile.RightConnectionProfileId ||
            left.ProfileId == right.ProfileId)
        {
            return new StorageFailure(
                "sync.session.identity_mismatch",
                StorageFailureKind.Security,
                "A connector returned a session for a different connection profile.");
        }

        return null;
    }

    private static StorageResult<StorageAddress> CreateRoot(
        IStorageEndpointSession session,
        string relativeRoot) => StorageAddress.Create(
        session.ProfileId,
        session.RootIdentity,
        relativeRoot);

    private static StorageResult<T> Fail<T>(
        string code,
        StorageFailureKind kind,
        string message) => StorageResult<T>.Fail(new StorageFailure(code, kind, message));
}
