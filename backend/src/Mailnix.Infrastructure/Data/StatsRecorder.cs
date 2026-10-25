using Mailnix.Core.Messages;
using Microsoft.EntityFrameworkCore;

namespace Mailnix.Infrastructure.Data;

/// <summary>
/// Adds to the daily stats counters as the worker processes mail. Changes are tracked on the caller's context and saved
/// with its next SaveChanges, i.e. in the same transaction as the action log. One worker writes per account, so
/// read-modify-write is safe.
/// </summary>
public sealed class StatsRecorder(MailnixDbContext db, TimeProvider time)
{
    /// <summary>Spam scores from here on count as high (SpamAssassin's default threshold).</summary>
    public const double HighSpamScore = 5;

    private readonly Dictionary<Guid, TimeZoneInfo> _zones = [];

    /// <summary>Today in the account owner's time zone: the day counters are added to.</summary>
    public async Task<DateOnly> TodayAsync(MailAccount account, CancellationToken ct)
    {
        if (!_zones.TryGetValue(account.UserId, out var zone))
        {
            var id = await db.Users.Where(u => u.Id == account.UserId).Select(u => u.TimeZone).SingleOrDefaultAsync(ct);
            _zones[account.UserId] = zone = RuleSchedule.Zone(id);
        }

        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).DateTime);
    }

    /// <summary>New mail checked by the rules: volume, senders and sender trust.</summary>
    /// <param name="actioned">Whether a rule acted on the message (live, not dry run).</param>
    public async Task NewMailAsync(MailAccount account, IReadOnlyList<(MessageFacts Facts, bool Actioned)> messages, CancellationToken ct)
    {
        if (messages.Count == 0)
        {
            return;
        }

        var day = await TodayAsync(account, ct);
        await AddAsync(account, day, Guid.Empty, StatsMetrics.Processed, messages.Count, ct);

        var security = await db.SecurityDaily.FindAsync([account.Id, day], ct);
        if (security is null)
        {
            security = new SecurityDaily { AccountId = account.Id, UserId = account.UserId, Date = day };
            db.SecurityDaily.Add(security);
        }

        foreach (var (facts, actioned) in messages)
        {
            var auth = facts.Authentication;
            bool Failed(string result) => result is "fail" or "softfail" or "permerror";
            var authFailed = Failed(auth.Spf) || Failed(auth.Dkim) || Failed(auth.Dmarc);

            security.Processed++;
            security.SpfFailed += Failed(auth.Spf) ? 1 : 0;
            security.DkimFailed += Failed(auth.Dkim) ? 1 : 0;
            security.DmarcFailed += Failed(auth.Dmarc) ? 1 : 0;
            security.Spoofing += facts.DisplayNameMismatch ? 1 : 0;
            security.HighSpam += facts.SpamScore >= HighSpamScore ? 1 : 0;

            var domain = facts.Message.From?.Domain is { Length: > 0 } d ? d : "(unknown)";
            var sender = await db.SenderDaily.FindAsync([account.Id, day, domain], ct);
            if (sender is null)
            {
                sender = new SenderDaily { AccountId = account.Id, UserId = account.UserId, Date = day, Domain = domain };
                db.SenderDaily.Add(sender);
            }

            sender.Processed++;
            sender.Actioned += actioned ? 1 : 0;
            sender.Bulk += facts.IsNewsletter ? 1 : 0;
            sender.AuthFailed += authFailed ? 1 : 0;
        }
    }

    /// <summary>
    /// Logged actions as they are added: executed actions by type and rule, failures, dry runs, and per rule the
    /// messages it acted on.
    /// </summary>
    public async Task ActionsAsync(MailAccount account, IEnumerable<ActionLogEntry> entries, CancellationToken ct)
    {
        var list = entries.ToList();
        if (list.Count == 0)
        {
            return;
        }

        var day = await TodayAsync(account, ct);
        foreach (var entry in list)
        {
            var metric = entry.DryRun ? StatsMetrics.DryRun : entry.Success ? StatsMetrics.For(entry.Action) : StatsMetrics.Failed;
            await AddAsync(account, day, entry.RuleId ?? Guid.Empty, metric, 1, ct);
        }

        var acted = list.Where(e => e.Success && !e.DryRun && e.RuleId is not null).ToList();
        foreach (var rule in acted.GroupBy(e => e.RuleId!.Value))
        {
            await AddAsync(account, day, rule.Key, StatsMetrics.Matched, rule.Select(e => (e.RunId, e.SourceUid)).Distinct().Count(), ct);
        }

        var messages = acted.Select(e => (e.RunId, e.SourceUid)).Distinct().Count();
        if (messages > 0)
        {
            await AddAsync(account, day, Guid.Empty, StatsMetrics.Matched, messages, ct);
        }
    }

    /// <summary>Actions reverted by undo, counted for the rule that took them.</summary>
    public async Task UndoneAsync(MailAccount account, IEnumerable<ActionLogEntry> reverted, CancellationToken ct)
    {
        var day = await TodayAsync(account, ct);
        foreach (var rule in reverted.GroupBy(e => e.RuleId ?? Guid.Empty))
        {
            await AddAsync(account, day, rule.Key, StatsMetrics.Undone, rule.Count(), ct);
        }
    }

    private async Task AddAsync(MailAccount account, DateOnly day, Guid ruleId, string metric, int count, CancellationToken ct)
    {
        var row = await db.StatsDaily.FindAsync([account.Id, day, ruleId, metric], ct);
        if (row is null)
        {
            db.StatsDaily.Add(new StatsDaily { AccountId = account.Id, UserId = account.UserId, Date = day, RuleId = ruleId, Metric = metric, Count = count });
        }
        else
        {
            row.Count += count;
        }
    }
}
