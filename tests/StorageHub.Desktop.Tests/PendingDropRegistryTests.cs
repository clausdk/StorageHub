namespace StorageHub.Desktop.Tests;

public sealed class PendingDropRegistryTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 9, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ADragAppearsImmediatelyAsAwaitingDestination()
    {
        var registry = new PendingDropRegistry(new FixedTimeProvider(Start));

        registry.Begin("t1", "reports", itemCount: 3);

        var entry = Assert.Single(registry.Snapshot());
        Assert.Equal(PendingDropState.AwaitingDestination, entry.State);
        Assert.Equal("Waiting for destination", entry.Describe());
        Assert.Equal("3 items from reports", entry.DescribeSource());
        Assert.False(entry.IsTerminal);
    }

    [Fact]
    public void ASingleItemIsNamedDirectly()
    {
        var registry = new PendingDropRegistry(new FixedTimeProvider(Start));

        registry.Begin("t1", "budget.xlsx", itemCount: 1);

        Assert.Equal("budget.xlsx", registry.Snapshot()[0].DescribeSource());
    }

    [Fact]
    public void QueuingRecordsTheDestination()
    {
        var registry = new PendingDropRegistry(new FixedTimeProvider(Start));
        registry.Begin("t1", "reports", 2);

        registry.MarkQueued("t1", @"C:\Users\sam\Desktop");

        var entry = registry.Snapshot()[0];
        Assert.Equal(PendingDropState.Queued, entry.State);
        Assert.Equal(@"C:\Users\sam\Desktop", entry.Destination);
        Assert.True(entry.IsTerminal);
    }

    [Fact]
    public void FailuresExplainThemselves()
    {
        var registry = new PendingDropRegistry(new FixedTimeProvider(Start));
        registry.Begin("t1", "reports", 1);

        registry.MarkFailed("t1", "The agent is unavailable.");

        Assert.Equal("Failed: The agent is unavailable.", registry.Snapshot()[0].Describe());
    }

    [Fact]
    public void AGestureOnlySettlesOnce()
    {
        // A late cancellation must not overwrite a drop that already queued real work.
        var registry = new PendingDropRegistry(new FixedTimeProvider(Start));
        registry.Begin("t1", "reports", 1);

        registry.MarkQueued("t1", @"C:\Drop");
        registry.MarkCancelled("t1", "too late");

        var entry = registry.Snapshot()[0];
        Assert.Equal(PendingDropState.Queued, entry.State);
        Assert.Equal(@"C:\Drop", entry.Destination);
    }

    [Fact]
    public void SettledEntriesExpireButLiveOnesNeverDo()
    {
        var time = new FixedTimeProvider(Start);
        var registry = new PendingDropRegistry(time);
        registry.Begin("settled", "reports", 1);
        registry.Begin("live", "archive", 1);
        registry.MarkQueued("settled", @"C:\Drop");

        time.Now = Start + PendingDropRegistry.TerminalLifetime - TimeSpan.FromSeconds(1);
        Assert.Equal(2, registry.Snapshot().Count);

        time.Now = Start + PendingDropRegistry.TerminalLifetime;
        var remaining = Assert.Single(registry.Snapshot());
        Assert.Equal("live", remaining.Token);
    }

    [Fact]
    public void TransitionsForUnknownTokensAreIgnored()
    {
        var registry = new PendingDropRegistry(new FixedTimeProvider(Start));

        registry.MarkQueued("missing", @"C:\Drop");

        Assert.Empty(registry.Snapshot());
    }

    [Fact]
    public void ChangesRaiseNotificationsSoViewsRefreshPromptly()
    {
        var registry = new PendingDropRegistry(new FixedTimeProvider(Start));
        var raised = 0;
        registry.Changed += (_, _) => raised++;

        registry.Begin("t1", "reports", 1);
        registry.MarkQueued("t1", @"C:\Drop");
        registry.MarkQueued("t1", @"C:\Other");

        // Two real transitions; the third is a no-op on an already settled entry.
        Assert.Equal(2, raised);
    }

    [Fact]
    public void TheRegistryStaysBounded()
    {
        var time = new FixedTimeProvider(Start);
        var registry = new PendingDropRegistry(time);
        for (var index = 0; index < 64; index++)
        {
            registry.Begin($"token-{index}", "reports", 1);
            registry.MarkQueued($"token-{index}", @"C:\Drop");
        }

        Assert.True(registry.Snapshot().Count <= 32);
    }

    [Fact]
    public void NewestGesturesAreListedFirst()
    {
        var time = new FixedTimeProvider(Start);
        var registry = new PendingDropRegistry(time);
        registry.Begin("first", "a", 1);
        time.Now = Start.AddSeconds(5);
        registry.Begin("second", "b", 1);

        Assert.Equal(["second", "first"], registry.Snapshot().Select(entry => entry.Token));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
