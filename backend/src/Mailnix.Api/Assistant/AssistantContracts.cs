using System.ComponentModel.DataAnnotations;
using Mailnix.Api.Rules;
using Mailnix.Api.Runs;
using Mailnix.Core.Rules;

namespace Mailnix.Api.Assistant;

public sealed record BlueprintFolderInfo(string Key, string? Suffix, bool DefaultOn);

/// <param name="Parameters">The template's parameters that are not bound to a folder or list (e.g. days).</param>
public sealed record BlueprintRuleInfo(string Key, string TemplateId, string? Folder, string? List, bool DefaultOn, IReadOnlyList<TemplateParameterInfo> Parameters);

public sealed record ListSeedInfo(string Key, NamedListKind Kind, IReadOnlyList<string> Entries);

public sealed record BlueprintInfo(string Id, IReadOnlyList<BlueprintFolderInfo> Folders, IReadOnlyList<BlueprintRuleInfo> Rules, IReadOnlyList<ListSeedInfo> Lists);

/// <param name="ListId">An existing list of the right kind; otherwise a new list is created from Name and Entries.</param>
public sealed record AssistantListChoice(Guid? ListId = null, [MaxLength(200)] string? Name = null, IReadOnlyList<string>? Entries = null);

/// <param name="Name">The rule's name (translated by the client).</param>
/// <param name="Parameters">Values for the template's unbound parameters; defaults apply to the others.</param>
public sealed record AssistantRuleChoice(
    [Required, MaxLength(100)] string Key,
    [Required, MaxLength(200)] string Name,
    IReadOnlyDictionary<string, string>? Parameters = null);

/// <param name="Folders">Folder key → folder template for the selected folders.</param>
/// <param name="Lists">List seed key → the list a rule uses.</param>
public sealed record AssistantSelection(
    [Required, MaxLength(100)] string BlueprintId,
    [Required] IReadOnlyDictionary<string, string> Folders,
    [Required] IReadOnlyList<AssistantRuleChoice> Rules,
    IReadOnlyDictionary<string, AssistantListChoice>? Lists = null);

/// <summary>Tries the selection on the newest messages of the inbox. Nothing is changed.</summary>
public sealed record AssistantAnalyzeRequest([Required] AssistantSelection Selection, [Range(1, 500)] int Limit = 300, bool Refresh = false);

public sealed record AssistantExample(string? From, string? Subject, DateTimeOffset? Date, string? TargetFolder);

/// <param name="Sorted">Messages this rule would move.</param>
/// <param name="Blocked">Messages it matches that an earlier rule already moves.</param>
public sealed record AssistantRuleAnalysis(string Key, int Matched, int Sorted, int Blocked, IReadOnlyList<AssistantExample> Examples);

public sealed record SenderDomainCount(string Domain, int Count);

/// <param name="SortedByExistingRules">Messages the account's existing rules already move.</param>
/// <param name="Unsorted">Messages no rule would move.</param>
/// <param name="UnsortedDomains">The most frequent sender domains of the unsorted messages.</param>
public sealed record AssistantAnalyzeResponse(
    string Folder,
    int TotalInFolder,
    int Examined,
    int SortedByExistingRules,
    int Unsorted,
    DateTimeOffset FetchedAt,
    bool FromCache,
    IReadOnlyList<AssistantRuleAnalysis> Rules,
    IReadOnlyList<SenderDomainCount> UnsortedDomains);

/// <param name="CreateFolders">Create the folders now instead of when the first message is moved there.</param>
/// <param name="RunOnInboxLimit">Also queue a run of the new rules on this many of the newest inbox messages.</param>
public sealed record AssistantApplyRequest(
    [Required] AssistantSelection Selection,
    bool DryRun = true,
    bool CreateFolders = false,
    [Range(1, 1000)] int? RunOnInboxLimit = null);

public sealed record AssistantCreatedRule(string Key, Guid Id, string Name);

public sealed record AssistantFolderResult(string Path, bool Created, string? Error);

public sealed record AssistantApplyResponse(
    IReadOnlyList<AssistantCreatedRule> Rules,
    IReadOnlyList<Guid> CreatedListIds,
    RunSummary? Run,
    IReadOnlyList<AssistantFolderResult> Folders);
