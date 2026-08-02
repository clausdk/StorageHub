using StorageHub.Domain.Identifiers;

namespace StorageHub.Sync.Tests;

public sealed class SyncStateMachineTests
{
    [Fact]
    public void State_machine_requires_scan_and_plan_before_execution()
    {
        var run = SyncRunState.Create(SyncRunId.New(), DateTimeOffset.UnixEpoch);

        run = SyncStateMachine.Transition(run, SyncRunPhase.Scanning, Later(run));
        run = SyncStateMachine.Transition(run, SyncRunPhase.Planning, Later(run));
        run = SyncStateMachine.Transition(run, SyncRunPhase.Ready, Later(run));
        run = SyncStateMachine.Transition(run, SyncRunPhase.Executing, Later(run));
        run = SyncStateMachine.Transition(run, SyncRunPhase.Verifying, Later(run));
        run = SyncStateMachine.Transition(run, SyncRunPhase.CommittingBaseline, Later(run));
        run = SyncStateMachine.Transition(run, SyncRunPhase.Completed, Later(run));

        Assert.Equal(SyncRunPhase.Completed, run.Phase);
        Assert.Equal(7, run.Revision);
    }

    [Fact]
    public void Cannot_execute_directly_from_pending()
    {
        var run = SyncRunState.Create(SyncRunId.New(), DateTimeOffset.UnixEpoch);

        Assert.Throws<InvalidOperationException>(() =>
            SyncStateMachine.Transition(run, SyncRunPhase.Executing, Later(run)));
    }

    [Theory]
    [InlineData(SyncRunPhase.BlockedConflict)]
    [InlineData(SyncRunPhase.BlockedDeletionGuard)]
    [InlineData(SyncRunPhase.BlockedEndpoint)]
    [InlineData(SyncRunPhase.BlockedCredential)]
    [InlineData(SyncRunPhase.BlockedTrust)]
    [InlineData(SyncRunPhase.Interrupted)]
    [InlineData(SyncRunPhase.NeedsReconciliation)]
    public void Safety_states_are_not_reported_as_success(SyncRunPhase phase)
    {
        Assert.False(SyncStateMachine.IsSuccessful(phase));
        Assert.False(SyncStateMachine.IsTerminal(phase));
    }

    [Fact]
    public void Baseline_commit_cannot_follow_interruption_without_reconciliation()
    {
        var run = new SyncRunState(
            SyncRunId.New(),
            SyncRunPhase.Interrupted,
            revision: 3,
            DateTimeOffset.UnixEpoch,
            SyncStatusCode.Interrupted);

        Assert.Throws<InvalidOperationException>(() =>
            SyncStateMachine.Transition(
                run,
                SyncRunPhase.CommittingBaseline,
                Later(run)));

        var reconciling = SyncStateMachine.Transition(
            run,
            SyncRunPhase.NeedsReconciliation,
            Later(run));
        Assert.Equal(SyncRunPhase.NeedsReconciliation, reconciling.Phase);
    }

    private static DateTimeOffset Later(SyncRunState state) =>
        state.TransitionedAtUtc.AddSeconds(1);
}
