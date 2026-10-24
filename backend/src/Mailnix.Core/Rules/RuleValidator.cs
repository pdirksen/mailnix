using System.Globalization;
using System.Text.RegularExpressions;

namespace Mailnix.Core.Rules;

/// <param name="Path">Where in the rule, e.g. <c>conditions.children[1].value</c> or <c>actions[0].folder</c>.</param>
public sealed record RuleValidationError(string Path, string Code, string Message);

public sealed record RuleValidationContext
{
    /// <summary>The user's named lists and their kinds.</summary>
    public IReadOnlyDictionary<Guid, NamedListKind> Lists { get; init; } = new Dictionary<Guid, NamedListKind>();

    /// <summary>The user's other rules (id → name) that may be referenced.</summary>
    public IReadOnlyDictionary<Guid, string> Rules { get; init; } = new Dictionary<Guid, string>();

    /// <summary>Existing reference edges between the user's rules (rule id → rules it references).</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyCollection<Guid>> References { get; init; } = new Dictionary<Guid, IReadOnlyCollection<Guid>>();
}

public static partial class RuleValidator
{
    public const int MaxConditions = 200;
    public const int MaxGroupDepth = 10;
    public const int MaxActions = 20;
    public const int MaxValueLength = 1000;

    public static IReadOnlyList<RuleValidationError> Validate(RuleDefinition rule, RuleValidationContext context)
    {
        var errors = new List<RuleValidationError>();
        var conditionCount = 0;
        ValidateNode(rule.Conditions, "conditions", depth: 1);

        if (conditionCount == 0)
        {
            errors.Add(new("conditions", "NoConditions", "A rule needs at least one condition."));
        }
        else if (conditionCount > MaxConditions)
        {
            errors.Add(new("conditions", "TooManyConditions", $"A rule can have at most {MaxConditions} conditions."));
        }

        ValidateActions(rule, errors);
        ValidateReferenceGraph(rule, context, errors);
        return errors;

        void ValidateNode(ConditionNode node, string path, int depth)
        {
            switch (node)
            {
                case ConditionGroup group:
                    if (depth > MaxGroupDepth)
                    {
                        errors.Add(new(path, "TooDeep", $"Groups can be nested at most {MaxGroupDepth} levels deep."));
                        return;
                    }

                    if (group.Children.Count == 0)
                    {
                        errors.Add(new(path, "EmptyGroup", "A group needs at least one condition."));
                    }

                    for (var i = 0; i < group.Children.Count; i++)
                    {
                        ValidateNode(group.Children[i], $"{path}.children[{i}]", depth + 1);
                    }

                    break;
                case Condition condition:
                    conditionCount++;
                    ValidateCondition(rule, condition, path, context, errors);
                    break;
            }
        }
    }

    /// <summary>Rules and named lists a rule's conditions refer to (for dependency tracking).</summary>
    public static (IReadOnlySet<Guid> Rules, IReadOnlySet<Guid> Lists) CollectReferences(ConditionNode root)
    {
        var rules = new HashSet<Guid>();
        var lists = new HashSet<Guid>();

        void Visit(ConditionNode node)
        {
            switch (node)
            {
                case ConditionGroup group:
                    foreach (var child in group.Children)
                    {
                        Visit(child);
                    }

                    break;
                case Condition { Field: ConditionField.Rule, Value: var value } when Guid.TryParse(value, out var id):
                    rules.Add(id);
                    break;
                case Condition { Operator: ConditionOperator.InList or ConditionOperator.NotInList, Value: var value } when Guid.TryParse(value, out var id):
                    lists.Add(id);
                    break;
            }
        }

        Visit(root);
        return (rules, lists);
    }

    private static void ValidateCondition(RuleDefinition rule, Condition condition, string path, RuleValidationContext context, List<RuleValidationError> errors)
    {
        if (!FieldCatalog.TryGet(condition.Field, out var definition))
        {
            errors.Add(new($"{path}.field", "UnknownField", $"Unknown field '{condition.Field}'."));
            return;
        }

        if (!definition.Operators.Contains(condition.Operator))
        {
            errors.Add(new($"{path}.operator", "OperatorNotAllowed", $"'{condition.Operator}' cannot be used with {condition.Field}."));
            return;
        }

        if (definition.RequiresHeaderName && (string.IsNullOrWhiteSpace(condition.HeaderName) || !HeaderName().IsMatch(condition.HeaderName)))
        {
            errors.Add(new($"{path}.headerName", "InvalidHeaderName", "Enter a header name such as X-Mailer."));
        }

        if (FieldCatalog.IsUnary(condition.Operator))
        {
            return;
        }

        var value = condition.Value?.Trim();
        var valuePath = $"{path}.value";
        if (string.IsNullOrEmpty(value))
        {
            errors.Add(new(valuePath, "ValueRequired", "Enter a value."));
            return;
        }

        if (value.Length > MaxValueLength)
        {
            errors.Add(new(valuePath, "ValueTooLong", $"Values can be at most {MaxValueLength} characters."));
            return;
        }

        switch (condition.Operator)
        {
            case ConditionOperator.Regex when RuleEvaluator.CompileRegex(condition.Value!, condition.CaseSensitive) is null:
                errors.Add(new(valuePath, "InvalidRegex", "Invalid regular expression (backreferences and lookarounds are not supported)."));
                return;
            case ConditionOperator.InList or ConditionOperator.NotInList:
                ValidateListReference(definition, value, valuePath, context, errors);
                return;
            case ConditionOperator.Matches or ConditionOperator.NotMatches:
                if (!Guid.TryParse(value, out var referenced) || !context.Rules.ContainsKey(referenced) && referenced != rule.Id)
                {
                    errors.Add(new(valuePath, "RuleNotFound", "The referenced rule does not exist."));
                }
                else if (referenced == rule.Id)
                {
                    errors.Add(new(valuePath, "SelfReference", "A rule cannot reference itself."));
                }

                return;
        }

        switch (definition.ValueType)
        {
            case FieldValueType.Number when !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _):
                errors.Add(new(valuePath, "InvalidNumber", "Enter a number."));
                break;
            case FieldValueType.Date when !DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _):
                errors.Add(new(valuePath, "InvalidDate", "Enter a date as YYYY-MM-DD."));
                break;
            case FieldValueType.Enum when !definition.Options!.Contains(value, StringComparer.OrdinalIgnoreCase):
                errors.Add(new(valuePath, "InvalidOption", $"Choose one of: {string.Join(", ", definition.Options!)}."));
                break;
        }
    }

    private static void ValidateListReference(FieldDefinition field, string value, string path, RuleValidationContext context, List<RuleValidationError> errors)
    {
        if (!Guid.TryParse(value, out var id) || !context.Lists.TryGetValue(id, out var kind))
        {
            errors.Add(new(path, "ListNotFound", "The list does not exist."));
        }
        else if (field.ValueType == FieldValueType.Domain && kind == NamedListKind.Address)
        {
            errors.Add(new(path, "ListKindMismatch", "Domain conditions need a domain or pattern list."));
        }
    }

    private static void ValidateActions(RuleDefinition rule, List<RuleValidationError> errors)
    {
        if (rule.Actions.Count > MaxActions)
        {
            errors.Add(new("actions", "TooManyActions", $"A rule can have at most {MaxActions} actions."));
        }

        if (rule.Actions.Count(a => a.IsTerminal) > 1)
        {
            errors.Add(new("actions", "MultipleTerminalActions", "A rule can move a message only once (move, delete, spam or archive)."));
        }

        for (var i = 0; i < rule.Actions.Count; i++)
        {
            var path = $"actions[{i}]";
            switch (rule.Actions[i])
            {
                case MoveToAction { Folder: var folder }:
                    AddTemplateErrors(folder, $"{path}.folder");
                    break;
                case CopyToAction { Folder: var folder }:
                    AddTemplateErrors(folder, $"{path}.folder");
                    break;
                case AddKeywordAction { Keyword: var added }:
                    AddKeywordErrors(added, $"{path}.keyword");
                    break;
                case RemoveKeywordAction { Keyword: var removed }:
                    AddKeywordErrors(removed, $"{path}.keyword");
                    break;
            }
        }

        void AddKeywordErrors(string keyword, string path)
        {
            if (string.IsNullOrEmpty(keyword) || keyword.Length > 100 || !Keyword().IsMatch(keyword))
            {
                errors.Add(new(path, "InvalidKeyword", "Keywords consist of letters, digits and - _ . $ without spaces."));
            }
        }

        void AddTemplateErrors(string folder, string path) =>
            errors.AddRange(FolderTemplate.Validate(folder).Select(message => new RuleValidationError(path, "InvalidFolder", message)));
    }

    private static void ValidateReferenceGraph(RuleDefinition rule, RuleValidationContext context, List<RuleValidationError> errors)
    {
        var own = CollectReferences(rule.Conditions).Rules.Where(id => id != rule.Id).ToList();
        if (own.Count == 0)
        {
            return;
        }

        IReadOnlyCollection<Guid> Edges(Guid id) =>
            id == rule.Id ? own : context.References.TryGetValue(id, out var edges) ? edges : [];

        // Cycle: some referenced rule (transitively) references this rule again.
        var path = FindPath(rule.Id, Edges);
        if (path is not null)
        {
            var names = path.Select(id => id == rule.Id ? rule.Name : context.Rules.GetValueOrDefault(id, id.ToString()));
            errors.Add(new("conditions", "ReferenceCycle", $"Rule references form a cycle: {string.Join(" → ", names)}."));
            return;
        }

        if (Depth(rule.Id, Edges, []) > RuleEvaluator.MaxReferenceDepth)
        {
            errors.Add(new("conditions", "ReferenceDepthExceeded", $"Rule references can be nested at most {RuleEvaluator.MaxReferenceDepth} levels deep."));
        }
    }

    /// <summary>Path start → … → start if the graph has a cycle through start, else null.</summary>
    private static List<Guid>? FindPath(Guid start, Func<Guid, IReadOnlyCollection<Guid>> edges)
    {
        var visited = new HashSet<Guid>();
        var stack = new List<Guid> { start };

        bool Visit(Guid node)
        {
            foreach (var next in edges(node))
            {
                if (next == start)
                {
                    stack.Add(start);
                    return true;
                }

                if (visited.Add(next))
                {
                    stack.Add(next);
                    if (Visit(next))
                    {
                        return true;
                    }

                    stack.RemoveAt(stack.Count - 1);
                }
            }

            return false;
        }

        return Visit(start) ? stack : null;
    }

    private static int Depth(Guid node, Func<Guid, IReadOnlyCollection<Guid>> edges, HashSet<Guid> visiting)
    {
        if (!visiting.Add(node))
        {
            return int.MaxValue / 2;
        }

        var children = edges(node);
        var depth = children.Count == 0 ? 0 : 1 + children.Max(child => Depth(child, edges, visiting));
        visiting.Remove(node);
        return depth;
    }

    [GeneratedRegex(@"^[!-9;-~]+$")]
    private static partial Regex HeaderName();

    // IMAP keyword atom: no spaces, parentheses, braces, quotes, wildcards or backslash.
    [GeneratedRegex(@"^[A-Za-z0-9_\-.$&+!#^|~]+$")]
    private static partial Regex Keyword();
}
