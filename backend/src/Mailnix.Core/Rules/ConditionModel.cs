using System.Text.Json.Serialization;

namespace Mailnix.Core.Rules;

/// <summary>A node of a rule's condition tree: either a group of nodes or a single condition.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ConditionGroup), "group")]
[JsonDerivedType(typeof(Condition), "condition")]
public abstract record ConditionNode;

public enum GroupOperator
{
    /// <summary>Every child matches (AND).</summary>
    All,

    /// <summary>At least one child matches (OR).</summary>
    Any,

    /// <summary>No child matches (NOT OR).</summary>
    None,
}

public sealed record ConditionGroup(GroupOperator Operator, IReadOnlyList<ConditionNode> Children) : ConditionNode;

public enum ConditionField
{
    // Sender, recipients, content
    From,
    FromName,
    FromDomain,
    To,
    Cc,
    ReplyTo,
    Recipients,
    Subject,
    Header,
    Body,
    Size,
    Date,
    AgeDays,

    // Status
    IsUnread,
    IsFlagged,
    IsAnswered,
    Keyword,

    // Mail type
    IsNewsletter,
    ListId,
    IsAutoReply,
    IsReply,
    IsReplyToMyMail,
    IsCalendarMessage,
    CalendarMethod,

    // Sender trust
    SpfResult,
    DkimResult,
    DmarcResult,
    DisplayNameMismatch,
    SpamScore,

    // Recipient details
    IAmOnlyInCcOrBcc,
    DeliveredTo,
    PlusTag,
    RecipientCount,

    // Attachments
    HasAttachment,
    AttachmentCount,
    AttachmentName,
    AttachmentExtension,
    AttachmentMimeType,
    AttachmentSize,

    // Another rule's conditions
    Rule,
}

public enum ConditionOperator
{
    Equals,
    NotEquals,
    Contains,
    NotContains,
    StartsWith,
    EndsWith,
    Regex,

    /// <summary>The domain or one of its subdomains (<c>example.com</c> matches <c>mail.example.com</c>).</summary>
    DomainMatches,

    /// <summary>Value is the id of a named list.</summary>
    InList,
    NotInList,
    Exists,
    NotExists,
    GreaterThan,
    LessThan,

    /// <summary>Date fields: before the given day (in the user's time zone).</summary>
    Before,

    /// <summary>Date fields: after the given day (in the user's time zone).</summary>
    After,
    IsTrue,
    IsFalse,

    /// <summary>Value is the id of a rule whose conditions must (not) match.</summary>
    Matches,
    NotMatches,
}

/// <param name="Value">Operand as text (numbers in invariant culture, dates as yyyy-MM-dd, ids as GUIDs); null for operators without one.</param>
/// <param name="HeaderName">Only for <see cref="ConditionField.Header"/>.</param>
public sealed record Condition(
    ConditionField Field,
    ConditionOperator Operator,
    string? Value = null,
    string? HeaderName = null,
    bool CaseSensitive = false) : ConditionNode;
