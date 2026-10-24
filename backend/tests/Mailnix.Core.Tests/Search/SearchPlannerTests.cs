using Mailnix.Core.Rules;
using static Mailnix.Core.Rules.ConditionField;
using static Mailnix.Core.Rules.ConditionOperator;
using static Mailnix.Core.Tests.TestData;

namespace Mailnix.Core.Tests.Search;

public sealed class SearchPlannerTests
{
    private static readonly RuleAction[] Flag = [new FlagAction()];

    private static string? Plan(ConditionNode conditions, EvaluationContext? context = null) =>
        SearchPlanner.Plan([Rule(conditions, Flag)], context ?? Context())?.Describe();

    [Fact]
    public void Text_conditions_become_header_searches()
    {
        Plan(All(When(From, Contains, "shop"), When(Subject, StartsWith, "Invoice"))).ShouldBe("FROM \"shop\" SUBJECT \"Invoice\"");
        Plan(When(FromDomain, DomainMatches, "Shop.Example")).ShouldBe("FROM \"shop.example\"");
        Plan(When(Recipients, ConditionOperator.Equals, "Me@Example.com")).ShouldBe("TO \"me@example.com\" OR CC \"me@example.com\"");
        Plan(When(Header, Contains, "high", headerName: "X-Priority")).ShouldBe("HEADER X-Priority \"high\"");
        Plan(When(ListId, Exists)).ShouldBe("HEADER List-Id \"\"");
    }

    [Fact]
    public void Flags_sizes_and_dates_become_their_search_keys()
    {
        Plan(All(When(IsUnread, IsTrue), When(IsFlagged, IsFalse))).ShouldBe("UNSEEN UNFLAGGED");
        Plan(When(Size, GreaterThan, "1048576")).ShouldBe("LARGER 1048576");
        Plan(When(Size, ConditionOperator.Equals, "100")).ShouldBe("LARGER 99 SMALLER 101");
        Plan(When(Keyword, ConditionOperator.Equals, "$Label1")).ShouldBe("KEYWORD $Label1");

        // Now is 2026-10-24 12:00 UTC; dates get two days of slack for the server's time zone.
        Plan(When(AgeDays, GreaterThan, "30")).ShouldBe("BEFORE 2026-09-26");
        Plan(When(AgeDays, LessThan, "7")).ShouldBe("SINCE 2026-10-15");
        Plan(When(Date, Before, "2026-03-01")).ShouldBe("BEFORE 2026-03-02");
        Plan(When(Date, After, "2026-03-01")).ShouldBe("SINCE 2026-02-27");
    }

    [Fact]
    public void Conditions_the_server_cannot_check_are_left_to_local_evaluation()
    {
        // Left out of an AND: the search still narrows down by the rest.
        Plan(All(When(From, Contains, "shop"), When(IsNewsletter, IsTrue), When(Subject, Regex, "^deal"))).ShouldBe("FROM \"shop\"");

        // In an OR, one unknown branch means any message could match.
        Plan(Any(When(From, Contains, "shop"), When(Body, Contains, "unsubscribe"))).ShouldBeNull();
        Plan(When(Subject, NotContains, "invoice")).ShouldBeNull();

        // Non-ASCII values are compared locally only (servers differ in charset support).
        Plan(When(Subject, Contains, "Rechnung für")).ShouldBeNull();
        Plan(When(FromDomain, DomainMatches, "müller.de")).ShouldBeNull();
    }

    [Fact]
    public void Only_exact_conditions_are_negated()
    {
        Plan(None(When(IsFlagged, IsTrue), When(Keyword, NotEquals, "x"))).ShouldBeNull();
        Plan(None(When(IsFlagged, IsTrue), When(IsUnread, IsTrue))).ShouldBe("NOT (FLAGGED OR UNSEEN)");

        // FROM "shop" also finds "shop" in display names, so it cannot be negated.
        Plan(None(When(From, Contains, "shop"))).ShouldBeNull();
    }

    [Fact]
    public void Rule_references_are_resolved()
    {
        var unread = Rule(When(IsUnread, IsTrue), name: "Unread");
        var shop = Rule(When(From, Contains, "shop"), name: "Shop");
        var context = Context(c => c with { Rules = new Dictionary<Guid, RuleDefinition> { [unread.Id] = unread, [shop.Id] = shop } });

        Plan(When(ConditionField.Rule, ConditionOperator.Matches, shop.Id.ToString()), context).ShouldBe("FROM \"shop\"");
        Plan(When(ConditionField.Rule, NotMatches, unread.Id.ToString()), context).ShouldBe("NOT UNSEEN");
        Plan(When(ConditionField.Rule, NotMatches, shop.Id.ToString()), context).ShouldBeNull();
        Plan(When(ConditionField.Rule, ConditionOperator.Matches, Guid.NewGuid().ToString()), context).ShouldBeNull();
    }

    [Fact]
    public void Small_address_and_domain_lists_become_an_or()
    {
        var vips = new NamedList(Guid.NewGuid(), "VIP", NamedListKind.Address, ["boss@example.com", "ceo@example.com"]);
        var patterns = new NamedList(Guid.NewGuid(), "Patterns", NamedListKind.Pattern, ["*@shop.*"]);
        var context = Context(c => c with { NamedLists = new Dictionary<Guid, NamedList> { [vips.Id] = vips, [patterns.Id] = patterns } });

        Plan(When(From, InList, vips.Id.ToString()), context).ShouldBe("FROM \"boss@example.com\" OR FROM \"ceo@example.com\"");
        Plan(When(From, InList, patterns.Id.ToString()), context).ShouldBeNull();
        Plan(When(From, NotInList, vips.Id.ToString()), context).ShouldBeNull();
    }

    [Fact]
    public void Several_rules_are_combined_and_rules_without_actions_are_ignored()
    {
        var context = Context();
        var newsletters = Rule(When(From, Contains, "news"), Flag);
        var invoices = Rule(When(Subject, Contains, "invoice"), Flag);
        var filterOnly = Rule(When(IsNewsletter, IsTrue));

        SearchPlanner.Plan([newsletters, invoices, filterOnly], context)!.Describe().ShouldBe("FROM \"news\" OR SUBJECT \"invoice\"");
        SearchPlanner.Plan([newsletters, Rule(When(IsNewsletter, IsTrue), Flag)], context).ShouldBeNull();
        SearchPlanner.Plan([filterOnly], context).ShouldBeNull();
    }
}
