using System.Security.Claims;
using System.Text.Json;
using Mailnix.Api.Hosting;
using Mailnix.Core.Rules;
using Mailnix.Infrastructure.Data;
using Mailnix.Worker;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Mailnix.Api.Runs;

internal static class RunEndpoints
{
    private const int MaxListed = 200;
    private const int MaxActionsShown = 2000;

    public static IEndpointRouteBuilder MapRunEndpoints(this IEndpointRouteBuilder routes)
    {
        var runs = routes.MapGroup("/runs").WithTags("Runs");
        runs.MapGet("/", List);
        runs.MapGet("/{id:guid}", Get);
        runs.MapPost("/{id:guid}/confirm", Confirm);
        runs.MapPost("/{id:guid}/cancel", Cancel);
        runs.MapPost("/{id:guid}/undo", Undo);

        routes.MapPost("/accounts/{id:guid}/runs", Start).WithTags("Runs").RequireRateLimiting(RateLimiting.MailConnections);
        routes.MapPost("/rules/{id:guid}/undo", UndoRule).WithTags("Runs");
        return routes;
    }

    internal static async Task<Ok<List<RunSummary>>> List(
        ClaimsPrincipal user,
        MailnixDbContext db,
        CancellationToken ct,
        Guid? accountId = null,
        RunState? state = null,
        int limit = 50)
    {
        var userId = user.GetUserId();
        var runs = await db.Runs
            .Where(r => r.UserId == userId && (accountId == null || r.AccountId == accountId) && (state == null || r.State == state))
            .OrderByDescending(r => r.CreatedAt)
            .Take(Math.Clamp(limit, 1, MaxListed))
            .Join(db.MailAccounts, r => r.AccountId, a => a.Id, (r, a) => new { Run = r, a.Name })
            .ToListAsync(ct);
        return TypedResults.Ok(runs.Select(x => RunSummary.From(x.Run, x.Name)).ToList());
    }

    internal static async Task<Results<Ok<RunDetail>, NotFound>> Get(Guid id, ClaimsPrincipal user, MailnixDbContext db, CancellationToken ct)
    {
        if (await FindAsync(db, id, user, ct) is not { } found)
        {
            return TypedResults.NotFound();
        }

        var actions = await db.ActionLog.Where(a => a.RunId == id).OrderBy(a => a.CreatedAt).Take(MaxActionsShown).ToListAsync(ct);
        var pending = found.Run.PendingPlanJson is { } json
            ? JsonSerializer.Deserialize<PendingPlan>(json, RuleJson.Options)!.Messages
                .Select(m => new PendingMessageItem(m.Uid, m.From, m.Subject, [.. m.Actions.Select(a => new PendingActionItem(a.RuleName, a.Type, a.TargetFolder, a.Keyword, a.DryRun))]))
                .ToList()
            : [];
        return TypedResults.Ok(new RunDetail(RunSummary.From(found.Run, found.AccountName), [.. actions.Select(ActionLogItem.Create)], pending));
    }

    internal static async Task<Results<Accepted<RunSummary>, NotFound, ProblemHttpResult>> Start(
        Guid id,
        StartRunRequest request,
        ClaimsPrincipal user,
        MailnixDbContext db,
        IWorkerSignals signals,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = user.GetUserId();
        var account = await db.MailAccounts.SingleOrDefaultAsync(a => a.Id == id && a.UserId == userId, ct);
        if (account is null)
        {
            return TypedResults.NotFound();
        }

        if (!account.Enabled)
        {
            return Paused();
        }

        var run = NewRun(account, RunTrigger.Manual, request.Folder?.Trim() is { Length: > 0 } folder ? folder : account.WatchedFolders.FirstOrDefault() ?? "INBOX", time);
        run.Limit = request.Limit;
        run.RuleIds = request.RuleIds is { Count: > 0 } ids ? [.. ids.Distinct()] : null;
        return await QueueAsync(db, signals, run, account, ct);
    }

    private static async Task<Results<Ok<RunSummary>, NotFound, ProblemHttpResult>> Confirm(Guid id, ClaimsPrincipal user, MailnixDbContext db, IWorkerSignals signals, TimeProvider time, CancellationToken ct)
    {
        if (await FindAsync(db, id, user, ct) is not { } found)
        {
            return TypedResults.NotFound();
        }

        var run = found.Run;
        if (run.State != RunState.AwaitingConfirmation || run.ExpiresAt < time.GetUtcNow())
        {
            return WrongState("This run is not waiting for confirmation (any more).");
        }

        // The worker executes the stored plan after re-checking that the messages are still there.
        run.State = RunState.Queued;
        await db.SaveChangesAsync(ct);
        signals.Wake(run.AccountId);
        return TypedResults.Ok(RunSummary.From(run, found.AccountName));
    }

    internal static async Task<Results<Ok<RunSummary>, NotFound, ProblemHttpResult>> Cancel(Guid id, ClaimsPrincipal user, MailnixDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (await FindAsync(db, id, user, ct) is not { } found)
        {
            return TypedResults.NotFound();
        }

        var run = found.Run;
        if (run.State is not (RunState.AwaitingConfirmation or RunState.Queued))
        {
            return WrongState("Only queued runs or runs waiting for confirmation can be cancelled.");
        }

        run.State = RunState.Cancelled;
        run.PendingPlanJson = null;
        run.FinishedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(RunSummary.From(run, found.AccountName));
    }

    internal static async Task<Results<Accepted<RunSummary>, NotFound, ProblemHttpResult>> Undo(
        Guid id,
        ClaimsPrincipal user,
        MailnixDbContext db,
        IWorkerSignals signals,
        TimeProvider time,
        CancellationToken ct)
    {
        if (await FindAsync(db, id, user, ct) is not { } found)
        {
            return TypedResults.NotFound();
        }

        if (!RunSummary.From(found.Run, found.AccountName).CanUndo)
        {
            return WrongState("This run cannot be undone (nothing was changed, it was already undone, or it is still running).");
        }

        if (await db.Runs.AnyAsync(r => r.UndoRunId == id && (r.State == RunState.Queued || r.State == RunState.Running), ct))
        {
            return WrongState("An undo of this run is already in progress.");
        }

        var account = await db.MailAccounts.SingleAsync(a => a.Id == found.Run.AccountId, ct);
        if (!account.Enabled)
        {
            return Paused();
        }

        var run = NewRun(account, RunTrigger.Undo, found.Run.Folder, time);
        run.UndoRunId = id;
        return await QueueAsync(db, signals, run, account, ct);
    }

    internal static async Task<Results<Accepted<RunSummary>, NotFound, ProblemHttpResult>> UndoRule(
        Guid id,
        UndoRuleRequest request,
        ClaimsPrincipal user,
        MailnixDbContext db,
        IWorkerSignals signals,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = user.GetUserId();
        var rule = await db.Rules.SingleOrDefaultAsync(r => r.Id == id && r.UserId == userId, ct);
        if (rule is null)
        {
            return TypedResults.NotFound();
        }

        var account = await db.MailAccounts.SingleAsync(a => a.Id == rule.AccountId, ct);
        if (!account.Enabled)
        {
            return Paused();
        }

        var run = NewRun(account, RunTrigger.Undo, rule.SourceFolders.FirstOrDefault() ?? account.WatchedFolders.FirstOrDefault() ?? "INBOX", time);
        run.UndoRuleId = id;
        run.UndoCount = request.Count;
        return await QueueAsync(db, signals, run, account, ct);
    }

    private static async Task<Results<Accepted<RunSummary>, NotFound, ProblemHttpResult>> QueueAsync(
        MailnixDbContext db,
        IWorkerSignals signals,
        RuleRun run,
        MailAccount account,
        CancellationToken ct)
    {
        db.Runs.Add(run);
        await db.SaveChangesAsync(ct);
        signals.Wake(account.Id);
        return TypedResults.Accepted($"/api/runs/{run.Id}", RunSummary.From(run, account.Name));
    }

    internal static RuleRun NewRun(MailAccount account, RunTrigger trigger, string folder, TimeProvider time) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = account.TenantId,
        UserId = account.UserId,
        AccountId = account.Id,
        Trigger = trigger,
        State = RunState.Queued,
        Folder = folder,
        CreatedAt = time.GetUtcNow(),
    };

    private static async Task<(RuleRun Run, string AccountName)?> FindAsync(MailnixDbContext db, Guid id, ClaimsPrincipal user, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var found = await db.Runs.Where(r => r.Id == id && r.UserId == userId)
            .Join(db.MailAccounts, r => r.AccountId, a => a.Id, (r, a) => new { Run = r, a.Name })
            .SingleOrDefaultAsync(ct);
        return found is null ? null : (found.Run, found.Name);
    }

    private static ProblemHttpResult WrongState(string title) =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: title, extensions: new Dictionary<string, object?> { ["errorCode"] = "WrongState" });

    internal static ProblemHttpResult Paused() =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "The account is paused. Switch \"Process new mail\" on first.",
            extensions: new Dictionary<string, object?> { ["errorCode"] = "AccountPaused" });
}
