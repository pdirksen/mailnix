using System.ComponentModel;
using System.Security.Claims;
using Mailnix.Api.Accounts;
using Mailnix.Api.Hosting;
using Mailnix.Api.Lists;
using Mailnix.Api.Rules;
using Mailnix.Api.Runs;
using Mailnix.Api.Stats;
using Mailnix.Core.Rules;
using Mailnix.Infrastructure.Data;
using Mailnix.Infrastructure.OAuth;
using Mailnix.Mail;
using Mailnix.Worker;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mailnix.Api.Mcp;

/// <summary>How a rule created or changed through MCP behaves until the user switches it live in the app.</summary>
public enum DraftMode
{
    /// <summary>Does nothing until enabled in the app.</summary>
    Disabled,

    /// <summary>Runs on new mail but only records what it would do ("dry run").</summary>
    DryRun,
}

/// <summary>
/// The MCP tools. They call the same handlers as the HTTP API, with the token's user, so the checks, the tenant
/// filter and the safety model are the same as in the app. Write tools need the read-write scope; none of them can
/// switch a rule live, confirm a capped run, delete anything or touch credentials.
/// </summary>
[McpServerToolType]
internal sealed class MailnixTools(IServiceProvider services, MailnixDbContext db, McpLimits limits, TimeProvider time)
{
    private const string RuleFormat =
        "Conditions are a tree: {\"kind\":\"group\",\"operator\":\"All|Any|None\",\"children\":[...]} or " +
        "{\"kind\":\"condition\",\"field\":\"FromDomain\",\"operator\":\"EndsWith\",\"value\":\"example.com\"}. " +
        "Actions: e.g. [{\"type\":\"MoveTo\",\"folder\":\"Archive/{year}\"},{\"type\":\"MarkRead\"}]. " +
        "get_field_catalog lists the fields, their operators and the folder placeholders.";

    // ---- Read ----

    [McpServerTool(Name = "list_accounts", ReadOnly = true, Idempotent = true)]
    [Description("Lists the user's mail accounts with their status, watched folders, special folders and confirmation threshold.")]
    public async Task<List<AccountResponse>> ListAccounts(RequestContext<CallToolRequestParams> request, CancellationToken ct) =>
        McpResults.Value<List<AccountResponse>>(await AccountEndpoints.List(Caller(request), db, ct));

    [McpServerTool(Name = "list_folders", ReadOnly = true, Idempotent = true)]
    [Description("Lists the folders of a mail account (asks the mail server), with message counts.")]
    public async Task<FolderListResponse> ListFolders(RequestContext<CallToolRequestParams> request, Guid accountId, CancellationToken ct)
    {
        var user = Caller(request);
        limits.MailConnection(user);
        return McpResults.Value<FolderListResponse>(await AccountEndpoints.Folders(
            accountId, user, db, services.GetRequiredService<IMailboxProbe>(), services.GetRequiredService<AccountCredentials>(), time, ct));
    }

    [McpServerTool(Name = "list_rules", ReadOnly = true, Idempotent = true)]
    [Description("Lists rules in priority order, with conditions, actions and state (Enabled, DryRun). A rule is live when it is enabled and not a dry run.")]
    public async Task<List<RuleResponse>> ListRules(
        RequestContext<CallToolRequestParams> request,
        [Description("Only this account's rules.")] Guid? accountId = null,
        CancellationToken ct = default) =>
        McpResults.Value<List<RuleResponse>>(await RuleEndpoints.List(Caller(request), db, ct, accountId));

    [McpServerTool(Name = "get_rule", ReadOnly = true, Idempotent = true)]
    [Description("Gets one rule with its conditions, actions and the rules that reference it.")]
    public async Task<RuleResponse> GetRule(RequestContext<CallToolRequestParams> request, Guid ruleId, CancellationToken ct) =>
        McpResults.Value<RuleResponse>(await RuleEndpoints.Get(ruleId, Caller(request), db, ct));

    [McpServerTool(Name = "get_field_catalog", ReadOnly = true, Idempotent = true)]
    [Description("Describes every condition field (type, allowed operators, category) and the placeholders folder names may use, e.g. {year} or {fromDomain}.")]
    public FieldCatalogResponse GetFieldCatalog(RequestContext<CallToolRequestParams> request)
    {
        Caller(request);
        return McpResults.Value<FieldCatalogResponse>(RuleEndpoints.FieldCatalogInfo());
    }

    [McpServerTool(Name = "list_named_lists", ReadOnly = true, Idempotent = true)]
    [Description("Lists the user's named lists (addresses, domains or patterns). Conditions use them with the operator InList and the list's id as value.")]
    public async Task<List<ListSummaryResponse>> ListNamedLists(RequestContext<CallToolRequestParams> request, CancellationToken ct) =>
        McpResults.Value<List<ListSummaryResponse>>(await ListEndpoints.List(Caller(request), db, ct));

    [McpServerTool(Name = "list_rule_templates", ReadOnly = true, Idempotent = true)]
    [Description("Lists the built-in rule templates and their parameters.")]
    public List<RuleTemplateInfo> ListRuleTemplates(RequestContext<CallToolRequestParams> request)
    {
        Caller(request);
        return McpResults.Value<List<RuleTemplateInfo>>(PreviewEndpoints.ListTemplates());
    }

    [McpServerTool(Name = "draft_rule_from_template", ReadOnly = true, Idempotent = true)]
    [Description("Fills in a rule template. Returns conditions and actions to preview or create; saves nothing.")]
    public async Task<RuleDraftResponse> DraftRuleFromTemplate(
        RequestContext<CallToolRequestParams> request,
        string templateId,
        [Description("Name for the rule.")] string name,
        [Description("Template parameters by name (see list_rule_templates).")] Dictionary<string, string>? parameters = null,
        CancellationToken ct = default)
    {
        var user = Caller(request);
        var draft = new RuleDraftRequest(name, parameters);
        await McpValidation.ValidateAsync(draft, services, ct);
        return McpResults.Value<RuleDraftResponse>(await PreviewEndpoints.Draft(templateId, draft, user, db, ct));
    }

    [McpServerTool(Name = "preview_rule", ReadOnly = true)]
    [Description(
        "Tries rule conditions and actions on the newest messages of a folder without changing anything, and shows " +
        "per message whether it matches and what would happen, including what other live rules would do. " + RuleFormat)]
    public async Task<PreviewResponse> PreviewRule(
        RequestContext<CallToolRequestParams> request,
        Guid accountId,
        ConditionNode conditions,
        IReadOnlyList<RuleAction> actions,
        [Description("Folder to sample; default: the account's first watched folder.")] string? folder = null,
        [Description("How many of the newest messages to check (1-500).")] int limit = 50,
        [Description("When previewing a change to an existing rule: its id, so it is not counted twice.")] Guid? ruleId = null,
        bool stopProcessing = false,
        [Description("Include the per-condition evaluation trace of every message (verbose).")] bool includeTrace = false,
        CancellationToken ct = default)
    {
        var user = Caller(request);
        var preview = new PreviewRequest(conditions, actions, ruleId, null, stopProcessing, folder, limit);
        await McpValidation.ValidateAsync(preview, services, ct);
        limits.Preview(user);
        var result = McpResults.Value<PreviewResponse>(await PreviewEndpoints.Preview(
            accountId,
            preview,
            user,
            db,
            services.GetRequiredService<RuleService>(),
            services.GetRequiredService<RuleSetLoader>(),
            services.GetRequiredService<MessageSampler>(),
            services.GetRequiredService<AccountCredentials>(),
            ct));
        return includeTrace ? result : result with { Messages = [.. result.Messages.Select(m => m with { Trace = null })] };
    }

    [McpServerTool(Name = "get_stats_overview", ReadOnly = true, Idempotent = true)]
    [Description("Today's and recent numbers: processed and actioned messages, pending confirmations, account health and hints.")]
    public async Task<OverviewResponse> GetStatsOverview(RequestContext<CallToolRequestParams> request, Guid? accountId = null, CancellationToken ct = default) =>
        McpResults.Value<OverviewResponse>(await StatsEndpoints.Overview(Caller(request), db, ct, accountId));

    [McpServerTool(Name = "list_activity", ReadOnly = true, Idempotent = true)]
    [Description("The latest actions rules took on messages (newest first), optionally for one account or rule. Page with 'before' (the last id of the previous page).")]
    public async Task<ActionPage> ListActivity(
        RequestContext<CallToolRequestParams> request,
        Guid? accountId = null,
        Guid? ruleId = null,
        [Description("Only failed actions.")] bool failedOnly = false,
        Guid? before = null,
        [Description("1-200.")] int limit = 50,
        CancellationToken ct = default) =>
        McpResults.Value<ActionPage>(await ActivityEndpoints.Actions(
            Caller(request), db, ct, accountId, ruleId, failedOnly: failedOnly, before: before, limit: Math.Clamp(limit, 1, 200)));

    [McpServerTool(Name = "list_runs", ReadOnly = true, Idempotent = true)]
    [Description("Lists recent runs (new mail, manual, scheduled, undo), newest first, with counts and state. Runs in state AwaitingConfirmation wait for the user in the app.")]
    public async Task<List<RunSummary>> ListRuns(
        RequestContext<CallToolRequestParams> request,
        Guid? accountId = null,
        RunState? state = null,
        [Description("1-200.")] int limit = 20,
        CancellationToken ct = default) =>
        McpResults.Value<List<RunSummary>>(await RunEndpoints.List(Caller(request), db, ct, accountId, state, Math.Clamp(limit, 1, 200)));

    [McpServerTool(Name = "get_run", ReadOnly = true, Idempotent = true)]
    [Description("Gets a run with its logged actions, or for a run awaiting confirmation the planned ones.")]
    public async Task<RunDetail> GetRun(RequestContext<CallToolRequestParams> request, Guid runId, CancellationToken ct) =>
        McpResults.Value<RunDetail>(await RunEndpoints.Get(runId, Caller(request), db, ct));

    // ---- Write ----

    [McpServerTool(Name = "create_rule", Destructive = false)]
    [Authorize(Policy = McpPolicies.Write)]
    [Description(
        "Creates a rule as a draft: disabled, or as a dry run that only records what it would do. Only the user can " +
        "switch it live, in the app. Preview it first with preview_rule. " + RuleFormat)]
    public async Task<RuleResponse> CreateRule(
        RequestContext<CallToolRequestParams> request,
        Guid accountId,
        string name,
        ConditionNode conditions,
        IReadOnlyList<RuleAction> actions,
        string? description = null,
        DraftMode mode = DraftMode.Disabled,
        [Description("Later rules are not evaluated for messages this rule matches.")] bool stopProcessing = false,
        [Description("OnNew (new mail in the source folders), Scheduled (with a cron schedule) or Manual.")] RuleTrigger trigger = RuleTrigger.OnNew,
        [Description("Cron expression for Scheduled rules, e.g. \"0 3 * * *\".")] string? schedule = null,
        [Description("Folders the rule works on; default: the account's watched folders.")] IReadOnlyList<string>? sourceFolders = null,
        CancellationToken ct = default)
    {
        var user = Caller(request);
        var create = new CreateRuleRequest(
            accountId, name, conditions, actions, description, Enabled: mode == DraftMode.DryRun, stopProcessing, DryRun: mode == DraftMode.DryRun, trigger, schedule, sourceFolders);
        await McpValidation.ValidateAsync(create, services, ct);
        var rule = McpResults.Value<RuleResponse>(await RuleEndpoints.Create(create, user, db, services.GetRequiredService<RuleService>(), time, ct));
        return rule with { Origin = await MarkRuleAsync(rule.Id, user, ct) };
    }

    [McpServerTool(Name = "update_rule", Destructive = false, Idempotent = true)]
    [Authorize(Policy = McpPolicies.Write)]
    [Description(
        "Replaces a draft rule (disabled or dry run) with new settings; it stays a draft. Live rules cannot be " +
        "changed here: the user changes them in the app. " + RuleFormat)]
    public async Task<RuleResponse> UpdateRule(
        RequestContext<CallToolRequestParams> request,
        Guid ruleId,
        string name,
        ConditionNode conditions,
        IReadOnlyList<RuleAction> actions,
        string? description = null,
        DraftMode mode = DraftMode.Disabled,
        bool stopProcessing = false,
        RuleTrigger trigger = RuleTrigger.OnNew,
        string? schedule = null,
        IReadOnlyList<string>? sourceFolders = null,
        CancellationToken ct = default)
    {
        var user = Caller(request);
        var userId = user.GetUserId();
        var existing = await db.Rules.AsNoTracking().Where(r => r.Id == ruleId && r.UserId == userId).Select(r => new { r.Enabled, r.DryRun }).SingleOrDefaultAsync(ct)
            ?? throw new McpException("Rule not found.");
        if (existing.Enabled && !existing.DryRun)
        {
            throw new McpException("This rule is live. Live rules can only be changed in the mailnix app.");
        }

        var update = new UpdateRuleRequest(name, conditions, actions, description, Enabled: mode == DraftMode.DryRun, stopProcessing, DryRun: mode == DraftMode.DryRun, trigger, schedule, sourceFolders);
        await McpValidation.ValidateAsync(update, services, ct);
        var rule = McpResults.Value<RuleResponse>(await RuleEndpoints.Update(ruleId, update, user, db, services.GetRequiredService<RuleService>(), time, ct));
        return rule with { Origin = await MarkRuleAsync(rule.Id, user, ct) };
    }

    [McpServerTool(Name = "start_run", Destructive = true)]
    [Authorize(Policy = McpPolicies.Write)]
    [Description(
        "Applies the account's live rules (or only the given ones) to the newest messages of a folder: messages are " +
        "really moved, flagged, etc. Dry-run rules only record. If the run would move more messages than the " +
        "account's threshold, it stops as AwaitingConfirmation and the user decides in the app. Everything can be undone with undo_run.")]
    public async Task<RunSummary> StartRun(
        RequestContext<CallToolRequestParams> request,
        Guid accountId,
        [Description("Default: the account's first watched folder.")] string? folder = null,
        [Description("How many of the newest messages (1-1000).")] int limit = 100,
        [Description("Only these rules.")] IReadOnlyList<Guid>? ruleIds = null,
        CancellationToken ct = default)
    {
        var user = Caller(request);
        var start = new StartRunRequest(folder, limit, ruleIds);
        await McpValidation.ValidateAsync(start, services, ct);
        limits.MailConnection(user);
        var run = McpResults.Value<RunSummary>(await RunEndpoints.Start(accountId, start, user, db, services.GetRequiredService<IWorkerSignals>(), time, ct));
        return run with { Origin = await MarkRunAsync(run.Id, user, ct) };
    }

    [McpServerTool(Name = "cancel_run", Destructive = false, Idempotent = true)]
    [Authorize(Policy = McpPolicies.Write)]
    [Description("Cancels a run that is queued or awaiting confirmation.")]
    public async Task<RunSummary> CancelRun(RequestContext<CallToolRequestParams> request, Guid runId, CancellationToken ct) =>
        McpResults.Value<RunSummary>(await RunEndpoints.Cancel(runId, Caller(request), db, time, ct));

    [McpServerTool(Name = "undo_run", Destructive = true)]
    [Authorize(Policy = McpPolicies.Write)]
    [Description("Reverts everything a completed run did (moves messages back, restores flags). Runs as a new undo run.")]
    public async Task<RunSummary> UndoRun(RequestContext<CallToolRequestParams> request, Guid runId, CancellationToken ct)
    {
        var user = Caller(request);
        var run = McpResults.Value<RunSummary>(await RunEndpoints.Undo(runId, user, db, services.GetRequiredService<IWorkerSignals>(), time, ct));
        return run with { Origin = await MarkRunAsync(run.Id, user, ct) };
    }

    [McpServerTool(Name = "undo_rule", Destructive = true)]
    [Authorize(Policy = McpPolicies.Write)]
    [Description("Reverts a rule's most recent actions (across runs). Runs as a new undo run.")]
    public async Task<RunSummary> UndoRule(
        RequestContext<CallToolRequestParams> request,
        Guid ruleId,
        [Description("How many of the latest actions (1-1000).")] int count = 10,
        CancellationToken ct = default)
    {
        var user = Caller(request);
        var undo = new UndoRuleRequest(count);
        await McpValidation.ValidateAsync(undo, services, ct);
        var run = McpResults.Value<RunSummary>(await RunEndpoints.UndoRule(ruleId, undo, user, db, services.GetRequiredService<IWorkerSignals>(), time, ct));
        return run with { Origin = await MarkRunAsync(run.Id, user, ct) };
    }

    /// <summary>
    /// The token's user. The SDK runs each call in its own DI scope, so the tenant filter is set here on this
    /// scope's DbContext rather than relying on the request middleware.
    /// </summary>
    private ClaimsPrincipal Caller(RequestContext<CallToolRequestParams> request)
    {
        var user = request.User;
        if (user?.FindFirstValue(McpClaims.Scope) is null
            || !Guid.TryParse(user.FindFirstValue(MailnixClaimsPrincipalFactory.TenantClaim), out var tenantId))
        {
            throw new McpException("Not signed in.");
        }

        db.TenantFilter = tenantId;
        return user;
    }

    private async Task<string> MarkRuleAsync(Guid ruleId, ClaimsPrincipal user, CancellationToken ct)
    {
        var origin = Origin(user);
        await db.Rules.Where(r => r.Id == ruleId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Origin, origin), ct);
        return origin;
    }

    private async Task<string> MarkRunAsync(Guid runId, ClaimsPrincipal user, CancellationToken ct)
    {
        var origin = Origin(user);
        await db.Runs.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Origin, origin), ct);
        return origin;
    }

    private static string Origin(ClaimsPrincipal user) => user.FindFirstValue(McpClaims.Origin) ?? "MCP";
}
