namespace Mailnix.Core.Rules;

/// <summary>Everything besides the message itself that rules may depend on.</summary>
public sealed record EvaluationContext
{
    public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>For date conditions and date-based folder templates.</summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Utc;

    /// <summary>The user's own addresses and aliases (normalized), for recipient conditions.</summary>
    public IReadOnlySet<string> MyAddresses { get; init; } = new HashSet<string>();

    /// <summary>Message-IDs of mail the user sent (from the Sent folder), for "reply to my mail".</summary>
    public IReadOnlySet<string> SentMessageIds { get; init; } = new HashSet<string>();

    public IReadOnlyDictionary<Guid, NamedList> NamedLists { get; init; } = new Dictionary<Guid, NamedList>();

    /// <summary>All of the user's rules by id, so rule-reference conditions can be resolved.</summary>
    public IReadOnlyDictionary<Guid, RuleDefinition> Rules { get; init; } = new Dictionary<Guid, RuleDefinition>();

    /// <summary>
    /// Authentication-Results headers are only trusted from these servers (authserv-id). When empty, only the
    /// topmost header counts: it was added by the receiving server, whereas lower ones may come from the sender.
    /// </summary>
    public IReadOnlySet<string> TrustedAuthServIds { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The account's special folders, targets of Delete/MarkAsSpam/Archive.</summary>
    public SpecialFolderTargets SpecialFolders { get; init; } = new(null, null, null);

    /// <summary>The server's hierarchy delimiter; "/" in folder templates is translated to it.</summary>
    public char FolderDelimiter { get; init; } = '/';
}

public sealed record SpecialFolderTargets(string? Trash, string? Junk, string? Archive);
