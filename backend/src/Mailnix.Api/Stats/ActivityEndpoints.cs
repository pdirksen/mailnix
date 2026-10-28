using System.Security.Claims;
using Mailnix.Api.Hosting;
using Mailnix.Api.Runs;
using Mailnix.Core.Rules;
using Mailnix.Infrastructure.Data;
using Mailnix.Worker;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Mailnix.Api.Stats;

/// <summary>Single actions from the action log (filterable, newest first) and undoing a selection of them.</summary>
internal static class ActivityEndpoints
{
    private const int MaxPage = 200;

    public static IEndpointRouteBuilder MapActivityEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/activity/actions", Actions).WithTags("Activity");
        routes.MapPost("/activity/actions/undo", Undo).WithTags("Activity");
        return routes;
    }

    /// <param name="from">First day (user's time zone).</param>
    /// <param name="to">Last day (user's time zone).</param>
    /// <param name="before">Paging: only entries older than this one (ids grow with time).</param>
    internal static async Task<Ok<ActionPage>> Actions(
        ClaimsPrincipal user,
        MailnixDbContext db,
        CancellationToken ct,
        Guid? accountId = null,
        Guid? ruleId = null,
        ActionType? action = null,
        DateOnly? from = null,
        DateOnly? to = null,
        bool failedOnly = false,
        Guid? before = null,
        int limit = 50)
    {
        var userId = user.GetUserId();
        var zone = RuleSchedule.Zone(await db.Users.Where(u => u.Id == userId).Select(u => u.TimeZone).SingleOrDefaultAsync(ct));
        DateTimeOffset? start = from is { } f ? StartOf(f, zone) : null;
        DateTimeOffset? end = to is { } t ? StartOf(t.AddDays(1), zone) : null;

        // Entries of undo runs record where a message was put back: bookkeeping, not actions.
        var query = db.ActionLog.AsNoTracking()
            .Where(a => a.UserId == userId && db.Runs.Any(r => r.Id == a.RunId && r.Trigger != RunTrigger.Undo))
            .Where(a => accountId == null || a.AccountId == accountId)
            .Where(a => ruleId == null || a.RuleId == ruleId)
            .Where(a => action == null || a.Action == action)
            .Where(a => start == null || a.CreatedAt >= start)
            .Where(a => end == null || a.CreatedAt < end)
            .Where(a => !failedOnly || !a.Success)
            .Where(a => before == null || a.Id.CompareTo(before.Value) < 0);

        limit = Math.Clamp(limit, 1, MaxPage);
        var page = await query
            .OrderByDescending(a => a.Id)
            .Take(limit + 1)
            .Join(db.MailAccounts, a => a.AccountId, account => account.Id, (a, account) => new { Entry = a, AccountName = account.Name })
            .Join(db.Runs, x => x.Entry.RunId, run => run.Id, (x, run) => new { x.Entry, x.AccountName, run.Trigger })
            .ToListAsync(ct);

        var items = page.Take(limit).Select(x => new ActionEntry(
            x.Entry.Id, x.Entry.RunId, x.Entry.AccountId, x.AccountName, x.Entry.RuleId, x.Entry.RuleName, x.Entry.Action, x.Entry.Keyword,
            x.Entry.SourceFolder, x.Entry.TargetFolder, x.Entry.From, x.Entry.Subject, x.Entry.DryRun, x.Entry.Success, x.Entry.Error,
            x.Entry.CreatedAt, x.Entry.UndoneAt, CanUndo(x.Entry, x.Trigger))).ToList();
        return TypedResults.Ok(new ActionPage(items, page.Count > limit ? items[^1].Id : null));
    }

    /// <summary>One undo run per account for the selected actions (with the rest of what their runs did to the same messages).</summary>
    private static async Task<Results<Accepted<List<RunSummary>>, ValidationProblem>> Undo(
        UndoActionsRequest request,
        ClaimsPrincipal user,
        MailnixDbContext db,
        IWorkerSignals signals,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = user.GetUserId();
        var ids = request.ActionIds.Distinct().Take(1000).ToList();
        var selected = await db.ActionLog.AsNoTracking()
            .Where(a => a.UserId == userId && ids.Contains(a.Id))
            .Join(db.Runs, a => a.RunId, r => r.Id, (a, r) => new { Entry = a, r.Trigger })
            .ToListAsync(ct);
        var undoable = selected.Where(x => CanUndo(x.Entry, x.Trigger)).Select(x => x.Entry).ToList();
        if (undoable.Count == 0)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["actionIds"] = ["None of these actions can be undone."] });
        }

        var accountIds = undoable.Select(a => a.AccountId).Distinct().ToList();
        var accounts = await db.MailAccounts.Where(a => accountIds.Contains(a.Id)).ToListAsync(ct);
        var runs = new List<RunSummary>();
        foreach (var account in accounts)
        {
            var mine = undoable.Where(a => a.AccountId == account.Id).ToList();
            var run = RunEndpoints.NewRun(account, RunTrigger.Undo, mine[0].SourceFolder, time);
            run.UndoActionIds = [.. mine.Select(a => a.Id)];
            db.Runs.Add(run);
            runs.Add(RunSummary.From(run, account.Name));
        }

        await db.SaveChangesAsync(ct);
        foreach (var account in accounts)
        {
            signals.Wake(account.Id);
        }

        return TypedResults.Accepted("/api/runs", runs);
    }

    private static bool CanUndo(ActionLogEntry entry, RunTrigger trigger) =>
        entry.Success && !entry.DryRun && entry.UndoneAt is null && entry.Action != ActionType.Purge && trigger != RunTrigger.Undo;

    private static DateTimeOffset StartOf(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
