using StorageHub.Domain.Identifiers;

namespace StorageHub.Transfers;

public sealed record TransferStateSnapshot
{
    public TransferStateSnapshot(
        TransferJobId transferJobId,
        TransferState state,
        long revision,
        int attempt,
        DateTimeOffset transitionedAtUtc,
        TransferStatusCode statusCode)
    {
        if (transferJobId.IsEmpty)
        {
            throw new ArgumentException("A transfer job ID is required.", nameof(transferJobId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);

        if (transitionedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Transition time must be UTC.", nameof(transitionedAtUtc));
        }

        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (!Enum.IsDefined(statusCode))
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        }

        if (TransferStateMachine.RequiresStatus(state) != (statusCode != TransferStatusCode.None))
        {
            throw new ArgumentException(
                "The status code must be present exactly when the durable state requires one.",
                nameof(statusCode));
        }

        TransferJobId = transferJobId;
        State = state;
        Revision = revision;
        Attempt = attempt;
        TransitionedAtUtc = transitionedAtUtc;
        StatusCode = statusCode;
    }

    public TransferJobId TransferJobId { get; }

    public TransferState State { get; }

    public long Revision { get; }

    public int Attempt { get; }

    public DateTimeOffset TransitionedAtUtc { get; }

    public TransferStatusCode StatusCode { get; }

    public static TransferStateSnapshot Create(
        TransferJobId transferJobId,
        DateTimeOffset createdAtUtc) =>
        new(
            transferJobId,
            TransferState.Pending,
            revision: 0,
            attempt: 0,
            createdAtUtc,
            TransferStatusCode.None);
}
