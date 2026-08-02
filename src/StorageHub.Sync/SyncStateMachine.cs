namespace StorageHub.Sync;

public static class SyncStateMachine
{
    public static SyncRunState Transition(
        SyncRunState current,
        SyncRunPhase next,
        DateTimeOffset transitionedAtUtc,
        SyncStatusCode statusCode = SyncStatusCode.None)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (!CanTransition(current.Phase, next))
        {
            throw new InvalidOperationException(
                $"Sync run cannot transition from {current.Phase} to {next}.");
        }

        if (transitionedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Transition time must be UTC.", nameof(transitionedAtUtc));
        }

        if (transitionedAtUtc < current.TransitionedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(transitionedAtUtc),
                "Transition time cannot move backwards.");
        }

        var effectiveStatus = statusCode == SyncStatusCode.None
            ? GetDefaultStatus(next)
            : statusCode;

        if (RequiresStatus(next) && effectiveStatus == SyncStatusCode.None)
        {
            throw new ArgumentException(
                $"Transitioning to {next} requires a machine-readable status code.",
                nameof(statusCode));
        }

        if (!RequiresStatus(next))
        {
            effectiveStatus = SyncStatusCode.None;
        }

        return new SyncRunState(
            current.SyncRunId,
            next,
            checked(current.Revision + 1),
            transitionedAtUtc,
            effectiveStatus);
    }

    public static bool IsSuccessful(SyncRunPhase phase) => phase == SyncRunPhase.Completed;

    public static bool IsTerminal(SyncRunPhase phase) =>
        phase is SyncRunPhase.Completed or SyncRunPhase.Cancelled;

    public static bool CanTransition(SyncRunPhase current, SyncRunPhase next) => current switch
    {
        SyncRunPhase.Pending => next is SyncRunPhase.Scanning or SyncRunPhase.Cancelled,
        SyncRunPhase.Scanning => next is
            SyncRunPhase.Planning or
            SyncRunPhase.BlockedEndpoint or
            SyncRunPhase.BlockedCredential or
            SyncRunPhase.BlockedTrust or
            SyncRunPhase.Interrupted or
            SyncRunPhase.Failed or
            SyncRunPhase.Cancelled,
        SyncRunPhase.Planning => next is
            SyncRunPhase.AwaitingApproval or
            SyncRunPhase.Ready or
            SyncRunPhase.BlockedConflict or
            SyncRunPhase.BlockedDeletionGuard or
            SyncRunPhase.BlockedEndpoint or
            SyncRunPhase.Interrupted or
            SyncRunPhase.Failed or
            SyncRunPhase.Cancelled,
        SyncRunPhase.AwaitingApproval => next is
            SyncRunPhase.Ready or
            SyncRunPhase.Pending or
            SyncRunPhase.Cancelled,
        SyncRunPhase.Ready => next is
            SyncRunPhase.Executing or
            SyncRunPhase.BlockedEndpoint or
            SyncRunPhase.BlockedCredential or
            SyncRunPhase.BlockedTrust or
            SyncRunPhase.Cancelled,
        SyncRunPhase.Executing => next is
            SyncRunPhase.Verifying or
            SyncRunPhase.BlockedEndpoint or
            SyncRunPhase.BlockedCredential or
            SyncRunPhase.BlockedTrust or
            SyncRunPhase.Interrupted or
            SyncRunPhase.NeedsReconciliation or
            SyncRunPhase.Failed or
            SyncRunPhase.Cancelled,
        SyncRunPhase.Verifying => next is
            SyncRunPhase.CommittingBaseline or
            SyncRunPhase.Interrupted or
            SyncRunPhase.NeedsReconciliation or
            SyncRunPhase.Failed or
            SyncRunPhase.Cancelled,
        SyncRunPhase.CommittingBaseline => next is
            SyncRunPhase.Completed or
            SyncRunPhase.Interrupted or
            SyncRunPhase.NeedsReconciliation or
            SyncRunPhase.Failed,
        SyncRunPhase.BlockedConflict or
        SyncRunPhase.BlockedDeletionGuard or
        SyncRunPhase.BlockedEndpoint or
        SyncRunPhase.BlockedCredential or
        SyncRunPhase.BlockedTrust => next is SyncRunPhase.Pending or SyncRunPhase.Cancelled,
        SyncRunPhase.Interrupted => next is
            SyncRunPhase.NeedsReconciliation or
            SyncRunPhase.Cancelled,
        SyncRunPhase.NeedsReconciliation => next is
            SyncRunPhase.Pending or
            SyncRunPhase.Verifying or
            SyncRunPhase.Failed or
            SyncRunPhase.Cancelled,
        SyncRunPhase.Failed => next is SyncRunPhase.Pending or SyncRunPhase.Cancelled,
        SyncRunPhase.Completed or SyncRunPhase.Cancelled => false,
        _ => false,
    };

    internal static bool RequiresStatus(SyncRunPhase phase) => phase is
        SyncRunPhase.BlockedConflict or
        SyncRunPhase.BlockedDeletionGuard or
        SyncRunPhase.BlockedEndpoint or
        SyncRunPhase.BlockedCredential or
        SyncRunPhase.BlockedTrust or
        SyncRunPhase.Interrupted or
        SyncRunPhase.NeedsReconciliation or
        SyncRunPhase.Failed;

    private static SyncStatusCode GetDefaultStatus(SyncRunPhase phase) => phase switch
    {
        SyncRunPhase.BlockedConflict => SyncStatusCode.ConflictRequiresDecision,
        SyncRunPhase.BlockedDeletionGuard => SyncStatusCode.DeletionGuardTriggered,
        SyncRunPhase.BlockedEndpoint => SyncStatusCode.EndpointUnavailable,
        SyncRunPhase.BlockedCredential => SyncStatusCode.CredentialUnavailable,
        SyncRunPhase.BlockedTrust => SyncStatusCode.TrustRequired,
        SyncRunPhase.Interrupted => SyncStatusCode.Interrupted,
        SyncRunPhase.NeedsReconciliation => SyncStatusCode.StateUncertain,
        _ => SyncStatusCode.None,
    };
}
