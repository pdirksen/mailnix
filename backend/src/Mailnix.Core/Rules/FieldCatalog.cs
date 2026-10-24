namespace Mailnix.Core.Rules;

public enum FieldCategory
{
    Sender,
    Recipients,
    Content,
    Status,
    MailType,
    SenderTrust,
    Attachments,
    Rules,
}

public enum FieldValueType
{
    Text,

    /// <summary>Email addresses; supports named lists.</summary>
    Address,

    /// <summary>Domain names; supports named lists and subdomain matching.</summary>
    Domain,
    Number,
    Boolean,

    /// <summary>A day, written as yyyy-MM-dd.</summary>
    Date,

    /// <summary>One of <see cref="FieldDefinition.Options"/>.</summary>
    Enum,

    /// <summary>The id of another rule.</summary>
    RuleReference,
}

public enum FieldUnit
{
    None,
    Bytes,
    Days,
}

/// <summary>What has to be fetched from the server, beyond envelope, flags, size and dates (always fetched).</summary>
[Flags]
public enum FetchRequirements
{
    None = 0,
    Headers = 1,
    BodyStructure = 2,
    Body = 4,
}

public sealed record FieldDefinition(
    ConditionField Field,
    FieldCategory Category,
    FieldValueType ValueType,
    IReadOnlyList<ConditionOperator> Operators,
    FetchRequirements Fetch = FetchRequirements.None,
    FieldUnit Unit = FieldUnit.None,
    IReadOnlyList<string>? Options = null,
    bool RequiresHeaderName = false);

public static class FieldCatalog
{
    private static readonly ConditionOperator[] TextOperators =
    [
        ConditionOperator.Equals, ConditionOperator.NotEquals, ConditionOperator.Contains, ConditionOperator.NotContains,
        ConditionOperator.StartsWith, ConditionOperator.EndsWith, ConditionOperator.Regex, ConditionOperator.Exists, ConditionOperator.NotExists,
    ];

    private static readonly ConditionOperator[] AddressOperators = [.. TextOperators, ConditionOperator.InList, ConditionOperator.NotInList];

    private static readonly ConditionOperator[] DomainOperators =
    [
        ConditionOperator.DomainMatches, ConditionOperator.Equals, ConditionOperator.NotEquals, ConditionOperator.Contains,
        ConditionOperator.NotContains, ConditionOperator.EndsWith, ConditionOperator.Regex, ConditionOperator.InList, ConditionOperator.NotInList,
    ];

    private static readonly ConditionOperator[] NumberOperators = [ConditionOperator.GreaterThan, ConditionOperator.LessThan, ConditionOperator.Equals];
    private static readonly ConditionOperator[] BooleanOperators = [ConditionOperator.IsTrue, ConditionOperator.IsFalse];
    private static readonly ConditionOperator[] EnumOperators = [ConditionOperator.Equals, ConditionOperator.NotEquals];

    /// <summary>Authentication-Results values (RFC 8601); "none" also when no result is present.</summary>
    public static readonly IReadOnlyList<string> AuthResultValues = ["pass", "fail", "softfail", "neutral", "none", "temperror", "permerror"];

    private static readonly FieldDefinition[] Definitions =
    [
        new(ConditionField.From, FieldCategory.Sender, FieldValueType.Address, AddressOperators),
        new(ConditionField.FromName, FieldCategory.Sender, FieldValueType.Text, TextOperators),
        new(ConditionField.FromDomain, FieldCategory.Sender, FieldValueType.Domain, DomainOperators),
        new(ConditionField.ReplyTo, FieldCategory.Sender, FieldValueType.Address, AddressOperators),

        new(ConditionField.To, FieldCategory.Recipients, FieldValueType.Address, AddressOperators),
        new(ConditionField.Cc, FieldCategory.Recipients, FieldValueType.Address, AddressOperators),
        new(ConditionField.Recipients, FieldCategory.Recipients, FieldValueType.Address, AddressOperators),
        new(ConditionField.IAmOnlyInCcOrBcc, FieldCategory.Recipients, FieldValueType.Boolean, BooleanOperators),
        new(ConditionField.DeliveredTo, FieldCategory.Recipients, FieldValueType.Address, AddressOperators, FetchRequirements.Headers),
        new(ConditionField.PlusTag, FieldCategory.Recipients, FieldValueType.Text, TextOperators, FetchRequirements.Headers),
        new(ConditionField.RecipientCount, FieldCategory.Recipients, FieldValueType.Number, NumberOperators),

        new(ConditionField.Subject, FieldCategory.Content, FieldValueType.Text, TextOperators),
        new(ConditionField.Header, FieldCategory.Content, FieldValueType.Text, TextOperators, FetchRequirements.Headers, RequiresHeaderName: true),
        new(ConditionField.Body, FieldCategory.Content, FieldValueType.Text, [ConditionOperator.Contains, ConditionOperator.NotContains, ConditionOperator.Regex], FetchRequirements.Body),
        new(ConditionField.Size, FieldCategory.Content, FieldValueType.Number, NumberOperators, Unit: FieldUnit.Bytes),
        new(ConditionField.Date, FieldCategory.Content, FieldValueType.Date, [ConditionOperator.Before, ConditionOperator.After]),
        new(ConditionField.AgeDays, FieldCategory.Content, FieldValueType.Number, NumberOperators, Unit: FieldUnit.Days),

        new(ConditionField.IsUnread, FieldCategory.Status, FieldValueType.Boolean, BooleanOperators),
        new(ConditionField.IsFlagged, FieldCategory.Status, FieldValueType.Boolean, BooleanOperators),
        new(ConditionField.IsAnswered, FieldCategory.Status, FieldValueType.Boolean, BooleanOperators),
        new(ConditionField.Keyword, FieldCategory.Status, FieldValueType.Text, [ConditionOperator.Equals, ConditionOperator.NotEquals, ConditionOperator.Exists, ConditionOperator.NotExists]),

        new(ConditionField.IsNewsletter, FieldCategory.MailType, FieldValueType.Boolean, BooleanOperators, FetchRequirements.Headers),
        new(ConditionField.ListId, FieldCategory.MailType, FieldValueType.Text, TextOperators, FetchRequirements.Headers),
        new(ConditionField.IsAutoReply, FieldCategory.MailType, FieldValueType.Boolean, BooleanOperators, FetchRequirements.Headers),
        new(ConditionField.IsReply, FieldCategory.MailType, FieldValueType.Boolean, BooleanOperators, FetchRequirements.Headers),
        new(ConditionField.IsReplyToMyMail, FieldCategory.MailType, FieldValueType.Boolean, BooleanOperators, FetchRequirements.Headers),

        new(ConditionField.SpfResult, FieldCategory.SenderTrust, FieldValueType.Enum, EnumOperators, FetchRequirements.Headers, Options: AuthResultValues),
        new(ConditionField.DkimResult, FieldCategory.SenderTrust, FieldValueType.Enum, EnumOperators, FetchRequirements.Headers, Options: AuthResultValues),
        new(ConditionField.DmarcResult, FieldCategory.SenderTrust, FieldValueType.Enum, EnumOperators, FetchRequirements.Headers, Options: AuthResultValues),
        new(ConditionField.DisplayNameMismatch, FieldCategory.SenderTrust, FieldValueType.Boolean, BooleanOperators),
        new(ConditionField.SpamScore, FieldCategory.SenderTrust, FieldValueType.Number,
            [ConditionOperator.GreaterThan, ConditionOperator.LessThan, ConditionOperator.Exists, ConditionOperator.NotExists], FetchRequirements.Headers),

        new(ConditionField.HasAttachment, FieldCategory.Attachments, FieldValueType.Boolean, BooleanOperators, FetchRequirements.BodyStructure),
        new(ConditionField.AttachmentCount, FieldCategory.Attachments, FieldValueType.Number, NumberOperators, FetchRequirements.BodyStructure),
        new(ConditionField.AttachmentName, FieldCategory.Attachments, FieldValueType.Text, TextOperators, FetchRequirements.BodyStructure),
        new(ConditionField.AttachmentExtension, FieldCategory.Attachments, FieldValueType.Text, TextOperators, FetchRequirements.BodyStructure),
        new(ConditionField.AttachmentMimeType, FieldCategory.Attachments, FieldValueType.Text, TextOperators, FetchRequirements.BodyStructure),
        new(ConditionField.AttachmentSize, FieldCategory.Attachments, FieldValueType.Number, NumberOperators, FetchRequirements.BodyStructure, FieldUnit.Bytes),

        new(ConditionField.Rule, FieldCategory.Rules, FieldValueType.RuleReference, [ConditionOperator.Matches, ConditionOperator.NotMatches]),
    ];

    private static readonly Dictionary<ConditionField, FieldDefinition> ByField = Definitions.ToDictionary(d => d.Field);

    public static IReadOnlyList<FieldDefinition> All => Definitions;

    public static FieldDefinition Get(ConditionField field) => ByField[field];

    public static bool TryGet(ConditionField field, out FieldDefinition definition) => ByField.TryGetValue(field, out definition!);

    /// <summary>Operators that take no operand.</summary>
    public static bool IsUnary(ConditionOperator op) =>
        op is ConditionOperator.Exists or ConditionOperator.NotExists or ConditionOperator.IsTrue or ConditionOperator.IsFalse;

    /// <summary>What must be fetched to evaluate these rules, including rules they reference.</summary>
    public static FetchRequirements RequiredFetch(IEnumerable<RuleDefinition> rules, IReadOnlyDictionary<Guid, RuleDefinition> allRules)
    {
        var result = FetchRequirements.None;
        var visited = new HashSet<Guid>();

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
                case Condition { Field: ConditionField.Rule } reference:
                    if (Guid.TryParse(reference.Value, out var id) && visited.Add(id) && allRules.TryGetValue(id, out var referenced))
                    {
                        Visit(referenced.Conditions);
                    }

                    break;
                case Condition condition when TryGet(condition.Field, out var definition):
                    result |= definition.Fetch;
                    break;
            }
        }

        foreach (var rule in rules)
        {
            visited.Add(rule.Id);
            Visit(rule.Conditions);
        }

        return result;
    }
}
