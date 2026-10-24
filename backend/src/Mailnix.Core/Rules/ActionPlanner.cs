using Mailnix.Core.Messages;

namespace Mailnix.Core.Rules;

public sealed record PlannedAction(Guid RuleId, ActionType Type, string? TargetFolder = null, string? Keyword = null)
{
    public bool IsTerminal => Type is ActionType.MoveTo or ActionType.Delete or ActionType.MarkAsSpam or ActionType.Archive;
}

public enum SkipReason
{
    /// <summary>An earlier rule (by priority) already moves the message.</summary>
    TerminalActionAlreadyPlanned,

    /// <summary>An earlier rule does the opposite (e.g. mark read vs. mark unread).</summary>
    ContradictsEarlierAction,

    /// <summary>An earlier rule already does the same.</summary>
    Duplicate,

    /// <summary>The message is already in the target folder.</summary>
    AlreadyInTargetFolder,

    /// <summary>The message already has this state (e.g. mark read on a read message).</summary>
    NoChange,

    /// <summary>Delete/spam/archive without the corresponding special folder configured on the account.</summary>
    SpecialFolderNotConfigured,
}

public sealed record SkippedAction(Guid RuleId, ActionType Type, string? TargetFolder, SkipReason Reason, Guid? BlockedByRuleId = null);

public sealed record RuleOutcome(Guid RuleId, string Name, bool Evaluated, bool Matched, bool DryRun, TraceNode? Trace);

/// <summary>What would happen to one message: actions in execution order (terminal action last).</summary>
public sealed record MessagePlan(
    IReadOnlyList<RuleOutcome> Rules,
    IReadOnlyList<PlannedAction> Actions,
    IReadOnlyList<SkippedAction> Skipped,
    IReadOnlyList<PlannedAction> DryRunActions)
{
    public bool AnyRuleMatched => Rules.Any(r => r.Matched);

    public PlannedAction? TerminalAction => Actions.FirstOrDefault(a => a.IsTerminal);
}

/// <summary>
/// Turns matching rules into actions for one message:
/// rules run in priority order; non-terminal actions of all matching rules are merged; only the first terminal
/// action applies and later ones are reported as skipped; StopProcessing ends evaluation. Dry-run rules are
/// planned separately and never influence the live plan.
/// </summary>
public static class ActionPlanner
{
    public static MessagePlan Plan(IEnumerable<RuleDefinition> rules, MessageSnapshot message, EvaluationContext context, bool trace = false)
    {
        var facts = new MessageFacts(message, context);
        var session = new EvaluationSession(facts, context, trace);
        var live = new Builder(facts, context);
        var dryRun = new List<PlannedAction>();
        var outcomes = new List<RuleOutcome>();
        var stopped = false;

        foreach (var rule in rules.Where(r => r.Enabled).OrderBy(r => r.Priority).ThenBy(r => r.Name, StringComparer.Ordinal))
        {
            if (stopped)
            {
                outcomes.Add(new RuleOutcome(rule.Id, rule.Name, Evaluated: false, Matched: false, rule.DryRun, null));
                continue;
            }

            var evaluation = session.EvaluateRule(rule);
            outcomes.Add(new RuleOutcome(rule.Id, rule.Name, Evaluated: true, evaluation.Matched, rule.DryRun, evaluation.Trace));
            if (!evaluation.Matched)
            {
                continue;
            }

            if (rule.DryRun)
            {
                var alone = new Builder(facts, context);
                alone.Add(rule);
                dryRun.AddRange(alone.Actions);
                continue;
            }

            live.Add(rule);
            stopped = rule.StopProcessing;
        }

        return new MessagePlan(outcomes, live.Actions, live.Skipped, dryRun);
    }

    private sealed class Builder(MessageFacts facts, EvaluationContext context)
    {
        private readonly List<PlannedAction> _nonTerminal = [];
        private readonly List<SkippedAction> _skipped = [];
        private PlannedAction? _terminal;

        public IReadOnlyList<PlannedAction> Actions => _terminal is null ? _nonTerminal : [.. _nonTerminal, _terminal];

        public IReadOnlyList<SkippedAction> Skipped => _skipped;

        private MessageSnapshot Message => facts.Message;

        public void Add(RuleDefinition rule)
        {
            foreach (var action in rule.Actions)
            {
                AddAction(rule.Id, action);
            }
        }

        private void AddAction(Guid ruleId, RuleAction action)
        {
            switch (action)
            {
                case MarkReadAction:
                    AddState(ruleId, ActionType.MarkRead, ActionType.MarkUnread, alreadySet: Message.IsSeen);
                    break;
                case MarkUnreadAction:
                    AddState(ruleId, ActionType.MarkUnread, ActionType.MarkRead, alreadySet: !Message.IsSeen);
                    break;
                case FlagAction:
                    AddState(ruleId, ActionType.Flag, ActionType.Unflag, alreadySet: Message.IsFlagged);
                    break;
                case UnflagAction:
                    AddState(ruleId, ActionType.Unflag, ActionType.Flag, alreadySet: !Message.IsFlagged);
                    break;
                case AddKeywordAction add:
                    AddState(ruleId, ActionType.AddKeyword, ActionType.RemoveKeyword, HasKeyword(add.Keyword), add.Keyword);
                    break;
                case RemoveKeywordAction remove:
                    AddState(ruleId, ActionType.RemoveKeyword, ActionType.AddKeyword, !HasKeyword(remove.Keyword), remove.Keyword);
                    break;
                case CopyToAction copy:
                    AddCopy(ruleId, FolderTemplate.Resolve(copy.Folder, facts, context));
                    break;
                case MoveToAction move:
                    AddTerminal(ruleId, ActionType.MoveTo, FolderTemplate.Resolve(move.Folder, facts, context));
                    break;
                case DeleteAction:
                    AddTerminal(ruleId, ActionType.Delete, context.SpecialFolders.Trash);
                    break;
                case MarkAsSpamAction:
                    AddTerminal(ruleId, ActionType.MarkAsSpam, context.SpecialFolders.Junk);
                    break;
                case ArchiveAction:
                    AddTerminal(ruleId, ActionType.Archive, context.SpecialFolders.Archive);
                    break;
            }
        }

        private bool HasKeyword(string keyword) => Message.Keywords.Contains(keyword, StringComparer.OrdinalIgnoreCase);

        private void AddState(Guid ruleId, ActionType type, ActionType opposite, bool alreadySet, string? keyword = null)
        {
            bool SameKeyword(PlannedAction a) => keyword is null || string.Equals(a.Keyword, keyword, StringComparison.OrdinalIgnoreCase);

            if (_nonTerminal.FirstOrDefault(a => a.Type == opposite && SameKeyword(a)) is { } contradicting)
            {
                _skipped.Add(new SkippedAction(ruleId, type, null, SkipReason.ContradictsEarlierAction, contradicting.RuleId));
            }
            else if (_nonTerminal.FirstOrDefault(a => a.Type == type && SameKeyword(a)) is { } duplicate)
            {
                _skipped.Add(new SkippedAction(ruleId, type, null, SkipReason.Duplicate, duplicate.RuleId));
            }
            else if (alreadySet)
            {
                _skipped.Add(new SkippedAction(ruleId, type, null, SkipReason.NoChange));
            }
            else
            {
                _nonTerminal.Add(new PlannedAction(ruleId, type, Keyword: keyword));
            }
        }

        private void AddCopy(Guid ruleId, string target)
        {
            if (target.Equals(Message.Folder, StringComparison.Ordinal))
            {
                _skipped.Add(new SkippedAction(ruleId, ActionType.CopyTo, target, SkipReason.AlreadyInTargetFolder));
            }
            else if (_nonTerminal.FirstOrDefault(a => a.Type == ActionType.CopyTo && a.TargetFolder == target) is { } duplicate)
            {
                _skipped.Add(new SkippedAction(ruleId, ActionType.CopyTo, target, SkipReason.Duplicate, duplicate.RuleId));
            }
            else
            {
                _nonTerminal.Add(new PlannedAction(ruleId, ActionType.CopyTo, target));
            }
        }

        private void AddTerminal(Guid ruleId, ActionType type, string? target)
        {
            if (target is null)
            {
                _skipped.Add(new SkippedAction(ruleId, type, null, SkipReason.SpecialFolderNotConfigured));
            }
            else if (_terminal is not null)
            {
                _skipped.Add(new SkippedAction(ruleId, type, target, SkipReason.TerminalActionAlreadyPlanned, _terminal.RuleId));
            }
            else if (target.Equals(Message.Folder, StringComparison.Ordinal))
            {
                // Never "move" a message to where it already is: that would re-trigger rules on that folder forever.
                _skipped.Add(new SkippedAction(ruleId, type, target, SkipReason.AlreadyInTargetFolder));
            }
            else
            {
                _terminal = new PlannedAction(ruleId, type, target);
            }
        }
    }
}
