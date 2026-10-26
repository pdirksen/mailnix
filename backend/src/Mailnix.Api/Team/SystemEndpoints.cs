using System.ComponentModel.DataAnnotations;
using Mailnix.Api.Auth;
using Mailnix.Api.Hosting;
using Mailnix.Infrastructure.Data;
using Mailnix.Infrastructure.Options;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mailnix.Api.Team;

/// <param name="Alive">Seen within the last two minutes.</param>
public sealed record WorkerInfo(string Id, string Host, DateTimeOffset StartedAt, DateTimeOffset LastSeenAt, int Leases, bool Alive);

public sealed record TenantRow(Guid Id, string Name, int Users, int Accounts, int? MaxAccounts, DateTimeOffset CreatedAt);

/// <param name="Version">The version of this API process.</param>
public sealed record SystemResponse(
    string Version,
    MailnixMode Mode,
    bool MultiTenant,
    IReadOnlyList<WorkerInfo> Workers,
    int Accounts,
    int UnleasedAccounts,
    int QueuedRuns,
    int AwaitingConfirmation,
    IReadOnlyList<TenantRow> Tenants);

public sealed record TenantLimitRequest([Range(1, 100_000)] int? MaxAccounts);

/// <summary>For the operator of the installation: workers, mailbox distribution, queues and all tenants.</summary>
internal static class SystemEndpoints
{
    private static readonly TimeSpan AliveWindow = TimeSpan.FromMinutes(2);

    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/system").WithTags("System").RequireAuthorization(Policies.Admin);
        group.MapGet("/", Get);
        group.MapPut("/tenants/{id:guid}", SetLimit);
        return routes;
    }

    private static async Task<Ok<SystemResponse>> Get(MailnixDbContext db, IOptions<MailnixHostOptions> host, IOptions<TenancyOptions> tenancy, TimeProvider time, CancellationToken ct)
    {
        // Across all tenants, by design.
        var now = time.GetUtcNow();
        var leases = await db.MailboxLeases.AsNoTracking().ToListAsync(ct);
        var held = leases.Where(l => l.WorkerId != null && l.ExpiresAt > now).GroupBy(l => l.WorkerId!).ToDictionary(g => g.Key, g => g.Count());
        var workers = (await db.Workers.AsNoTracking().OrderBy(w => w.Host).ThenBy(w => w.StartedAt).ToListAsync(ct))
            .Select(w => new WorkerInfo(w.Id, w.Host, w.StartedAt, w.LastSeenAt, held.GetValueOrDefault(w.Id), now - w.LastSeenAt < AliveWindow))
            .ToList();

        var enabled = await db.MailAccounts.IgnoreQueryFilters().Where(a => a.Enabled).Select(a => a.Id).ToListAsync(ct);
        var leased = leases.Where(l => l.WorkerId != null && l.ExpiresAt > now).Select(l => l.AccountId).ToHashSet();
        var users = await db.Users.GroupBy(u => u.TenantId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var accounts = await db.MailAccounts.IgnoreQueryFilters().GroupBy(a => a.TenantId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var tenants = (await db.Tenants.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct))
            .Select(t => new TenantRow(t.Id, t.Name, users.GetValueOrDefault(t.Id), accounts.GetValueOrDefault(t.Id), t.MaxAccounts, t.CreatedAt))
            .ToList();

        return TypedResults.Ok(new SystemResponse(
            AppVersion.Current,
            host.Value.Mode,
            tenancy.Value.Mode == TenancyMode.MultiTenant,
            workers,
            enabled.Count,
            enabled.Count(id => !leased.Contains(id)),
            await db.Runs.IgnoreQueryFilters().CountAsync(r => r.State == RunState.Queued, ct),
            await db.Runs.IgnoreQueryFilters().CountAsync(r => r.State == RunState.AwaitingConfirmation, ct),
            tenants));
    }

    private static async Task<Results<NoContent, NotFound>> SetLimit(Guid id, TenantLimitRequest request, MailnixDbContext db, CancellationToken ct) =>
        await db.Tenants.Where(t => t.Id == id).ExecuteUpdateAsync(u => u.SetProperty(t => t.MaxAccounts, request.MaxAccounts), ct) > 0
            ? TypedResults.NoContent()
            : TypedResults.NotFound();
}
