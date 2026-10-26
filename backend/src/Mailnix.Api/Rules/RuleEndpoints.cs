using System.Security.Claims;
using Mailnix.Api.Hosting;
using Mailnix.Core.Rules;
using Mailnix.Infrastructure.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Mailnix.Api.Rules;

internal static class RuleEndpoints
{
    public static RouteGroupBuilder MapRuleEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/rules").WithTags("Rules");

        group.MapGet("/field-catalog", FieldCatalogInfo);
        group.MapGet("/", List);
        group.MapGet("/{id:guid}", Get);
        group.MapPost("/", Create);
        group.MapPut("/{id:guid}", Update);
        group.MapDelete("/{id:guid}", Delete);
        group.MapPost("/reorder", Reorder);
        group.MapPost("/dry-run", SetDryRun);
        group.MapPost("/validate", Validate);

        return group;
    }

    private static Ok<FieldCatalogResponse> FieldCatalogInfo() =>
        TypedResults.Ok(new FieldCatalogResponse(FieldCatalog.All, FolderTemplate.Placeholders));

    private static async Task<Ok<List<RuleResponse>>> List(ClaimsPrincipal user, MailnixDbContext db, CancellationToken ct, Guid? accountId = null)
    {
        var userId = user.GetUserId();
        var rules = await db.Rules
            .Where(r => r.UserId == userId && (accountId == null || r.AccountId == accountId))
            .OrderBy(r => r.AccountId).ThenBy(r => r.Priority).ThenBy(r => r.Name)
            .ToListAsync(ct);
        var usedBy = await UsedByAsync(db, [.. rules.Select(r => r.Id)], ct);
        var zone = await ZoneAsync(db, userId, ct);
        return TypedResults.Ok(rules.Select(r => RuleResponse.From(r, [.. usedBy[r.Id]], zone)).ToList());
    }

    private static async Task<Results<Ok<RuleResponse>, NotFound>> Get(Guid id, ClaimsPrincipal user, MailnixDbContext db, CancellationToken ct)
    {
        if (await FindOwnedAsync(db, id, user, ct) is not { } rule)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(RuleResponse.From(rule, [.. (await UsedByAsync(db, [rule.Id], ct))[rule.Id]], await ZoneAsync(db, rule.UserId, ct)));
    }

    private static async Task<Results<Created<RuleResponse>, ValidationProblem, NotFound>> Create(
        CreateRuleRequest request,
        ClaimsPrincipal user,
        MailnixDbContext db,
        RuleService rules,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = user.GetUserId();
        var account = await db.MailAccounts.SingleOrDefaultAsync(a => a.Id == request.AccountId && a.UserId == userId, ct);
        if (account is null)
        {
            return TypedResults.NotFound();
        }

        var id = Guid.CreateVersion7();
        var definition = ToDefinition(id, request.Name, request.Conditions, request.Actions);
        var errors = await rules.ValidateAsync(userId, definition, request.Trigger, request.Schedule, request.SourceFolders, ct);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(RuleService.ToProblemErrors(errors));
        }

        var lastPriority = await db.Rules.Where(r => r.AccountId == account.Id).MaxAsync(r => (int?)r.Priority, ct);
        var rule = NewRule(account, id, request, (lastPriority ?? -1) + 1, time.GetUtcNow());
        db.Rules.Add(rule);
        await db.SaveChangesAsync(ct);

        return TypedResults.Created($"/api/rules/{rule.Id}", RuleResponse.From(rule, [], await ZoneAsync(db, userId, ct)));
    }

    /// <summary>A new rule of the account (validated by the caller), with its references.</summary>
    internal static MailRule NewRule(MailAccount account, Guid id, CreateRuleRequest request, int priority, DateTimeOffset now)
    {
        var rule = new MailRule
        {
            Id = id,
            TenantId = account.TenantId,
            UserId = account.UserId,
            AccountId = account.Id,
            Name = request.Name.Trim(),
            Description = NullIfBlank(request.Description),
            Priority = priority,
            Enabled = request.Enabled,
            StopProcessing = request.StopProcessing,
            DryRun = request.DryRun,
            Trigger = request.Trigger,
            Schedule = request.Trigger == RuleTrigger.Scheduled ? request.Schedule?.Trim() : null,
            SourceFolders = NormalizeFolders(request.SourceFolders),
            ConditionsJson = RuleService.Serialize(request.Conditions),
            ActionsJson = RuleService.Serialize(request.Actions),
            CreatedAt = now,
            UpdatedAt = now,
        };
        RuleService.SyncReferences(rule, request.Conditions);
        return rule;
    }

    private static async Task<Results<Ok<RuleResponse>, ValidationProblem, NotFound>> Update(
        Guid id,
        UpdateRuleRequest request,
        ClaimsPrincipal user,
        MailnixDbContext db,
        RuleService rules,
        TimeProvider time,
        CancellationToken ct)
    {
        if (await FindOwnedAsync(db, id, user, ct, includeReferences: true) is not { } rule)
        {
            return TypedResults.NotFound();
        }

        var definition = ToDefinition(id, request.Name, request.Conditions, request.Actions);
        var errors = await rules.ValidateAsync(user.GetUserId(), definition, request.Trigger, request.Schedule, request.SourceFolders, ct);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(RuleService.ToProblemErrors(errors));
        }

        rule.Name = request.Name.Trim();
        rule.Description = NullIfBlank(request.Description);
        rule.Enabled = request.Enabled;
        rule.StopProcessing = request.StopProcessing;
        rule.DryRun = request.DryRun;
        rule.Trigger = request.Trigger;
        rule.Schedule = request.Trigger == RuleTrigger.Scheduled ? request.Schedule?.Trim() : null;
        rule.SourceFolders = NormalizeFolders(request.SourceFolders);
        rule.ConditionsJson = RuleService.Serialize(request.Conditions);
        rule.ActionsJson = RuleService.Serialize(request.Actions);
        rule.UpdatedAt = time.GetUtcNow();
        RuleService.SyncReferences(rule, request.Conditions);
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(RuleResponse.From(rule, [.. (await UsedByAsync(db, [rule.Id], ct))[rule.Id]], await ZoneAsync(db, rule.UserId, ct)));
    }

    private static async Task<TimeZoneInfo> ZoneAsync(MailnixDbContext db, Guid userId, CancellationToken ct) =>
        RuleSchedule.Zone(await db.Users.Where(u => u.Id == userId).Select(u => u.TimeZone).SingleOrDefaultAsync(ct));

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> Delete(
        Guid id,
        ClaimsPrincipal user,
        MailnixDbContext db,
        RuleService rules,
        CancellationToken ct)
    {
        if (await FindOwnedAsync(db, id, user, ct) is not { } rule)
        {
            return TypedResults.NotFound();
        }

        var dependents = await rules.DependentsAsync([rule.Id], ct);
        if (dependents.Count > 0)
        {
            return InUseProblem(
                $"\"{rule.Name}\" is used by other rules: {string.Join(", ", dependents.Select(d => d.Dependent.Name))}.",
                dependents.Select(d => d.Dependent));
        }

        db.Rules.Remove(rule);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, ValidationProblem, NotFound>> Reorder(
        ReorderRulesRequest request,
        ClaimsPrincipal user,
        MailnixDbContext db,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = user.GetUserId();
        var rules = await db.Rules.Where(r => r.AccountId == request.AccountId && r.UserId == userId).ToListAsync(ct);
        if (rules.Count == 0 && !await db.MailAccounts.AnyAsync(a => a.Id == request.AccountId && a.UserId == userId, ct))
        {
            return TypedResults.NotFound();
        }

        if (request.RuleIds.Count != rules.Count || !request.RuleIds.ToHashSet().SetEquals(rules.Select(r => r.Id)))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["ruleIds"] = ["List every rule of the account exactly once."],
            });
        }

        var now = time.GetUtcNow();
        for (var i = 0; i < request.RuleIds.Count; i++)
        {
            var rule = rules.Single(r => r.Id == request.RuleIds[i]);
            if (rule.Priority != i)
            {
                rule.Priority = i;
                rule.UpdatedAt = now;
            }
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound>> SetDryRun(
        SetDryRunRequest request,
        ClaimsPrincipal user,
        MailnixDbContext db,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = user.GetUserId();
        var ids = request.RuleIds.ToHashSet();
        var rules = await db.Rules.Where(r => ids.Contains(r.Id) && r.UserId == userId).ToListAsync(ct);
        if (rules.Count != ids.Count)
        {
            return TypedResults.NotFound();
        }

        var now = time.GetUtcNow();
        foreach (var rule in rules.Where(r => r.DryRun != request.DryRun))
        {
            rule.DryRun = request.DryRun;
            rule.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<RuleValidationResponse>, NotFound>> Validate(
        ValidateRuleRequest request,
        ClaimsPrincipal user,
        MailnixDbContext db,
        RuleService rules,
        CancellationToken ct)
    {
        var userId = user.GetUserId();
        if (!await db.MailAccounts.AnyAsync(a => a.Id == request.AccountId && a.UserId == userId, ct))
        {
            return TypedResults.NotFound();
        }

        var definition = ToDefinition(request.RuleId ?? Guid.Empty, "(draft)", request.Conditions, request.Actions);
        var errors = await rules.ValidateAsync(userId, definition, request.Trigger, request.Schedule, null, ct);
        return TypedResults.Ok(new RuleValidationResponse(errors.Count == 0, errors));
    }

    internal static ProblemHttpResult InUseProblem(string title, IEnumerable<RuleSummary> dependents) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: title,
            extensions: new Dictionary<string, object?> { ["errorCode"] = "InUse", ["dependents"] = dependents.DistinctBy(d => d.Id).ToList() });

    private static RuleDefinition ToDefinition(Guid id, string name, ConditionNode conditions, IReadOnlyList<RuleAction> actions) =>
        new() { Id = id, Name = name, Conditions = conditions, Actions = actions };

    private static List<string> NormalizeFolders(IReadOnlyList<string>? folders) =>
        folders is null ? [] : [.. folders.Select(f => f.Trim()).Where(f => f.Length > 0).Distinct(StringComparer.Ordinal)];

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Task<MailRule?> FindOwnedAsync(MailnixDbContext db, Guid id, ClaimsPrincipal user, CancellationToken ct, bool includeReferences = false)
    {
        var userId = user.GetUserId();
        IQueryable<MailRule> query = db.Rules;
        if (includeReferences)
        {
            query = query.Include(r => r.References).Include(r => r.ListReferences).AsSplitQuery();
        }

        return query.SingleOrDefaultAsync(r => r.Id == id && r.UserId == userId, ct);
    }

    private static async Task<ILookup<Guid, RuleSummary>> UsedByAsync(MailnixDbContext db, IReadOnlyCollection<Guid> ruleIds, CancellationToken ct)
    {
        var rows = await db.RuleReferences
            .Where(r => ruleIds.Contains(r.ReferencedRuleId))
            .Join(db.Rules, r => r.RuleId, rule => rule.Id, (r, rule) => new { r.ReferencedRuleId, rule.Id, rule.Name, rule.AccountId })
            .ToListAsync(ct);
        return rows.ToLookup(r => r.ReferencedRuleId, r => new RuleSummary(r.Id, r.Name, r.AccountId));
    }
}
