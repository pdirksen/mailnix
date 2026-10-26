using System.Security.Claims;
using System.Text;
using Mailnix.Api.Hosting;
using Mailnix.Api.Rules;
using Mailnix.Core.Rules;
using Mailnix.Infrastructure.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Mailnix.Api.Lists;

internal static class ListEndpoints
{
    public const int MaxEntries = 10_000;
    private const int MaxImportBytes = 2 * 1024 * 1024;
    private const int MaxReportedInvalid = 100;

    public static RouteGroupBuilder MapListEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/lists").WithTags("Lists");

        group.MapGet("/", List);
        group.MapGet("/{id:guid}", Get);
        group.MapPost("/", Create);
        group.MapPut("/{id:guid}", Update);
        group.MapDelete("/{id:guid}", Delete);
        group.MapGet("/{id:guid}/export", Export);
        group.MapPost("/{id:guid}/import", Import).Accepts<string>("text/csv", "text/plain");

        return group;
    }

    private static async Task<Ok<List<ListSummaryResponse>>> List(ClaimsPrincipal user, MailnixDbContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var lists = await db.NamedLists
            .Where(l => l.UserId == userId)
            .OrderBy(l => l.Name)
            .Select(l => new ListSummaryResponse(
                l.Id,
                l.Name,
                l.Kind,
                l.Description,
                l.Entries.Count,
                db.RuleListReferences.Count(r => r.ListId == l.Id),
                l.UpdatedAt))
            .ToListAsync(ct);
        return TypedResults.Ok(lists);
    }

    private static async Task<Results<Ok<ListResponse>, NotFound>> Get(Guid id, ClaimsPrincipal user, MailnixDbContext db, CancellationToken ct) =>
        await FindOwnedAsync(db, id, user, ct) is { } list ? TypedResults.Ok(await ToResponseAsync(db, list, ct)) : TypedResults.NotFound();

    private static async Task<Results<Created<ListResponse>, ValidationProblem>> Create(
        CreateListRequest request,
        ClaimsPrincipal user,
        MailnixDbContext db,
        TimeProvider time,
        CancellationToken ct)
    {
        var (entries, errors) = PrepareEntries(request.Kind, request.Entries ?? []);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var userId = user.GetUserId();
        var now = time.GetUtcNow();
        var list = new NamedListEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = await db.Users.Where(u => u.Id == userId).Select(u => u.TenantId).SingleAsync(ct),
            UserId = userId,
            Name = request.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            Kind = request.Kind,
            CreatedAt = now,
            UpdatedAt = now,
        };
        list.Entries.AddRange(entries.Select(e => NewEntry(list.Id, e)));
        db.NamedLists.Add(list);
        await db.SaveChangesAsync(ct);

        return TypedResults.Created($"/api/lists/{list.Id}", await ToResponseAsync(db, list, ct));
    }

    private static async Task<Results<Ok<ListResponse>, ValidationProblem, NotFound>> Update(
        Guid id,
        UpdateListRequest request,
        ClaimsPrincipal user,
        MailnixDbContext db,
        TimeProvider time,
        CancellationToken ct)
    {
        if (await FindOwnedAsync(db, id, user, ct) is not { } list)
        {
            return TypedResults.NotFound();
        }

        var (entries, errors) = PrepareEntries(list.Kind, request.Entries ?? []);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        list.Name = request.Name.Trim();
        list.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        ReplaceEntries(list, entries);
        list.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(await ToResponseAsync(db, list, ct));
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> Delete(Guid id, ClaimsPrincipal user, MailnixDbContext db, CancellationToken ct)
    {
        if (await FindOwnedAsync(db, id, user, ct) is not { } list)
        {
            return TypedResults.NotFound();
        }

        var usedBy = await UsedByAsync(db, id, ct);
        if (usedBy.Count > 0)
        {
            return RuleEndpoints.InUseProblem($"\"{list.Name}\" is used by rules: {string.Join(", ", usedBy.Select(r => r.Name))}.", usedBy);
        }

        db.NamedLists.Remove(list);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<FileContentHttpResult, NotFound>> Export(Guid id, ClaimsPrincipal user, MailnixDbContext db, CancellationToken ct)
    {
        if (await FindOwnedAsync(db, id, user, ct) is not { } list)
        {
            return TypedResults.NotFound();
        }

        var csv = ListCsv.Write(list.Entries.OrderBy(e => e.Value, StringComparer.Ordinal).Select(e => (NamedList.ToDisplay(list.Kind, e.Value), e.Note)));
        var fileName = string.Concat(list.Name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')) + ".csv";
        return TypedResults.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv; charset=utf-8", fileName);
    }

    private static async Task<Results<Ok<ListImportResponse>, NotFound, ValidationProblem>> Import(
        Guid id,
        HttpRequest request,
        ClaimsPrincipal user,
        MailnixDbContext db,
        TimeProvider time,
        CancellationToken ct,
        bool replace = false)
    {
        if (await FindOwnedAsync(db, id, user, ct) is not { } list)
        {
            return TypedResults.NotFound();
        }

        if (request.ContentLength > MaxImportBytes)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [$"Files can be at most {MaxImportBytes / 1024 / 1024} MB."] });
        }

        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var csv = await reader.ReadToEndAsync(ct);

        var existing = replace ? [] : list.Entries.Select(e => new ListEntryDto(e.Value, e.Note)).ToList();
        var seen = existing.Select(e => e.Value).ToHashSet(StringComparer.Ordinal);
        var invalid = new List<InvalidListEntry>();
        var (added, duplicates) = (0, 0);
        foreach (var (line, value, note) in ListCsv.Parse(csv))
        {
            if (NamedList.ValidateEntry(list.Kind, value) is { } error)
            {
                invalid.Add(new InvalidListEntry(line, value.Length > 100 ? value[..100] : value, error));
                continue;
            }

            if (!seen.Add(NamedList.Normalize(list.Kind, value)))
            {
                duplicates++;
                continue;
            }

            existing.Add(new ListEntryDto(NamedList.Normalize(list.Kind, value), Truncate(note, 500)));
            added++;
        }

        if (existing.Count > MaxEntries)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [$"A list can have at most {MaxEntries} entries."] });
        }

        ReplaceEntries(list, existing);
        list.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(new ListImportResponse(added, duplicates, [.. invalid.Take(MaxReportedInvalid)], await ToResponseAsync(db, list, ct)));
    }

    /// <summary>Normalizes and deduplicates entries; validation errors are keyed by entry index.</summary>
    internal static (List<ListEntryDto> Entries, Dictionary<string, string[]> Errors) PrepareEntries(NamedListKind kind, IReadOnlyList<ListEntryDto> input)
    {
        var errors = new Dictionary<string, string[]>();
        if (input.Count > MaxEntries)
        {
            errors["entries"] = [$"A list can have at most {MaxEntries} entries."];
            return ([], errors);
        }

        var entries = new List<ListEntryDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < input.Count; i++)
        {
            if (NamedList.ValidateEntry(kind, input[i].Value) is { } error)
            {
                errors[$"entries[{i}].value"] = [$"{input[i].Value}: {error}"];
            }
            else if (seen.Add(NamedList.Normalize(kind, input[i].Value)))
            {
                entries.Add(new ListEntryDto(NamedList.Normalize(kind, input[i].Value), Truncate(input[i].Note, 500)));
            }
        }

        return (entries, errors);
    }

    private static void ReplaceEntries(NamedListEntity list, IReadOnlyList<ListEntryDto> entries)
    {
        var wanted = entries.ToDictionary(e => e.Value, StringComparer.Ordinal);
        list.Entries.RemoveAll(e => !wanted.ContainsKey(e.Value));
        foreach (var entry in list.Entries)
        {
            entry.Note = wanted[entry.Value].Note;
        }

        var present = list.Entries.Select(e => e.Value).ToHashSet(StringComparer.Ordinal);
        list.Entries.AddRange(entries.Where(e => !present.Contains(e.Value)).Select(e => NewEntry(list.Id, e)));
    }

    internal static NamedListEntryEntity NewEntry(Guid listId, ListEntryDto entry) =>
        new() { Id = Guid.CreateVersion7(), ListId = listId, Value = entry.Value, Note = entry.Note };

    private static string? Truncate(string? text, int max) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim() is var t && t.Length > max ? t[..max] : text.Trim();

    private static Task<NamedListEntity?> FindOwnedAsync(MailnixDbContext db, Guid id, ClaimsPrincipal user, CancellationToken ct)
    {
        var userId = user.GetUserId();
        return db.NamedLists.Include(l => l.Entries).SingleOrDefaultAsync(l => l.Id == id && l.UserId == userId, ct);
    }

    private static Task<List<RuleSummary>> UsedByAsync(MailnixDbContext db, Guid listId, CancellationToken ct) =>
        db.RuleListReferences
            .Where(r => r.ListId == listId)
            .Join(db.Rules, r => r.RuleId, rule => rule.Id, (r, rule) => new RuleSummary(rule.Id, rule.Name, rule.AccountId))
            .ToListAsync(ct);

    private static async Task<ListResponse> ToResponseAsync(MailnixDbContext db, NamedListEntity list, CancellationToken ct) => new(
        list.Id,
        list.Name,
        list.Kind,
        list.Description,
        [.. list.Entries.OrderBy(e => e.Value, StringComparer.Ordinal).Select(e => new ListEntryDto(NamedList.ToDisplay(list.Kind, e.Value), e.Note))],
        await UsedByAsync(db, list.Id, ct),
        list.UpdatedAt);
}
