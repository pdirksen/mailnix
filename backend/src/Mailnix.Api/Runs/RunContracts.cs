using System.ComponentModel.DataAnnotations;
using Mailnix.Core.Rules;
using Mailnix.Infrastructure.Data;

namespace Mailnix.Api.Runs;

/// <summary>Run rules now over the newest messages of a folder.</summary>
/// <param name="Folder">Defaults to the account's first watched folder.</param>
/// <param name="RuleIds">Only these rules (they run even when switched off); default: all active rules for the folder.</param>
public sealed record StartRunRequest(
    [MaxLength(500)] string? Folder = null,
    [Range(1, 1000)] int Limit = 100,
    IReadOnlyList<Guid>? RuleIds = null);

public sealed record UndoRuleRequest([Range(1, 1000)] int Count = 10);

public sealed record RunSummary(
    Guid Id,
    Guid AccountId,
    string AccountName,
    RunTrigger Trigger,
    RunState State,
    string Folder,
    string? SearchFilter,
    int MessagesExamined,
    int MessagesMatched,
    int PlannedActions,
    int ExecutedActions,
    int FailedActions,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset? ExpiresAt,
    Guid? UndoneByRunId,
    Guid? UndoRunId,
    Guid? UndoRuleId,
    bool CanUndo,
    string? Origin)
{
    public static RunSummary From(RuleRun run, string accountName) => new(
        run.Id,
        run.AccountId,
        accountName,
        run.Trigger,
        run.State,
        run.Folder,
        run.SearchFilter,
        run.MessagesExamined,
        run.MessagesMatched,
        run.PlannedActions,
        run.ExecutedActions,
        run.FailedActions,
        run.Error,
        run.CreatedAt,
        run.StartedAt,
        run.FinishedAt,
        run.ExpiresAt,
        run.UndoneByRunId,
        run.UndoRunId,
        run.UndoRuleId,
        CanUndo: run.State is RunState.Completed or RunState.CompletedWithErrors
            && run.Trigger is not (RunTrigger.Undo or RunTrigger.Purge) && run.UndoneByRunId is null && run.ExecutedActions > 0,
        run.Origin);
}

public sealed record ActionLogItem(
    Guid Id,
    Guid? RuleId,
    string? RuleName,
    ActionType Action,
    string? Keyword,
    string SourceFolder,
    string? TargetFolder,
    string? From,
    string? Subject,
    bool DryRun,
    bool Success,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UndoneAt)
{
    public static ActionLogItem Create(ActionLogEntry e) => new(
        e.Id, e.RuleId, e.RuleName, e.Action, e.Keyword, e.SourceFolder, e.TargetFolder, e.From, e.Subject, e.DryRun, e.Success, e.Error, e.CreatedAt, e.UndoneAt);
}

public sealed record PendingActionItem(string RuleName, ActionType Type, string? TargetFolder, string? Keyword, bool DryRun);

public sealed record PendingMessageItem(uint Uid, string? From, string? Subject, IReadOnlyList<PendingActionItem> Actions);

public sealed record RunDetail(RunSummary Run, IReadOnlyList<ActionLogItem> Actions, IReadOnlyList<PendingMessageItem> Pending);
