using Mailnix.Core.Rules;

namespace Mailnix.Infrastructure.Data;

public enum RuleTrigger
{
    /// <summary>Runs on new mail in the source folders.</summary>
    OnNew,

    /// <summary>Runs on a cron schedule over existing mail (e.g. cleanups).</summary>
    Scheduled,

    /// <summary>Runs only when started by the user.</summary>
    Manual,
}

/// <summary>A stored rule; conditions and actions are JSON in the shape of <see cref="RuleDefinition"/>.</summary>
public class MailRule
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public Guid AccountId { get; set; }

    public MailAccount? Account { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Lower runs first.</summary>
    public int Priority { get; set; }

    public bool Enabled { get; set; } = true;

    public bool StopProcessing { get; set; }

    public bool DryRun { get; set; }

    public RuleTrigger Trigger { get; set; } = RuleTrigger.OnNew;

    /// <summary>Cron expression for <see cref="RuleTrigger.Scheduled"/>.</summary>
    public string? Schedule { get; set; }

    /// <summary>Folders the rule applies to; empty means the account's watched folders.</summary>
    public List<string> SourceFolders { get; set; } = [];

    /// <summary>Scheduled rules: when the scheduler last queued a run (the next one is due after this).</summary>
    public DateTimeOffset? LastScheduledAt { get; set; }

    public required string ConditionsJson { get; set; }

    public required string ActionsJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Who made the last change outside the app (e.g. an MCP client); null when it was made in the app.</summary>
    public string? Origin { get; set; }

    public List<RuleReference> References { get; set; } = [];

    public List<RuleListReference> ListReferences { get; set; } = [];
}

/// <summary>A rule's condition refers to another rule. Kept in sync on save; blocks deleting the referenced rule.</summary>
public class RuleReference
{
    public Guid RuleId { get; set; }

    public Guid ReferencedRuleId { get; set; }
}

/// <summary>A rule's condition uses a named list. Kept in sync on save; blocks deleting the list.</summary>
public class RuleListReference
{
    public Guid RuleId { get; set; }

    public Guid ListId { get; set; }
}

public class NamedListEntity
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public NamedListKind Kind { get; set; }

    public List<NamedListEntryEntity> Entries { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public class NamedListEntryEntity
{
    public Guid Id { get; set; }

    public Guid ListId { get; set; }

    /// <summary>Normalized (see <see cref="NamedList.Normalize"/>).</summary>
    public required string Value { get; set; }

    public string? Note { get; set; }
}
