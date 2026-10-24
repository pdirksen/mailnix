using Mailnix.Core.Rules;

namespace Mailnix.Infrastructure.Data;

public enum RunTrigger
{
    /// <summary>New mail in a watched folder.</summary>
    NewMail,

    /// <summary>Started by the user ("run now", "apply to existing mail").</summary>
    Manual,

    /// <summary>A scheduled rule's cron schedule.</summary>
    Scheduled,

    /// <summary>Reverting an earlier run or a rule's recent actions.</summary>
    Undo,

    /// <summary>Permanently removing messages that have been in Trash longer than the account allows.</summary>
    Purge,
}

public enum RunState
{
    /// <summary>Waiting for the account's worker.</summary>
    Queued,
    Running,

    /// <summary>Would move more messages than the account's threshold; waits for the user.</summary>
    AwaitingConfirmation,
    Completed,

    /// <summary>Some actions failed (see the log entries).</summary>
    CompletedWithErrors,
    Failed,
    Cancelled,

    /// <summary>Not confirmed in time.</summary>
    Expired,
}

public class RuleRun
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public Guid AccountId { get; set; }

    public RunTrigger Trigger { get; set; }

    public RunState State { get; set; }

    public required string Folder { get; set; }

    /// <summary>The server-side search that preselected the messages (see SearchPlanner); null when all were checked.</summary>
    public string? SearchFilter { get; set; }

    /// <summary>Manual runs: how many of the newest messages to process.</summary>
    public int? Limit { get; set; }

    /// <summary>Manual runs: only these rules (null = all enabled rules of the account).</summary>
    public List<Guid>? RuleIds { get; set; }

    /// <summary>Undo runs: the run to revert.</summary>
    public Guid? UndoRunId { get; set; }

    /// <summary>Undo runs: revert the last <see cref="UndoCount"/> actions of this rule.</summary>
    public Guid? UndoRuleId { get; set; }

    public int? UndoCount { get; set; }

    /// <summary>For runs awaiting confirmation: the planned actions (JSON), executed as-is once confirmed.</summary>
    public string? PendingPlanJson { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public int MessagesExamined { get; set; }

    public int MessagesMatched { get; set; }

    public int PlannedActions { get; set; }

    public int ExecutedActions { get; set; }

    public int FailedActions { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Set when this run was reverted.</summary>
    public Guid? UndoneByRunId { get; set; }
}

/// <summary>One action on one message. Keeps what undo needs to put things back.</summary>
public class ActionLogEntry
{
    public Guid Id { get; set; }

    public Guid RunId { get; set; }

    public Guid AccountId { get; set; }

    public Guid UserId { get; set; }

    public Guid? RuleId { get; set; }

    public string? RuleName { get; set; }

    public ActionType Action { get; set; }

    public string? Keyword { get; set; }

    public required string SourceFolder { get; set; }

    public uint SourceUid { get; set; }

    public uint SourceUidValidity { get; set; }

    public string? TargetFolder { get; set; }

    public uint? TargetUid { get; set; }

    public uint? TargetUidValidity { get; set; }

    public string? MessageId { get; set; }

    public string? From { get; set; }

    public string? Subject { get; set; }

    /// <summary>Flags before the action ("Seen", "Flagged", keywords), to restore them on undo.</summary>
    public List<string> PreviousFlags { get; set; } = [];

    /// <summary>Planned by a dry-run rule: logged, not executed.</summary>
    public bool DryRun { get; set; }

    public bool Success { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? UndoneAt { get; set; }
}

/// <summary>
/// A message mailnix has seen in the account's Trash folder, and since when. The server only knows when a message
/// arrived, not when it was deleted, so the Trash purge counts from the first time mailnix saw it there.
/// </summary>
public class TrashObservation
{
    public Guid AccountId { get; set; }

    public required string Folder { get; set; }

    public uint UidValidity { get; set; }

    public uint Uid { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }
}

/// <summary>How far mailnix has processed a watched folder.</summary>
public class FolderState
{
    public Guid AccountId { get; set; }

    public required string Folder { get; set; }

    public uint UidValidity { get; set; }

    /// <summary>Messages with a higher UID are new.</summary>
    public uint LastProcessedUid { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
