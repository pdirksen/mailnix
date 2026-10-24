using Mailnix.Core.Rules;
using static Mailnix.Core.Rules.ConditionField;
using static Mailnix.Core.Rules.ConditionOperator;
using static Mailnix.Core.Tests.TestData;

namespace Mailnix.Core.Tests;

public sealed class RuleValidatorTests
{
    private static readonly Guid ListId = Guid.NewGuid();
    private static readonly Guid AddressListId = Guid.NewGuid();

    private static RuleValidationContext Context(
        IReadOnlyDictionary<Guid, string>? rules = null,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<Guid>>? references = null) => new()
        {
            Lists = new Dictionary<Guid, NamedListKind> { [ListId] = NamedListKind.Domain, [AddressListId] = NamedListKind.Address },
            Rules = rules ?? new Dictionary<Guid, string>(),
            References = references ?? new Dictionary<Guid, IReadOnlyCollection<Guid>>(),
        };

    private static IReadOnlyList<string> Codes(RuleDefinition rule, RuleValidationContext? context = null) =>
        [.. RuleValidator.Validate(rule, context ?? Context()).Select(e => e.Code)];

    [Fact]
    public void A_complete_rule_is_valid()
    {
        var rule = Rule(
            All(When(FromDomain, InList, ListId.ToString()), When(AgeDays, GreaterThan, "30"), Any(When(IsNewsletter, IsTrue))),
            [new MarkReadAction(), new MoveToAction("INBOX/Archive/{year}")]);

        RuleValidator.Validate(rule, Context()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(IsNewsletter, Contains, "x", "OperatorNotAllowed")]
    [InlineData(Subject, Contains, null, "ValueRequired")]
    [InlineData(Size, GreaterThan, "1 MB", "InvalidNumber")]
    [InlineData(ConditionField.Date, Before, "24.10.2026", "InvalidDate")]
    [InlineData(DmarcResult, ConditionOperator.Equals, "broken", "InvalidOption")]
    [InlineData(Subject, ConditionOperator.Regex, "(?<=x)y", "InvalidRegex")]
    [InlineData(Subject, ConditionOperator.Regex, "([a-z]", "InvalidRegex")]
    public void Invalid_conditions_are_reported(ConditionField field, ConditionOperator op, string? value, string code)
    {
        Codes(Rule(When(field, op, value))).ShouldBe([code]);
    }

    [Fact]
    public void Header_conditions_need_a_valid_header_name()
    {
        Codes(Rule(When(Header, Contains, "x"))).ShouldBe(["InvalidHeaderName"]);
        Codes(Rule(When(Header, Contains, "x", headerName: "X Bad"))).ShouldBe(["InvalidHeaderName"]);
        Codes(Rule(When(Header, Contains, "x", headerName: "X-Mailer"))).ShouldBeEmpty();
    }

    [Fact]
    public void Lists_must_exist_and_fit_the_field()
    {
        Codes(Rule(When(From, InList, Guid.NewGuid().ToString()))).ShouldBe(["ListNotFound"]);
        Codes(Rule(When(FromDomain, InList, AddressListId.ToString()))).ShouldBe(["ListKindMismatch"]);
        Codes(Rule(When(From, InList, ListId.ToString()))).ShouldBeEmpty();
    }

    [Fact]
    public void Rules_need_conditions_and_groups_need_children()
    {
        Codes(Rule(All())).ShouldBe(["EmptyGroup", "NoConditions"]);
        Codes(Rule(All(When(Subject, Exists), Any()))).ShouldBe(["EmptyGroup"]);
    }

    [Fact]
    public void Actions_are_checked()
    {
        var rule = Rule(When(Subject, Exists),
        [
            new MoveToAction("Archive/{oops}"),
            new DeleteAction(),
            new AddKeywordAction("has space"),
        ]);

        Codes(rule).ShouldBe(["MultipleTerminalActions", "InvalidFolder", "InvalidKeyword"]);
    }

    [Fact]
    public void Referenced_rules_must_exist_and_not_be_the_rule_itself()
    {
        var id = Guid.NewGuid();

        Codes(Rule(When(ConditionField.Rule, ConditionOperator.Matches, Guid.NewGuid().ToString()))).ShouldBe(["RuleNotFound"]);
        Codes(Rule(When(ConditionField.Rule, ConditionOperator.Matches, id.ToString()), id: id)).ShouldBe(["SelfReference"]);
    }

    [Fact]
    public void Reference_cycles_are_rejected_with_the_path()
    {
        // Existing: B references C, C references A. Saving A with a reference to B closes the cycle.
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var context = Context(
            rules: new Dictionary<Guid, string> { [b] = "B", [c] = "C" },
            references: new Dictionary<Guid, IReadOnlyCollection<Guid>> { [b] = [c], [c] = [a] });

        var errors = RuleValidator.Validate(Rule(When(ConditionField.Rule, ConditionOperator.Matches, b.ToString()), id: a, name: "A"), context);

        errors.Single().Code.ShouldBe("ReferenceCycle");
        errors.Single().Message.ShouldContain("A → B → C → A");
    }

    [Fact]
    public void Reference_chains_deeper_than_the_limit_are_rejected()
    {
        var chain = Enumerable.Range(0, RuleEvaluator.MaxReferenceDepth + 1).Select(_ => Guid.NewGuid()).ToList();
        var references = chain.Zip(chain.Skip(1)).ToDictionary(p => p.First, p => (IReadOnlyCollection<Guid>)[p.Second]);
        var context = Context(rules: chain.ToDictionary(id => id, id => id.ToString()), references: references);

        var deep = Rule(When(ConditionField.Rule, ConditionOperator.Matches, chain[0].ToString()));
        var shallow = Rule(When(ConditionField.Rule, ConditionOperator.Matches, chain[2].ToString()));

        Codes(deep, context).ShouldBe(["ReferenceDepthExceeded"]);
        Codes(shallow, context).ShouldBeEmpty();
    }

    [Fact]
    public void References_are_collected_for_dependency_tracking()
    {
        var (rule, list) = (Guid.NewGuid(), Guid.NewGuid());

        var (rules, lists) = RuleValidator.CollectReferences(All(
            When(ConditionField.Rule, NotMatches, rule.ToString()),
            Any(When(From, NotInList, list.ToString()), When(Subject, Contains, "x"))));

        rules.ShouldBe([rule]);
        lists.ShouldBe([list]);
    }
}
