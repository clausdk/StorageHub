namespace StorageHub.Agent.IntegrationTests;

public sealed class AdaptiveConcurrencyControllerTests
{
    [Fact]
    public void Adaptive_controller_starts_slow_ramps_and_backs_off()
    {
        var controller = new AdaptiveConcurrencyController(enabled: true, minimum: 1, maximum: 4);

        Assert.Equal(1, controller.CurrentLimit);
        controller.ReportSuccess(10_000, TimeSpan.FromSeconds(1));
        controller.ReportSuccess(10_000, TimeSpan.FromSeconds(1));
        Assert.Equal(2, controller.CurrentLimit);
        controller.ReportSuccess(1_000, TimeSpan.FromSeconds(1));
        Assert.Equal(1, controller.CurrentLimit);
    }

    [Fact]
    public void Provider_failure_reduces_concurrency_without_crossing_floor()
    {
        var controller = new AdaptiveConcurrencyController(enabled: true, minimum: 2, maximum: 5);
        controller.ReportSuccess(100, TimeSpan.FromSeconds(1));
        controller.ReportSuccess(100, TimeSpan.FromSeconds(1));
        Assert.Equal(3, controller.CurrentLimit);

        controller.ReportFailure();
        controller.ReportFailure();

        Assert.Equal(2, controller.CurrentLimit);
    }

    [Fact]
    public void Manual_mode_uses_the_configured_ceiling()
    {
        var controller = new AdaptiveConcurrencyController(enabled: false, minimum: 1, maximum: 7);

        controller.ReportFailure();

        Assert.Equal(7, controller.CurrentLimit);
    }
}
