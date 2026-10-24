using System.ComponentModel.DataAnnotations;
using Mailnix.Core.Rules;

namespace Mailnix.Api.Rules;

/// <summary>A draft rule to try against real messages. Nothing is changed on the server.</summary>
/// <param name="RuleId">When editing a saved rule: the draft replaces it in the account's rule set.</param>
/// <param name="Folder">Folder to read; defaults to the account's first watched folder.</param>
/// <param name="Refresh">Read the folder again instead of using messages fetched in the last minutes.</param>
public sealed record PreviewRequest(
    [Required] ConditionNode Conditions,
    [Required] IReadOnlyList<RuleAction> Actions,
    Guid? RuleId = null,
    [MaxLength(200)] string? Name = null,
    bool StopProcessing = false,
    [MaxLength(500)] string? Folder = null,
    [Range(1, 500)] int Limit = 100,
    bool Refresh = false);

public sealed record PreviewAction(ActionType Type, string? TargetFolder, string? Keyword, Guid RuleId, string RuleName);

public sealed record PreviewSkippedAction(ActionType Type, string? TargetFolder, SkipReason Reason, string? BlockedByRuleName);

public sealed record PreviewMessage(
    uint Uid,
    string? From,
    string? FromName,
    string? Subject,
    DateTimeOffset? Date,
    bool IsSeen,
    bool Matched,
    TraceNode? Trace,
    IReadOnlyList<PreviewAction> Actions,
    IReadOnlyList<PreviewSkippedAction> Skipped,
    IReadOnlyList<PreviewAction> OtherRulesActions);

/// <param name="Limit">The number of messages actually read (lower than requested when body conditions are used).</param>
/// <param name="TerminalActions">Messages the draft would move, delete, archive or mark as spam.</param>
/// <param name="ExceedsConfirmationThreshold">The draft would move more messages in this sample than the account's threshold.</param>
public sealed record PreviewResponse(
    string Folder,
    int TotalInFolder,
    int Limit,
    int Examined,
    int Matched,
    int TerminalActions,
    int ConfirmationThreshold,
    bool ExceedsConfirmationThreshold,
    DateTimeOffset FetchedAt,
    bool FromCache,
    IReadOnlyList<PreviewMessage> Messages);

public sealed record TemplateParameterInfo(string Name, TemplateParameterType Type, string? Default);

public sealed record RuleTemplateInfo(string Id, IReadOnlyList<TemplateParameterInfo> Parameters);

public sealed record RuleDraftRequest([Required, MaxLength(200)] string Name, IReadOnlyDictionary<string, string>? Parameters = null);

public sealed record RuleDraftResponse(
    string Name,
    ConditionNode Conditions,
    IReadOnlyList<RuleAction> Actions,
    Infrastructure.Data.RuleTrigger Trigger,
    string? Schedule);
