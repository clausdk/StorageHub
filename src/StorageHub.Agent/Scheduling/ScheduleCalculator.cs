using Cronos;

namespace StorageHub.Agent.Scheduling;

public enum MisfireAction
{
    NotDue,
    RunOneCoalescedOccurrence,
    SkipExpiredOccurrence
}

public static class ScheduleCalculator
{
    public static DateTimeOffset? GetNextOccurrence(
        CronScheduleDefinition schedule,
        DateTimeOffset after,
        bool inclusive = false)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var expression = CronExpression.Parse(schedule.Expression, CronFormat.Standard);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
        return expression.GetNextOccurrence(after, timeZone, inclusive);
    }

    public static MisfireAction EvaluateMisfire(
        CronScheduleDefinition schedule,
        DateTimeOffset scheduledFor,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (observedAt < scheduledFor)
        {
            return MisfireAction.NotDue;
        }

        return observedAt - scheduledFor <= schedule.MisfireGrace
            ? MisfireAction.RunOneCoalescedOccurrence
            : MisfireAction.SkipExpiredOccurrence;
    }
}
