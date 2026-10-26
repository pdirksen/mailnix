using Mailnix.Core.Messages;
using Mailnix.Core.Rules;
using static Mailnix.Core.Tests.TestData;

namespace Mailnix.Core.Tests;

public sealed class RuleTemplateTests
{
    private static readonly Guid DomainList = Guid.NewGuid();
    private static readonly Guid AddressList = Guid.NewGuid();

    private static readonly RuleValidationContext Lists = new()
    {
        Lists = new Dictionary<Guid, NamedListKind> { [DomainList] = NamedListKind.Domain, [AddressList] = NamedListKind.Address },
    };

    public static TheoryData<string> TemplateIds => [.. RuleTemplates.All.Select(t => t.Id)];

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Every_template_builds_a_valid_rule_with_its_defaults(string id)
    {
        var template = RuleTemplates.Find(id)!;
        var draft = Build(id);
        var rule = new RuleDefinition { Id = Guid.NewGuid(), Name = draft.Name, Conditions = draft.Conditions, Actions = draft.Actions };

        RuleValidator.Validate(rule, Lists).ShouldBeEmpty();
        (draft.Trigger == RuleTriggerKind.Scheduled).ShouldBe(draft.Schedule is not null);
        template.Parameters.Select(p => p.Name).ShouldBeUnique();
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

    [Theory]
    [InlineData("orders-shipping", "Your order #4711 has shipped", true)]
    [InlineData("orders-shipping", "Versandbestätigung für Ihre Bestellung", true)]
    [InlineData("orders-shipping", "Ihr Paket kommt heute", true)]
    [InlineData("orders-shipping", "Lunch tomorrow?", false)]
    [InlineData("invoices", "Invoice INV-2026-10", true)]
    [InlineData("invoices", "Ihre Rechnung für Oktober", true)]
    [InlineData("invoices", "Abrechnungsfragen zum Projekt", false)]
    [InlineData("travel", "Buchungsbestätigung Hotel Adler", true)]
    [InlineData("travel", "Your boarding pass for LH123", true)]
    [InlineData("travel", "Meeting notes", false)]
    public void Keyword_templates_match_english_and_german_subjects(string id, string subject, bool expected)
    {
        Matches(Build(id), Simple(subject: subject)).ShouldBe(expected);
    }

    [Theory]
    [InlineData("noreply@github.com", true)]
    [InlineData("No-Reply@accounts.example", true)]
    [InlineData("notifications@service.example", true)]
    [InlineData("alice@example.org", false)]
    public void Notifications_match_machine_senders(string from, bool expected)
    {
        Matches(Build("notifications"), Simple(from: from)).ShouldBe(expected);
    }

    [Fact]
    public void Notifications_leave_newsletters_alone()
    {
        var newsletter = Fixture("newsletter.eml") with { From = new EmailAddress("noreply@shop.example") };

        Matches(Build("notifications"), newsletter).ShouldBeFalse();
        Matches(Build("newsletters-to-folder"), newsletter).ShouldBeTrue();
    }

    [Theory]
    [InlineData("google-invite.eml", true)]
    [InlineData("outlook-invite.eml", true)]
    [InlineData("outlook-reply.eml", true)]
    [InlineData("cc-only.eml", false)]
    public void Calendar_invites_match_google_and_outlook(string fixture, bool expected)
    {
        Matches(Build("calendar-invites"), Fixture(fixture)).ShouldBe(expected);
    }

    private static RuleDraft Build(string id)
    {
        var template = RuleTemplates.Find(id)!;
        var input = template.Parameters
            .Where(p => p.Type is TemplateParameterType.DomainList or TemplateParameterType.AddressList)
            .ToDictionary(p => p.Name, p => (p.Type == TemplateParameterType.AddressList ? AddressList : DomainList).ToString());
        var values = RuleTemplates.ResolveParameters(template, input, out var errors);
        errors.ShouldBeEmpty();
        return template.Build(values, id);
    }

    private static bool Matches(RuleDraft draft, MessageSnapshot message)
    {
        var context = Context();
        var rule = new RuleDefinition { Id = Guid.NewGuid(), Name = draft.Name, Conditions = draft.Conditions, Actions = draft.Actions };
        return RuleEvaluator.Evaluate(rule, new MessageFacts(message, context), context).Matched;
    }
}
