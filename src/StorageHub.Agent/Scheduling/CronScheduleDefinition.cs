using Cronos;

namespace StorageHub.Agent.Scheduling;

public sealed record CronScheduleDefinition
{
    private CronScheduleDefinition(string expression, string timeZoneId, TimeSpan misfireGrace)
    {
        Expression = expression;
        TimeZoneId = timeZoneId;
        MisfireGrace = misfireGrace;
    }

    public string Expression { get; }

    public string TimeZoneId { get; }

    public TimeSpan MisfireGrace { get; }

    public static bool TryCreate(
        string expression,
        string timeZoneId,
        out CronScheduleDefinition? schedule,
        out string? error,
        TimeSpan? misfireGrace = null)
    {
        schedule = null;
        error = null;

        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "A five-field cron expression is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            error = "A time-zone identifier is required.";
            return false;
        }

        try
        {
            _ = CronExpression.Parse(expression, CronFormat.Standard);
            _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception exception) when (exception is CronFormatException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            error = "The cron expression or time-zone identifier is invalid.";
            return false;
        }

        var grace = misfireGrace ?? TimeSpan.FromHours(24);
        if (grace <= TimeSpan.Zero)
        {
            error = "Misfire grace must be greater than zero.";
            return false;
        }

        schedule = new CronScheduleDefinition(expression, timeZoneId, grace);
        return true;
    }
}
