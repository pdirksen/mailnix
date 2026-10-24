using System.ComponentModel.DataAnnotations;
using Mailnix.Api.Rules;
using Mailnix.Core.Rules;

namespace Mailnix.Api.Lists;

public sealed record ListEntryDto(string Value, string? Note = null);

public sealed record CreateListRequest(
    [Required, MaxLength(200)] string Name,
    NamedListKind Kind,
    [MaxLength(2000)] string? Description = null,
    IReadOnlyList<ListEntryDto>? Entries = null);

public sealed record UpdateListRequest(
    [Required, MaxLength(200)] string Name,
    [MaxLength(2000)] string? Description = null,
    IReadOnlyList<ListEntryDto>? Entries = null);

public sealed record ListSummaryResponse(
    Guid Id,
    string Name,
    NamedListKind Kind,
    string? Description,
    int EntryCount,
    int UsedByRuleCount,
    DateTimeOffset UpdatedAt);

public sealed record ListResponse(
    Guid Id,
    string Name,
    NamedListKind Kind,
    string? Description,
    IReadOnlyList<ListEntryDto> Entries,
    IReadOnlyList<RuleSummary> UsedBy,
    DateTimeOffset UpdatedAt);

public sealed record InvalidListEntry(int Line, string Value, string Message);

public sealed record ListImportResponse(int Added, int Duplicates, IReadOnlyList<InvalidListEntry> Invalid, ListResponse List);
