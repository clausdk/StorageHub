using System.Collections.ObjectModel;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Capabilities;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Abstractions;
using StorageHub.Storage.Models;
using StorageHub.Transfers;

namespace StorageHub.Sync;

public enum SyncPlanExecutionMode
{
    Execute = 0,
    Preview = 1,
}

public enum SyncPlanExecutionEventKind
{
    PlanValidated = 0,
    OperationPreviewed = 1,
    OperationStarted = 2,
    OperationProgress = 3,
    OperationCompleted = 4,
    PlanCompleted = 5,
    PlanFailed = 6,
    PlanCancelled = 7,
}

/// <summary>A safe event suitable for forwarding over the local agent event stream.</summary>
public sealed record SyncPlanExecutionEvent(
    SyncPlanExecutionEventKind Kind,
    OperationPlanId PlanId,
    SyncPlanDigest PlanDigest,
    int? OperationSequence,
    SyncPlanOperationKind? OperationKind,
    int ProcessedOperations,
    int TotalOperations,
    long BytesTransferred,
    long? TotalBytes,
    StorageFailure? Failure,
    DateTimeOffset OccurredAtUtc);

/// <summary>
/// Completeness evidence produced by the scan/planning phase. The executor never infers this
/// evidence from an empty result and never performs its own lossy fallback listing.
/// </summary>
public sealed record SyncExecutionSnapshots
{
    public SyncExecutionSnapshots(
        SnapshotCompleteness left,
        SnapshotCompleteness right,
        long baselineItemCount,
        IReadOnlyDictionary<ConnectionProfileId, string>? verifiedRootIdentities = null)
    {
        Left = left ?? throw new ArgumentNullException(nameof(left));
        Right = right ?? throw new ArgumentNullException(nameof(right));
        ArgumentOutOfRangeException.ThrowIfNegative(baselineItemCount);
        BaselineItemCount = baselineItemCount;

        var roots = new Dictionary<ConnectionProfileId, string>();
        foreach (var (profileId, rootIdentity) in verifiedRootIdentities ??
                 new Dictionary<ConnectionProfileId, string>())
        {
            if (profileId.IsEmpty || string.IsNullOrWhiteSpace(rootIdentity) || rootIdentity.Any(char.IsControl))
            {
                throw new ArgumentException(
                    "Verified root evidence requires non-empty profile IDs and safe root identities.",
                    nameof(verifiedRootIdentities));
            }

            roots.Add(profileId, rootIdentity);
        }

        VerifiedRootIdentities = new ReadOnlyDictionary<ConnectionProfileId, string>(roots);
    }

    public SnapshotCompleteness Left { get; }

    public SnapshotCompleteness Right { get; }

    public long BaselineItemCount { get; }

    /// <summary>
    /// Exact endpoint roots proven by the scan that produced this snapshot. A boolean
    /// "verified" flag alone is insufficient to bind a destructive plan to a root.
    /// </summary>
    public IReadOnlyDictionary<ConnectionProfileId, string> VerifiedRootIdentities { get; }
}

/// <summary>
/// All inputs needed to preflight and execute one immutable plan. Sessions remain caller-owned.
/// </summary>
public sealed record SyncPlanExecutionRequest
{
    public SyncPlanExecutionRequest(
        ImmutableSyncPlan plan,
        SyncPlanDigest approvedDigest,
        IReadOnlyDictionary<ConnectionProfileId, IStorageEndpointSession> sessions,
        SyncExecutionSnapshots snapshots,
        SyncPlanExecutionMode mode = SyncPlanExecutionMode.Execute,
        DeletionSafetyPolicy? deletionPolicy = null,
        TransferExecutionOptions? transferOptions = null,
        string? profilePolicySha256 = null)
        : this(
            plan,
            approvedDigest,
            approvedExecution: default,
            sessions,
            snapshots,
            mode,
            deletionPolicy,
            transferOptions,
            profilePolicySha256)
    {
    }

    public SyncPlanExecutionRequest(
        ImmutableSyncPlan plan,
        SyncExecutionApproval approvedExecution,
        IReadOnlyDictionary<ConnectionProfileId, IStorageEndpointSession> sessions,
        SyncExecutionSnapshots snapshots,
        SyncPlanExecutionMode mode = SyncPlanExecutionMode.Execute,
        DeletionSafetyPolicy? deletionPolicy = null,
        TransferExecutionOptions? transferOptions = null,
        string? profilePolicySha256 = null)
        : this(
            plan,
            plan?.Digest ?? default,
            approvedExecution,
            sessions,
            snapshots,
            mode,
            deletionPolicy,
            transferOptions,
            profilePolicySha256)
    {
    }

    private SyncPlanExecutionRequest(
        ImmutableSyncPlan plan,
        SyncPlanDigest approvedDigest,
        SyncExecutionApproval approvedExecution,
        IReadOnlyDictionary<ConnectionProfileId, IStorageEndpointSession> sessions,
        SyncExecutionSnapshots snapshots,
        SyncPlanExecutionMode mode,
        DeletionSafetyPolicy? deletionPolicy,
        TransferExecutionOptions? transferOptions,
        string? profilePolicySha256)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        ArgumentNullException.ThrowIfNull(sessions);
        Snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        var sessionSnapshot = new Dictionary<ConnectionProfileId, IStorageEndpointSession>();
        foreach (var (profileId, session) in sessions)
        {
            if (profileId.IsEmpty)
            {
                throw new ArgumentException("Session keys must be non-empty profile IDs.", nameof(sessions));
            }

            if (session is null)
            {
                throw new ArgumentException("Sessions cannot contain null values.", nameof(sessions));
            }
            if (profileId != session.ProfileId)
            {
                throw new ArgumentException(
                    "Each session key must match the session's profile ID.",
                    nameof(sessions));
            }

            sessionSnapshot.Add(profileId, session);
        }

        ApprovedDigest = approvedDigest;
        ApprovedExecution = approvedExecution;
        Sessions = new ReadOnlyDictionary<ConnectionProfileId, IStorageEndpointSession>(sessionSnapshot);
        Mode = mode;
        DeletionPolicy = deletionPolicy ?? DeletionSafetyPolicy.Default;
        TransferOptions = transferOptions ?? new TransferExecutionOptions();
        ProfilePolicySha256 = profilePolicySha256;
    }

    public ImmutableSyncPlan Plan { get; }

    public SyncPlanDigest ApprovedDigest { get; init; }

    public SyncExecutionApproval ApprovedExecution { get; init; }

    public IReadOnlyDictionary<ConnectionProfileId, IStorageEndpointSession> Sessions { get; }

    public SyncExecutionSnapshots Snapshots { get; init; }

    public SyncPlanExecutionMode Mode { get; }

    public DeletionSafetyPolicy DeletionPolicy { get; }

    public TransferExecutionOptions TransferOptions { get; }
    public string? ProfilePolicySha256 { get; }
}

public sealed record SyncPlanExecutionReport(
    OperationPlanId PlanId,
    SyncPlanDigest PlanDigest,
    SyncPlanExecutionMode Mode,
    int TotalOperations,
    int ProcessedOperations,
    int ExecutedOperations,
    long BytesTransferred,
    long PlannedDeletionCount,
    DeletionSafetyDecision DeletionDecision);

/// <summary>
/// Executes a fully materialized sync plan in canonical sequence order. The complete plan is
/// validated before the first provider call; execution stops on the first failed operation.
/// </summary>
public static class SyncPlanExecutor
{
    public static async ValueTask<StorageResult<SyncPlanExecutionReport>> ExecuteAsync(
        SyncPlanExecutionRequest request,
        IProgress<SyncPlanExecutionEvent>? progress = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        timeProvider ??= TimeProvider.System;
        var processedOperations = 0;
        long bytesTransferred = 0;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preflight = Preflight(request, cancellationToken);
            if (preflight.IsFailure)
            {
                ReportFailure(
                    request,
                    progress,
                    timeProvider,
                    preflight.Error,
                    processedOperations: 0);
                return StorageResult<SyncPlanExecutionReport>.Fail(preflight.Error);
            }

            var totalOperations = request.Plan.Operations.Length;
            Report(
                request,
                progress,
                timeProvider,
                SyncPlanExecutionEventKind.PlanValidated,
                operation: null,
                processedOperations: 0,
                bytesTransferred: 0,
                totalBytes: null,
                failure: null);

            if (request.Mode == SyncPlanExecutionMode.Preview)
            {
                foreach (var operation in request.Plan.Operations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    processedOperations = operation.Sequence + 1;
                    Report(
                        request,
                        progress,
                        timeProvider,
                        SyncPlanExecutionEventKind.OperationPreviewed,
                        operation,
                        processedOperations,
                        bytesTransferred: 0,
                        totalBytes: operation.ExpectedLength,
                        failure: null);
                }

                var previewReport = new SyncPlanExecutionReport(
                    request.Plan.PlanId,
                    request.Plan.Digest,
                    request.Mode,
                    totalOperations,
                    totalOperations,
                    ExecutedOperations: 0,
                    BytesTransferred: 0,
                    preflight.Value.PlannedDeletionCount,
                    preflight.Value.DeletionDecision);
                Report(
                    request,
                    progress,
                    timeProvider,
                    SyncPlanExecutionEventKind.PlanCompleted,
                    operation: null,
                    processedOperations: totalOperations,
                    bytesTransferred: 0,
                    totalBytes: null,
                    failure: null);
                return StorageResult<SyncPlanExecutionReport>.Success(previewReport);
            }

            processedOperations = 0;
            foreach (var operation in request.Plan.Operations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Report(
                    request,
                    progress,
                    timeProvider,
                    SyncPlanExecutionEventKind.OperationStarted,
                    operation,
                    processedOperations,
                    bytesTransferred: 0,
                    totalBytes: operation.ExpectedLength,
                    failure: null);

                var operationResult = await ExecuteOperationAsync(
                    request,
                    operation,
                    processedOperations,
                    progress,
                    timeProvider,
                    cancellationToken).ConfigureAwait(false);
                if (operationResult.IsFailure)
                {
                    ReportFailure(
                        request,
                        progress,
                        timeProvider,
                        operationResult.Error,
                        processedOperations,
                        operation,
                        bytesTransferred);
                    return StorageResult<SyncPlanExecutionReport>.Fail(operationResult.Error);
                }

                bytesTransferred = checked(bytesTransferred + operationResult.Value);
                processedOperations++;
                Report(
                    request,
                    progress,
                    timeProvider,
                    SyncPlanExecutionEventKind.OperationCompleted,
                    operation,
                    processedOperations,
                    bytesTransferred: operationResult.Value,
                    totalBytes: operation.ExpectedLength,
                    failure: null);
            }

            var report = new SyncPlanExecutionReport(
                request.Plan.PlanId,
                request.Plan.Digest,
                request.Mode,
                totalOperations,
                processedOperations,
                processedOperations,
                bytesTransferred,
                preflight.Value.PlannedDeletionCount,
                preflight.Value.DeletionDecision);
            Report(
                request,
                progress,
                timeProvider,
                SyncPlanExecutionEventKind.PlanCompleted,
                operation: null,
                processedOperations,
                bytesTransferred,
                totalBytes: null,
                failure: null);
            return StorageResult<SyncPlanExecutionReport>.Success(report);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var failure = new StorageFailure(
                "sync.execution.cancelled",
                StorageFailureKind.Cancelled,
                "Sync plan execution was cancelled.");
            ReportFailure(
                request,
                progress,
                timeProvider,
                failure,
                processedOperations,
                operation: null,
                bytesTransferred: bytesTransferred);
            return StorageResult<SyncPlanExecutionReport>.Fail(failure);
        }
    }

    private static StorageResult<PreflightReport> Preflight(
        SyncPlanExecutionRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.Plan.HasValidDigest || request.ApprovedDigest != request.Plan.Digest)
        {
            return PreflightFailure(
                "sync.plan.digest_mismatch",
                StorageFailureKind.Conflict,
                "The plan no longer matches the digest that was approved for execution.");
        }

        var destructiveExecution = request.Mode == SyncPlanExecutionMode.Execute &&
            RequiresDestructiveApproval(request);
        if (!request.ApprovedExecution.IsSpecified && destructiveExecution)
        {
            return PreflightFailure(
                "sync.execution.approval_required",
                StorageFailureKind.Security,
                "A destructive sync requires an approval token bound to its snapshots, roots, and safety limits.");
        }

        if (request.ApprovedExecution.IsSpecified)
        {
            var currentApproval = SyncExecutionApproval.Compute(
                request.Plan,
                request.Sessions,
                request.Snapshots,
                request.Mode,
                request.DeletionPolicy,
                request.TransferOptions,
                request.ProfilePolicySha256);
            if (currentApproval != request.ApprovedExecution)
            {
                return PreflightFailure(
                    "sync.execution.approval_mismatch",
                    StorageFailureKind.Conflict,
                    "Execution inputs no longer match the snapshots, roots, capabilities, or limits that were approved.");
            }
        }

        if (request.TransferOptions.BufferSize is <= 0 or > BoundedStreamCopier.MaximumBufferSize)
        {
            return PreflightFailure(
                "sync.transfer.buffer_invalid",
                StorageFailureKind.Validation,
                $"The transfer buffer must be between 1 and {BoundedStreamCopier.MaximumBufferSize} bytes.");
        }

        foreach (var operation in request.Plan.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operationValidation = ValidateOperation(request, operation);
            if (operationValidation.IsFailure)
            {
                return StorageResult<PreflightReport>.Fail(operationValidation.Error);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var plannedDeletionCount = request.Plan.Operations.LongCount(operation => operation.IsDestructive);
        var deletionDecision = request.DeletionPolicy.Evaluate(
            plannedDeletionCount,
            request.Snapshots.BaselineItemCount,
            request.Snapshots.Left,
            request.Snapshots.Right);
        if (!deletionDecision.Allowed)
        {
            return PreflightFailure(
                "sync.plan.deletion_blocked",
                StorageFailureKind.Security,
                $"Deletion safety policy blocked this plan: {deletionDecision.Reason}.",
                providerCode: deletionDecision.Reason.ToString());
        }

        return StorageResult<PreflightReport>.Success(
            new PreflightReport(plannedDeletionCount, deletionDecision));
    }

    private static StorageResult ValidateOperation(
        SyncPlanExecutionRequest request,
        SyncPlanOperation operation)
    {
        switch (operation.Kind)
        {
            case SyncPlanOperationKind.Copy:
                {
                    if (operation.Destination is null)
                    {
                        return InvalidOperation("A copy operation requires a destination address.");
                    }

                    var source = ValidateAddressAndCapability(
                        request,
                        operation.SourceOrTarget,
                        StorageFeature.ReadStream,
                        operation.Kind);
                    if (source.IsFailure)
                    {
                        return source;
                    }

                    var destination = ValidateAddressAndCapability(
                        request,
                        operation.Destination,
                        StorageFeature.WriteStream,
                        operation.Kind);
                    return destination.IsFailure
                        ? destination
                        : ValidateOverwriteSafety(request, operation);
                }

            case SyncPlanOperationKind.Delete:
                {
                    if (operation.Destination is not null || operation.ExpectedLength is not null ||
                        operation.SourceDigest is not null || operation.DestinationDigest is not null)
                    {
                        return InvalidOperation("A delete operation contains unsupported fields.");
                    }

                    var delete = ValidateAddressAndCapability(
                        request,
                        operation.SourceOrTarget,
                        StorageFeature.Delete,
                        operation.Kind);
                    return delete.IsFailure
                        ? delete
                        : ValidateDeleteSafety(request, operation.SourceOrTarget);
                }

            case SyncPlanOperationKind.CreateDirectory:
                if (operation.Destination is not null || operation.ExpectedLength is not null ||
                    operation.SourceDigest is not null || operation.DestinationDigest is not null)
                {
                    return InvalidOperation("A create-directory operation contains unsupported fields.");
                }

                return ValidateAddressAndCapability(
                    request,
                    operation.SourceOrTarget,
                    StorageFeature.CreateDirectory,
                    operation.Kind);

            default:
                return StorageResult.Fail(new StorageFailure(
                    "sync.plan.operation_unsupported",
                    StorageFailureKind.Unsupported,
                    $"Sync operation kind '{operation.Kind}' is not supported."));
        }
    }

    private static StorageResult ValidateAddressAndCapability(
        SyncPlanExecutionRequest request,
        StorageAddress address,
        StorageFeature requiredFeature,
        SyncPlanOperationKind operationKind)
    {
        if (!request.Sessions.TryGetValue(address.ProfileId, out var session))
        {
            return StorageResult.Fail(new StorageFailure(
                "sync.plan.session_missing",
                StorageFailureKind.Unavailable,
                "A connection session required by the sync plan is unavailable."));
        }

        var addressValidation = session.ValidateAddress(address);
        if (addressValidation.IsFailure)
        {
            return addressValidation;
        }

        if (!session.Capabilities.Supports(requiredFeature))
        {
            return StorageResult.Fail(new StorageFailure(
                "sync.plan.operation_unsupported",
                StorageFailureKind.Unsupported,
                $"The endpoint does not support {requiredFeature} required by {operationKind}."));
        }

        return StorageResult.Success();
    }

    private static bool RequiresDestructiveApproval(SyncPlanExecutionRequest request) =>
        request.Plan.Operations.Any(operation =>
            operation.Kind == SyncPlanOperationKind.Delete ||
            request.TransferOptions.Overwrite && operation.Kind == SyncPlanOperationKind.Copy);

    private static StorageResult ValidateOverwriteSafety(
        SyncPlanExecutionRequest request,
        SyncPlanOperation operation)
    {
        if (!request.TransferOptions.Overwrite)
        {
            return StorageResult.Success();
        }

        var destination = operation.Destination!;
        if (string.IsNullOrWhiteSpace(destination.VersionId) &&
            string.IsNullOrWhiteSpace(destination.EntityTag))
        {
            return UnsafeMutation(
                "sync.overwrite.identity_required",
                "A sync overwrite requires the exact destination version or entity tag captured during planning.");
        }

        if (string.IsNullOrWhiteSpace(operation.SourceOrTarget.VersionId) &&
            string.IsNullOrWhiteSpace(operation.SourceOrTarget.EntityTag) &&
            operation.SourceDigest is null)
        {
            return UnsafeMutation(
                "sync.overwrite.source_evidence_required",
                "A sync overwrite requires an exact source version, entity tag, or planned portable SHA-256 evidence.");
        }

        if (!request.Snapshots.Left.IsComplete || !request.Snapshots.Right.IsComplete)
        {
            return UnsafeMutation(
                "sync.overwrite.snapshot_incomplete",
                "A sync overwrite requires complete scans of both approved roots.");
        }

        var sourceRoot = ValidateVerifiedRoot(request, operation.SourceOrTarget);
        if (sourceRoot.IsFailure)
        {
            return sourceRoot;
        }

        var destinationRoot = ValidateVerifiedRoot(request, destination);
        if (destinationRoot.IsFailure)
        {
            return destinationRoot;
        }

        var session = request.Sessions[destination.ProfileId];
        var sourceSession = request.Sessions[operation.SourceOrTarget.ProfileId];
        if (operation.SourceOrTarget.VersionId is not null &&
            sourceSession.Capabilities[StorageFeature.ObjectVersioning].Level != FeatureSupportLevel.Native)
        {
            return UnsafeMutation(
                "sync.overwrite.conditional_source_unsupported",
                "The source cannot enforce a version-conditional read for this overwrite.");
        }

        if (session.Capabilities[StorageFeature.ConditionalUpdate].Level == FeatureSupportLevel.Native &&
            session.Capabilities[StorageFeature.AtomicReplace].Level == FeatureSupportLevel.Native)
        {
            return StorageResult.Success();
        }

        foreach (var feature in new[]
                 {
                     StorageFeature.ObjectVersioning,
                     StorageFeature.TemporaryFiles,
                     StorageFeature.FileMove,
                     StorageFeature.AtomicRename,
                 })
        {
            if (session.Capabilities[feature].Level != FeatureSupportLevel.Native)
            {
                return UnsafeMutation(
                    "sync.overwrite.atomic_unsupported",
                    $"The destination cannot safely overwrite because native {feature} support is unavailable.");
            }
        }

        return StorageResult.Success();
    }

    private static StorageResult ValidateDeleteSafety(
        SyncPlanExecutionRequest request,
        StorageAddress target)
    {
        if (request.Mode == SyncPlanExecutionMode.Preview)
        {
            return StorageResult.Success();
        }

        if (string.IsNullOrWhiteSpace(target.VersionId) &&
            string.IsNullOrWhiteSpace(target.EntityTag))
        {
            return UnsafeMutation(
                "sync.delete.identity_required",
                "A sync delete requires the exact target version or entity tag captured during planning.");
        }

        var root = ValidateVerifiedRoot(request, target);
        if (root.IsFailure)
        {
            return root;
        }

        var session = request.Sessions[target.ProfileId];
        return session.Capabilities[StorageFeature.ConditionalDelete].Level == FeatureSupportLevel.Native
            ? StorageResult.Success()
            : UnsafeMutation(
                "sync.delete.conditional_unsupported",
                "The endpoint cannot enforce an identity-conditional delete; deletion was blocked.");
    }

    private static StorageResult ValidateVerifiedRoot(
        SyncPlanExecutionRequest request,
        StorageAddress address)
    {
        if (!request.Snapshots.VerifiedRootIdentities.TryGetValue(address.ProfileId, out var approvedRoot) ||
            !StringComparer.Ordinal.Equals(approvedRoot, address.RootIdentity) ||
            !StringComparer.Ordinal.Equals(approvedRoot, request.Sessions[address.ProfileId].RootIdentity))
        {
            return UnsafeMutation(
                "sync.root_evidence.mismatch",
                "The destructive operation is not bound to exact scan-time and runtime root identities.");
        }

        return StorageResult.Success();
    }

    private static StorageResult UnsafeMutation(string code, string message) => StorageResult.Fail(
        new StorageFailure(code, StorageFailureKind.Unsupported, message));

    private static async ValueTask<StorageResult<long>> ExecuteOperationAsync(
        SyncPlanExecutionRequest request,
        SyncPlanOperation operation,
        int processedOperations,
        IProgress<SyncPlanExecutionEvent>? progress,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var sourceOrTargetSession = request.Sessions[operation.SourceOrTarget.ProfileId];
        switch (operation.Kind)
        {
            case SyncPlanOperationKind.Copy:
                {
                    var destination = operation.Destination!;
                    var intent = new TransferIntent(
                        TransferJobId.New(),
                        TransferOperationKind.Copy,
                        operation.SourceOrTarget,
                        destination,
                        operation.ExpectedLength,
                        TransferVerificationPolicy.StrongHashWhenAvailable,
                        timeProvider.GetUtcNow(),
                        expectedSourceDigest: operation.SourceDigest,
                        expectedDestinationDigest: operation.DestinationDigest);
                    var transferProgress = new TransferProgressAdapter(
                        request,
                        operation,
                        processedOperations,
                        progress,
                        timeProvider);
                    var transfer = await TransferExecutor.ExecuteAsync(
                        intent,
                        sourceOrTargetSession,
                        request.Sessions[destination.ProfileId],
                        request.TransferOptions,
                        transferProgress,
                        cancellationToken).ConfigureAwait(false);
                    return transfer.IsFailure
                        ? StorageResult<long>.Fail(transfer.Error)
                        : StorageResult<long>.Success(transfer.Value.BytesTransferred);
                }

            case SyncPlanOperationKind.Delete:
                {
                    var deletion = await sourceOrTargetSession.DeleteAsync(
                        new StorageDeleteRequest(
                            operation.SourceOrTarget,
                            Recursive: false,
                            IgnoreMissing: false,
                            ExpectedVersionId: operation.SourceOrTarget.VersionId,
                            ExpectedEntityTag: operation.SourceOrTarget.EntityTag),
                        cancellationToken).ConfigureAwait(false);
                    return deletion.IsFailure
                        ? StorageResult<long>.Fail(deletion.Error)
                        : StorageResult<long>.Success(0);
                }

            case SyncPlanOperationKind.CreateDirectory:
                {
                    var creation = await sourceOrTargetSession.CreateDirectoryAsync(
                        operation.SourceOrTarget,
                        cancellationToken).ConfigureAwait(false);
                    return creation.IsFailure
                        ? StorageResult<long>.Fail(creation.Error)
                        : StorageResult<long>.Success(0);
                }

            default:
                return StorageResult<long>.Fail(new StorageFailure(
                    "sync.plan.operation_unsupported",
                    StorageFailureKind.Unsupported,
                    $"Sync operation kind '{operation.Kind}' is not supported."));
        }
    }

    private static StorageResult InvalidOperation(string message) =>
        StorageResult.Fail(new StorageFailure(
            "sync.plan.operation_invalid",
            StorageFailureKind.Validation,
            message));

    private static StorageResult<PreflightReport> PreflightFailure(
        string code,
        StorageFailureKind kind,
        string message,
        string? providerCode = null) =>
        StorageResult<PreflightReport>.Fail(new StorageFailure(
            code,
            kind,
            message,
            providerCode: providerCode));

    private static void ReportFailure(
        SyncPlanExecutionRequest request,
        IProgress<SyncPlanExecutionEvent>? progress,
        TimeProvider timeProvider,
        StorageFailure failure,
        int processedOperations,
        SyncPlanOperation? operation = null,
        long bytesTransferred = 0) =>
        Report(
            request,
            progress,
            timeProvider,
            failure.Kind == StorageFailureKind.Cancelled
                ? SyncPlanExecutionEventKind.PlanCancelled
                : SyncPlanExecutionEventKind.PlanFailed,
            operation,
            processedOperations,
            bytesTransferred,
            totalBytes: operation?.ExpectedLength,
            failure);

    private static void Report(
        SyncPlanExecutionRequest request,
        IProgress<SyncPlanExecutionEvent>? progress,
        TimeProvider timeProvider,
        SyncPlanExecutionEventKind kind,
        SyncPlanOperation? operation,
        int processedOperations,
        long bytesTransferred,
        long? totalBytes,
        StorageFailure? failure) =>
        progress?.Report(new SyncPlanExecutionEvent(
            kind,
            request.Plan.PlanId,
            request.Plan.Digest,
            operation?.Sequence,
            operation?.Kind,
            processedOperations,
            request.Plan.Operations.Length,
            bytesTransferred,
            totalBytes,
            failure,
            timeProvider.GetUtcNow()));

    private sealed record PreflightReport(
        long PlannedDeletionCount,
        DeletionSafetyDecision DeletionDecision);

    private sealed class TransferProgressAdapter(
        SyncPlanExecutionRequest request,
        SyncPlanOperation operation,
        int processedOperations,
        IProgress<SyncPlanExecutionEvent>? progress,
        TimeProvider timeProvider) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => SyncPlanExecutor.Report(
            request,
            progress,
            timeProvider,
            SyncPlanExecutionEventKind.OperationProgress,
            operation,
            processedOperations,
            value.BytesTransferred,
            value.TotalBytes,
            failure: null);
    }

}
