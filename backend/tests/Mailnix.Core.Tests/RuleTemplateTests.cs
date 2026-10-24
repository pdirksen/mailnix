using Mailnix.Core.Rules;

namespace Mailnix.Core.Tests;

public sealed class RuleTemplateTests
{
    private static readonly Guid DomainList = Guid.NewGuid();

    public static TheoryData<string> TemplateIds => [.. RuleTemplates.All.Select(t => t.Id)];

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Every_template_builds_a_valid_rule_with_its_defaults(string id)
    {
        var template = RuleTemplates.Find(id)!;
        var values = RuleTemplates.ResolveParameters(template, new Dictionary<string, string> { ["list"] = DomainList.ToString() }, out var errors);
        errors.ShouldBeEmpty();

        var draft = template.Build(values, "Name");
        var rule = new RuleDefinition { Id = Guid.NewGuid(), Name = draft.Name, Conditions = draft.Conditions, Actions = draft.Actions };

        RuleValidator.Validate(rule, new RuleValidationContext { Lists = new Dictionary<Guid, NamedListKind> { [DomainList] = NamedListKind.Domain } })
            .ShouldBeEmpty();
        (draft.Trigger == RuleTriggerKind.Scheduled).ShouldBe(draft.Schedule is not null);
    }

    [Fact]
    public void Invalid_parameters_are_reported()
    {
        RuleTemplates.ResolveParameters(RuleTemplates.Find("newsletter-cleanup")!, new Dictionary<string, string> { ["days"] = "-3", ["folder"] = "A/{nope}" }, out var errors);

        errors.Keys.ShouldBe(["days", "folder"], ignoreOrder: true);
    }

    [Fact]
    public void Megabytes_become_bytes()
    {
        var template = RuleTemplates.Find("large-attachments")!;
        var draft = template.Build(RuleTemplates.ResolveParameters(template, new Dictionary<string, string> { ["megabytes"] = "2.5" }, out _), "Large");

        draft.Conditions.ShouldBe(new Condition(ConditionField.AttachmentSize, ConditionOperator.GreaterThan, "2621440"));
    }
}
