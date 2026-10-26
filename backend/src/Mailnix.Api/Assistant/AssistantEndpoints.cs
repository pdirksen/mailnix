using System.Security.Claims;
using System.Security.Cryptography;
using Mailnix.Api.Accounts;
using Mailnix.Api.Hosting;
using Mailnix.Api.Lists;
using Mailnix.Api.Rules;
using Mailnix.Api.Runs;
using Mailnix.Core.Messages;
using Mailnix.Core.Rules;
using Mailnix.Infrastructure.Data;
using Mailnix.Infrastructure.OAuth;
using Mailnix.Mail;
using Mailnix.Worker;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Mailnix.Api.Assistant;

/// <summary>
/// The setup assistant: a folder blueprint plus the rules that fill it, tried on the inbox and created in one go.
/// </summary>
internal static class AssistantEndpoints
{
    private const int MaxExamples = 3;
    private const int MaxDomains = 15;

    public static IEndpointRouteBuilder MapAssistantEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/assistant/blueprints", Blueprints).WithTags("Assistant");

        var account = routes.MapGroup("/accounts/{id:guid}/assistant").WithTags("Assistant");
        account.MapPost("/analyze", Analyze).RequireRateLimiting(RateLimiting.Previews);
        account.MapPost("/apply", Apply).RequireRateLimiting(RateLimiting.MailConnections);
        return routes;
    }

    private static Ok<List<BlueprintInfo>> Blueprints() =>
        TypedResults.Ok(FolderBlueprints.All.Select(b => new BlueprintInfo(
            b.Id,
            [.. b.Folders.Select(f => new BlueprintFolderInfo(f.Key, f.Suffix, f.DefaultOn))],
            [.. b.Rules.Select(r => new BlueprintRuleInfo(
                r.Key,
                r.TemplateId,
                r.Folder,
                r.List,
                r.DefaultOn,
                [.. RuleTemplates.Find(r.TemplateId)!.Parameters
                    .Where(p => !(p.Name == "folder" && r.Folder is not null) && !(p.Name == "list" && r.List is not null))
                    .Select(p => new TemplateParameterInfo(p.Name, p.Type, p.Default))]))],
            [.. b.ListSeeds.Select(s => new ListSeedInfo(s.Key, s.Kind, s.Entries))])).ToList());

    private static async Task<Results<Ok<AssistantAnalyzeResponse>, NotFound, ValidationProblem, ProblemHttpResult>> Analyze(
        Guid id,
        AssistantAnalyzeRequest request,
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

        var (resolved, errors) = await ResolveAsync(userId, request.Selection, db, rules, ct);
        if (resolved is null)
        {
            return TypedResults.ValidationProblem(errors);
        }

        // The suggestions run after the account's existing rules, as they will once created.
        var loaded = await loader.LoadAsync(account, ct);
        var start = loaded.AccountRules.Count == 0 ? 0 : loaded.AccountRules.Max(r => r.Priority) + 1;
        var drafts = resolved.Drafts.Select((d, i) => ToDefinition(Guid.NewGuid(), d.Draft) with { Priority = start + i }).ToList();
        var keys = drafts.Select((d, i) => (d.Id, resolved.Drafts[i].Key)).ToDictionary(x => x.Id, x => x.Key);
        var ruleSet = loaded.AccountRules.Concat(drafts).ToList();
        var allRules = new Dictionary<Guid, RuleDefinition>(loaded.Context.Rules);
        var namedLists = new Dictionary<Guid, NamedList>(loaded.Context.NamedLists);
        foreach (var draft in drafts)
        {
            allRules[draft.Id] = draft;
        }

        foreach (var list in resolved.Lists.Where(l => l.IsNew))
        {
            namedLists[list.Id] = new NamedList(list.Id, list.Name, list.Kind, list.Entries.Select(e => e.Value));
        }

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

        var folder = account.WatchedFolders.FirstOrDefault() ?? "INBOX";
        var requirements = FieldCatalog.RequiredFetch(ruleSet, allRules);
        var limit = requirements.HasFlag(FetchRequirements.Body) ? Math.Min(request.Limit, PreviewEndpoints.MaxMessagesWithBody) : request.Limit;
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

        var context = loaded.Context with { SentMessageIds = sentIds, Rules = allRules, NamedLists = namedLists };
        var tallies = resolved.Drafts.ToDictionary(d => d.Key, _ => new Tally());
        var sortedByExisting = 0;
        var unsorted = new List<MessageSnapshot>();
        foreach (var message in read.Sample.Messages)
        {
            var plan = ActionPlanner.Plan(ruleSet, message, context);
            var terminal = plan.TerminalAction;
            if (terminal is null)
            {
                unsorted.Add(message);
            }
            else if (!keys.ContainsKey(terminal.RuleId))
            {
                sortedByExisting++;
            }

            foreach (var outcome in plan.Rules.Where(r => r.Matched))
            {
                if (!keys.TryGetValue(outcome.RuleId, out var key))
                {
                    continue;
                }

                var tally = tallies[key];
                tally.Matched++;
                if (terminal?.RuleId == outcome.RuleId)
                {
                    tally.Sorted++;
                    tally.Add(message, terminal.TargetFolder);
                }
                else if (plan.Skipped.Any(s => s.RuleId == outcome.RuleId && s.Reason == SkipReason.TerminalActionAlreadyPlanned))
                {
                    tally.Blocked++;
                }
                else
                {
                    tally.Add(message, null);
                }
            }
        }

        var domains = unsorted
            .Where(m => m.From is { Domain.Length: > 0 } from && !context.MyAddresses.Contains(from.Address))
            .GroupBy(m => m.From!.Domain)
            .Select(g => new SenderDomainCount(g.Key, g.Count()))
            .OrderByDescending(d => d.Count).ThenBy(d => d.Domain, StringComparer.Ordinal)
            .Take(MaxDomains)
            .ToList();

        return TypedResults.Ok(new AssistantAnalyzeResponse(
            read.Sample.Folder,
            read.Sample.TotalMessages,
            read.Sample.Messages.Count,
            sortedByExisting,
            unsorted.Count,
            read.FetchedAt,
            read.FromCache,
            [.. tallies.Select(t => new AssistantRuleAnalysis(t.Key, t.Value.Matched, t.Value.Sorted, t.Value.Blocked, t.Value.Examples))],
            domains));
    }

    private static async Task<Results<Ok<AssistantApplyResponse>, NotFound, ValidationProblem, ProblemHttpResult>> Apply(
        Guid id,
        AssistantApplyRequest request,
        ClaimsPrincipal user,
        MailnixDbContext db,
        RuleService rules,
        AccountCredentials credentials,
        IMailboxSessionFactory sessions,
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

        if (request.RunOnInboxLimit is not null && !account.Enabled)
        {
            return RunEndpoints.Paused();
        }

        var (resolved, errors) = await ResolveAsync(userId, request.Selection, db, rules, ct);
        if (resolved is null)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var now = time.GetUtcNow();
        var newLists = resolved.Lists.Where(l => l.IsNew).ToList();
        foreach (var list in newLists)
        {
            var entity = new NamedListEntity
            {
                Id = list.Id,
                TenantId = account.TenantId,
                UserId = userId,
                Name = list.Name,
                Kind = list.Kind,
                CreatedAt = now,
                UpdatedAt = now,
            };
            entity.Entries.AddRange(list.Entries.Select(e => ListEndpoints.NewEntry(list.Id, e)));
            db.NamedLists.Add(entity);
        }

        var lastPriority = await db.Rules.Where(r => r.AccountId == account.Id).MaxAsync(r => (int?)r.Priority, ct) ?? -1;
        var created = new List<AssistantCreatedRule>();
        foreach (var (key, draft) in resolved.Drafts)
        {
            var rule = RuleEndpoints.NewRule(
                account,
                Guid.CreateVersion7(),
                new CreateRuleRequest(account.Id, draft.Name, draft.Conditions, draft.Actions, DryRun: request.DryRun, Trigger: PreviewEndpoints.ToTrigger(draft.Trigger), Schedule: draft.Schedule),
                lastPriority + 1 + created.Count,
                now);
            db.Rules.Add(rule);
            created.Add(new AssistantCreatedRule(key, rule.Id, rule.Name));
        }

        RuleRun? run = null;
        if (request.RunOnInboxLimit is { } limit && created.Count > 0)
        {
            run = RunEndpoints.NewRun(account, RunTrigger.Manual, account.WatchedFolders.FirstOrDefault() ?? "INBOX", time);
            run.Limit = limit;
            run.RuleIds = [.. created.Select(r => r.Id)];
            db.Runs.Add(run);
        }

        await db.SaveChangesAsync(ct);
        if (run is not null)
        {
            signals.Wake(account.Id);
        }

        var folders = request.CreateFolders ? await CreateFoldersAsync(account, request.Selection.Folders.Values, credentials, sessions, ct) : [];
        return TypedResults.Ok(new AssistantApplyResponse(
            created,
            [.. newLists.Select(l => l.Id)],
            run is null ? null : RunSummary.From(run, account.Name),
            folders));
    }

    /// <summary>Creates the folders up to their first placeholder; moves create the rest when they happen.</summary>
    private static async Task<List<AssistantFolderResult>> CreateFoldersAsync(
        MailAccount account,
        IEnumerable<string> templates,
        AccountCredentials credentials,
        IMailboxSessionFactory sessions,
        CancellationToken ct)
    {
        var delimiter = account.FolderDelimiter is [var d] ? d : '/';
        var paths = templates.Select(t => FolderTemplate.StaticPrefix(t, delimiter)).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        IMailboxSession session;
        try
        {
            session = await sessions.OpenAsync(await credentials.SettingsAsync(account, ct), ct);
        }
        catch (Exception ex) when (ex is MailConnectionException or CryptographicException)
        {
            return [.. paths.Select(p => new AssistantFolderResult(p, false, ex.Message))];
        }

        await using (session)
        {
            var results = new List<AssistantFolderResult>();
            foreach (var path in paths)
            {
                try
                {
                    await session.CreateFolderAsync(path, ct);
                    results.Add(new AssistantFolderResult(path, true, null));
                }
                catch (Exception ex) when (ex is MailKit.Net.Imap.ImapCommandException or MailKit.Net.Imap.ImapProtocolException or InvalidOperationException or MailConnectionException)
                {
                    results.Add(new AssistantFolderResult(path, false, ex.Message));
                }
            }

            return results;
        }
    }

    private sealed record ResolvedList(string Key, Guid Id, NamedListKind Kind, string Name, IReadOnlyList<ListEntryDto> Entries, bool IsNew);

    private sealed record Resolved(IReadOnlyList<BlueprintDraft> Drafts, IReadOnlyList<ResolvedList> Lists);

    /// <summary>Checks the selection and builds its rules; lists to be created get their ids here.</summary>
    private static async Task<(Resolved? Resolved, Dictionary<string, string[]> Errors)> ResolveAsync(
        Guid userId,
        AssistantSelection selection,
        MailnixDbContext db,
        RuleService rules,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (FolderBlueprints.Find(selection.BlueprintId) is not { } blueprint)
        {
            errors["blueprintId"] = ["Unknown blueprint."];
            return (null, errors);
        }

        var selected = selection.Rules.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var seeds = blueprint.Rules.Where(r => r.List is not null && selected.Contains(r.Key)).Select(r => r.List!).Distinct()
            .Select(key => FolderBlueprints.Seeds.Single(s => s.Key == key));
        var owned = await db.NamedLists.Where(l => l.UserId == userId).Select(l => new { l.Id, l.Kind, l.Name }).ToListAsync(ct);
        var lists = new List<ResolvedList>();
        foreach (var seed in seeds)
        {
            var choice = selection.Lists?.GetValueOrDefault(seed.Key);
            if (choice?.ListId is { } listId)
            {
                var existing = owned.SingleOrDefault(l => l.Id == listId);
                if (existing is null || (existing.Kind == NamedListKind.Address) != (seed.Kind == NamedListKind.Address))
                {
                    errors[$"lists.{seed.Key}"] = [seed.Kind == NamedListKind.Address ? "Choose one of your address lists." : "Choose one of your domain or pattern lists."];
                }
                else
                {
                    lists.Add(new ResolvedList(seed.Key, listId, existing.Kind, existing.Name, [], IsNew: false));
                }

                continue;
            }

            var name = choice?.Name?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                errors[$"lists.{seed.Key}.name"] = ["Name the new list."];
            }

            var (entries, entryErrors) = ListEndpoints.PrepareEntries(seed.Kind, [.. (choice?.Entries ?? seed.Entries).Select(e => new ListEntryDto(e))]);
            foreach (var (path, messages) in entryErrors)
            {
                errors[$"lists.{seed.Key}.{path}"] = messages;
            }

            lists.Add(new ResolvedList(seed.Key, Guid.CreateVersion7(), seed.Kind, name ?? string.Empty, entries, IsNew: true));
        }

        var drafts = blueprint.Expand(
            new BlueprintSelection(
                selection.Folders,
                lists.ToDictionary(l => l.Key, l => l.Id),
                [.. selection.Rules.Select(r => new BlueprintRuleChoice(r.Key, r.Name, r.Parameters))]),
            out var expandErrors);
        foreach (var (path, message) in expandErrors)
        {
            errors[path] = [message];
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        var newLists = lists.Where(l => l.IsNew).ToDictionary(l => l.Id, l => l.Kind);
        foreach (var (key, draft) in drafts)
        {
            var ruleErrors = await rules.ValidateAsync(userId, ToDefinition(Guid.Empty, draft), PreviewEndpoints.ToTrigger(draft.Trigger), draft.Schedule, null, ct, newLists);
            foreach (var group in ruleErrors.GroupBy(e => e.Path))
            {
                errors[$"rules.{key}.{group.Key}"] = [.. group.Select(e => e.Message)];
            }
        }

        return errors.Count > 0 ? (null, errors) : (new Resolved(drafts, lists), errors);
    }

    private static RuleDefinition ToDefinition(Guid id, RuleDraft draft) =>
        new() { Id = id, Name = draft.Name, Conditions = draft.Conditions, Actions = draft.Actions };

    private sealed class Tally
    {
        public int Matched { get; set; }

        public int Sorted { get; set; }

        public int Blocked { get; set; }

        public List<AssistantExample> Examples { get; } = [];

        public void Add(MessageSnapshot message, string? targetFolder)
        {
            if (Examples.Count < MaxExamples)
            {
                Examples.Add(new AssistantExample(message.From?.Address, message.Subject, message.EffectiveDate, targetFolder));
            }
        }
    }
}
