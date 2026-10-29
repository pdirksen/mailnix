using System.Text.Json;
using Mailnix.Core.Messages;
using Mailnix.Core.Rules;
using Mailnix.Infrastructure.Data;
using Mailnix.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailnix.Worker;

public sealed record PendingAction(Guid RuleId, string RuleName, ActionType Type, string? TargetFolder, string? Keyword, bool DryRun)
{
    public PlannedAction ToPlanned() => new(RuleId, Type, TargetFolder, Keyword);
}

public sealed record PendingMessage(uint Uid, string? MessageId, string? From, string? Subject, List<string> PreviousFlags, List<PendingAction> Actions)
{
    public static PendingMessage Create(MessageSnapshot message, MessagePlan plan, IReadOnlyDictionary<Guid, RuleDefinition> rules)
    {
        string Name(Guid id) => rules.TryGetValue(id, out var rule) ? rule.Name : string.Empty;
        List<string> flags = [.. message.Keywords];
        if (message.IsSeen)
        {
            flags.Add(Flags.Seen);
        }

        if (message.IsFlagged)
        {
            flags.Add(Flags.Flagged);
        }

        return new PendingMessage(
            message.Uid,
            message.MessageId,
            message.From?.Address,
            message.Subject is { Length: > 300 } subject ? subject[..300] : message.Subject,
            flags,
            [
                .. plan.Actions.Select(a => new PendingAction(a.RuleId, Name(a.RuleId), a.Type, a.TargetFolder, a.Keyword, DryRun: false)),
                .. plan.DryRunActions.Select(a => new PendingAction(a.RuleId, Name(a.RuleId), a.Type, a.TargetFolder, a.Keyword, DryRun: true)),
            ]);
    }
}

/// <summary>Stored on runs awaiting confirmation; executed as planned once the user confirms.</summary>
public sealed record PendingPlan(uint UidValidity, List<PendingMessage> Messages);

internal static class Flags
{
    public const string Seen = "\\Seen";
    public const string Flagged = "\\Flagged";
}

/// <summary>Plans, gates, executes and logs rule runs on one mailbox session.</summary>
internal sealed partial class RunProcessor(
    MailnixDbContext db,
    RuleSetLoader loader,
    TrashPurger purger,
    StatsRecorder stats,
    ILiveNotifier notifier,
    IOptions<WorkerOptions> options,
    TimeProvider time,
    ILogger<RunProcessor> logger)
{
    private WorkerOptions Options => options.Value;

    /// <summary>
    /// Applies OnNew rules to messages that arrived in a watched folder since the last pass. On the first pass
    /// (or after the folder was recreated) existing mail is left alone: only mail arriving from now on is processed.
    /// </summary>
    public async Task ProcessNewMailAsync(IMailboxSession session, MailAccount account, string folder, CancellationToken ct)
    {
        // Read-only until a rule actually changes something (ExecuteAsync reopens the folder for writing).
        var status = await session.OpenAsync(folder, writable: false, ct);
        var state = await db.FolderStates.SingleOrDefaultAsync(f => f.AccountId == account.Id && f.Folder == folder, ct);
        var now = time.GetUtcNow();
        if (state is null || state.UidValidity != status.UidValidity)
        {
            if (state is null)
            {
                state = new FolderState { AccountId = account.Id, Folder = folder };
                db.FolderStates.Add(state);
            }

            state.UidValidity = status.UidValidity;
            state.LastProcessedUid = status.UidNext > 0 ? status.UidNext - 1 : await HighestUidAsync(session, folder, ct);
            state.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            return;
        }

        if (status.UidNext > 0 && status.UidNext - 1 <= state.LastProcessedUid)
        {
            return;
        }

        var loaded = await loader.LoadAsync(account, ct);
        var rules = loaded.AccountRules
            .Where(r => r.Enabled && loaded.Stored[r.Id].Trigger == RuleTrigger.OnNew && loaded.AppliesTo(r.Id, folder, account.WatchedFolders))
            .ToList();
        // Headers are always read (BODY.PEEK, the message stays unread): the stats need sender and trust details.
        var requirements = (rules.Count == 0 ? FetchRequirements.None : FieldCatalog.RequiredFetch(rules, loaded.Context.Rules)) | FetchRequirements.Headers;
        var messages = await session.FetchNewAsync(folder, state.LastProcessedUid, Options.MaxNewMessagesPerPass, requirements, ct);
        if (messages.Count == 0)
        {
            return;
        }

        // Messages mailnix itself just put here (rules, undo) are not new mail: neither processed again nor counted.
        var since = now - Options.LoopGuardWindow;
        var uids = messages.Select(m => m.Uid).ToList();
        var placedByUs = await db.ActionLog
            .Where(a => a.AccountId == account.Id && a.TargetFolder == folder && a.TargetUidValidity == status.UidValidity
                && a.TargetUid != null && uids.Contains(a.TargetUid.Value) && a.CreatedAt > since && a.Success && !a.DryRun)
            .Select(a => a.TargetUid!.Value)
            .ToListAsync(ct);
        var candidates = messages.Where(m => !placedByUs.Contains(m.Uid)).ToList();

        var actioned = new HashSet<uint>();
        if (rules.Count > 0)
        {
            var context = await ContextAsync(session, account, loaded, rules, ct);
            var planned = Plan(candidates, rules, context);
            actioned = [.. planned.Where(p => p.Pending.Actions.Any(a => !a.DryRun)).Select(p => p.Message.Uid)];
            if (planned.Any(p => p.Pending.Actions.Count > 0))
            {
                var run = NewRun(account, RunTrigger.NewMail, folder, RunState.Running);
                run.StartedAt = now;
                db.Runs.Add(run);
                await ApplyAsync(session, account, run, folder, status.UidValidity, planned, candidates.Count, gate: true, ct);
            }
        }

        await stats.NewMailAsync(account, [.. candidates.Select(m => (new MessageFacts(m, loaded.Context), actioned.Contains(m.Uid)))], ct);

        state.LastProcessedUid = messages.Max(m => m.Uid);
        state.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Runs left "Running" by a watcher that stopped or lost its connection mid-run. Only the watcher holding the
    /// account's lease runs its jobs, so when it (re)connects, nothing else works on them. A job from the queue that has
    /// logged no action yet starts over; the others are marked as interrupted (their logged actions stay undoable).
    /// </summary>
    public async Task RecoverInterruptedAsync(MailAccount account, CancellationToken ct)
    {
        var interrupted = await db.Runs.Where(r => r.AccountId == account.Id && r.State == RunState.Running).ToListAsync(ct);
        if (interrupted.Count == 0)
        {
            return;
        }

        foreach (var run in interrupted)
        {
            if (run.Trigger != RunTrigger.NewMail && !await db.ActionLog.AnyAsync(a => a.RunId == run.Id, ct))
            {
                run.State = RunState.Queued;
                run.StartedAt = null;
            }
            else
            {
                // New mail needs no run of its own: its messages are fetched again by the next pass.
                Fail(run, "Interrupted: mailnix stopped or lost the connection to the mail server during this run. The actions listed here were carried out.");
            }
        }

        await db.SaveChangesAsync(ct);
        LogRecovered(account.Id, interrupted.Count);
        foreach (var run in interrupted)
        {
            await NotifyAsync(account, run);
        }
    }

    /// <summary>Runs queued jobs of the account: manual and scheduled runs, confirmed runs, undo and Trash purges.</summary>
    public async Task ProcessQueuedAsync(IMailboxSession session, MailAccount account, UndoProcessor undo, CancellationToken ct)
    {
        await ExpirePendingAsync(account, ct);
        var queued = await db.Runs.Where(r => r.AccountId == account.Id && r.State == RunState.Queued).OrderBy(r => r.CreatedAt).ToListAsync(ct);
        foreach (var run in queued)
        {
            run.State = RunState.Running;
            run.StartedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await NotifyAsync(account, run);
            try
            {
                if (run.Trigger == RunTrigger.Undo)
                {
                    await undo.UndoAsync(session, account, run, ct);
                }
                else if (run.Trigger == RunTrigger.Purge)
                {
                    await purger.PurgeAsync(session, account, run, ct);
                }
                else if (run.PendingPlanJson is not null)
                {
                    await ExecuteConfirmedAsync(session, account, run, ct);
                }
                else
                {
                    await ExecuteRulesAsync(session, account, run, ct);
                }
            }
            catch (Mail.FolderNotFoundException ex)
            {
                Fail(run, ex.Message);
            }

            run.FinishedAt ??= time.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await NotifyAsync(account, run);
        }
    }

    /// <summary>
    /// Manual and scheduled runs over existing mail. The server preselects candidates (see <see cref="SearchPlanner"/>);
    /// the newest of them, up to the run's limit, are fetched and evaluated like new mail.
    /// </summary>
    private async Task ExecuteRulesAsync(IMailboxSession session, MailAccount account, RuleRun run, CancellationToken ct)
    {
        var status = await session.OpenAsync(run.Folder, writable: false, ct);
        var loaded = await loader.LoadAsync(account, ct);

        // Rules chosen by the user run even when switched off ("apply this rule to existing mail"); a scheduled
        // rule that was switched off since its run was queued does not.
        var rules = run.RuleIds is { Count: > 0 } chosen
            ? loaded.AccountRules.Where(r => chosen.Contains(r.Id) && (run.Trigger != RunTrigger.Scheduled || r.Enabled)).Select(r => r with { Enabled = true }).ToList()
            : loaded.AccountRules.Where(r => r.Enabled && loaded.AppliesTo(r.Id, run.Folder, account.WatchedFolders)).ToList();
        if (rules.All(r => r.Actions.Count == 0))
        {
            Complete(run);
            return;
        }

        var context = await ContextAsync(session, account, loaded, rules, ct);
        var filter = Options.SearchPushdown ? SearchPlanner.Plan(rules, context) : null;
        var search = await session.SearchAsync(run.Folder, filter, ct);
        if (filter is not null && search.Filtered)
        {
            var described = filter.Describe();
            run.SearchFilter = described.Length > 2000 ? described[..1999] + "…" : described;
        }

        var limit = run.Trigger == RunTrigger.Scheduled
            ? Options.MaxScheduledRunMessages
            : Math.Clamp(run.Limit ?? 100, 1, Options.MaxManualRunMessages);
        var requirements = FieldCatalog.RequiredFetch(rules, loaded.Context.Rules);
        var messages = new List<MessageSnapshot>();
        foreach (var batch in search.Uids.OrderDescending().Take(limit).Chunk(Options.FetchBatchSize))
        {
            messages.AddRange(await session.FetchAsync(run.Folder, batch, requirements, ct));
        }

        await ApplyAsync(session, account, run, run.Folder, status.UidValidity, Plan(messages, rules, context), messages.Count, gate: true, ct);
    }

    private async Task ExecuteConfirmedAsync(IMailboxSession session, MailAccount account, RuleRun run, CancellationToken ct)
    {
        var plan = JsonSerializer.Deserialize<PendingPlan>(run.PendingPlanJson!, RuleJson.Options)!;
        var status = await session.OpenAsync(run.Folder, writable: false, ct);
        if (status.UidValidity != plan.UidValidity)
        {
            Fail(run, "The folder was recreated on the server since the run was planned; nothing was changed.");
            return;
        }

        // Re-check that each message is still there (and still the same message) before acting on it.
        var present = (await session.FetchAsync(run.Folder, [.. plan.Messages.Select(m => m.Uid)], FetchRequirements.None, ct))
            .Where(s => plan.Messages.First(m => m.Uid == s.Uid).MessageId is not { } id || s.MessageId == id)
            .ToDictionary(s => s.Uid);
        var gone = plan.Messages.Where(m => !present.ContainsKey(m.Uid)).ToList();
        var missing = new List<ActionLogEntry>();
        foreach (var message in gone)
        {
            foreach (var action in message.Actions.Where(a => !a.DryRun))
            {
                missing.Add(Entry(run, account, run.Folder, plan.UidValidity, message, action, success: false, "The message is no longer in the folder."));
                run.FailedActions++;
            }
        }

        db.ActionLog.AddRange(missing);
        await stats.ActionsAsync(account, missing, ct);

        var remaining = plan.Messages.Where(m => present.ContainsKey(m.Uid)).Select(m => (present[m.Uid], m)).ToList();
        await ExecuteAndLogAsync(session, account, run, run.Folder, plan.UidValidity, remaining, ct);
        run.PendingPlanJson = null;
        Complete(run);
    }

    /// <summary>Executes a plan, or parks it for confirmation when it would move more messages than the account allows.</summary>
    private async Task ApplyAsync(
        IMailboxSession session,
        MailAccount account,
        RuleRun run,
        string folder,
        uint uidValidity,
        List<(MessageSnapshot Message, PendingMessage Pending)> planned,
        int examined,
        bool gate,
        CancellationToken ct)
    {
        var withActions = planned.Where(p => p.Pending.Actions.Count > 0).ToList();
        run.MessagesExamined = examined;
        run.MessagesMatched = withActions.Count;
        run.PlannedActions = withActions.Sum(p => p.Pending.Actions.Count(a => !a.DryRun));

        var moving = withActions.Count(p => p.Pending.Actions.Any(a => !a.DryRun && IsTerminal(a.Type)));
        if (gate && moving > account.BlastRadiusLimit)
        {
            run.State = RunState.AwaitingConfirmation;
            run.PendingPlanJson = JsonSerializer.Serialize(new PendingPlan(uidValidity, [.. withActions.Select(p => p.Pending)]), RuleJson.Options);
            run.ExpiresAt = time.GetUtcNow() + Options.ConfirmationExpiry;
            LogAwaitingConfirmation(account.Id, moving, account.BlastRadiusLimit);
            await db.SaveChangesAsync(ct);
            await NotifyAsync(account, run);
            return;
        }

        await ExecuteAndLogAsync(session, account, run, folder, uidValidity, withActions, ct);
        Complete(run);
        await NotifyAsync(account, run);
    }

    private async Task ExecuteAndLogAsync(
        IMailboxSession session,
        MailAccount account,
        RuleRun run,
        string folder,
        uint uidValidity,
        List<(MessageSnapshot Message, PendingMessage Pending)> work,
        CancellationToken ct)
    {
        var logged = new List<ActionLogEntry>();
        foreach (var (_, pending) in work)
        {
            foreach (var action in pending.Actions.Where(a => a.DryRun))
            {
                logged.Add(Entry(run, account, folder, uidValidity, pending, action, success: true, null, dryRun: true));
            }
        }

        var live = work
            .Select(w => new MessageActions(w.Message, [.. w.Pending.Actions.Where(a => !a.DryRun).Select(a => a.ToPlanned())]))
            .Where(w => w.Actions.Count > 0)
            .ToList();
        if (live.Count > 0)
        {
            var outcomes = await session.ExecuteAsync(folder, live, ct);
            var byUid = work.ToDictionary(w => w.Message.Uid, w => w.Pending);
            foreach (var outcome in outcomes)
            {
                var pending = byUid[outcome.Uid];
                var action = pending.Actions.First(a => !a.DryRun && a.ToPlanned() == outcome.Action);
                logged.Add(Entry(run, account, folder, uidValidity, pending, action, outcome.Success, outcome.Error, false, outcome.TargetUid, outcome.TargetUidValidity));
                if (outcome.Success)
                {
                    run.ExecutedActions++;
                }
                else
                {
                    run.FailedActions++;
                }
            }
        }

        db.ActionLog.AddRange(logged);
        await stats.ActionsAsync(account, logged, ct);
    }

    private static List<(MessageSnapshot Message, PendingMessage Pending)> Plan(IReadOnlyList<MessageSnapshot> messages, IReadOnlyList<RuleDefinition> rules, EvaluationContext context) =>
        [.. messages.Select(m => (m, PendingMessage.Create(m, ActionPlanner.Plan(rules, m, context), context.Rules)))];

    private static async Task<EvaluationContext> ContextAsync(IMailboxSession session, MailAccount account, LoadedRuleSet loaded, IReadOnlyList<RuleDefinition> rules, CancellationToken ct)
    {
        var context = loaded.Context with { FolderDelimiter = session.Delimiter };
        if (FieldCatalog.UsedFields(rules, loaded.Context.Rules).Contains(ConditionField.IsReplyToMyMail) && account.SpecialFolders.Sent is { } sent)
        {
            context = context with { SentMessageIds = await session.MessageIdsAsync(sent, 500, ct) };
        }

        return context;
    }

    private async Task ExpirePendingAsync(MailAccount account, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var expired = await db.Runs.Where(r => r.AccountId == account.Id && r.State == RunState.AwaitingConfirmation && r.ExpiresAt < now).ToListAsync(ct);
        foreach (var run in expired)
        {
            run.State = RunState.Expired;
            run.PendingPlanJson = null;
            run.FinishedAt = now;
        }

        if (expired.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            foreach (var run in expired)
            {
                await NotifyAsync(account, run);
            }
        }
    }

    private static async Task<uint> HighestUidAsync(IMailboxSession session, string folder, CancellationToken ct) =>
        (await session.FetchLatestAsync(folder, 1, FetchRequirements.None, ct)) is [var latest, ..] ? latest.Uid : 0;

    internal static bool IsTerminal(ActionType type) => type is ActionType.MoveTo or ActionType.Delete or ActionType.MarkAsSpam or ActionType.Archive;

    private RuleRun NewRun(MailAccount account, RunTrigger trigger, string folder, RunState state) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = account.TenantId,
        UserId = account.UserId,
        AccountId = account.Id,
        Trigger = trigger,
        State = state,
        Folder = folder,
        CreatedAt = time.GetUtcNow(),
    };

    private ActionLogEntry Entry(
        RuleRun run,
        MailAccount account,
        string folder,
        uint uidValidity,
        PendingMessage message,
        PendingAction action,
        bool success,
        string? error,
        bool dryRun = false,
        uint? targetUid = null,
        uint? targetUidValidity = null) => new()
        {
            Id = Guid.CreateVersion7(),
            RunId = run.Id,
            AccountId = account.Id,
            UserId = account.UserId,
            RuleId = action.RuleId == Guid.Empty ? null : action.RuleId,
            RuleName = action.RuleName,
            Action = action.Type,
            Keyword = action.Keyword,
            SourceFolder = folder,
            SourceUid = message.Uid,
            SourceUidValidity = uidValidity,
            TargetFolder = action.TargetFolder,
            TargetUid = targetUid,
            TargetUidValidity = targetUidValidity,
            MessageId = message.MessageId,
            From = message.From,
            Subject = message.Subject,
            PreviousFlags = message.PreviousFlags,
            DryRun = dryRun,
            Success = success,
            Error = error is { Length: > 1000 } ? error[..1000] : error,
            CreatedAt = time.GetUtcNow(),
        };

    private void Complete(RuleRun run)
    {
        run.State = run.FailedActions > 0 ? RunState.CompletedWithErrors : RunState.Completed;
        run.FinishedAt = time.GetUtcNow();
    }

    private void Fail(RuleRun run, string error)
    {
        run.State = RunState.Failed;
        run.Error = error;
        run.PendingPlanJson = null;
        run.FinishedAt = time.GetUtcNow();
    }

    private Task NotifyAsync(MailAccount account, RuleRun run) =>
        notifier.NotifyAsync(account.UserId, LiveEvents.RunUpdated, new RunEvent(run.Id, account.Id, run.State, run.Trigger));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Account {AccountId}: {Count} runs were interrupted and have been queued again or marked as failed")]
    private partial void LogRecovered(Guid accountId, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run for account {AccountId} would move {Count} messages (threshold {Threshold}); waiting for confirmation")]
    private partial void LogAwaitingConfirmation(Guid accountId, int count, int threshold);
}
