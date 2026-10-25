using System.Globalization;
using System.Security.Claims;
using System.Text;
using Mailnix.Api.Hosting;
using Mailnix.Core.Rules;
using Mailnix.Infrastructure.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Mailnix.Api.Stats;

/// <summary>
/// Stats from the daily counters (never from the action log, which is pruned). Days are the user's time zone; ranges
/// default to the last 30 days and include both ends.
/// </summary>
internal static class StatsEndpoints
{
    private const int MaxRangeDays = 400;

    private static readonly ActionType[] MovedActions = [ActionType.MoveTo, ActionType.Archive, ActionType.MarkAsSpam];

    public static IEndpointRouteBuilder MapStatsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/stats").WithTags("Stats");
        group.MapGet("/overview", Overview);
        group.MapGet("/timeseries", TimeSeriesEndpoint);
        group.MapGet("/rules", Rules);
        group.MapGet("/senders", Senders);
        group.MapGet("/folders", Folders);
        group.MapGet("/security", Security);
        group.MapGet("/reliability", Reliability);
        group.MapGet("/export", Export);
        return routes;
    }

    /// <summary>A validated date range in the user's time zone.</summary>
    private sealed record Range(DateOnly From, DateOnly To, DateOnly Today, TimeZoneInfo Zone)
    {
        public int Days => To.DayNumber - From.DayNumber + 1;

        public DateTimeOffset StartUtc => Start(From);

        public DateTimeOffset EndUtc => Start(To.AddDays(1));

        private DateTimeOffset Start(DateOnly day)
        {
            var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            return new DateTimeOffset(local, Zone.GetUtcOffset(local));
        }
    }

    private static async Task<Range> RangeAsync(MailnixDbContext db, Guid userId, DateOnly? from, DateOnly? to, CancellationToken ct, int defaultDays = 30)
    {
        var zone = RuleSchedule.Zone(await db.Users.Where(u => u.Id == userId).Select(u => u.TimeZone).SingleOrDefaultAsync(ct));
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
        var end = to ?? today;
        var start = from ?? end.AddDays(-(defaultDays - 1));
        if (start > end)
        {
            (start, end) = (end, start);
        }

        if (end.DayNumber - start.DayNumber >= MaxRangeDays)
        {
            start = end.AddDays(-(MaxRangeDays - 1));
        }

        return new Range(start, end, today, zone);
    }

    private static DateOnly Bucket(DateOnly day, Granularity granularity) => granularity switch
    {
        Granularity.Week => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)),
        Granularity.Month => new DateOnly(day.Year, day.Month, 1),
        _ => day,
    };

    private static List<DateOnly> Buckets(Range range, Granularity granularity)
    {
        var buckets = new List<DateOnly>();
        for (var day = range.From; day <= range.To; day = day.AddDays(1))
        {
            var bucket = Bucket(day, granularity);
            if (buckets.Count == 0 || buckets[^1] != bucket)
            {
                buckets.Add(bucket);
            }
        }

        return buckets;
    }

    private static TimeSeries Build<T>(Range range, Granularity granularity, IEnumerable<T> rows, Func<T, DateOnly> date, Func<T, string> key, Func<T, int> value, Func<string, string?>? name = null)
    {
        var buckets = Buckets(range, granularity);
        var index = buckets.Select((b, i) => (b, i)).ToDictionary(x => x.b, x => x.i);
        var series = rows
            .GroupBy(key)
            .Select(g =>
            {
                var values = new int[buckets.Count];
                foreach (var row in g)
                {
                    if (index.TryGetValue(Bucket(date(row), granularity), out var i))
                    {
                        values[i] += value(row);
                    }
                }

                return new Series(g.Key, name?.Invoke(g.Key), values);
            })
            .OrderByDescending(s => s.Values.Sum())
            .ToList();
        return new TimeSeries(buckets, series);
    }

    private static IQueryable<StatsDaily> Counters(MailnixDbContext db, Guid userId, Range range, Guid? accountId, Guid? ruleId = null) =>
        db.StatsDaily.AsNoTracking().Where(s => s.UserId == userId && s.Date >= range.From && s.Date <= range.To
            && (accountId == null || s.AccountId == accountId) && (ruleId == null || s.RuleId == ruleId));

    private static bool IsAction(string metric) => !StatsMetrics.NonActions.Contains(metric);

    private static KpiSet Kpis(IEnumerable<StatsDaily> rows)
    {
        var list = rows.ToList();
        int Sum(Func<StatsDaily, bool> predicate) => list.Where(predicate).Sum(r => r.Count);
        var moved = MovedActions.Select(StatsMetrics.For).ToHashSet();
        return new KpiSet(
            Sum(r => r.Metric == StatsMetrics.Processed),
            Sum(r => IsAction(r.Metric)),
            Sum(r => moved.Contains(r.Metric)),
            Sum(r => r.Metric == StatsMetrics.For(ActionType.Delete)),
            Sum(r => r.Metric == StatsMetrics.For(ActionType.Flag)),
            Sum(r => r.Metric == StatsMetrics.Failed));
    }

    private static async Task<Ok<OverviewResponse>> Overview(ClaimsPrincipal user, MailnixDbContext db, CancellationToken ct, Guid? accountId = null)
    {
        var userId = user.GetUserId();
        var range = await RangeAsync(db, userId, null, null, ct);
        var counters = await Counters(db, userId, range, accountId).ToListAsync(ct);
        var today = counters.Where(c => c.Date == range.Today).ToList();

        var accounts = await db.MailAccounts.AsNoTracking().Where(a => a.UserId == userId && (accountId == null || a.Id == accountId)).OrderBy(a => a.Name).ToListAsync(ct);
        var health = accounts.Select(a => new AccountHealth(
            a.Id, a.Name, a.Provider, a.Enabled, a.Status, a.LastErrorCode, a.LastCheckedAt,
            today.Where(c => c.AccountId == a.Id && c.Metric == StatsMetrics.Processed).Sum(c => c.Count),
            today.Where(c => c.AccountId == a.Id && IsAction(c.Metric)).Sum(c => c.Count))).ToList();

        // Unread in the watched folders, from each account's latest snapshot.
        int? unread = null;
        foreach (var account in accounts)
        {
            var latest = await db.FolderSnapshots.AsNoTracking().Where(s => s.AccountId == account.Id).MaxAsync(s => (DateOnly?)s.Date, ct);
            if (latest is { } day)
            {
                unread = (unread ?? 0) + await db.FolderSnapshots.AsNoTracking()
                    .Where(s => s.AccountId == account.Id && s.Date == day && account.WatchedFolders.Contains(s.Folder))
                    .SumAsync(s => s.Unread, ct);
            }
        }

        var pending = await db.Runs.CountAsync(r => r.UserId == userId && r.State == RunState.AwaitingConfirmation && (accountId == null || r.AccountId == accountId), ct);
        var rules = await db.Rules.AsNoTracking().Where(r => r.UserId == userId && (accountId == null || r.AccountId == accountId)).ToListAsync(ct);
        var ruleNames = rules.ToDictionary(r => r.Id, r => r.Name);

        var weekStart = range.Today.AddDays(-6);
        var topRules = counters
            .Where(c => c.Date >= weekStart && c.Metric == StatsMetrics.Matched && c.RuleId != Guid.Empty && ruleNames.ContainsKey(c.RuleId))
            .GroupBy(c => c.RuleId)
            .Select(g => new RuleCount(g.Key, ruleNames[g.Key], g.First().AccountId, g.Sum(c => c.Count)))
            .OrderByDescending(r => r.Count)
            .Take(5)
            .ToList();

        return TypedResults.Ok(new OverviewResponse(
            range.Today,
            Kpis(today),
            Kpis(counters.Where(c => c.Date == range.Today.AddDays(-1))),
            pending,
            accounts.Count(a => a.Enabled && a.Status == MailAccountStatus.Error),
            unread,
            health,
            Build(range, Granularity.Day, counters.Where(c => IsAction(c.Metric)), c => c.Date, c => c.Metric, c => c.Count),
            topRules,
            Hints(rules, counters, range)));
    }

    private static List<Hint> Hints(List<MailRule> rules, List<StatsDaily> counters, Range range)
    {
        if (rules.Count == 0)
        {
            return [new Hint(HintKind.NoRules)];
        }

        var hints = new List<Hint>();
        var matched = counters.Where(c => c.Metric == StatsMetrics.Matched).Select(c => c.RuleId).ToHashSet();
        var settled = range.StartUtc; // rules created before the 30-day window had their chance
        foreach (var rule in rules.Where(r => r.Enabled && !r.DryRun && r.CreatedAt < settled && !matched.Contains(r.Id)).Take(3))
        {
            hints.Add(new Hint(HintKind.NeverMatched, rule.Id, rule.Name));
        }

        var since = range.Today.AddDays(-13);
        foreach (var rule in rules.Where(r => r.Enabled && r.DryRun))
        {
            var planned = counters.Where(c => c.RuleId == rule.Id && c.Date >= since && c.Metric == StatsMetrics.DryRun).Sum(c => c.Count);
            if (planned > 0)
            {
                hints.Add(new Hint(HintKind.DryRunReady, rule.Id, rule.Name, planned));
            }
        }

        return hints;
    }

    /// <param name="metric">"actions" (all actions), "processed", or one metric (Matched, Failed, Undone, DryRun, MoveTo, ...).</param>
    /// <param name="groupBy">"action" (per metric), "account", "rule" or "none".</param>
    private static async Task<Results<Ok<TimeSeries>, ValidationProblem>> TimeSeriesEndpoint(
        ClaimsPrincipal user,
        MailnixDbContext db,
        CancellationToken ct,
        string metric = "actions",
        string groupBy = "action",
        DateOnly? from = null,
        DateOnly? to = null,
        Granularity granularity = Granularity.Day,
        Guid? accountId = null,
        Guid? ruleId = null)
    {
        var known = Enum.GetNames<ActionType>().Concat(StatsMetrics.NonActions).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (metric is not ("actions" or "processed") && !known.Contains(metric))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["metric"] = [$"Unknown metric '{metric}'."] });
        }

        if (groupBy is not ("action" or "account" or "rule" or "none"))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["groupBy"] = ["Use action, account, rule or none."] });
        }

        var userId = user.GetUserId();
        var range = await RangeAsync(db, userId, from, to, ct);
        var rows = (await Counters(db, userId, range, accountId, ruleId).ToListAsync(ct))
            .Where(c => metric switch
            {
                "actions" => IsAction(c.Metric),
                "processed" => c.Metric == StatsMetrics.Processed,
                _ => c.Metric.Equals(metric, StringComparison.OrdinalIgnoreCase),
            })
            .Where(c => groupBy != "rule" || c.RuleId != Guid.Empty)
            .ToList();

        Func<StatsDaily, string> key = groupBy switch
        {
            "account" => c => c.AccountId.ToString(),
            "rule" => c => c.RuleId.ToString(),
            "none" => _ => metric,
            _ => c => c.Metric,
        };
        var names = groupBy switch
        {
            "account" => await db.MailAccounts.Where(a => a.UserId == userId).ToDictionaryAsync(a => a.Id.ToString(), a => a.Name, ct),
            "rule" => await db.Rules.Where(r => r.UserId == userId).ToDictionaryAsync(r => r.Id.ToString(), r => r.Name, ct),
            _ => [],
        };
        return TypedResults.Ok(Build(range, granularity, rows, c => c.Date, key, c => c.Count, k => names.GetValueOrDefault(k)));
    }

    private static async Task<Ok<RuleStatsResponse>> Rules(ClaimsPrincipal user, MailnixDbContext db, CancellationToken ct, DateOnly? from = null, DateOnly? to = null, Guid? accountId = null) =>
        TypedResults.Ok(await RuleStatsAsync(db, user.GetUserId(), await RangeAsync(db, user.GetUserId(), from, to, ct), accountId, ct));

    private static async Task<RuleStatsResponse> RuleStatsAsync(MailnixDbContext db, Guid userId, Range range, Guid? accountId, CancellationToken ct)
    {
        var rules = await db.Rules.AsNoTracking().Where(r => r.UserId == userId && (accountId == null || r.AccountId == accountId)).OrderBy(r => r.Name).ToListAsync(ct);
        var accounts = await db.MailAccounts.Where(a => a.UserId == userId).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        var counters = await Counters(db, userId, range, accountId).Where(c => c.RuleId != Guid.Empty).ToListAsync(ct);
        var lastMatches = await db.StatsDaily.AsNoTracking()
            .Where(s => s.UserId == userId && s.Metric == StatsMetrics.Matched && s.RuleId != Guid.Empty)
            .GroupBy(s => s.RuleId)
            .Select(g => new { RuleId = g.Key, Last = g.Max(s => s.Date) })
            .ToDictionaryAsync(x => x.RuleId, x => x.Last, ct);

        var rows = rules.Select(rule =>
        {
            var own = counters.Where(c => c.RuleId == rule.Id).ToList();
            int Sum(Func<string, bool> metric) => own.Where(c => metric(c.Metric)).Sum(c => c.Count);
            var matched = Sum(m => m == StatsMetrics.Matched);
            return new RuleStatsRow(
                rule.Id,
                rule.Name,
                rule.AccountId,
                accounts.GetValueOrDefault(rule.AccountId) ?? string.Empty,
                rule.Enabled,
                rule.DryRun,
                matched,
                Sum(IsAction),
                Sum(m => m == StatsMetrics.Failed),
                Sum(m => m == StatsMetrics.Undone),
                Sum(m => m == StatsMetrics.DryRun),
                lastMatches.TryGetValue(rule.Id, out var last) ? last : null,
                Math.Round((double)matched / range.Days, 2));
        }).OrderByDescending(r => r.Matched).ThenBy(r => r.Name).ToList();
        return new RuleStatsResponse(range.From, range.To, rows);
    }

    private static async Task<Ok<SendersResponse>> Senders(
        ClaimsPrincipal user, MailnixDbContext db, CancellationToken ct, DateOnly? from = null, DateOnly? to = null, Guid? accountId = null, Granularity granularity = Granularity.Day, int limit = 15)
    {
        var userId = user.GetUserId();
        var range = await RangeAsync(db, userId, from, to, ct);
        var rows = await db.SenderDaily.AsNoTracking()
            .Where(s => s.UserId == userId && s.Date >= range.From && s.Date <= range.To && (accountId == null || s.AccountId == accountId))
            .ToListAsync(ct);
        var byDomain = rows.GroupBy(r => r.Domain)
            .Select(g => new SenderRow(g.Key, g.Sum(r => r.Processed), g.Sum(r => r.Actioned), g.Sum(r => r.Bulk), g.Sum(r => r.AuthFailed)))
            .ToList();
        var totals = new SenderRow(string.Empty, rows.Sum(r => r.Processed), rows.Sum(r => r.Actioned), rows.Sum(r => r.Bulk), rows.Sum(r => r.AuthFailed));
        limit = Math.Clamp(limit, 1, 100);

        var volume = Build(range, granularity, rows.SelectMany(r => new[] { (r.Date, Key: "processed", Value: r.Processed), (r.Date, Key: "actioned", Value: r.Actioned), (r.Date, Key: "bulk", Value: r.Bulk) }),
            r => r.Date, r => r.Key, r => r.Value);
        return TypedResults.Ok(new SendersResponse(
            totals,
            [.. byDomain.OrderByDescending(d => d.Processed).Take(limit)],
            [.. byDomain.Where(d => d.Actioned > 0).OrderByDescending(d => d.Actioned).Take(limit)],
            volume));
    }

    private static async Task<Ok<FoldersResponse>> Folders(ClaimsPrincipal user, MailnixDbContext db, CancellationToken ct, DateOnly? from = null, DateOnly? to = null, Guid? accountId = null)
    {
        var userId = user.GetUserId();
        var range = await RangeAsync(db, userId, from, to, ct, defaultDays: 90);
        var snapshots = await db.FolderSnapshots.AsNoTracking()
            .Where(s => s.UserId == userId && s.Date >= range.From && s.Date <= range.To && (accountId == null || s.AccountId == accountId))
            .ToListAsync(ct);
        var accounts = await db.MailAccounts.Where(a => a.UserId == userId).ToDictionaryAsync(a => a.Id, a => a.Name, ct);

        var latestByAccount = snapshots.GroupBy(s => s.AccountId).ToDictionary(g => g.Key, g => g.Max(s => s.Date));
        var folders = snapshots
            .Where(s => latestByAccount[s.AccountId] == s.Date)
            .Select(s =>
            {
                var first = snapshots.Where(o => o.AccountId == s.AccountId && o.Folder == s.Folder).MinBy(o => o.Date)!;
                return new FolderRow(s.AccountId, accounts.GetValueOrDefault(s.AccountId) ?? string.Empty, s.Folder, s.Messages, s.Unread,
                    first.Date == s.Date ? null : s.Messages - first.Messages);
            })
            .OrderByDescending(f => f.Messages)
            .ToList();

        var growth = Build(range, Granularity.Day,
            snapshots.SelectMany(s => new[] { (s.Date, Key: "messages", Value: s.Messages), (s.Date, Key: "unread", Value: s.Unread) }),
            s => s.Date, s => s.Key, s => s.Value);
        return TypedResults.Ok(new FoldersResponse(latestByAccount.Count == 0 ? null : latestByAccount.Values.Max(), folders, growth));
    }

    private static async Task<Ok<SecurityResponse>> Security(
        ClaimsPrincipal user, MailnixDbContext db, CancellationToken ct, DateOnly? from = null, DateOnly? to = null, Guid? accountId = null, Granularity granularity = Granularity.Day)
    {
        var userId = user.GetUserId();
        var range = await RangeAsync(db, userId, from, to, ct);
        var rows = await db.SecurityDaily.AsNoTracking()
            .Where(s => s.UserId == userId && s.Date >= range.From && s.Date <= range.To && (accountId == null || s.AccountId == accountId))
            .ToListAsync(ct);
        var totals = new SecurityTotals(rows.Sum(r => r.Processed), rows.Sum(r => r.SpfFailed), rows.Sum(r => r.DkimFailed), rows.Sum(r => r.DmarcFailed), rows.Sum(r => r.Spoofing), rows.Sum(r => r.HighSpam));
        var series = Build(range, granularity,
            rows.SelectMany(r => new[]
            {
                (r.Date, Key: "spf", Value: r.SpfFailed), (r.Date, Key: "dkim", Value: r.DkimFailed), (r.Date, Key: "dmarc", Value: r.DmarcFailed),
                (r.Date, Key: "spoofing", Value: r.Spoofing), (r.Date, Key: "highSpam", Value: r.HighSpam),
            }),
            r => r.Date, r => r.Key, r => r.Value);

        var failing = await db.SenderDaily.AsNoTracking()
            .Where(s => s.UserId == userId && s.Date >= range.From && s.Date <= range.To && s.AuthFailed > 0 && (accountId == null || s.AccountId == accountId))
            .GroupBy(s => s.Domain)
            .Select(g => new SenderRow(g.Key, g.Sum(s => s.Processed), g.Sum(s => s.Actioned), g.Sum(s => s.Bulk), g.Sum(s => s.AuthFailed)))
            .ToListAsync(ct);
        return TypedResults.Ok(new SecurityResponse(totals, series, [.. failing.OrderByDescending(f => f.AuthFailed).Take(10)]));
    }

    private static async Task<Ok<ReliabilityResponse>> Reliability(
        ClaimsPrincipal user, MailnixDbContext db, CancellationToken ct, DateOnly? from = null, DateOnly? to = null, Guid? accountId = null, Granularity granularity = Granularity.Day)
    {
        var userId = user.GetUserId();
        var range = await RangeAsync(db, userId, from, to, ct);
        var (start, end) = (range.StartUtc, range.EndUtc);
        var runs = await db.Runs.AsNoTracking()
            .Where(r => r.UserId == userId && (accountId == null || r.AccountId == accountId) && r.CreatedAt >= start && r.CreatedAt < end)
            .Select(r => new { r.State, r.Trigger, r.StartedAt, r.FinishedAt })
            .ToListAsync(ct);

        var failed = await Counters(db, userId, range, accountId).Where(c => c.Metric == StatsMetrics.Failed).ToListAsync(ct);
        var accounts = await db.MailAccounts.AsNoTracking().Where(a => a.UserId == userId && (accountId == null || a.Id == accountId)).OrderBy(a => a.Name).ToListAsync(ct);
        return TypedResults.Ok(new ReliabilityResponse(
            [.. runs.GroupBy(r => r.State).Select(g => new RunStateCount(g.Key, g.Count())).OrderByDescending(s => s.Count)],
            [.. runs.GroupBy(r => r.Trigger).Select(g =>
            {
                var durations = g.Where(r => r.StartedAt is not null && r.FinishedAt is not null).Select(r => (r.FinishedAt!.Value - r.StartedAt!.Value).TotalSeconds).ToList();
                return new TriggerStats(g.Key, g.Count(), g.Count(r => r.State is RunState.Failed or RunState.CompletedWithErrors), durations.Count == 0 ? null : Math.Round(durations.Average(), 1));
            })],
            Build(range, granularity, failed, c => c.Date, _ => StatsMetrics.Failed, c => c.Count),
            await db.Runs.CountAsync(r => r.UserId == userId && r.State == RunState.AwaitingConfirmation && (accountId == null || r.AccountId == accountId), ct),
            [.. accounts.Select(a => new AccountHealth(a.Id, a.Name, a.Provider, a.Enabled, a.Status, a.LastErrorCode, a.LastCheckedAt, 0, 0))]));
    }

    /// <summary>A section as CSV (spreadsheet-safe: cells cannot start a formula).</summary>
    private static async Task<Results<FileContentHttpResult, ValidationProblem>> Export(
        ClaimsPrincipal user, MailnixDbContext db, CancellationToken ct, string section = "volume", DateOnly? from = null, DateOnly? to = null, Guid? accountId = null, Granularity granularity = Granularity.Day)
    {
        var userId = user.GetUserId();
        var range = await RangeAsync(db, userId, from, to, ct, defaultDays: section == "folders" ? 90 : 30);
        List<string[]> table;
        switch (section)
        {
            case "volume":
                var counters = await Counters(db, userId, range, accountId).ToListAsync(ct);
                var series = Build(range, granularity, counters.Where(c => c.RuleId == Guid.Empty || IsAction(c.Metric) || c.Metric == StatsMetrics.Failed), c => c.Date, c => c.Metric, c => c.Count);
                table = [["date", .. series.Series.Select(s => s.Key)], .. series.Buckets.Select((b, i) => (string[])[Iso(b), .. series.Series.Select(s => Num(s.Values[i]))])];
                break;
            case "rules":
                var rules = await RuleStatsAsync(db, userId, range, accountId, ct);
                table = [["rule", "account", "enabled", "dry_run", "matched", "actions", "failed", "undone", "dry_run_actions", "last_match", "matches_per_day"],
                    .. rules.Rules.Select(r => new[] { r.Name, r.AccountName, r.Enabled.ToString(), r.DryRun.ToString(), Num(r.Matched), Num(r.Actions), Num(r.Failed), Num(r.Undone), Num(r.DryRunActions), r.LastMatch is { } d ? Iso(d) : string.Empty, r.MatchesPerDay.ToString(CultureInfo.InvariantCulture) })];
                break;
            case "senders":
                var senders = (await Senders(user, db, ct, range.From, range.To, accountId, granularity, 100)).Value!;
                table = [["domain", "processed", "actioned", "bulk", "auth_failed"], .. senders.TopProcessed.Select(s => new[] { s.Domain, Num(s.Processed), Num(s.Actioned), Num(s.Bulk), Num(s.AuthFailed) })];
                break;
            case "folders":
                var folders = (await Folders(user, db, ct, range.From, range.To, accountId)).Value!;
                table = [["account", "folder", "messages", "unread", "change"], .. folders.Folders.Select(f => new[] { f.AccountName, f.Folder, Num(f.Messages), Num(f.Unread), f.Change is { } c ? Num(c) : string.Empty })];
                break;
            case "security":
                var security = (await Security(user, db, ct, range.From, range.To, accountId, granularity)).Value!;
                table = [["date", .. security.Series.Series.Select(s => s.Key)], .. security.Series.Buckets.Select((b, i) => (string[])[Iso(b), .. security.Series.Series.Select(s => Num(s.Values[i]))])];
                break;
            case "reliability":
                var reliability = (await Reliability(user, db, ct, range.From, range.To, accountId, granularity)).Value!;
                table = [["trigger", "runs", "failed", "average_seconds"], .. reliability.ByTrigger.Select(t => new[] { t.Trigger.ToString(), Num(t.Runs), Num(t.Failed), t.AverageSeconds?.ToString(CultureInfo.InvariantCulture) ?? string.Empty })];
                break;
            default:
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["section"] = ["Use volume, rules, senders, folders, security or reliability."] });
        }

        var csv = new StringBuilder();
        foreach (var row in table)
        {
            csv.AppendJoin(',', row.Select(Cell)).Append("\r\n");
        }

        return TypedResults.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(), "text/csv; charset=utf-8",
            $"mailnix-{section}-{Iso(range.From)}-{Iso(range.To)}.csv");
    }

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Cell(string value)
    {
        // A leading =, +, - or @ would make spreadsheets evaluate a formula (CSV injection).
        var safe = value.Length > 0 && value[0] is '=' or '+' or '-' or '@' && !int.TryParse(value, out _) ? "'" + value : value;
        return safe.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{safe.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : safe;
    }
}
