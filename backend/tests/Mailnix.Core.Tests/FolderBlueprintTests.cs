using Mailnix.Core.Rules;

namespace Mailnix.Core.Tests;

public sealed class FolderBlueprintTests
{
    public static TheoryData<string> BlueprintIds => [.. FolderBlueprints.All.Select(b => b.Id)];

    [Theory]
    [MemberData(nameof(BlueprintIds))]
    public void Every_rule_refers_to_an_existing_template_folder_and_list(string id)
    {
        var blueprint = FolderBlueprints.Find(id)!;

        blueprint.Folders.Select(f => f.Key).ShouldBeUnique();
        blueprint.Rules.Select(r => r.Key).ShouldBeUnique();
        foreach (var rule in blueprint.Rules)
        {
            var template = RuleTemplates.Find(rule.TemplateId).ShouldNotBeNull(rule.Key);
            (rule.Folder is not null).ShouldBe(template.Parameters.Any(p => p.Name == "folder"), rule.Key);
            (rule.List is not null).ShouldBe(template.Parameters.Any(p => p.Name == "list"), rule.Key);
            if (rule.Folder is not null)
            {
                blueprint.Folders.ShouldContain(f => f.Key == rule.Folder, rule.Key);
            }

            if (rule.List is not null)
            {
                var seed = FolderBlueprints.Seeds.Where(s => s.Key == rule.List).ShouldHaveSingleItem();
                var parameter = template.Parameters.Single(p => p.Name == "list");
                (seed.Kind == NamedListKind.Address).ShouldBe(parameter.Type == TemplateParameterType.AddressList, rule.Key);
            }
        }
    }

    [Theory]
    [MemberData(nameof(BlueprintIds))]
    public void Defaults_expand_to_valid_rules_in_blueprint_order(string id)
    {
        var blueprint = FolderBlueprints.Find(id)!;
        var (selection, lists) = Defaults(blueprint);

        var drafts = blueprint.Expand(selection, out var errors);

        errors.ShouldBeEmpty();
        drafts.Select(d => d.Key).ShouldBe(blueprint.Rules.Where(r => r.DefaultOn).Select(r => r.Key));
        foreach (var (key, draft) in drafts)
        {
            var rule = new RuleDefinition { Id = Guid.NewGuid(), Name = draft.Name, Conditions = draft.Conditions, Actions = draft.Actions };
            RuleValidator.Validate(rule, new RuleValidationContext { Lists = lists }).ShouldBeEmpty(key);
        }
    }

    [Fact]
    public void Folders_and_lists_are_bound_to_the_template_parameters()
    {
        var blueprint = FolderBlueprints.Find("by-type")!;
        var social = Guid.NewGuid();
        var selection = new BlueprintSelection(
            new Dictionary<string, string> { ["social"] = "INBOX/Soziales" },
            new Dictionary<string, Guid> { ["social"] = social },
            [new BlueprintRuleChoice("social", "Soziale Netzwerke")]);

        var draft = blueprint.Expand(selection, out _).ShouldHaveSingleItem().Draft;

        draft.Name.ShouldBe("Soziale Netzwerke");
        draft.Conditions.ShouldBe(new Condition(ConditionField.FromDomain, ConditionOperator.InList, social.ToString()));
        draft.Actions.ShouldBe([new MoveToAction("INBOX/Soziales")]);
    }

    [Fact]
    public void Selection_problems_are_reported_per_rule_and_folder()
    {
        var blueprint = FolderBlueprints.Find("by-type")!;
        var selection = new BlueprintSelection(
            new Dictionary<string, string> { ["invoices"] = "INBOX/{nope}", ["attic"] = "INBOX/Attic", ["archive"] = "INBOX/Archive" },
            new Dictionary<string, Guid>(),
            [
                new BlueprintRuleChoice("travel", "Travel"),
                new BlueprintRuleChoice("social", "Social"),
                new BlueprintRuleChoice("archive", "Archive", new Dictionary<string, string> { ["days"] = "0" }),
                new BlueprintRuleChoice("teleport", "?"),
            ]);

        blueprint.Expand(selection, out var errors).ShouldBeEmpty();

        errors.Keys.ShouldBe(
            ["folders.invoices", "folders.attic", "rules.travel.folder", "rules.social.folder", "rules.archive.days", "rules.teleport"],
            ignoreOrder: true);
    }

    private static (BlueprintSelection Selection, Dictionary<Guid, NamedListKind> Lists) Defaults(FolderBlueprint blueprint)
    {
        var seeds = blueprint.ListSeeds.ToDictionary(s => s.Key, _ => Guid.NewGuid());
        var lists = blueprint.ListSeeds.ToDictionary(s => seeds[s.Key], s => s.Kind);
        var selection = new BlueprintSelection(
            blueprint.Folders.ToDictionary(f => f.Key, f => f.Suffix is null ? $"INBOX/{f.Key}" : $"INBOX/{f.Key}/{f.Suffix}"),
            seeds,
            [.. blueprint.Rules.Where(r => r.DefaultOn).Select(r => new BlueprintRuleChoice(r.Key, r.Key))]);
        return (selection, lists);
    }
}
