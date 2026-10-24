using System.Security.Claims;
using Mailnix.Api.Accounts;
using Mailnix.Api.Hosting;
using Mailnix.Core.Rules;
using Mailnix.Infrastructure.Data;
using Mailnix.Mail;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Mailnix.Api.Rules;

internal static class PreviewEndpoints
{
    /// <summary>Bodies cost one round trip per message, so previews of body conditions read fewer messages.</summary>
    public const int MaxMessagesWithBody = 100;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);

    public static IEndpointRouteBuilder MapPreviewEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/accounts/{id:guid}/rules/preview", Preview)
            .WithTags("Rules")
            .RequireRateLimiting(RateLimiting.Previews);

        var templates = routes.MapGroup("/rule-templates").WithTags("Rules");
        templates.MapGet("/", ListTemplates);
        templates.MapPost("/{templateId}/draft", Draft);
        return routes;
    }

    private static async Task<Results<Ok<PreviewResponse>, NotFound, ValidationProblem, ProblemHttpResult>> Preview(
        Guid id,
        PreviewRequest request,
        ClaimsPrincipal user,
        MailnixDbContext db,
        RuleService rules,
        RuleSetLoader loader,
        IMessageReader reader,
        MailSecretProtector secrets,
        IMemoryCache cache,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = user.GetUserId();
        var account = await db.MailAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id && a.UserId == userId, ct);
        if (account is null)
        {
            return TypedResults.NotFound();
        }

        var draftId = request.RuleId ?? Guid.Empty;
        var draft = new RuleDefinition
        {
            Id = draftId,
            Name = string.IsNullOrWhiteSpace(request.Name) ? "(draft)" : request.Name.Trim(),
            Conditions = request.Conditions,
            Actions = request.Actions,
            StopProcessing = request.StopProcessing,
        };
        var errors = await rules.ValidateAsync(userId, draft, RuleTrigger.Manual, null, null, ct);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(RuleService.ToProblemErrors(errors));
        }

        var loaded = await loader.LoadAsync(account, ct);

        // The draft takes the place of the saved rule it edits (same priority), otherwise it runs last.
        var saved = loaded.AccountRules.FirstOrDefault(r => r.Id == draftId);
        draft = draft with { Priority = saved?.Priority ?? (loaded.AccountRules.Count == 0 ? 0 : loaded.AccountRules.Max(r => r.Priority) + 1) };
        var ruleSet = loaded.AccountRules.Where(r => r.Id != draftId).Append(draft).ToList();
        var allRules = new Dictionary<Guid, RuleDefinition>(loaded.Context.Rules) { [draftId] = draft };

        if (AccountEndpoints.TryUnprotect(secrets, account) is not { } password)
        {
            return AccountEndpoints.CredentialsUnreadableProblem();
        }

        var settings = AccountEndpoints.ToSettings(account, password);
        var folder = request.Folder?.Trim() is { Length: > 0 } requested ? requested : account.WatchedFolders.FirstOrDefault() ?? "INBOX";
        var requirements = FieldCatalog.RequiredFetch(ruleSet, allRules);
        var limit = requirements.HasFlag(FetchRequirements.Body) ? Math.Min(request.Limit, MaxMessagesWithBody) : request.Limit;

        FolderSample sample;
        IReadOnlySet<string> sentIds;
        bool fromCache;
        DateTimeOffset fetchedAt;
        try
        {
            // Cached per account state, folder, size and fetched data, so editing a rule does not hit the server again.
            var key = (account.Id, account.UpdatedAt, folder, limit, requirements);
            CachedSample? entry = null;
            fromCache = !request.Refresh && cache.TryGetValue(key, out entry) && entry is not null;
            if (!fromCache)
            {
                entry = new CachedSample(await reader.ReadLatestAsync(settings, folder, limit, requirements, ct), time.GetUtcNow());
                cache.Set(key, entry, CacheDuration);
            }

            (sample, fetchedAt) = (entry!.Sample, entry.FetchedAt);
            sentIds = FieldCatalog.UsedFields(ruleSet, allRules).Contains(ConditionField.IsReplyToMyMail) && account.SpecialFolders.Sent is { } sent
                ? await cache.GetOrCreateAsync((account.Id, account.UpdatedAt, "sent-ids"), async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = CacheDuration;
                    return await reader.ReadMessageIdsAsync(settings, sent, 500, ct);
                }) ?? new HashSet<string>()
                : new HashSet<string>();
        }
        catch (MailConnectionException ex)
        {
            return AccountEndpoints.ConnectionProblem(ex);
        }
        catch (Mail.FolderNotFoundException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["folder"] = [ex.Message] });
        }

        var context = loaded.Context with { SentMessageIds = sentIds, Rules = allRules };
        var names = ruleSet.ToDictionary(r => r.Id, r => r.Name);
        var messages = sample.Messages.Select(message =>
        {
            var plan = ActionPlanner.Plan(ruleSet, message, context, trace: true);
            var outcome = plan.Rules.Single(r => r.RuleId == draftId);
            PreviewAction ToAction(PlannedAction a) => new(a.Type, a.TargetFolder, a.Keyword, a.RuleId, names[a.RuleId]);
            return new PreviewMessage(
                message.Uid,
                message.From?.Address,
                message.From?.Name,
                message.Subject,
                message.EffectiveDate,
                message.IsSeen,
                outcome.Matched,
                outcome.Trace,
                [.. plan.Actions.Where(a => a.RuleId == draftId).Select(ToAction)],
                [.. plan.Skipped.Where(s => s.RuleId == draftId)
                    .Select(s => new PreviewSkippedAction(s.Type, s.TargetFolder, s.Reason, s.BlockedByRuleId is { } b ? names.GetValueOrDefault(b) : null))],
                [.. plan.Actions.Where(a => a.RuleId != draftId).Select(ToAction)]);
        }).ToList();

        var terminal = messages.Count(m => m.Actions.Any(a => a.Type is ActionType.MoveTo or ActionType.Delete or ActionType.MarkAsSpam or ActionType.Archive));
        return TypedResults.Ok(new PreviewResponse(
            sample.Folder,
            sample.TotalMessages,
            limit,
            messages.Count,
            messages.Count(m => m.Matched),
            terminal,
            account.BlastRadiusLimit,
            terminal > account.BlastRadiusLimit,
            fetchedAt,
            fromCache,
            messages));
    }

    private sealed record CachedSample(FolderSample Sample, DateTimeOffset FetchedAt);

    private static Ok<List<RuleTemplateInfo>> ListTemplates() =>
        TypedResults.Ok(RuleTemplates.All
            .Select(t => new RuleTemplateInfo(t.Id, [.. t.Parameters.Select(p => new TemplateParameterInfo(p.Name, p.Type, p.Default))]))
            .ToList());

    private static async Task<Results<Ok<RuleDraftResponse>, NotFound, ValidationProblem>> Draft(
        string templateId,
        RuleDraftRequest request,
        ClaimsPrincipal user,
        MailnixDbContext db,
        CancellationToken ct)
    {
        if (RuleTemplates.Find(templateId) is not { } template)
        {
            return TypedResults.NotFound();
        }

        var values = RuleTemplates.ResolveParameters(template, request.Parameters, out var errors);
        if (values.TryGetValue("list", out var listId))
        {
            var userId = user.GetUserId();
            var list = await db.NamedLists.Where(l => l.Id == Guid.Parse(listId) && l.UserId == userId).Select(l => (NamedListKind?)l.Kind).SingleOrDefaultAsync(ct);
            if (list is null or NamedListKind.Address)
            {
                errors["list"] = "Choose one of your domain or pattern lists.";
            }
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors.ToDictionary(e => $"parameters.{e.Key}", e => new[] { e.Value }));
        }

        var draft = template.Build(values, request.Name.Trim());
        return TypedResults.Ok(new RuleDraftResponse(
            draft.Name,
            draft.Conditions,
            draft.Actions,
            draft.Trigger switch
            {
                RuleTriggerKind.Scheduled => RuleTrigger.Scheduled,
                RuleTriggerKind.Manual => RuleTrigger.Manual,
                _ => RuleTrigger.OnNew,
            },
            draft.Schedule));
    }
}
