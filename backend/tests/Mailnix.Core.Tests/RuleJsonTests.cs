using System.Text.Json;
using Mailnix.Core.Rules;
using static Mailnix.Core.Tests.TestData;

namespace Mailnix.Core.Tests;

public sealed class RuleJsonTests
{
    [Fact]
    public void Conditions_and_actions_round_trip()
    {
        ConditionNode conditions = All(
            When(ConditionField.FromDomain, ConditionOperator.DomainMatches, "shop.example"),
            None(When(ConditionField.Header, ConditionOperator.Exists, headerName: "X-Important")));
        IReadOnlyList<RuleAction> actions = [new MarkReadAction(), new MoveToAction("INBOX/Shops/{fromDomain}")];

        var conditionJson = JsonSerializer.Serialize(conditions, RuleJson.Options);
        var actionJson = JsonSerializer.Serialize(actions, RuleJson.Options);

        conditionJson.ShouldStartWith("""{"kind":"group","operator":"All","children":[{"kind":"condition","field":"FromDomain","operator":"DomainMatches","value":"shop.example""");
        actionJson.ShouldBe("""[{"type":"MarkRead"},{"type":"MoveTo","folder":"INBOX/Shops/{fromDomain}"}]""");
        JsonSerializer.Serialize(JsonSerializer.Deserialize<ConditionNode>(conditionJson, RuleJson.Options), RuleJson.Options).ShouldBe(conditionJson);
        JsonSerializer.Deserialize<IReadOnlyList<RuleAction>>(actionJson, RuleJson.Options).ShouldBe(actions);
    }

    [Fact]
    public void Discriminators_may_come_after_other_properties()
    {
        var node = JsonSerializer.Deserialize<ConditionNode>(
            """{"field":"Subject","operator":"Contains","value":"Invoice","kind":"condition"}""", RuleJson.Options);

        node.ShouldBe(new Condition(ConditionField.Subject, ConditionOperator.Contains, "Invoice"));
    }
}
