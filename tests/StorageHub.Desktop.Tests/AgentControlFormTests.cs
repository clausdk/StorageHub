namespace StorageHub.Desktop.Tests;

public sealed class AgentControlFormTests
{
    [Fact]
    public void AgentControlWindowConstructsAndDisposesOnStaWithoutBeingShown()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var withStatus = new AgentControlForm(
                () => new AgentMonitorStatus(
                    AgentConnectionState.Connected, 2, 1, "All good", DateTimeOffset.UnixEpoch),
                new StubController());

            // A build with no packaged lifecycle still opens; it simply cannot control the process.
            using var withoutController = new AgentControlForm(() => null, controller: null);
        });
    }

    [Theory]
    [InlineData(AgentConnectionState.Connected)]
    [InlineData(AgentConnectionState.RecoveryOnly)]
    [InlineData(AgentConnectionState.Disconnected)]
    [InlineData(AgentConnectionState.Starting)]
    public void EveryStateHasAReadableLabelAndExplanation(AgentConnectionState state)
    {
        var label = AgentControlForm.DescribeState(state);
        var detail = AgentControlForm.DescribeDefaultDetail(state);

        Assert.False(string.IsNullOrWhiteSpace(label));
        Assert.False(string.IsNullOrWhiteSpace(detail));
    }

    [Fact]
    public void RecoveryModeExplainsThatDurableFeaturesAreUnavailable()
    {
        // Recovery is the state where the agent is running yet nothing durable works, which is
        // exactly what a bare "running" indicator hides.
        var detail = AgentControlForm.DescribeDefaultDetail(AgentConnectionState.RecoveryOnly);

        Assert.Contains("recovery", AgentControlForm.DescribeState(AgentConnectionState.RecoveryOnly),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("transfer queue", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sync", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RunningAndStoppedStatesAreDistinguishable()
    {
        Assert.NotEqual(
            AgentControlForm.DescribeState(AgentConnectionState.Connected),
            AgentControlForm.DescribeState(AgentConnectionState.Disconnected));
        Assert.Contains("not running",
            AgentControlForm.DescribeState(AgentConnectionState.Disconnected),
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubController : IAgentLifecycleController
    {
        public Task<AgentLifecycleResult> ExecuteAsync(
            AgentLifecycleAction action,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentLifecycleResult(true, $"{action} completed."));
    }
}
