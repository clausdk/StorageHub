using StorageHub.Agent.Scheduling;

namespace StorageHub.Agent.IntegrationTests;

public sealed class ScheduleCalculatorTests
{
    [Fact]
    public void RejectsInvalidCronAndTimeZone()
    {
        Assert.False(CronScheduleDefinition.TryCreate("not cron", "Europe/Copenhagen", out _, out var cronError));
        Assert.NotNull(cronError);
        Assert.False(CronScheduleDefinition.TryCreate("0 2 * * *", "Nowhere/Invalid", out _, out var zoneError));
        Assert.NotNull(zoneError);
    }

    [Fact]
    public void DefaultsMisfireGraceToTwentyFourHours()
    {
        Assert.True(CronScheduleDefinition.TryCreate("0 2 * * *", "Europe/Copenhagen", out var schedule, out _));

        Assert.Equal(TimeSpan.FromHours(24), schedule!.MisfireGrace);
    }

    [Fact]
    public void MisfiresCoalesceOnceWithinGraceAndSkipAfterGrace()
    {
        Assert.True(CronScheduleDefinition.TryCreate("0 2 * * *", "UTC", out var schedule, out _));
        var due = new DateTimeOffset(2026, 8, 1, 2, 0, 0, TimeSpan.Zero);

        Assert.Equal(MisfireAction.NotDue, ScheduleCalculator.EvaluateMisfire(schedule!, due, due.AddMinutes(-1)));
        Assert.Equal(MisfireAction.RunOneCoalescedOccurrence, ScheduleCalculator.EvaluateMisfire(schedule!, due, due.AddHours(4)));
        Assert.Equal(MisfireAction.SkipExpiredOccurrence, ScheduleCalculator.EvaluateMisfire(schedule!, due, due.AddHours(25)));
    }

    [Fact]
    public void SpringForwardOccurrenceUsesFirstValidLocalInstant()
    {
        Assert.True(CronScheduleDefinition.TryCreate("30 2 * * *", "Europe/Copenhagen", out var schedule, out _));
        var after = new DateTimeOffset(2026, 3, 28, 3, 0, 0, TimeSpan.FromHours(1));

        var next = ScheduleCalculator.GetNextOccurrence(schedule!, after);
        var local = TimeZoneInfo.ConvertTime(next!.Value, TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen"));

        Assert.Equal(new DateOnly(2026, 3, 29), DateOnly.FromDateTime(local.DateTime));
        Assert.True(local.Hour >= 3);
    }

    [Fact]
    public void FallBackCronDoesNotReplayTheDuplicatedWallTime()
    {
        Assert.True(CronScheduleDefinition.TryCreate("30 2 * * *", "Europe/Copenhagen", out var schedule, out _));
        var beforeFold = new DateTimeOffset(2026, 10, 24, 3, 0, 0, TimeSpan.FromHours(2));

        var first = ScheduleCalculator.GetNextOccurrence(schedule!, beforeFold);
        var second = ScheduleCalculator.GetNextOccurrence(schedule!, first!.Value);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen");
        var firstLocal = TimeZoneInfo.ConvertTime(first.Value, zone);
        var secondLocal = TimeZoneInfo.ConvertTime(second!.Value, zone);

        Assert.Equal(new DateOnly(2026, 10, 25), DateOnly.FromDateTime(firstLocal.DateTime));
        Assert.Equal(new DateOnly(2026, 10, 26), DateOnly.FromDateTime(secondLocal.DateTime));
    }
}
