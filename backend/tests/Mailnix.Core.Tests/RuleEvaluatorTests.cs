using Mailnix.Core.Messages;
using Mailnix.Core.Rules;
using static Mailnix.Core.Rules.ConditionField;
using static Mailnix.Core.Rules.ConditionOperator;
using static Mailnix.Core.Tests.TestData;

namespace Mailnix.Core.Tests;

public sealed class RuleEvaluatorTests
{
    [Theory]
    [InlineData(Subject, ConditionOperator.Equals, "hello", true)]
    [InlineData(Subject, ConditionOperator.Equals, "Hello world", false)]
    [InlineData(Subject, Contains, "ELL", true)]
    [InlineData(Subject, NotContains, "ell", false)]
    [InlineData(Subject, StartsWith, "he", true)]
    [InlineData(Subject, EndsWith, "lo", true)]
    [InlineData(Subject, ConditionOperator.Regex, "^h.l+o$", true)]
    [InlineData(From, ConditionOperator.Equals, "ALICE@example.org", true)]
    [InlineData(From, EndsWith, "@example.org", true)]
    [InlineData(FromName, ConditionOperator.Equals, "alice", true)]
    [InlineData(FromDomain, DomainMatches, "example.org", true)]
    [InlineData(FromDomain, ConditionOperator.Equals, "EXAMPLE.org.", true)]
    public void Text_operators_are_case_insensitive_by_default(ConditionField field, ConditionOperator op, string value, bool expected)
    {
        Matches(When(field, op, value), Simple()).ShouldBe(expected);
    }

    [Fact]
    public void Case_sensitive_comparison_can_be_requested()
    {
        Matches(When(Subject, Contains, "hello", caseSensitive: true), Simple()).ShouldBeFalse();
        Matches(When(Subject, Contains, "Hello", caseSensitive: true), Simple()).ShouldBeTrue();
        Matches(When(Subject, ConditionOperator.Regex, "^hello$", caseSensitive: true), Simple()).ShouldBeFalse();
    }

    [Theory]
    [InlineData("news@mail.shop.example", true)]
    [InlineData("news@shop.example", true)]
    [InlineData("news@evilshop.example", false)]
    [InlineData("news@shop.example.evil.test", false)]
    public void Domain_matching_includes_subdomains_but_not_lookalikes(string address, bool expected)
    {
        Matches(When(FromDomain, DomainMatches, "shop.example"), Simple(from: address)).ShouldBe(expected);
    }

    [Fact]
    public void Domain_matching_normalizes_internationalized_domains()
    {
        Matches(When(FromDomain, DomainMatches, "müller-shop.de"), Simple(from: "info@MÜLLER-shop.de")).ShouldBeTrue();
    }

    [Fact]
    public void Negated_operators_on_multi_value_fields_mean_none_matches()
    {
        var message = Simple() with { To = [new("me@example.com"), new("boss@example.com")] };

        Matches(When(ConditionField.To, NotEquals, "boss@example.com"), message).ShouldBeFalse();
        Matches(When(ConditionField.To, NotEquals, "other@example.com"), message).ShouldBeTrue();
        Matches(When(ConditionField.To, ConditionOperator.Equals, "boss@example.com"), message).ShouldBeTrue();
    }

    [Fact]
    public void Exists_checks_for_any_non_empty_value()
    {
        var message = Fixture("newsletter.eml");

        Matches(When(Header, Exists, headerName: "list-unsubscribe"), message).ShouldBeTrue();
        Matches(When(Header, NotExists, headerName: "X-Not-There"), message).ShouldBeTrue();
        Matches(When(Header, Contains, "one-click", headerName: "List-Unsubscribe-Post"), message).ShouldBeTrue();
    }

    [Theory]
    [InlineData(Size, GreaterThan, "1000", true)]
    [InlineData(Size, LessThan, "1000", false)]
    [InlineData(Size, ConditionOperator.Equals, "2048", true)]
    [InlineData(AgeDays, LessThan, "1", true)]
    [InlineData(AgeDays, GreaterThan, "0.01", true)]
    [InlineData(RecipientCount, ConditionOperator.Equals, "1", true)]
    public void Number_operators(ConditionField field, ConditionOperator op, string value, bool expected)
    {
        Matches(When(field, op, value), Simple()).ShouldBe(expected);
    }

    [Fact]
    public void Age_in_days_is_measured_from_arrival()
    {
        var old = Simple(receivedAt: Now.AddDays(-45));

        Matches(When(AgeDays, GreaterThan, "30"), old).ShouldBeTrue();
        Matches(When(AgeDays, GreaterThan, "30"), Simple()).ShouldBeFalse();
    }

    [Fact]
    public void Date_boundaries_use_the_users_time_zone()
    {
        // 23:30 UTC on Oct 19 is already Oct 20 in Berlin.
        var message = Simple(receivedAt: new DateTimeOffset(2026, 10, 19, 23, 30, 0, TimeSpan.Zero));

        Matches(When(ConditionField.Date, Before, "2026-10-20"), message).ShouldBeFalse();
        Matches(When(ConditionField.Date, After, "2026-10-19"), message).ShouldBeTrue();
        Matches(When(ConditionField.Date, Before, "2026-10-20"), message, Context(c => c with { TimeZone = TimeZoneInfo.Utc })).ShouldBeTrue();
    }

    [Fact]
    public void Boolean_and_enum_fields()
    {
        var phishing = Fixture("phishing.eml");

        Matches(When(IsUnread, IsTrue), Simple()).ShouldBeTrue();
        Matches(When(IsUnread, IsTrue), Simple(isSeen: true)).ShouldBeFalse();
        Matches(When(DmarcResult, ConditionOperator.Equals, "FAIL"), phishing).ShouldBeTrue();
        Matches(When(SpfResult, NotEquals, "pass"), phishing).ShouldBeTrue();
        Matches(When(DisplayNameMismatch, IsTrue), phishing).ShouldBeTrue();
        Matches(When(SpamScore, GreaterThan, "5"), phishing).ShouldBeTrue();
        Matches(When(SpamScore, NotExists), Simple() with { Headers = [] }).ShouldBeTrue();
    }

    [Fact]
    public void Attachment_and_recipient_fields()
    {
        var invoice = Fixture("plus-addressed.eml");

        Matches(When(HasAttachment, IsTrue), invoice).ShouldBeTrue();
        Matches(When(AttachmentExtension, ConditionOperator.Equals, "PDF"), invoice).ShouldBeTrue();
        Matches(When(AttachmentMimeType, StartsWith, "image/"), invoice).ShouldBeTrue();
        Matches(When(AttachmentCount, ConditionOperator.Equals, "2"), invoice).ShouldBeTrue();
        Matches(When(PlusTag, ConditionOperator.Equals, "shop"), invoice).ShouldBeTrue();
        Matches(When(DeliveredTo, ConditionOperator.Equals, "me+shop@example.com"), invoice).ShouldBeTrue();
        Matches(When(Recipients, Contains, "accounting@"), invoice).ShouldBeTrue();
        Matches(When(IAmOnlyInCcOrBcc, IsTrue), Fixture("cc-only.eml")).ShouldBeTrue();
    }

    [Fact]
    public void Groups_combine_children()
    {
        var newsletter = When(IsNewsletter, IsTrue);
        var fromShop = When(FromDomain, DomainMatches, "shop.example");
        var fromPartner = When(FromDomain, DomainMatches, "partner.example");
        var message = Fixture("newsletter.eml");

        Matches(All(newsletter, fromShop), message).ShouldBeTrue();
        Matches(All(newsletter, fromPartner), message).ShouldBeFalse();
        Matches(Any(fromPartner, fromShop), message).ShouldBeTrue();
        Matches(None(fromPartner), message).ShouldBeTrue();
        Matches(None(fromPartner, fromShop), message).ShouldBeFalse();
        Matches(All(newsletter, None(When(Subject, Contains, "Rechnung"))), message).ShouldBeTrue();
    }

    [Fact]
    public void Trace_reports_every_node_with_actual_values()
    {
        var rule = Rule(All(When(Subject, Contains, "nope"), When(FromDomain, DomainMatches, "example.org")));
        var context = Context();

        var traced = RuleEvaluator.Evaluate(rule, new MessageFacts(Simple(), context), context, trace: true);
        var quick = RuleEvaluator.Evaluate(rule, new MessageFacts(Simple(), context), context);

        traced.Matched.ShouldBeFalse();
        quick.Trace.ShouldBeNull();
        var root = traced.Trace.ShouldNotBeNull();
        root.Result.ShouldBe(traced.Matched);
        root.Operator.ShouldBe(GroupOperator.All);
        root.Children.Count.ShouldBe(2); // no short-circuit when tracing
        root.Children[0].Result.ShouldBeFalse();
        root.Children[0].Actual.ShouldBe(["Hello"]);
        root.Children[1].Result.ShouldBeTrue();
        root.Children[1].Actual.ShouldBe(["example.org"]);
    }

    [Fact]
    public void Fields_that_were_not_fetched_are_marked_in_the_trace()
    {
        var context = Context();
        var rule = Rule(When(IsNewsletter, IsTrue));

        var result = RuleEvaluator.Evaluate(rule, new MessageFacts(Simple(), context), context, trace: true);

        result.Matched.ShouldBeFalse();
        result.Trace!.Note.ShouldBe(TraceNote.NotLoaded);
    }

    [Fact]
    public void Invalid_regex_never_matches()
    {
        var context = Context();
        var result = RuleEvaluator.Evaluate(Rule(When(Subject, ConditionOperator.Regex, "(unclosed")), new MessageFacts(Simple(), context), context, trace: true);

        result.Matched.ShouldBeFalse();
        result.Trace!.Note.ShouldBe(TraceNote.InvalidValue);
    }

    [Fact]
    public void Named_lists_match_addresses_domains_and_patterns()
    {
        var vip = new NamedList(Guid.NewGuid(), "VIP", NamedListKind.Address, ["Boss@Example.com", "alice@example.org"]);
        var shops = new NamedList(Guid.NewGuid(), "Shops", NamedListKind.Domain, ["shop.example", "@store.example"]);
        var noreply = new NamedList(Guid.NewGuid(), "No-reply", NamedListKind.Pattern, ["noreply@*", "*@*.bounce.example"]);
        var context = Context(c => c with { NamedLists = new[] { vip, shops, noreply }.ToDictionary(l => l.Id) });

        Matches(When(From, InList, vip.Id.ToString()), Simple(), context).ShouldBeTrue();
        Matches(When(From, InList, vip.Id.ToString()), Simple(from: "boss+news@example.com"), context).ShouldBeTrue();
        Matches(When(From, NotInList, vip.Id.ToString()), Simple(from: "eve@example.org"), context).ShouldBeTrue();
        Matches(When(FromDomain, InList, shops.Id.ToString()), Simple(from: "a@mail.shop.example"), context).ShouldBeTrue();
        Matches(When(From, InList, shops.Id.ToString()), Simple(from: "a@store.example"), context).ShouldBeTrue();
        Matches(When(From, InList, noreply.Id.ToString()), Simple(from: "NoReply@service.example"), context).ShouldBeTrue();
        Matches(When(From, InList, noreply.Id.ToString()), Simple(from: "x@mail.bounce.example"), context).ShouldBeTrue();
        Matches(When(From, InList, noreply.Id.ToString()), Simple(), context).ShouldBeFalse();
    }

    [Fact]
    public void Missing_lists_match_in_neither_direction()
    {
        var missing = Guid.NewGuid().ToString();

        Matches(When(From, InList, missing), Simple()).ShouldBeFalse();
        Matches(When(From, NotInList, missing), Simple()).ShouldBeFalse();
    }

    [Fact]
    public void Rule_references_evaluate_the_referenced_conditions()
    {
        var newsletters = Rule(When(IsNewsletter, IsTrue), name: "Newsletters", enabled: false);
        var vipSenders = Rule(When(FromDomain, DomainMatches, "partner.example"), name: "VIP");
        var context = Context(c => c with { Rules = new[] { newsletters, vipSenders }.ToDictionary(r => r.Id) });
        var conditions = All(
            When(ConditionField.Rule, ConditionOperator.Matches, newsletters.Id.ToString()),
            When(ConditionField.Rule, NotMatches, vipSenders.Id.ToString()));

        // The referenced rule is disabled: it still works as a filter.
        Matches(conditions, Fixture("newsletter.eml"), context).ShouldBeTrue();
        Matches(conditions, Fixture("out-of-office.eml"), context).ShouldBeFalse();

        var traced = RuleEvaluator.Evaluate(Rule(conditions), new MessageFacts(Fixture("newsletter.eml"), context), context, trace: true);
        traced.Trace!.Children[0].ReferencedRuleName.ShouldBe("Newsletters");
        traced.Trace.Children[0].Children.Single().Result.ShouldBeTrue();
    }

    [Fact]
    public void Transitive_references_work_and_unknown_rules_never_match()
    {
        var a = Rule(When(IsNewsletter, IsTrue), name: "A");
        var b = Rule(When(ConditionField.Rule, ConditionOperator.Matches, a.Id.ToString()), name: "B");
        var context = Context(c => c with { Rules = new[] { a, b }.ToDictionary(r => r.Id) });

        Matches(When(ConditionField.Rule, ConditionOperator.Matches, b.Id.ToString()), Fixture("newsletter.eml"), context).ShouldBeTrue();
        Matches(When(ConditionField.Rule, NotMatches, Guid.NewGuid().ToString()), Fixture("newsletter.eml"), context).ShouldBeFalse();
    }

    [Fact]
    public void Reference_cycles_are_cut_at_runtime()
    {
        // Validation prevents cycles; should one exist anyway (e.g. edited database), evaluation must still terminate.
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var a = Rule(When(ConditionField.Rule, ConditionOperator.Matches, idB.ToString()), id: idA, name: "A");
        var b = Rule(When(ConditionField.Rule, NotMatches, idA.ToString()), id: idB, name: "B");
        var context = Context(c => c with { Rules = new[] { a, b }.ToDictionary(r => r.Id) });

        var result = RuleEvaluator.Evaluate(a, new MessageFacts(Simple(), context), context, trace: true);

        result.Matched.ShouldBeFalse();
        // A → B → (A again: cut)
        result.Trace!.ReferencedRuleName.ShouldBe("B");
        result.Trace.Children.Single().Note.ShouldBe(TraceNote.ReferenceDepthExceeded);
    }
}
