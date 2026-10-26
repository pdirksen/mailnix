namespace Mailnix.Core.Rules;

/// <summary>A folder of a blueprint. Its name is translated in the UI by <see cref="Key"/>.</summary>
/// <param name="Suffix">Levels the UI appends to the chosen name, e.g. <c>{year}</c> for <c>Archive/{year}</c>.</param>
public sealed record BlueprintFolder(string Key, string? Suffix = null, bool DefaultOn = true);

/// <summary>A named list the assistant creates for a blueprint (e.g. "Social networks"), unless an existing one is chosen.</summary>
public sealed record ListSeed(string Key, NamedListKind Kind, IReadOnlyList<string> Entries);

/// <summary>A suggested rule: a <see cref="RuleTemplate"/> whose folder and list parameters are bound to the blueprint.</summary>
/// <param name="Folder">Key of the folder bound to the template's "folder" parameter.</param>
/// <param name="List">Key of the <see cref="ListSeed"/> bound to the template's "list" parameter.</param>
public sealed record BlueprintRule(string Key, string TemplateId, string? Folder = null, bool DefaultOn = true, string? List = null);

/// <summary>A selected rule; <paramref name="Parameters"/> override the template's other parameters (e.g. days).</summary>
public sealed record BlueprintRuleChoice(string Key, string Name, IReadOnlyDictionary<string, string>? Parameters = null);

/// <param name="Folders">Folder key → folder template; folders that are left out are not used.</param>
/// <param name="Lists">List seed key → id of the list to use (existing or about to be created).</param>
public sealed record BlueprintSelection(
    IReadOnlyDictionary<string, string> Folders,
    IReadOnlyDictionary<string, Guid> Lists,
    IReadOnlyList<BlueprintRuleChoice> Rules);

public sealed record BlueprintDraft(string Key, RuleDraft Draft);

/// <summary>
/// A folder structure with the rules that fill it. Rules are listed in priority order: specific ones first,
/// catch-alls like the archive last.
/// </summary>
public sealed record FolderBlueprint(string Id, IReadOnlyList<BlueprintFolder> Folders, IReadOnlyList<BlueprintRule> Rules)
{
    /// <summary>The lists the rules of this blueprint use.</summary>
    public IEnumerable<ListSeed> ListSeeds => Rules
        .Where(r => r.List is not null)
        .Select(r => FolderBlueprints.Seeds.Single(s => s.Key == r.List))
        .Distinct();

    /// <summary>Builds the selected rules in priority order; errors are keyed like <c>rules.{key}.{parameter}</c>.</summary>
    public IReadOnlyList<BlueprintDraft> Expand(BlueprintSelection selection, out Dictionary<string, string> errors)
    {
        errors = [];
        foreach (var key in selection.Folders.Keys.Where(k => Folders.All(f => f.Key != k)))
        {
            errors[$"folders.{key}"] = "Unknown folder.";
        }

        foreach (var (key, folder) in selection.Folders)
        {
            if (FolderTemplate.Validate(folder) is [var first, ..])
            {
                errors[$"folders.{key}"] = first;
            }
        }

        var chosen = new Dictionary<string, BlueprintRuleChoice>();
        foreach (var choice in selection.Rules)
        {
            if (Rules.All(r => r.Key != choice.Key))
            {
                errors[$"rules.{choice.Key}"] = "Unknown rule.";
            }
            else if (!chosen.TryAdd(choice.Key, choice))
            {
                errors[$"rules.{choice.Key}"] = "The rule is selected twice.";
            }
        }

        var drafts = new List<BlueprintDraft>();
        foreach (var rule in Rules.Where(r => chosen.ContainsKey(r.Key)))
        {
            var choice = chosen[rule.Key];
            var template = RuleTemplates.Find(rule.TemplateId)!;
            var input = new Dictionary<string, string>(choice.Parameters ?? new Dictionary<string, string>());
            if (rule.Folder is not null)
            {
                if (!selection.Folders.TryGetValue(rule.Folder, out var folder))
                {
                    errors[$"rules.{rule.Key}.folder"] = "Its folder is not selected.";
                    continue;
                }

                input["folder"] = folder;
            }

            if (rule.List is not null)
            {
                if (!selection.Lists.TryGetValue(rule.List, out var list))
                {
                    errors[$"rules.{rule.Key}.list"] = "Choose a list.";
                    continue;
                }

                input["list"] = list.ToString();
            }

            var values = RuleTemplates.ResolveParameters(template, input, out var parameterErrors);
            foreach (var (name, error) in parameterErrors)
            {
                errors[$"rules.{rule.Key}.{name}"] = error;
            }

            if (parameterErrors.Count == 0)
            {
                drafts.Add(new BlueprintDraft(rule.Key, template.Build(values, string.IsNullOrWhiteSpace(choice.Name) ? rule.Key : choice.Name.Trim())));
            }
        }

        return errors.Count == 0 ? drafts : [];
    }
}

public static class FolderBlueprints
{
    public static readonly IReadOnlyList<ListSeed> Seeds =
    [
        new("social", NamedListKind.Domain,
        [
            "facebookmail.com", "linkedin.com", "xing.com", "x.com", "twitter.com", "instagram.com", "pinterest.com",
            "reddit.com", "redditmail.com", "tiktok.com", "youtube.com", "meetup.com", "nextdoor.com", "quora.com",
        ]),
        new("clients", NamedListKind.Domain, []),
        new("senders", NamedListKind.Domain, []),
        new("vip", NamedListKind.Address, []),
    ];

    public static readonly IReadOnlyList<FolderBlueprint> All =
    [
        new("minimal",
            [new("newsletters"), new("notifications"), new("autoReplies", DefaultOn: false), new("archive", "{year}")],
            [
                new("newsletters", "newsletters-to-folder", "newsletters"),
                new("notifications", "notifications", "notifications"),
                new("auto-replies", "auto-replies", "autoReplies", DefaultOn: false),
                new("archive", "archive-by-year", "archive"),
            ]),

        new("by-type",
            [
                new("calendar"), new("invoices"), new("travel"), new("orders"), new("social"), new("newsletters"),
                new("notifications"), new("autoReplies"), new("archive", "{year}"),
            ],
            [
                new("quarantine", "quarantine-failed-dmarc", DefaultOn: false),
                new("calendar", "calendar-invites", "calendar"),
                new("invoices", "invoices", "invoices"),
                new("travel", "travel", "travel"),
                new("orders", "orders-shipping", "orders"),
                new("social", "social", "social", List: "social"),
                new("newsletters", "newsletters-to-folder", "newsletters"),
                new("notifications", "notifications", "notifications"),
                new("auto-replies", "auto-replies", "autoReplies"),
                new("archive", "archive-by-year", "archive"),
            ]),

        new("business",
            [
                new("clients", "{fromDomain}"), new("invoices"), new("admin"), new("newsletters"), new("fyi", DefaultOn: false),
                new("archive", "{year}"),
            ],
            [
                new("vip", "vip-flag", List: "vip"),
                new("clients", "sort-by-sender-domain", "clients", List: "clients"),
                new("invoices", "invoices", "invoices"),
                new("notifications", "notifications", "admin"),
                new("newsletters", "newsletters-to-folder", "newsletters"),
                new("fyi", "cc-only-fyi", "fyi", DefaultOn: false),
                new("archive", "archive-by-year", "archive"),
            ]),

        new("by-sender",
            [new("shops", "{plusTag}"), new("newsletters", "{fromDomain}"), new("senders", "{fromDomain}"), new("archive", "{year}/{month}")],
            [
                new("shops", "shops-by-plus-tag", "shops"),
                new("newsletters", "newsletters-to-folder", "newsletters"),
                new("senders", "sort-by-sender-domain", "senders", List: "senders"),
                new("archive", "archive-by-year", "archive"),
            ]),
    ];

    public static FolderBlueprint? Find(string id) => All.FirstOrDefault(b => b.Id == id);
}
