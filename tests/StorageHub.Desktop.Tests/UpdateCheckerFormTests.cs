namespace StorageHub.Desktop.Tests;

/// <summary>
/// The updater types are internal by design, and xUnit requires public test classes, so the states
/// under test are enumerated inside each method rather than passed as theory parameters.
/// </summary>
public sealed class UpdateCheckerFormTests
{
    [Fact]
    public void TheOneActionButtonFollowsTheUpdateState()
    {
        Assert.Equal(UpdateAction.Check, UpdateCheckerForm.NextAction(DesktopUpdateState.Idle));
        Assert.Equal(UpdateAction.Check, UpdateCheckerForm.NextAction(DesktopUpdateState.UpToDate));
        Assert.Equal(UpdateAction.Check, UpdateCheckerForm.NextAction(DesktopUpdateState.Failed));
        Assert.Equal(UpdateAction.Download, UpdateCheckerForm.NextAction(DesktopUpdateState.UpdateAvailable));
        Assert.Equal(UpdateAction.Restart, UpdateCheckerForm.NextAction(DesktopUpdateState.ReadyToRestart));
    }

    [Fact]
    public void WorkInFlightOffersNoAction()
    {
        // Re-entering a check or download mid-flight is what the old message-box chain allowed.
        Assert.Equal(UpdateAction.None, UpdateCheckerForm.NextAction(DesktopUpdateState.Checking));
        Assert.Equal(UpdateAction.None, UpdateCheckerForm.NextAction(DesktopUpdateState.Downloading));
        Assert.Equal(UpdateAction.None, UpdateCheckerForm.NextAction(DesktopUpdateState.Installing));
    }

    [Fact]
    public void BuildsThatCannotUpdateOfferNoAction()
    {
        Assert.Equal(UpdateAction.None, UpdateCheckerForm.NextAction(DesktopUpdateState.Unavailable));
        Assert.Equal(UpdateAction.None, UpdateCheckerForm.NextAction(DesktopUpdateState.Disabled));
    }

    [Fact]
    public void EveryStateHasAHeadlineAndAnExplanation()
    {
        foreach (var state in Enum.GetValues<DesktopUpdateState>())
        {
            var snapshot = new DesktopUpdateSnapshot(state, "engine message", "1.2.3", 42);

            Assert.False(
                string.IsNullOrWhiteSpace(UpdateCheckerForm.DescribeHeadline(snapshot)),
                $"No headline for {state}.");
            Assert.False(
                string.IsNullOrWhiteSpace(UpdateCheckerForm.DescribeDetail(snapshot)),
                $"No detail for {state}.");
        }
    }

    [Fact]
    public void EveryStateResolvesToAKnownAction()
    {
        foreach (var state in Enum.GetValues<DesktopUpdateState>())
        {
            Assert.True(Enum.IsDefined(UpdateCheckerForm.NextAction(state)), $"No action for {state}.");
        }
    }

    [Fact]
    public void AnAvailableUpdateNamesItsVersion()
    {
        var snapshot = new DesktopUpdateSnapshot(
            DesktopUpdateState.UpdateAvailable, "ignored", "1.0.0-rc.59", null);

        Assert.Contains("1.0.0-rc.59", UpdateCheckerForm.DescribeHeadline(snapshot), StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadingDoesNotClaimTheVersionIsInstalled()
    {
        // Downloading changes nothing until the restart, and the wording has to say so.
        var available = UpdateCheckerForm.DescribeDetail(new DesktopUpdateSnapshot(
            DesktopUpdateState.UpdateAvailable, "ignored", "1.0.0-rc.59"));

        Assert.Contains("does not change the installed version", available, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFailureSurfacesTheEngineReason()
    {
        var snapshot = new DesktopUpdateSnapshot(
            DesktopUpdateState.Failed, "The release feed was unreachable.", null);

        Assert.Equal("The release feed was unreachable.", UpdateCheckerForm.DescribeDetail(snapshot));
    }

    [Fact]
    public void APortableBuildExplainsWhyItCannotUpdate()
    {
        var detail = UpdateCheckerForm.DescribeDetail(
            new DesktopUpdateSnapshot(DesktopUpdateState.Unavailable, "ignored"));

        Assert.Contains("Portable", detail, StringComparison.OrdinalIgnoreCase);
    }
}
