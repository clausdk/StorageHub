namespace StorageHub.Transfers;

public static class TransferStateMachine
{
    public static TransferStateSnapshot Transition(
        TransferStateSnapshot current,
        TransferState next,
        DateTimeOffset transitionedAtUtc,
        TransferStatusCode statusCode = TransferStatusCode.None)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (!CanTransition(current.State, next))
        {
            throw new InvalidOperationException(
                $"Transfer cannot transition from {current.State} to {next}.");
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

        var effectiveStatus = statusCode == TransferStatusCode.None
            ? GetDefaultStatus(next)
            : statusCode;

        if (RequiresStatus(next) && effectiveStatus == TransferStatusCode.None)
        {
            throw new ArgumentException(
                $"Transitioning to {next} requires a machine-readable status code.",
                nameof(statusCode));
        }

        if (!RequiresStatus(next))
        {
            effectiveStatus = TransferStatusCode.None;
        }

        var attempt = next == TransferState.Preparing
            ? checked(current.Attempt + 1)
            : current.Attempt;

        return new TransferStateSnapshot(
            current.TransferJobId,
            next,
            checked(current.Revision + 1),
            attempt,
            transitionedAtUtc,
            effectiveStatus);
    }

    public static bool IsTerminal(TransferState state) =>
        state is TransferState.Completed or TransferState.Cancelled;

    public static bool CanTransition(TransferState current, TransferState next) => current switch
    {
        TransferState.Pending => next is TransferState.Preparing or TransferState.Cancelled,
        TransferState.Preparing => next is
            TransferState.Connecting or
            TransferState.BlockedCredential or
            TransferState.BlockedTrust or
            TransferState.Failed or
            TransferState.Cancelled or
            TransferState.Interrupted,
        TransferState.Connecting => next is
            TransferState.Transferring or
            TransferState.BlockedCredential or
            TransferState.BlockedTrust or
            TransferState.Retrying or
            TransferState.Failed or
            TransferState.Cancelled or
            TransferState.Interrupted,
        TransferState.Transferring => next is
            TransferState.Verifying or
            TransferState.Paused or
            TransferState.Retrying or
            TransferState.Failed or
            TransferState.Cancelled or
            TransferState.Interrupted or
            TransferState.NeedsReconciliation,
        TransferState.Verifying => next is
            TransferState.Finalizing or
            TransferState.RestartRequired or
            TransferState.Retrying or
            TransferState.Failed or
            TransferState.Cancelled or
            TransferState.Interrupted or
            TransferState.NeedsReconciliation,
        TransferState.Finalizing => next is
            TransferState.Completed or
            TransferState.CleanupPending or
            TransferState.NeedsReconciliation or
            TransferState.Failed or
            TransferState.Interrupted,
        TransferState.Paused => next is
            TransferState.Pending or
            TransferState.Cancelled,
        TransferState.Retrying => next is
            TransferState.Preparing or
            TransferState.BlockedCredential or
            TransferState.BlockedTrust or
            TransferState.Failed or
            TransferState.Cancelled,
        TransferState.BlockedCredential or TransferState.BlockedTrust => next is
            TransferState.Pending or
            TransferState.Cancelled,
        TransferState.Interrupted => next is
            TransferState.NeedsReconciliation or
            TransferState.RestartRequired or
            TransferState.Cancelled,
        TransferState.NeedsReconciliation => next is
            TransferState.Preparing or
            TransferState.RestartRequired or
            TransferState.Completed or
            TransferState.Failed or
            TransferState.Cancelled,
        TransferState.RestartRequired => next is
            TransferState.Pending or
            TransferState.Cancelled,
        TransferState.CleanupPending => next is
            TransferState.Completed or
            TransferState.Failed,
        TransferState.Failed => next is
            TransferState.Pending or
            TransferState.Cancelled,
        TransferState.Completed or TransferState.Cancelled => false,
        _ => false,
    };

    internal static bool RequiresStatus(TransferState state) => state is
        TransferState.BlockedCredential or
        TransferState.BlockedTrust or
        TransferState.Interrupted or
        TransferState.NeedsReconciliation or
        TransferState.RestartRequired or
        TransferState.CleanupPending or
        TransferState.Failed;

    private static TransferStatusCode GetDefaultStatus(TransferState state) => state switch
    {
        TransferState.BlockedCredential => TransferStatusCode.CredentialUnavailable,
        TransferState.BlockedTrust => TransferStatusCode.TrustRequired,
        TransferState.Interrupted => TransferStatusCode.Interrupted,
        TransferState.NeedsReconciliation => TransferStatusCode.StateUncertain,
        TransferState.RestartRequired => TransferStatusCode.ResumeNotSupported,
        TransferState.CleanupPending => TransferStatusCode.CleanupPending,
        _ => TransferStatusCode.None,
    };
}
