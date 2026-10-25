using Mailnix.Core.Rules;
using Mailnix.Infrastructure.Data;
using Mailnix.Mail;

namespace Mailnix.Api.Stats;

public enum Granularity
{
    Day,

    /// <summary>ISO weeks, starting on Monday.</summary>
    Week,
    Month,
}

/// <summary>Values per bucket (day, week or month start), one series per key.</summary>
public sealed record TimeSeries(IReadOnlyList<DateOnly> Buckets, IReadOnlyList<Series> Series);

/// <param name="Key">Metric, account or rule id, depending on the grouping.</param>
/// <param name="Name">Account or rule name where the key is an id.</param>
public sealed record Series(string Key, string? Name, IReadOnlyList<int> Values);

public sealed record KpiSet(int Processed, int Actions, int Moved, int Trashed, int Flagged, int Failed);

public sealed record AccountHealth(
    Guid Id,
    string Name,
    MailProvider Provider,
    bool Enabled,
    MailAccountStatus Status,
    ConnectionErrorCode? LastErrorCode,
    DateTimeOffset? LastCheckedAt,
    int ProcessedToday,
    int ActionsToday);

public sealed record RuleCount(Guid RuleId, string Name, Guid AccountId, int Count);

public enum HintKind
{
    /// <summary>No rules yet: start from a template.</summary>
    NoRules,

    /// <summary>An active rule that has not acted on anything for 30 days.</summary>
    NeverMatched,

    /// <summary>A dry-run rule whose planned actions look ready to run for real.</summary>
    DryRunReady,
}

public sealed record Hint(HintKind Kind, Guid? RuleId = null, string? RuleName = null, int? Count = null);

/// <param name="Unread">Unread messages in the watched folders as of the latest daily snapshot; null without one.</param>
public sealed record OverviewResponse(
    DateOnly Today,
    KpiSet TodayKpis,
    KpiSet YesterdayKpis,
    int PendingConfirmations,
    int AccountsWithErrors,
    int? Unread,
    IReadOnlyList<AccountHealth> Accounts,
    TimeSeries Activity,
    IReadOnlyList<RuleCount> TopRules,
    IReadOnlyList<Hint> Hints);

public sealed record RuleStatsRow(
    Guid RuleId,
    string Name,
    Guid AccountId,
    string AccountName,
    bool Enabled,
    bool DryRun,
    int Matched,
    int Actions,
    int Failed,
    int Undone,
    int DryRunActions,
    DateOnly? LastMatch,
    double MatchesPerDay);

public sealed record RuleStatsResponse(DateOnly From, DateOnly To, IReadOnlyList<RuleStatsRow> Rules);

public sealed record SenderRow(string Domain, int Processed, int Actioned, int Bulk, int AuthFailed);

public sealed record SendersResponse(SenderRow Totals, IReadOnlyList<SenderRow> TopProcessed, IReadOnlyList<SenderRow> TopActioned, TimeSeries Volume);

public sealed record FolderRow(Guid AccountId, string AccountName, string Folder, int Messages, int Unread, int? Change);

/// <param name="Date">Day of the latest snapshot.</param>
/// <param name="Folders">Latest sizes; <see cref="FolderRow.Change"/> compares with the first snapshot in the range.</param>
/// <param name="Growth">Total messages and unread per day.</param>
public sealed record FoldersResponse(DateOnly? Date, IReadOnlyList<FolderRow> Folders, TimeSeries Growth);

public sealed record SecurityTotals(int Processed, int SpfFailed, int DkimFailed, int DmarcFailed, int Spoofing, int HighSpam);

public sealed record SecurityResponse(SecurityTotals Totals, TimeSeries Series, IReadOnlyList<SenderRow> TopFailingDomains);

public sealed record RunStateCount(RunState State, int Count);

public sealed record TriggerStats(RunTrigger Trigger, int Runs, int Failed, double? AverageSeconds);

public sealed record ReliabilityResponse(
    IReadOnlyList<RunStateCount> RunsByState,
    IReadOnlyList<TriggerStats> ByTrigger,
    TimeSeries FailedActions,
    int PendingNow,
    IReadOnlyList<AccountHealth> Accounts);

/// <summary>One logged action, for the activity list.</summary>
public sealed record ActionEntry(
    Guid Id,
    Guid RunId,
    Guid AccountId,
    string AccountName,
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
    DateTimeOffset? UndoneAt,
    bool CanUndo);

/// <param name="NextBefore">Pass as <c>before</c> to load older entries; null at the end.</param>
public sealed record ActionPage(IReadOnlyList<ActionEntry> Items, Guid? NextBefore);

public sealed record UndoActionsRequest(IReadOnlyList<Guid> ActionIds);
