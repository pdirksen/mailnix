using Mailnix.Core.Messages;

namespace Mailnix.Core.Rules;

/// <summary>
/// Translates rule conditions into a server-side search that narrows down which messages need to be fetched.
/// The search is always a superset: every message the rules match locally is among its results (conditions the
/// server cannot check count as "could match"). Candidates are still evaluated locally, so the search can only
/// make runs faster, never change their outcome.
/// </summary>
/// <remarks>
/// Assumes what IMAP servers do in practice: case-insensitive substring search on decoded header fields and
/// arrival dates within a day of UTC. Internationalized domains written in Unicode inside headers (rare) are the
/// exception: rules see them as punycode, so address searches are only pushed down with plain ASCII values.
/// </remarks>
public static class SearchPlanner
{
    /// <summary>Named lists up to this size become an OR of their entries; larger ones are checked locally only.</summary>
    public const int MaxListEntries = 50;

    /// <summary>Days of slack around date conditions: server dates may be in another time zone than the user's.</summary>
    private const int DateMarginDays = 2;

    /// <summary>
    /// Search for messages that any of <paramref name="rules"/> could act on; null when every message is a candidate.
    /// Rules without actions are left out (they never act; references to them are resolved through the context).
    /// </summary>
    public static SearchFilter? Plan(IEnumerable<RuleDefinition> rules, EvaluationContext context)
    {
        var parts = new List<SearchFilter>();
        foreach (var rule in rules.Where(r => r.Actions.Count > 0))
        {
            var (filter, _) = new Translator(context, rule.Id).Translate(rule.Conditions);
            if (filter is null)
            {
                return null;
            }

            parts.Add(filter);
        }

        return parts.Count switch
        {
            0 => null,
            1 => parts[0],
            _ => new SearchOr(parts),
        };
    }

    /// <summary>
    /// Translation of one condition tree. Each node gives a filter (null = no restriction) and whether it is exact,
    /// i.e. matches precisely the messages the condition matches; only exact filters may be negated.
    /// </summary>
    private sealed class Translator(EvaluationContext context, Guid ruleId)
    {
        private readonly Stack<Guid> _references = new([ruleId]);

        public (SearchFilter? Filter, bool Exact) Translate(ConditionNode node) => node switch
        {
            ConditionGroup group => TranslateGroup(group),
            Condition { Field: ConditionField.Rule } reference => TranslateReference(reference),
            Condition condition => TranslateCondition(condition),
            _ => (null, false),
        };

        private (SearchFilter?, bool) TranslateGroup(ConditionGroup group)
        {
            var children = group.Children.Select(Translate).ToList();
            switch (group.Operator)
            {
                case GroupOperator.All:
                    // Unrestricted children can be left out of an AND; the result is then no longer exact.
                    var restricted = children.Where(c => c.Filter is not null).Select(c => c.Filter!).ToList();
                    var exact = children.Count > 0 && children.All(c => c.Filter is not null && c.Exact);
                    return restricted.Count switch
                    {
                        0 => (null, false),
                        1 => (restricted[0], exact),
                        _ => (new SearchAnd(restricted), exact),
                    };

                case GroupOperator.Any:
                    if (children.Count == 0 || children.Any(c => c.Filter is null))
                    {
                        return (null, false);
                    }

                    return children.Count == 1 ? children[0] : (new SearchOr([.. children.Select(c => c.Filter!)]), children.All(c => c.Exact));

                case GroupOperator.None:
                    if (children.Count == 0 || children.Any(c => c.Filter is null || !c.Exact))
                    {
                        return (null, false);
                    }

                    var inner = children.Count == 1 ? children[0].Filter! : new SearchOr([.. children.Select(c => c.Filter!)]);
                    return (new SearchNot(inner), true);

                default:
                    return (null, false);
            }
        }

        private (SearchFilter?, bool) TranslateReference(Condition condition)
        {
            // Mirrors the evaluator: missing rules, cycles and too deep chains never match (any superset is fine).
            if (!Guid.TryParse(condition.Value, out var id) || !context.Rules.TryGetValue(id, out var referenced)
                || _references.Contains(id) || _references.Count > RuleEvaluator.MaxReferenceDepth)
            {
                return (null, false);
            }

            _references.Push(id);
            var (filter, exact) = Translate(referenced.Conditions);
            _references.Pop();

            if (condition.Operator == ConditionOperator.Matches)
            {
                return (filter, exact);
            }

            return filter is not null && exact ? (new SearchNot(filter), true) : (null, false);
        }

        private (SearchFilter?, bool) TranslateCondition(Condition condition)
        {
            var definition = FieldCatalog.Get(condition.Field);
            return definition.ValueType switch
            {
                FieldValueType.Boolean => TranslateBoolean(condition),
                FieldValueType.Number => TranslateNumber(condition),
                FieldValueType.Date => TranslateDate(condition),
                _ when condition.Field == ConditionField.Keyword => TranslateKeyword(condition),
                _ when condition.Operator is ConditionOperator.InList => TranslateList(condition, definition.ValueType),
                _ => TranslateText(condition, definition.ValueType),
            };
        }

        private static (SearchFilter?, bool) TranslateBoolean(Condition condition)
        {
            var flag = condition.Field switch
            {
                ConditionField.IsUnread => SearchFlagKind.Seen,
                ConditionField.IsFlagged => SearchFlagKind.Flagged,
                ConditionField.IsAnswered => SearchFlagKind.Answered,
                _ => (SearchFlagKind?)null,
            };
            if (flag is null)
            {
                return (null, false);
            }

            var wanted = condition.Operator == ConditionOperator.IsTrue;
            // "Unread" is the absence of \Seen.
            var set = flag == SearchFlagKind.Seen ? !wanted : wanted;
            return (new SearchFlagIs(flag.Value, set), true);
        }

        private (SearchFilter?, bool) TranslateNumber(Condition condition)
        {
            if (!double.TryParse(condition.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var operand)
                || double.IsNaN(operand) || double.IsInfinity(operand) || operand < 0 || operand > int.MaxValue)
            {
                return (null, false);
            }

            switch (condition.Field, condition.Operator)
            {
                case (ConditionField.Size, ConditionOperator.GreaterThan):
                    return (new SearchLarger((long)Math.Floor(operand)), operand == Math.Floor(operand));
                case (ConditionField.Size, ConditionOperator.LessThan):
                    return operand < 1 ? (null, false) : (new SearchSmaller((long)Math.Ceiling(operand)), operand == Math.Ceiling(operand));
                case (ConditionField.Size, ConditionOperator.Equals) when operand == Math.Floor(operand) && operand >= 1:
                    var size = (long)operand;
                    return (new SearchAnd([new SearchLarger(size - 1), new SearchSmaller(size + 1)]), true);

                // Age is measured from the arrival date: older than n days = arrived before now - n days.
                case (ConditionField.AgeDays, ConditionOperator.GreaterThan):
                    return (new SearchBefore(Day(context.Now.AddDays(-operand)).AddDays(DateMarginDays)), false);
                case (ConditionField.AgeDays, ConditionOperator.LessThan):
                    return (new SearchSince(Day(context.Now.AddDays(-operand)).AddDays(-DateMarginDays)), false);
                default:
                    return (null, false);
            }
        }

        private (SearchFilter?, bool) TranslateDate(Condition condition)
        {
            if (!DateOnly.TryParseExact(condition.Value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day))
            {
                return (null, false);
            }

            return condition.Operator switch
            {
                ConditionOperator.Before => (new SearchBefore(Day(StartOfDay(day)).AddDays(DateMarginDays)), false),
                ConditionOperator.After => (new SearchSince(Day(StartOfDay(day.AddDays(1))).AddDays(-DateMarginDays)), false),
                _ => (null, false),
            };
        }

        private static (SearchFilter?, bool) TranslateKeyword(Condition condition) =>
            condition.Operator == ConditionOperator.Equals && IsAtom(condition.Value)
                ? (new SearchKeyword(condition.Value!), false)
                : (null, false);

        private (SearchFilter?, bool) TranslateList(Condition condition, FieldValueType type)
        {
            if (!Guid.TryParse(condition.Value, out var id) || !context.NamedLists.TryGetValue(id, out var list)
                || list.Kind == NamedListKind.Pattern || list.Count == 0 || list.Count > MaxListEntries
                || list.Entries.Any(e => !IsSearchable(e)))
            {
                return (null, false);
            }

            if (type is not (FieldValueType.Address or FieldValueType.Domain))
            {
                return (null, false);
            }

            // Every entry is part of the matching address (or its domain), hence a substring of the header field.
            var parts = list.Entries.Select(e => Text(condition, e)).ToList();
            if (parts.Any(p => p is null))
            {
                return (null, false);
            }

            return (parts.Count == 1 ? parts[0] : new SearchOr([.. parts.Select(p => p!)]), false);
        }

        private static (SearchFilter?, bool) TranslateText(Condition condition, FieldValueType type)
        {
            if (condition.Operator is not (ConditionOperator.Equals or ConditionOperator.Contains or ConditionOperator.StartsWith
                or ConditionOperator.EndsWith or ConditionOperator.DomainMatches or ConditionOperator.Exists))
            {
                return (null, false);
            }

            if (condition.Operator == ConditionOperator.Exists)
            {
                var header = condition.Field switch
                {
                    ConditionField.Header when IsHeaderName(condition.HeaderName) => condition.HeaderName,
                    ConditionField.ListId => "List-Id",
                    _ => null,
                };
                return header is null ? (null, false) : (new SearchText(SearchTextField.Header, string.Empty, header), false);
            }

            var operand = Operand(condition, type);
            if (!IsSearchable(operand) || !IsSearchable(condition.Value))
            {
                return (null, false);
            }

            return (Text(condition, operand), false);
        }

        /// <summary>The search for "this header field contains <paramref name="value"/>", or null if the field is not searchable.</summary>
        private static SearchFilter? Text(Condition condition, string value) => condition.Field switch
        {
            ConditionField.From or ConditionField.FromDomain or ConditionField.FromName => new SearchText(SearchTextField.From, value),
            ConditionField.To => new SearchText(SearchTextField.To, value),
            ConditionField.Cc => new SearchText(SearchTextField.Cc, value),
            ConditionField.Recipients => new SearchOr([new SearchText(SearchTextField.To, value), new SearchText(SearchTextField.Cc, value)]),
            ConditionField.ReplyTo => new SearchText(SearchTextField.Header, value, "Reply-To"),
            ConditionField.Subject => new SearchText(SearchTextField.Subject, value),
            ConditionField.ListId => new SearchText(SearchTextField.Header, value, "List-Id"),
            ConditionField.Header when IsHeaderName(condition.HeaderName) => new SearchText(SearchTextField.Header, value, condition.HeaderName),
            _ => null,
        };

        /// <summary>The operand as the evaluator compares it (see EvaluationSession.NormalizeOperand).</summary>
        private static string Operand(Condition condition, FieldValueType type)
        {
            var value = condition.Value ?? string.Empty;
            return type switch
            {
                FieldValueType.Domain when condition.Operator is not ConditionOperator.Contains => EmailAddress.NormalizeDomain(value),
                FieldValueType.Address when condition.Operator == ConditionOperator.Equals && value.Contains('@', StringComparison.Ordinal)
                    => new EmailAddress(value).Address,
                _ => value,
            };
        }

        private DateTimeOffset StartOfDay(DateOnly day)
        {
            var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            return new DateTimeOffset(local, context.TimeZone.GetUtcOffset(local));
        }

        private static DateOnly Day(DateTimeOffset instant) => DateOnly.FromDateTime(instant.UtcDateTime);

        /// <summary>Plain printable ASCII without punycode: safe to compare against raw header text.</summary>
        private static bool IsSearchable(string? value) =>
            !string.IsNullOrWhiteSpace(value)
            && value.All(c => c is >= ' ' and <= '~')
            && !value.Contains("xn--", StringComparison.OrdinalIgnoreCase);

        private static bool IsHeaderName(string? name) =>
            !string.IsNullOrEmpty(name) && name.All(c => c is > ' ' and <= '~' and not ':');

        private static bool IsAtom(string? keyword) =>
            !string.IsNullOrEmpty(keyword) && keyword.All(c => c is > ' ' and <= '~' and not ('(' or ')' or '{' or '%' or '*' or '"' or '\\' or ']'));
    }
}
