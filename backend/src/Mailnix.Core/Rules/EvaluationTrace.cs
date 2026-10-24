namespace Mailnix.Core.Rules;

/// <summary>Why a condition did not evaluate normally.</summary>
public enum TraceNote
{
    /// <summary>The data this field needs was not fetched for this message.</summary>
    NotLoaded,
    RegexTimeout,
    InvalidValue,
    ListNotFound,
    RuleNotFound,

    /// <summary>Rule references nested too deeply or in a cycle; treated as not matching.</summary>
    ReferenceDepthExceeded,
}

/// <summary>
/// Mirrors the condition tree with each node's outcome, so the UI can explain why a rule did or did not match.
/// </summary>
public sealed record TraceNode
{
    public required bool Result { get; init; }

    /// <summary>Set for groups.</summary>
    public GroupOperator? Operator { get; init; }

    /// <summary>Set for conditions.</summary>
    public Condition? Condition { get; init; }

    /// <summary>The message's values the condition was compared against (shortened).</summary>
    public IReadOnlyList<string> Actual { get; init; } = [];

    public TraceNote? Note { get; init; }

    /// <summary>For rule references: the referenced rule's name and its own trace.</summary>
    public string? ReferencedRuleName { get; init; }

    public IReadOnlyList<TraceNode> Children { get; init; } = [];
}
