using StorageHub.Domain.Identifiers;

namespace StorageHub.Sync;

public sealed record SyncRunState
{
    public SyncRunState(
        SyncRunId syncRunId,
        SyncRunPhase phase,
        long revision,
        DateTimeOffset transitionedAtUtc,
        SyncStatusCode statusCode)
    {
        if (syncRunId.IsEmpty)
        {
            throw new ArgumentException("A sync run ID is required.", nameof(syncRunId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(revision);

        if (!Enum.IsDefined(phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }

        if (!Enum.IsDefined(statusCode))
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        }

        if (SyncStateMachine.RequiresStatus(phase) != (statusCode != SyncStatusCode.None))
        {
            throw new ArgumentException(
                "The status code must be present exactly when the durable phase requires one.",
                nameof(statusCode));
        }

        if (transitionedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Transition time must be UTC.", nameof(transitionedAtUtc));
        }

        SyncRunId = syncRunId;
        Phase = phase;
        Revision = revision;
        TransitionedAtUtc = transitionedAtUtc;
        StatusCode = statusCode;
    }

    public SyncRunId SyncRunId { get; }

    public SyncRunPhase Phase { get; }

    public long Revision { get; }

    public DateTimeOffset TransitionedAtUtc { get; }

    public SyncStatusCode StatusCode { get; }

    public static SyncRunState Create(SyncRunId syncRunId, DateTimeOffset createdAtUtc) =>
        new(syncRunId, SyncRunPhase.Pending, 0, createdAtUtc, SyncStatusCode.None);
}
