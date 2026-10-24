using System.Text.Json.Serialization;

namespace Mailnix.Core.Rules;

public enum ActionType
{
    MarkRead,
    MarkUnread,
    Flag,
    Unflag,
    AddKeyword,
    RemoveKeyword,
    CopyTo,
    MoveTo,
    Delete,
    MarkAsSpam,
    Archive,
}

/// <summary>What a matching rule does. At most one action per message may be terminal (move it somewhere).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(MarkReadAction), nameof(ActionType.MarkRead))]
[JsonDerivedType(typeof(MarkUnreadAction), nameof(ActionType.MarkUnread))]
[JsonDerivedType(typeof(FlagAction), nameof(ActionType.Flag))]
[JsonDerivedType(typeof(UnflagAction), nameof(ActionType.Unflag))]
[JsonDerivedType(typeof(AddKeywordAction), nameof(ActionType.AddKeyword))]
[JsonDerivedType(typeof(RemoveKeywordAction), nameof(ActionType.RemoveKeyword))]
[JsonDerivedType(typeof(CopyToAction), nameof(ActionType.CopyTo))]
[JsonDerivedType(typeof(MoveToAction), nameof(ActionType.MoveTo))]
[JsonDerivedType(typeof(DeleteAction), nameof(ActionType.Delete))]
[JsonDerivedType(typeof(MarkAsSpamAction), nameof(ActionType.MarkAsSpam))]
[JsonDerivedType(typeof(ArchiveAction), nameof(ActionType.Archive))]
public abstract record RuleAction
{
    [JsonIgnore]
    public ActionType Type => this switch
    {
        MarkReadAction => ActionType.MarkRead,
        MarkUnreadAction => ActionType.MarkUnread,
        FlagAction => ActionType.Flag,
        UnflagAction => ActionType.Unflag,
        AddKeywordAction => ActionType.AddKeyword,
        RemoveKeywordAction => ActionType.RemoveKeyword,
        CopyToAction => ActionType.CopyTo,
        MoveToAction => ActionType.MoveTo,
        DeleteAction => ActionType.Delete,
        MarkAsSpamAction => ActionType.MarkAsSpam,
        ArchiveAction => ActionType.Archive,
        _ => throw new InvalidOperationException($"Unknown action {GetType().Name}."),
    };

    /// <summary>Terminal actions move the message out of its folder; later terminal actions cannot apply.</summary>
    [JsonIgnore]
    public bool IsTerminal => Type is ActionType.MoveTo or ActionType.Delete or ActionType.MarkAsSpam or ActionType.Archive;
}

public sealed record MarkReadAction : RuleAction;

public sealed record MarkUnreadAction : RuleAction;

public sealed record FlagAction : RuleAction;

public sealed record UnflagAction : RuleAction;

public sealed record AddKeywordAction(string Keyword) : RuleAction;

public sealed record RemoveKeywordAction(string Keyword) : RuleAction;

/// <param name="Folder">Folder template, e.g. <c>INBOX/Archive/{year}</c> ("/" separates levels).</param>
public sealed record CopyToAction(string Folder) : RuleAction;

/// <param name="Folder">Folder template, e.g. <c>INBOX/Archive/{year}</c> ("/" separates levels).</param>
public sealed record MoveToAction(string Folder) : RuleAction;

/// <summary>Moves to the account's trash folder. Rules never delete permanently.</summary>
public sealed record DeleteAction : RuleAction;

/// <summary>Moves to the account's spam folder.</summary>
public sealed record MarkAsSpamAction : RuleAction;

/// <summary>Moves to the account's archive folder.</summary>
public sealed record ArchiveAction : RuleAction;
