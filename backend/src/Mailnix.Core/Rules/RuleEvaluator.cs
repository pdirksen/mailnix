using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Mailnix.Core.Messages;

namespace Mailnix.Core.Rules;

public sealed record RuleEvaluation(bool Matched, TraceNode? Trace);

/// <summary>
/// Evaluates rule conditions against one message. Pure: the same message, context and rules always give the
/// same result, which is what makes the preview trustworthy.
/// </summary>
public static class RuleEvaluator
{
    /// <summary>Maximum nesting of rule references; deeper chains (or cycles) never match.</summary>
    public const int MaxReferenceDepth = 5;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly ConcurrentDictionary<(string Pattern, bool CaseSensitive), Regex?> RegexCache = new();

    /// <summary>Evaluates one rule's conditions.</summary>
    /// <param name="trace">Build an explanation trace; disables short-circuiting so every node is reported.</param>
    public static RuleEvaluation Evaluate(RuleDefinition rule, MessageFacts facts, EvaluationContext context, bool trace = false) =>
        new EvaluationSession(facts, context, trace).EvaluateRule(rule);

    /// <summary>Evaluates several rules for the same message, sharing results of referenced rules.</summary>
    public static IReadOnlyList<RuleEvaluation> EvaluateAll(IEnumerable<RuleDefinition> rules, MessageFacts facts, EvaluationContext context, bool trace = false)
    {
        var session = new EvaluationSession(facts, context, trace);
        return [.. rules.Select(session.EvaluateRule)];
    }

    /// <summary>Compiles a user pattern, or returns null if it is invalid or unsupported.</summary>
    public static Regex? CompileRegex(string pattern, bool caseSensitive) =>
        RegexCache.GetOrAdd((pattern, caseSensitive), key =>
        {
            try
            {
                // Non-backtracking: linear time in the input, so user patterns cannot hang the server.
                var options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant
                    | (key.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
                return new Regex(key.Pattern, options, RegexTimeout);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return null;
            }
        });
}

/// <summary>
/// Evaluation of rules against one message: caches results of referenced rules and guards against reference cycles.
/// </summary>
public sealed class EvaluationSession(MessageFacts facts, EvaluationContext context, bool trace)
{
    private const int MaxActualValues = 5;
    private const int MaxActualLength = 200;

    private readonly Dictionary<Guid, RuleEvaluation> _references = [];
    private readonly Stack<Guid> _referenceStack = new();

    private MessageSnapshot Message => facts.Message;

    public RuleEvaluation EvaluateRule(RuleDefinition rule)
    {
        _referenceStack.Push(rule.Id);
        try
        {
            var node = EvaluateNode(rule.Conditions);
            return new RuleEvaluation(node.Result, trace ? node : null);
        }
        finally
        {
            _referenceStack.Pop();
        }
    }

    private TraceNode EvaluateNode(ConditionNode node) => node switch
    {
        ConditionGroup group => EvaluateGroup(group),
        Condition condition => EvaluateCondition(condition),
        _ => throw new ArgumentOutOfRangeException(nameof(node), node, "Unknown condition node."),
    };

    private TraceNode EvaluateGroup(ConditionGroup group)
    {
        var children = new List<TraceNode>(group.Children.Count);
        bool? decided = null;
        foreach (var child in group.Children)
        {
            var result = EvaluateNode(child);
            children.Add(result);

            // All: first false decides. Any/None: first true decides.
            if (decided is null && result.Result == (group.Operator != GroupOperator.All))
            {
                decided = group.Operator == GroupOperator.Any;
                if (!trace)
                {
                    break;
                }
            }
        }

        var matched = decided ?? group.Operator != GroupOperator.Any;
        return new TraceNode { Result = matched, Operator = group.Operator, Children = trace ? children : [] };
    }

    private TraceNode EvaluateCondition(Condition condition)
    {
        if (condition.Field == ConditionField.Rule)
        {
            return EvaluateReference(condition);
        }

        var definition = FieldCatalog.Get(condition.Field);
        var note = IsLoaded(definition.Fetch) ? (TraceNote?)null : TraceNote.NotLoaded;
        var (result, actual, matchNote) = definition.ValueType switch
        {
            FieldValueType.Number => MatchNumber(condition, ReadNumber(condition.Field)),
            FieldValueType.Boolean => MatchBoolean(condition, ReadBoolean(condition.Field)),
            FieldValueType.Date => MatchDate(condition, Message.EffectiveDate),
            FieldValueType.Address when condition.Operator is ConditionOperator.InList or ConditionOperator.NotInList =>
                MatchAddressList(condition, ReadAddresses(condition.Field)),
            FieldValueType.Domain when condition.Operator is ConditionOperator.InList or ConditionOperator.NotInList =>
                MatchDomainList(condition, ReadTexts(condition)),
            _ => MatchText(condition, definition.ValueType, ReadTexts(condition)),
        };

        return new TraceNode { Result = result, Condition = condition, Actual = actual, Note = matchNote ?? note };
    }

    private TraceNode EvaluateReference(Condition condition)
    {
        var negate = condition.Operator == ConditionOperator.NotMatches;
        if (!Guid.TryParse(condition.Value, out var id) || !context.Rules.TryGetValue(id, out var referenced))
        {
            // A missing rule never matches, in either direction.
            return new TraceNode { Result = false, Condition = condition, Note = TraceNote.RuleNotFound };
        }

        if (_referenceStack.Contains(id) || _referenceStack.Count > RuleEvaluator.MaxReferenceDepth)
        {
            return new TraceNode { Result = false, Condition = condition, Note = TraceNote.ReferenceDepthExceeded, ReferencedRuleName = referenced.Name };
        }

        if (!_references.TryGetValue(id, out var evaluation))
        {
            evaluation = EvaluateRule(referenced);
            _references[id] = evaluation;
        }

        return new TraceNode
        {
            Result = evaluation.Matched != negate,
            Condition = condition,
            ReferencedRuleName = referenced.Name,
            Children = evaluation.Trace is null ? [] : [evaluation.Trace],
        };
    }

    private bool IsLoaded(FetchRequirements required) =>
        (!required.HasFlag(FetchRequirements.Headers) || Message.Headers is not null)
        && (!required.HasFlag(FetchRequirements.BodyStructure) || Message.Attachments is not null)
        && (!required.HasFlag(FetchRequirements.Body) || Message.BodyText is not null);

    private IReadOnlyList<EmailAddress> ReadAddresses(ConditionField field) => field switch
    {
        ConditionField.From => Message.From is null ? [] : [Message.From],
        ConditionField.To => Message.To,
        ConditionField.Cc => Message.Cc,
        ConditionField.ReplyTo => Message.ReplyTo,
        ConditionField.Recipients => [.. Message.To, .. Message.Cc],
        ConditionField.DeliveredTo => facts.DeliveredTo,
        _ => [],
    };

    private IReadOnlyList<string> ReadTexts(Condition condition)
    {
        IEnumerable<string?> values = condition.Field switch
        {
            ConditionField.From or ConditionField.To or ConditionField.Cc or ConditionField.ReplyTo
                or ConditionField.Recipients or ConditionField.DeliveredTo => ReadAddresses(condition.Field).Select(a => a.Address),
            ConditionField.FromName => [Message.From?.Name],
            ConditionField.FromDomain => [Message.From?.Domain],
            ConditionField.Subject => [Message.Subject],
            ConditionField.Header => condition.HeaderName is null ? [] : Message.HeaderValues(condition.HeaderName),
            ConditionField.Body => [Message.BodyText],
            ConditionField.Keyword => Message.Keywords,
            ConditionField.ListId => [facts.ListId],
            ConditionField.PlusTag => facts.PlusTags,
            ConditionField.SpfResult => [facts.Authentication.Spf],
            ConditionField.DkimResult => [facts.Authentication.Dkim],
            ConditionField.DmarcResult => [facts.Authentication.Dmarc],
            ConditionField.AttachmentName => Attachments.Select(a => a.FileName),
            ConditionField.AttachmentExtension => Attachments.Select(a => a.Extension),
            ConditionField.AttachmentMimeType => Attachments.Select(a => a.MimeType),
            _ => [],
        };
        return [.. values.Where(v => !string.IsNullOrEmpty(v)).Select(v => v!)];
    }

    private IReadOnlyList<AttachmentInfo> Attachments => Message.Attachments ?? [];

    private double? ReadNumber(ConditionField field) => field switch
    {
        ConditionField.Size => Message.Size,
        ConditionField.AgeDays => Message.EffectiveDate is { } date ? (context.Now - date).TotalDays : null,
        ConditionField.SpamScore => facts.SpamScore,
        ConditionField.RecipientCount => facts.RecipientCount,
        ConditionField.AttachmentCount => Message.Attachments?.Count,
        ConditionField.AttachmentSize => Message.Attachments is null ? null : Attachments.Count == 0 ? 0 : Attachments.Max(a => a.Size),
        _ => null,
    };

    private bool ReadBoolean(ConditionField field) => field switch
    {
        ConditionField.IsUnread => !Message.IsSeen,
        ConditionField.IsFlagged => Message.IsFlagged,
        ConditionField.IsAnswered => Message.IsAnswered,
        ConditionField.IsNewsletter => facts.IsNewsletter,
        ConditionField.IsAutoReply => facts.IsAutoReply,
        ConditionField.IsReply => facts.IsReply,
        ConditionField.IsReplyToMyMail => facts.IsReplyToMyMail,
        ConditionField.DisplayNameMismatch => facts.DisplayNameMismatch,
        ConditionField.IAmOnlyInCcOrBcc => facts.IAmOnlyInCcOrBcc,
        ConditionField.HasAttachment => Attachments.Count > 0,
        _ => false,
    };

    private static (bool, IReadOnlyList<string>, TraceNote?) MatchText(Condition condition, FieldValueType type, IReadOnlyList<string> values)
    {
        var comparison = condition.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var operand = NormalizeOperand(condition.Value ?? string.Empty, type, condition.Operator);
        TraceNote? note = null;

        bool Any(Func<string, bool> predicate) => values.Any(predicate);

        var result = condition.Operator switch
        {
            ConditionOperator.Equals => Any(v => v.Equals(operand, comparison)),
            ConditionOperator.NotEquals => !Any(v => v.Equals(operand, comparison)),
            ConditionOperator.Contains => Any(v => v.Contains(operand, comparison)),
            ConditionOperator.NotContains => !Any(v => v.Contains(operand, comparison)),
            ConditionOperator.StartsWith => Any(v => v.StartsWith(operand, comparison)),
            ConditionOperator.EndsWith => Any(v => v.EndsWith(operand, comparison)),
            ConditionOperator.DomainMatches => Any(v => EmailAddress.IsDomainOrSubdomain(EmailAddress.NormalizeDomain(v), operand)),
            ConditionOperator.Exists => values.Count > 0,
            ConditionOperator.NotExists => values.Count == 0,
            ConditionOperator.Regex => MatchRegex(condition, values, ref note),
            _ => false,
        };
        return (result, Shorten(values), note);
    }

    private static string NormalizeOperand(string operand, FieldValueType type, ConditionOperator op) => type switch
    {
        FieldValueType.Domain when op is not ConditionOperator.Regex and not ConditionOperator.Contains and not ConditionOperator.NotContains
            => EmailAddress.NormalizeDomain(operand),
        FieldValueType.Address when op is ConditionOperator.Equals or ConditionOperator.NotEquals && operand.Contains('@', StringComparison.Ordinal)
            => new EmailAddress(operand).Address,
        _ => operand,
    };

    private static bool MatchRegex(Condition condition, IReadOnlyList<string> values, ref TraceNote? note)
    {
        if (RuleEvaluator.CompileRegex(condition.Value ?? string.Empty, condition.CaseSensitive) is not { } regex)
        {
            note = TraceNote.InvalidValue;
            return false;
        }

        try
        {
            return values.Any(regex.IsMatch);
        }
        catch (RegexMatchTimeoutException)
        {
            note = TraceNote.RegexTimeout;
            return false;
        }
    }

    private (bool, IReadOnlyList<string>, TraceNote?) MatchAddressList(Condition condition, IReadOnlyList<EmailAddress> addresses)
    {
        var actual = Shorten([.. addresses.Select(a => a.Address)]);
        if (!TryGetList(condition, out var list))
        {
            return (false, actual, TraceNote.ListNotFound);
        }

        var any = addresses.Any(list.ContainsAddress);
        return (condition.Operator == ConditionOperator.InList ? any : !any, actual, null);
    }

    private (bool, IReadOnlyList<string>, TraceNote?) MatchDomainList(Condition condition, IReadOnlyList<string> domains)
    {
        if (!TryGetList(condition, out var list))
        {
            return (false, Shorten(domains), TraceNote.ListNotFound);
        }

        var any = domains.Any(list.ContainsDomain);
        return (condition.Operator == ConditionOperator.InList ? any : !any, Shorten(domains), null);
    }

    private bool TryGetList(Condition condition, out NamedList list)
    {
        list = null!;
        return Guid.TryParse(condition.Value, out var id) && context.NamedLists.TryGetValue(id, out list!);
    }

    private static (bool, IReadOnlyList<string>, TraceNote?) MatchNumber(Condition condition, double? value)
    {
        IReadOnlyList<string> actual = value is null ? [] : [value.Value.ToString("0.##", CultureInfo.InvariantCulture)];
        switch (condition.Operator)
        {
            case ConditionOperator.Exists:
                return (value is not null, actual, null);
            case ConditionOperator.NotExists:
                return (value is null, actual, null);
        }

        if (!double.TryParse(condition.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var operand))
        {
            return (false, actual, TraceNote.InvalidValue);
        }

        var result = value is { } v && condition.Operator switch
        {
            ConditionOperator.GreaterThan => v > operand,
            ConditionOperator.LessThan => v < operand,
            ConditionOperator.Equals => Math.Abs(v - operand) < 1e-9,
            _ => false,
        };
        return (result, actual, null);
    }

    private static (bool, IReadOnlyList<string>, TraceNote?) MatchBoolean(Condition condition, bool value) =>
        (condition.Operator == ConditionOperator.IsTrue ? value : !value, [value ? "true" : "false"], null);

    private (bool, IReadOnlyList<string>, TraceNote?) MatchDate(Condition condition, DateTimeOffset? value)
    {
        IReadOnlyList<string> actual = value is null
            ? []
            : [TimeZoneInfo.ConvertTime(value.Value, context.TimeZone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)];
        if (!DateOnly.TryParseExact(condition.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return (false, actual, TraceNote.InvalidValue);
        }

        if (value is null)
        {
            return (false, actual, null);
        }

        // Day boundaries in the user's time zone: "before 2026-03-01" ends at local midnight.
        var startOfDay = StartOfDay(day);
        var result = condition.Operator switch
        {
            ConditionOperator.Before => value.Value < startOfDay,
            ConditionOperator.After => value.Value >= StartOfDay(day.AddDays(1)),
            _ => false,
        };
        return (result, actual, null);
    }

    private DateTimeOffset StartOfDay(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, context.TimeZone.GetUtcOffset(local));
    }

    private static IReadOnlyList<string> Shorten(IReadOnlyList<string> values) =>
        [.. values.Take(MaxActualValues).Select(v => v.Length <= MaxActualLength ? v : v[..MaxActualLength] + "…")];
}
