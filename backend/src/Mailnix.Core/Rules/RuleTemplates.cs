using System.Globalization;

namespace Mailnix.Core.Rules;

public enum TemplateParameterType
{
    /// <summary>A folder template (e.g. INBOX/Archive).</summary>
    Folder,

    /// <summary>A whole number of days.</summary>
    Days,

    /// <summary>Megabytes.</summary>
    Megabytes,

    /// <summary>A named list of domains or patterns.</summary>
    DomainList,

    /// <summary>A named list of exact addresses.</summary>
    AddressList,
}

public sealed record TemplateParameter(string Name, TemplateParameterType Type, string? Default);

/// <summary>A rule as produced by a template: not saved yet, opened in the builder for preview and editing.</summary>
public sealed record RuleDraft(
    string Name,
    ConditionNode Conditions,
    IReadOnlyList<RuleAction> Actions,
    RuleTriggerKind Trigger,
    string? Schedule);

/// <summary>Mirrors the storage-level trigger without depending on it.</summary>
public enum RuleTriggerKind
{
    OnNew,
    Scheduled,
    Manual,
}

/// <summary>
/// A built-in starting point. Names and descriptions are translated in the UI by <see cref="Id"/>; the default
/// rule name passed to <see cref="Build"/> lets the server stay language-neutral.
/// </summary>
public sealed record RuleTemplate(string Id, IReadOnlyList<TemplateParameter> Parameters, Func<IReadOnlyDictionary<string, string>, string, RuleDraft> Build);

public static class RuleTemplates
{
    private const string Daily = "0 3 * * *";

    // Keyword patterns cover English and German (the UI languages). Only the start of a word is anchored, so German
    // compounds like "Versandbestätigung" match too.
    private const string NotificationSenders = @"^(no-?reply|do-?not-?reply|notifications?|notify|alerts?|mailer-daemon|postmaster)@";
    private const string OrderSubjects = @"\b(order|bestell|shipped|shipping|shipment|versand|versendet|liefer|delivery|delivered|tracking|sendung|paket|parcel)";
    private const string InvoiceSubjects = @"\b(invoice|receipt|rechnung|quittung|zahlungsbeleg|kassenbeleg|kontoauszug|zahlungsbest|payment confirmation)";
    private const string InvoiceAttachments = @"\b(invoice|receipt|rechnung|quittung)";
    private const string TravelSubjects = @"\b(booking|buchung|reservation|reservierung|itinerary|reiseplan|boarding|check-in|flight|flug|e-ticket|hotel)";

    public static readonly IReadOnlyList<RuleTemplate> All =
    [
        new("newsletters-to-folder",
            [new("folder", TemplateParameterType.Folder, "INBOX/Newsletters")],
            (p, name) => new RuleDraft(name,
                new Condition(ConditionField.IsNewsletter, ConditionOperator.IsTrue),
                [new MoveToAction(p["folder"])],
                RuleTriggerKind.OnNew, null)),

        new("newsletter-cleanup",
            [new("days", TemplateParameterType.Days, "30"), new("folder", TemplateParameterType.Folder, "INBOX/Archive/Newsletters/{year}")],
            (p, name) => new RuleDraft(name,
                new ConditionGroup(GroupOperator.All,
                [
                    new Condition(ConditionField.IsNewsletter, ConditionOperator.IsTrue),
                    new Condition(ConditionField.AgeDays, ConditionOperator.GreaterThan, p["days"]),
                ]),
                [new MarkReadAction(), new MoveToAction(p["folder"])],
                RuleTriggerKind.Scheduled, Daily)),

        new("archive-by-year",
            [new("days", TemplateParameterType.Days, "90"), new("folder", TemplateParameterType.Folder, "INBOX/Archive/{year}")],
            (p, name) => new RuleDraft(name,
                new ConditionGroup(GroupOperator.All,
                [
                    new Condition(ConditionField.AgeDays, ConditionOperator.GreaterThan, p["days"]),
                    new Condition(ConditionField.IsFlagged, ConditionOperator.IsFalse),
                ]),
                [new MoveToAction(p["folder"])],
                RuleTriggerKind.Scheduled, Daily)),

        new("sort-by-sender-domain",
            [new("list", TemplateParameterType.DomainList, null), new("folder", TemplateParameterType.Folder, "INBOX/Senders/{fromDomain}")],
            (p, name) => new RuleDraft(name,
                new Condition(ConditionField.FromDomain, ConditionOperator.InList, p["list"]),
                [new MoveToAction(p["folder"])],
                RuleTriggerKind.OnNew, null)),

        new("auto-replies",
            [new("folder", TemplateParameterType.Folder, "INBOX/Auto-Replies")],
            (p, name) => new RuleDraft(name,
                new Condition(ConditionField.IsAutoReply, ConditionOperator.IsTrue),
                [new MarkReadAction(), new MoveToAction(p["folder"])],
                RuleTriggerKind.OnNew, null)),

        new("shops-by-plus-tag",
            [new("folder", TemplateParameterType.Folder, "INBOX/Shops/{plusTag}")],
            (p, name) => new RuleDraft(name,
                new Condition(ConditionField.PlusTag, ConditionOperator.Exists),
                [new MoveToAction(p["folder"])],
                RuleTriggerKind.OnNew, null)),

        new("quarantine-failed-dmarc",
            [],
            (_, name) => new RuleDraft(name,
                new ConditionGroup(GroupOperator.Any,
                [
                    new Condition(ConditionField.DmarcResult, ConditionOperator.Equals, "fail"),
                    new Condition(ConditionField.DisplayNameMismatch, ConditionOperator.IsTrue),
                ]),
                [new MarkAsSpamAction()],
                RuleTriggerKind.OnNew, null)),

        new("notifications",
            [new("folder", TemplateParameterType.Folder, "INBOX/Notifications")],
            (p, name) => new RuleDraft(name,
                new ConditionGroup(GroupOperator.All,
                [
                    new Condition(ConditionField.From, ConditionOperator.Regex, NotificationSenders),
                    new Condition(ConditionField.IsNewsletter, ConditionOperator.IsFalse),
                ]),
                [new MoveToAction(p["folder"])],
                RuleTriggerKind.OnNew, null)),

        new("orders-shipping",
            [new("folder", TemplateParameterType.Folder, "INBOX/Orders")],
            (p, name) => new RuleDraft(name,
                new Condition(ConditionField.Subject, ConditionOperator.Regex, OrderSubjects),
                [new MoveToAction(p["folder"])],
                RuleTriggerKind.OnNew, null)),

        new("invoices",
            [new("folder", TemplateParameterType.Folder, "INBOX/Invoices")],
            (p, name) => new RuleDraft(name,
                new ConditionGroup(GroupOperator.Any,
                [
                    new Condition(ConditionField.Subject, ConditionOperator.Regex, InvoiceSubjects),
                    new Condition(ConditionField.AttachmentName, ConditionOperator.Regex, InvoiceAttachments),
                ]),
                [new MoveToAction(p["folder"])],
                RuleTriggerKind.OnNew, null)),

        new("travel",
            [new("folder", TemplateParameterType.Folder, "INBOX/Travel")],
            (p, name) => new RuleDraft(name,
                new ConditionGroup(GroupOperator.Any,
                [
                    new Condition(ConditionField.Subject, ConditionOperator.Regex, TravelSubjects),
                    new Condition(ConditionField.AttachmentExtension, ConditionOperator.Equals, "pkpass"),
                ]),
                [new MoveToAction(p["folder"])],
                RuleTriggerKind.OnNew, null)),

        new("social",
            [new("list", TemplateParameterType.DomainList, null), new("folder", TemplateParameterType.Folder, "INBOX/Social")],
            (p, name) => new RuleDraft(name,
                new Condition(ConditionField.FromDomain, ConditionOperator.InList, p["list"]),
                [new MoveToAction(p["folder"])],
                RuleTriggerKind.OnNew, null)),

        new("calendar-invites",
            [new("folder", TemplateParameterType.Folder, "INBOX/Calendar")],
            (p, name) => new RuleDraft(name,
                new Condition(ConditionField.IsCalendarMessage, ConditionOperator.IsTrue),
                [new MoveToAction(p["folder"])],
                RuleTriggerKind.OnNew, null)),

        new("cc-only-fyi",
            [new("folder", TemplateParameterType.Folder, "INBOX/FYI")],
            (p, name) => new RuleDraft(name,
                new Condition(ConditionField.IAmOnlyInCcOrBcc, ConditionOperator.IsTrue),
                [new MoveToAction(p["folder"])],
                RuleTriggerKind.OnNew, null)),

        new("vip-flag",
            [new("list", TemplateParameterType.AddressList, null)],
            (p, name) => new RuleDraft(name,
                new Condition(ConditionField.From, ConditionOperator.InList, p["list"]),
                [new FlagAction()],
                RuleTriggerKind.OnNew, null)),

        new("large-attachments",
            [new("megabytes", TemplateParameterType.Megabytes, "10")],
            (p, name) => new RuleDraft(name,
                new Condition(ConditionField.AttachmentSize, ConditionOperator.GreaterThan,
                    (double.Parse(p["megabytes"], CultureInfo.InvariantCulture) * 1024 * 1024).ToString("0", CultureInfo.InvariantCulture)),
                [new FlagAction(), new AddKeywordAction("large-attachment")],
                RuleTriggerKind.OnNew, null)),
    ];

    public static RuleTemplate? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    /// <summary>Checks and completes parameters (defaults applied); returns errors keyed by parameter name.</summary>
    public static Dictionary<string, string> ResolveParameters(RuleTemplate template, IReadOnlyDictionary<string, string>? input, out Dictionary<string, string> errors)
    {
        errors = [];
        var values = new Dictionary<string, string>();
        foreach (var parameter in template.Parameters)
        {
            var value = input?.GetValueOrDefault(parameter.Name)?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                value = parameter.Default;
            }

            var error = value is null ? "Required." : parameter.Type switch
            {
                TemplateParameterType.Folder => FolderTemplate.Validate(value) is [var first, ..] ? first : null,
                TemplateParameterType.Days or TemplateParameterType.Megabytes =>
                    double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number > 0 && number <= 100_000 ? null : "Enter a positive number.",
                TemplateParameterType.DomainList or TemplateParameterType.AddressList => Guid.TryParse(value, out _) ? null : "Choose a list.",
                _ => null,
            };
            if (error is null)
            {
                values[parameter.Name] = value!;
            }
            else
            {
                errors[parameter.Name] = error;
            }
        }

        return values;
    }
}
