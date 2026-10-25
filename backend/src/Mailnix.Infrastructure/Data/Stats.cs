using Mailnix.Core.Rules;

namespace Mailnix.Infrastructure.Data;

/// <summary>
/// Metrics of <see cref="StatsDaily"/>: the names of <see cref="ActionType"/> for executed actions (MoveTo, Delete,
/// Flag, Purge, ...) plus these.
/// </summary>
public static class StatsMetrics
{
    /// <summary>New mail that arrived in a watched folder and was checked by the rules.</summary>
    public const string Processed = "Processed";

    /// <summary>Messages a rule acted on (per rule; also with the empty rule id for all rules together).</summary>
    public const string Matched = "Matched";

    public const string Failed = "Failed";

    /// <summary>Actions a dry-run rule would have taken.</summary>
    public const string DryRun = "DryRun";

    /// <summary>Actions reverted by undo (counted for the rule that took them).</summary>
    public const string Undone = "Undone";

    /// <summary>Metrics that are not actions on messages.</summary>
    public static readonly IReadOnlySet<string> NonActions = new HashSet<string> { Processed, Matched, Failed, DryRun, Undone };

    public static string For(ActionType action) => action.ToString();
}

/// <summary>
/// Daily counters per account, rule and metric, in the user's time zone. Stats read these instead of the action log,
/// which is pruned after a while.
/// </summary>
public class StatsDaily
{
    public Guid AccountId { get; set; }

    public Guid UserId { get; set; }

    public DateOnly Date { get; set; }

    /// <summary><see cref="Guid.Empty"/> for counters that belong to no single rule (e.g. Processed).</summary>
    public Guid RuleId { get; set; }

    public required string Metric { get; set; }

    public int Count { get; set; }
}

/// <summary>Daily counters per sender domain (new mail only).</summary>
public class SenderDaily
{
    public Guid AccountId { get; set; }

    public Guid UserId { get; set; }

    public DateOnly Date { get; set; }

    public required string Domain { get; set; }

    public int Processed { get; set; }

    /// <summary>Messages a rule acted on.</summary>
    public int Actioned { get; set; }

    /// <summary>Newsletters and other bulk mail.</summary>
    public int Bulk { get; set; }

    /// <summary>Messages failing SPF, DKIM or DMARC.</summary>
    public int AuthFailed { get; set; }
}

/// <summary>Daily sender-trust counters (new mail only).</summary>
public class SecurityDaily
{
    public Guid AccountId { get; set; }

    public Guid UserId { get; set; }

    public DateOnly Date { get; set; }

    public int Processed { get; set; }

    public int SpfFailed { get; set; }

    public int DkimFailed { get; set; }

    public int DmarcFailed { get; set; }

    /// <summary>The display name shows an address that is not the sender's.</summary>
    public int Spoofing { get; set; }

    /// <summary>Spam score of 5 or more.</summary>
    public int HighSpam { get; set; }
}

/// <summary>Size of a folder once a day (from IMAP STATUS).</summary>
public class FolderSnapshot
{
    public Guid AccountId { get; set; }

    public Guid UserId { get; set; }

    public required string Folder { get; set; }

    public DateOnly Date { get; set; }

    public int Messages { get; set; }

    public int Unread { get; set; }

    public DateTimeOffset CapturedAt { get; set; }
}
