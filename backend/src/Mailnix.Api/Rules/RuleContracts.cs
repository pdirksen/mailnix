using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Mailnix.Core.Rules;
using Mailnix.Infrastructure.Data;

namespace Mailnix.Api.Rules;

public sealed record CreateRuleRequest(
    Guid AccountId,
    [Required, MaxLength(200)] string Name,
    [Required] ConditionNode Conditions,
    [Required] IReadOnlyList<RuleAction> Actions,
    [MaxLength(2000)] string? Description = null,
    bool Enabled = true,
    bool StopProcessing = false,
    bool DryRun = false,
    RuleTrigger Trigger = RuleTrigger.OnNew,
    [MaxLength(100)] string? Schedule = null,
    IReadOnlyList<string>? SourceFolders = null);

public sealed record UpdateRuleRequest(
    [Required, MaxLength(200)] string Name,
    [Required] ConditionNode Conditions,
    [Required] IReadOnlyList<RuleAction> Actions,
    [MaxLength(2000)] string? Description = null,
    bool Enabled = true,
    bool StopProcessing = false,
    bool DryRun = false,
    RuleTrigger Trigger = RuleTrigger.OnNew,
    [MaxLength(100)] string? Schedule = null,
    IReadOnlyList<string>? SourceFolders = null);

/// <summary>Validates a rule without saving it (for the rule builder). RuleId is set when editing an existing rule.</summary>
public sealed record ValidateRuleRequest(
    Guid AccountId,
    [Required] ConditionNode Conditions,
    [Required] IReadOnlyList<RuleAction> Actions,
    Guid? RuleId = null,
    RuleTrigger Trigger = RuleTrigger.OnNew,
    string? Schedule = null);

public sealed record RuleValidationResponse(bool Valid, IReadOnlyList<RuleValidationError> Errors);

public sealed record ReorderRulesRequest(Guid AccountId, [Required] IReadOnlyList<Guid> RuleIds);

public sealed record RuleSummary(Guid Id, string Name, Guid AccountId);

public sealed record RuleResponse(
    Guid Id,
    Guid AccountId,
    string Name,
    string? Description,
    int Priority,
    bool Enabled,
    bool StopProcessing,
    bool DryRun,
    RuleTrigger Trigger,
    string? Schedule,
    IReadOnlyList<string> SourceFolders,
    ConditionNode Conditions,
    IReadOnlyList<RuleAction> Actions,
    IReadOnlyList<RuleSummary> UsedBy,
    DateTimeOffset? LastScheduledAt,
    DateTimeOffset? NextRunAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Origin)
{
    /// <param name="zone">The user's time zone, in which schedules run.</param>
    public static RuleResponse From(MailRule rule, IReadOnlyList<RuleSummary> usedBy, TimeZoneInfo zone) => new(
        rule.Id,
        rule.AccountId,
        rule.Name,
        rule.Description,
        rule.Priority,
        rule.Enabled,
        rule.StopProcessing,
        rule.DryRun,
        rule.Trigger,
        rule.Schedule,
        rule.SourceFolders,
        JsonSerializer.Deserialize<ConditionNode>(rule.ConditionsJson, RuleJson.Options)!,
        JsonSerializer.Deserialize<IReadOnlyList<RuleAction>>(rule.ActionsJson, RuleJson.Options)!,
        usedBy,
        rule.LastScheduledAt,
        RuleSchedule.NextRun(rule, zone),
        rule.CreatedAt,
        rule.UpdatedAt,
        rule.Origin);
}

public sealed record FieldCatalogResponse(IReadOnlyList<FieldDefinition> Fields, IReadOnlyList<string> FolderPlaceholders);

/// <summary>Switches dry-run on or off for several rules (e.g. "go live" after the setup assistant).</summary>
public sealed record SetDryRunRequest([Required, MinLength(1), MaxLength(200)] IReadOnlyList<Guid> RuleIds, bool DryRun);
