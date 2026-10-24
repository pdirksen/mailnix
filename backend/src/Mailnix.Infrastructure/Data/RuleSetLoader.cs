using System.Text.Json;
using Mailnix.Core.Messages;
using Mailnix.Core.Rules;
using Microsoft.EntityFrameworkCore;

namespace Mailnix.Infrastructure.Data;

/// <param name="AccountRules">The account's rules in priority order (enabled or not).</param>
/// <param name="Context">Evaluation context without Sent-folder data (that needs the mail server).</param>
/// <param name="Stored">The stored rules by id, for scheduling details (trigger, source folders).</param>
public sealed record LoadedRuleSet(IReadOnlyList<RuleDefinition> AccountRules, EvaluationContext Context, IReadOnlyDictionary<Guid, MailRule> Stored)
{
    /// <summary>Folders a rule runs on: its own source folders, or else the account's watched folders.</summary>
    public bool AppliesTo(Guid ruleId, string folder, IReadOnlyCollection<string> watchedFolders) =>
        Stored.TryGetValue(ruleId, out var rule)
        && (rule.SourceFolders.Count > 0 ? rule.SourceFolders.Contains(folder) : watchedFolders.Contains(folder));
}

/// <summary>Loads everything rule evaluation needs for one account. Shared by the preview and the mail worker.</summary>
public sealed class RuleSetLoader(MailnixDbContext db, TimeProvider time)
{
    public async Task<LoadedRuleSet> LoadAsync(MailAccount account, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == account.UserId, ct);
        var userRules = await db.Rules.AsNoTracking().Where(r => r.UserId == account.UserId).ToListAsync(ct);
        var lists = await db.NamedLists.AsNoTracking().Include(l => l.Entries).Where(l => l.UserId == account.UserId).ToListAsync(ct);

        var definitions = userRules.ToDictionary(r => r.Id, ToDefinition);
        var accountRules = userRules
            .Where(r => r.AccountId == account.Id)
            .OrderBy(r => r.Priority).ThenBy(r => r.Name, StringComparer.Ordinal)
            .Select(r => definitions[r.Id])
            .ToList();

        var myAddresses = new[] { account.Username, user.Email }
            .Where(a => a is not null && a.Contains('@', StringComparison.Ordinal))
            .Select(a => new EmailAddress(a!).Address)
            .ToHashSet(StringComparer.Ordinal);

        var context = new EvaluationContext
        {
            Now = time.GetUtcNow(),
            TimeZone = TimeZoneInfo.TryFindSystemTimeZoneById(user.TimeZone, out var zone) ? zone : TimeZoneInfo.Utc,
            MyAddresses = myAddresses,
            NamedLists = lists.ToDictionary(l => l.Id, l => new NamedList(l.Id, l.Name, l.Kind, l.Entries.Select(e => e.Value))),
            Rules = definitions,
            SpecialFolders = new SpecialFolderTargets(account.SpecialFolders.Trash, account.SpecialFolders.Junk, account.SpecialFolders.Archive),
            FolderDelimiter = account.FolderDelimiter is [var delimiter] ? delimiter : '/',
        };
        return new LoadedRuleSet(accountRules, context, userRules.ToDictionary(r => r.Id));
    }

    public static RuleDefinition ToDefinition(MailRule rule) => new()
    {
        Id = rule.Id,
        Name = rule.Name,
        Priority = rule.Priority,
        Enabled = rule.Enabled,
        StopProcessing = rule.StopProcessing,
        DryRun = rule.DryRun,
        Conditions = JsonSerializer.Deserialize<ConditionNode>(rule.ConditionsJson, RuleJson.Options)!,
        Actions = JsonSerializer.Deserialize<IReadOnlyList<RuleAction>>(rule.ActionsJson, RuleJson.Options)!,
    };
}
