using Cronos;

namespace Mailnix.Infrastructure.Data;

/// <summary>When scheduled rules run. Shared by the scheduler (worker) and the API (next-run display, validation).</summary>
public static class RuleSchedule
{
    /// <summary>Parses a standard five-field cron expression ("minute hour day month weekday").</summary>
    public static bool TryParse(string? expression, out CronExpression cron)
    {
        cron = null!;
        if (string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }

        try
        {
            cron = CronExpression.Parse(expression.Trim());
            return true;
        }
        catch (CronFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// The next time a scheduled rule is due, in the user's time zone: the first occurrence after its last run, or
    /// after its last change (editing or enabling a rule starts its schedule afresh). Null for other rules.
    /// </summary>
    public static DateTimeOffset? NextRun(MailRule rule, TimeZoneInfo zone)
    {
        if (rule.Trigger != RuleTrigger.Scheduled || !rule.Enabled || !TryParse(rule.Schedule, out var cron))
        {
            return null;
        }

        var from = rule.LastScheduledAt is { } last && last > rule.UpdatedAt ? last : rule.UpdatedAt;
        return cron.GetNextOccurrence(from, zone);
    }

    public static TimeZoneInfo Zone(string? timeZoneId) =>
        timeZoneId is not null && TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var zone) ? zone : TimeZoneInfo.Utc;
}
