using System.Text.Json;
using Mailnix.Core.Rules;
using Mailnix.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Mailnix.Api.Rules;

/// <summary>Validation and dependency bookkeeping shared by the rule endpoints.</summary>
internal sealed class RuleService(MailnixDbContext db)
{
    public const int MaxSourceFolders = 20;

    /// <summary>Validates a rule definition plus its scheduling settings against the user's rules and lists.</summary>
    /// <param name="newLists">Lists that are created together with the rule.</param>
    public async Task<List<RuleValidationError>> ValidateAsync(
        Guid userId,
        RuleDefinition rule,
        RuleTrigger trigger,
        string? schedule,
        IReadOnlyList<string>? sourceFolders,
        CancellationToken ct,
        IReadOnlyDictionary<Guid, NamedListKind>? newLists = null)
    {
        var context = await LoadValidationContextAsync(userId, rule.Id, ct);
        if (newLists is not null)
        {
            context = context with { Lists = context.Lists.Concat(newLists).ToDictionary() };
        }

        var errors = RuleValidator.Validate(rule, context).ToList();

        if (trigger == RuleTrigger.Scheduled)
        {
            if (string.IsNullOrWhiteSpace(schedule))
            {
                errors.Add(new("schedule", "ScheduleRequired", "Scheduled rules need a schedule (cron expression)."));
            }
            else if (!TryParseCron(schedule))
            {
                errors.Add(new("schedule", "InvalidSchedule", "Invalid cron expression, e.g. \"0 3 * * *\" for daily at 3:00."));
            }
        }

        if (sourceFolders is { Count: > MaxSourceFolders })
        {
            errors.Add(new("sourceFolders", "TooManyFolders", $"A rule can watch at most {MaxSourceFolders} folders."));
        }
        else if (sourceFolders?.Any(f => string.IsNullOrWhiteSpace(f) || f.Length > 500) == true)
        {
            errors.Add(new("sourceFolders", "InvalidFolder", "Folder names must not be empty."));
        }

        return errors;
    }

    /// <summary>Replaces the rule's stored rule and list dependencies with those in its conditions.</summary>
    public static void SyncReferences(MailRule entity, ConditionNode conditions)
    {
        var (rules, lists) = RuleValidator.CollectReferences(conditions);
        entity.References.RemoveAll(r => !rules.Contains(r.ReferencedRuleId));
        entity.References.AddRange(rules
            .Where(id => entity.References.All(r => r.ReferencedRuleId != id))
            .Select(id => new RuleReference { RuleId = entity.Id, ReferencedRuleId = id }));
        entity.ListReferences.RemoveAll(r => !lists.Contains(r.ListId));
        entity.ListReferences.AddRange(lists
            .Where(id => entity.ListReferences.All(r => r.ListId != id))
            .Select(id => new RuleListReference { RuleId = entity.Id, ListId = id }));
    }

    /// <summary>Rules that reference any of the given rules (excluding the given rules themselves).</summary>
    public async Task<List<(Guid ReferencedRuleId, RuleSummary Dependent)>> DependentsAsync(IReadOnlyCollection<Guid> ruleIds, CancellationToken ct)
    {
        var rows = await db.RuleReferences
            .Where(r => ruleIds.Contains(r.ReferencedRuleId) && !ruleIds.Contains(r.RuleId))
            .Join(db.Rules, r => r.RuleId, rule => rule.Id, (r, rule) => new { r.ReferencedRuleId, rule.Id, rule.Name, rule.AccountId })
            .ToListAsync(ct);
        return [.. rows.Select(x => (x.ReferencedRuleId, new RuleSummary(x.Id, x.Name, x.AccountId)))];
    }

    public static string Serialize(ConditionNode conditions) => JsonSerializer.Serialize(conditions, RuleJson.Options);

    public static string Serialize(IReadOnlyList<RuleAction> actions) => JsonSerializer.Serialize(actions, RuleJson.Options);

    public static Dictionary<string, string[]> ToProblemErrors(IEnumerable<RuleValidationError> errors) =>
        errors.GroupBy(e => e.Path).ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray());

    private static bool TryParseCron(string expression) => RuleSchedule.TryParse(expression, out _);

    private async Task<RuleValidationContext> LoadValidationContextAsync(Guid userId, Guid ruleId, CancellationToken ct)
    {
        var lists = await db.NamedLists.Where(l => l.UserId == userId).Select(l => new { l.Id, l.Kind }).ToListAsync(ct);
        var rules = await db.Rules.Where(r => r.UserId == userId && r.Id != ruleId).Select(r => new { r.Id, r.Name }).ToListAsync(ct);
        var ruleIds = rules.Select(r => r.Id).ToList();
        var references = await db.RuleReferences.Where(r => ruleIds.Contains(r.RuleId)).ToListAsync(ct);

        return new RuleValidationContext
        {
            Lists = lists.ToDictionary(l => l.Id, l => l.Kind),
            Rules = rules.ToDictionary(r => r.Id, r => r.Name),
            References = references.GroupBy(r => r.RuleId).ToDictionary(g => g.Key, g => (IReadOnlyCollection<Guid>)[.. g.Select(r => r.ReferencedRuleId)]),
        };
    }
}
