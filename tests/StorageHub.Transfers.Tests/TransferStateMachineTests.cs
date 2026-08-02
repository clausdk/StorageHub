using StorageHub.Domain.Identifiers;

namespace StorageHub.Transfers.Tests;

public sealed class TransferStateMachineTests
{
    [Fact]
    public void Transition_increments_revision_and_records_durable_state()
    {
        var transferId = TransferJobId.New();
        var before = new TransferStateSnapshot(
            transferId,
            TransferState.Pending,
            revision: 7,
            attempt: 0,
            DateTimeOffset.UnixEpoch,
            TransferStatusCode.None);
        var transitionedAt = DateTimeOffset.UnixEpoch.AddMinutes(1);

        var after = TransferStateMachine.Transition(
            before,
            TransferState.Preparing,
            transitionedAt);

        Assert.Equal(transferId, after.TransferJobId);
        Assert.Equal(TransferState.Preparing, after.State);
        Assert.Equal(8, after.Revision);
        Assert.Equal(1, after.Attempt);
        Assert.Equal(transitionedAt, after.TransitionedAtUtc);
        Assert.Equal(TransferStatusCode.None, after.StatusCode);
    }

    [Fact]
    public void Cannot_skip_from_pending_to_completed()
    {
        var snapshot = TransferStateSnapshot.Create(TransferJobId.New(), DateTimeOffset.UnixEpoch);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            TransferStateMachine.Transition(
                snapshot,
                TransferState.Completed,
                DateTimeOffset.UnixEpoch.AddSeconds(1)));

        Assert.Contains("Pending", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Completed", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TransferState.BlockedCredential)]
    [InlineData(TransferState.BlockedTrust)]
    [InlineData(TransferState.Interrupted)]
    [InlineData(TransferState.NeedsReconciliation)]
    [InlineData(TransferState.RestartRequired)]
    [InlineData(TransferState.CleanupPending)]
    public void Safety_states_are_nonterminal(TransferState state)
    {
        Assert.False(TransferStateMachine.IsTerminal(state));
    }

    [Theory]
    [InlineData(TransferState.Completed)]
    [InlineData(TransferState.Cancelled)]
    public void Completed_and_cancelled_are_terminal(TransferState state)
    {
        Assert.True(TransferStateMachine.IsTerminal(state));
    }

    [Fact]
    public void Blocked_trust_can_only_requeue_or_cancel()
    {
        var snapshot = new TransferStateSnapshot(
            TransferJobId.New(),
            TransferState.BlockedTrust,
            revision: 2,
            attempt: 1,
            DateTimeOffset.UnixEpoch,
            TransferStatusCode.TrustRequired);

        var requeued = TransferStateMachine.Transition(
            snapshot,
            TransferState.Pending,
            DateTimeOffset.UnixEpoch.AddSeconds(1));

        Assert.Equal(TransferState.Pending, requeued.State);
        Assert.Equal(TransferStatusCode.None, requeued.StatusCode);
        Assert.Throws<InvalidOperationException>(() =>
            TransferStateMachine.Transition(
                snapshot,
                TransferState.Transferring,
                DateTimeOffset.UnixEpoch.AddSeconds(1)));
    }

    [Fact]
    public void Failure_state_requires_a_nonempty_status_code()
    {
        var snapshot = new TransferStateSnapshot(
            TransferJobId.New(),
            TransferState.Transferring,
            revision: 3,
            attempt: 1,
            DateTimeOffset.UnixEpoch,
            TransferStatusCode.None);

        Assert.Throws<ArgumentException>(() =>
            TransferStateMachine.Transition(
                snapshot,
                TransferState.Failed,
                DateTimeOffset.UnixEpoch.AddSeconds(1)));
    }
}
