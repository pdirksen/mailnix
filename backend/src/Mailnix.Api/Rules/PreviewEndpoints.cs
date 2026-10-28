using System.Security.Cryptography;
using System.Security.Claims;
using Mailnix.Api.Accounts;
using Mailnix.Api.Hosting;
using Mailnix.Core.Rules;
using Mailnix.Infrastructure.Data;
using Mailnix.Infrastructure.OAuth;
using Mailnix.Mail;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Mailnix.Api.Rules;

internal static class PreviewEndpoints
{
    /// <summary>Bodies cost one round trip per message, so previews of body conditions read fewer messages.</summary>
    public const int MaxMessagesWithBody = 100;

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

    internal static async Task<Results<Ok<PreviewResponse>, NotFound, ValidationProblem, ProblemHttpResult>> Preview(
        Guid id,
        PreviewRequest request,
        ClaimsPrincipal user,
        MailnixDbContext db,
        RuleService rules,
        RuleSetLoader loader,
        MessageSampler sampler,
        AccountCredentials credentials,
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

        ImapConnectionSettings settings;
        try
        {
            settings = await credentials.SettingsAsync(account, ct);
        }
        catch (CryptographicException)
        {
            return AccountEndpoints.CredentialsUnreadableProblem();
        }
        catch (MailConnectionException ex)
        {
            return AccountEndpoints.ConnectionProblem(ex);
        }
        var folder = request.Folder?.Trim() is { Length: > 0 } requested ? requested : account.WatchedFolders.FirstOrDefault() ?? "INBOX";
        var requirements = FieldCatalog.RequiredFetch(ruleSet, allRules);
        var limit = requirements.HasFlag(FetchRequirements.Body) ? Math.Min(request.Limit, MaxMessagesWithBody) : request.Limit;

        MessageSample read;
        IReadOnlySet<string> sentIds;
        try
        {
            read = await sampler.ReadAsync(account, settings, folder, limit, requirements, request.Refresh, ct);
            sentIds = await sampler.SentIdsAsync(account, settings, ruleSet, allRules, ct);
        }
        catch (MailConnectionException ex)
        {
            return AccountEndpoints.ConnectionProblem(ex);
        }
        catch (Mail.FolderNotFoundException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["folder"] = [ex.Message] });
        }

        var (sample, fetchedAt, fromCache) = read;
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

    internal static Ok<List<RuleTemplateInfo>> ListTemplates() =>
        TypedResults.Ok(RuleTemplates.All
            .Select(t => new RuleTemplateInfo(t.Id, [.. t.Parameters.Select(p => new TemplateParameterInfo(p.Name, p.Type, p.Default))]))
            .ToList());

    internal static async Task<Results<Ok<RuleDraftResponse>, NotFound, ValidationProblem>> Draft(
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
        var userId = user.GetUserId();
        foreach (var parameter in template.Parameters.Where(p => p.Type is TemplateParameterType.DomainList or TemplateParameterType.AddressList))
        {
            if (!values.TryGetValue(parameter.Name, out var listId))
            {
                continue;
            }

            var kind = await db.NamedLists.Where(l => l.Id == Guid.Parse(listId) && l.UserId == userId).Select(l => (NamedListKind?)l.Kind).SingleOrDefaultAsync(ct);
            var wantsAddresses = parameter.Type == TemplateParameterType.AddressList;
            if (kind is null || (kind == NamedListKind.Address) != wantsAddresses)
            {
                errors[parameter.Name] = wantsAddresses ? "Choose one of your address lists." : "Choose one of your domain or pattern lists.";
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
            ToTrigger(draft.Trigger),
            draft.Schedule));
    }

    internal static RuleTrigger ToTrigger(RuleTriggerKind kind) => kind switch
    {
        RuleTriggerKind.Scheduled => RuleTrigger.Scheduled,
        RuleTriggerKind.Manual => RuleTrigger.Manual,
        _ => RuleTrigger.OnNew,
    };
}
