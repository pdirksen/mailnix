using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Mailnix.Api.Auth;
using Mailnix.Api.Hosting;
using Mailnix.Infrastructure.Data;
using Mailnix.Infrastructure.Identity;
using Mailnix.Infrastructure.OAuth;
using Mailnix.Worker;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mailnix.Api.Team;

public sealed record TenantInfo(Guid Id, string Name, int? MaxAccounts, int Accounts);

public sealed record TeamMember(Guid Id, string Email, string? DisplayName, bool IsTenantAdmin, bool IsAdmin, int Accounts, DateTimeOffset CreatedAt);

public sealed record InvitationItem(Guid Id, string? Email, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

public sealed record TeamResponse(TenantInfo Tenant, IReadOnlyList<TeamMember> Users, IReadOnlyList<InvitationItem> Invitations);

public sealed record RenameTenantRequest([Required, MaxLength(200)] string Name);

public sealed record CreateInvitationRequest([EmailAddress, MaxLength(320)] string? Email = null);

/// <param name="Url">The signup link with the token; shown only now.</param>
public sealed record CreatedInvitation(InvitationItem Invitation, string Url);

public sealed record SetTenantAdminRequest(bool IsTenantAdmin);

/// <summary>Tenant admins manage the users of their own tenant: invitations, admin rights, removal.</summary>
internal static class TeamEndpoints
{
    private static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(7);

    public static IEndpointRouteBuilder MapTeamEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/team").WithTags("Team").RequireAuthorization(Policies.TenantAdmin);
        group.MapGet("/", Get);
        group.MapPut("/", Rename);
        group.MapPost("/invitations", Invite);
        group.MapDelete("/invitations/{id:guid}", Revoke);
        group.MapPut("/users/{id:guid}/admin", SetTenantAdmin);
        group.MapDelete("/users/{id:guid}", RemoveUser);
        return routes;
    }

    private static async Task<Ok<TeamResponse>> Get(ClaimsPrincipal user, MailnixDbContext db, UserManager<AppUser> users, TimeProvider time, CancellationToken ct)
    {
        var tenantId = user.GetTenantId(db);
        var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId, ct);
        var accounts = await db.MailAccounts.Where(a => a.TenantId == tenantId).GroupBy(a => a.UserId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var members = new List<TeamMember>();
        foreach (var member in await db.Users.Where(u => u.TenantId == tenantId).OrderBy(u => u.Email).ToListAsync(ct))
        {
            var roles = await users.GetRolesAsync(member);
            members.Add(new TeamMember(member.Id, member.Email!, member.DisplayName, roles.Contains(Roles.TenantAdmin), roles.Contains(Roles.Admin), accounts.GetValueOrDefault(member.Id), member.CreatedAt));
        }

        var now = time.GetUtcNow();
        var invitations = await db.Invitations
            .Where(i => i.TenantId == tenantId && i.AcceptedAt == null && i.ExpiresAt > now)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new InvitationItem(i.Id, i.Email, i.CreatedAt, i.ExpiresAt))
            .ToListAsync(ct);
        return TypedResults.Ok(new TeamResponse(new TenantInfo(tenant.Id, tenant.Name, tenant.MaxAccounts, accounts.Values.Sum()), members, invitations));
    }

    private static async Task<NoContent> Rename(RenameTenantRequest request, ClaimsPrincipal user, MailnixDbContext db, CancellationToken ct)
    {
        var tenantId = user.GetTenantId(db);
        var name = request.Name.Trim();
        await db.Tenants.Where(t => t.Id == tenantId).ExecuteUpdateAsync(u => u.SetProperty(t => t.Name, name), ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<CreatedInvitation>> Invite(
        CreateInvitationRequest request,
        ClaimsPrincipal user,
        HttpRequest http,
        MailnixDbContext db,
        IOptions<OAuthOptions> oauth,
        TimeProvider time,
        CancellationToken ct)
    {
        var token = InvitationTokens.Create();
        var now = time.GetUtcNow();
        var invitation = new Invitation
        {
            Id = Guid.CreateVersion7(),
            TenantId = user.GetTenantId(db),
            Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
            TokenHash = InvitationTokens.Hash(token),
            CreatedByUserId = user.GetUserId(),
            CreatedAt = now,
            ExpiresAt = now + InvitationLifetime,
        };
        db.Invitations.Add(invitation);
        await db.SaveChangesAsync(ct);

        var baseUrl = oauth.Value.RedirectBaseUrl?.TrimEnd('/') ?? $"{http.Scheme}://{http.Host}";
        return TypedResults.Ok(new CreatedInvitation(
            new InvitationItem(invitation.Id, invitation.Email, invitation.CreatedAt, invitation.ExpiresAt),
            $"{baseUrl}/register?invite={Uri.EscapeDataString(token)}"));
    }

    private static async Task<Results<NoContent, NotFound>> Revoke(Guid id, ClaimsPrincipal user, MailnixDbContext db, CancellationToken ct)
    {
        var tenantId = user.GetTenantId(db);
        return await db.Invitations.Where(i => i.Id == id && i.TenantId == tenantId && i.AcceptedAt == null).ExecuteDeleteAsync(ct) > 0
            ? TypedResults.NoContent()
            : TypedResults.NotFound();
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> SetTenantAdmin(
        Guid id, SetTenantAdminRequest request, ClaimsPrincipal user, MailnixDbContext db, UserManager<AppUser> users, CancellationToken ct)
    {
        var tenantId = user.GetTenantId(db);
        if (await db.Users.SingleOrDefaultAsync(u => u.Id == id && u.TenantId == tenantId, ct) is not { } member)
        {
            return TypedResults.NotFound();
        }

        if (request.IsTenantAdmin)
        {
            await users.AddToRoleAsync(member, Roles.TenantAdmin);
        }
        else
        {
            if (await IsLastTenantAdminAsync(db, users, tenantId, member))
            {
                return Problem("LastTenantAdmin", "Someone has to stay administrator of the organization.");
            }

            await users.RemoveFromRoleAsync(member, Roles.TenantAdmin);
        }

        return TypedResults.NoContent();
    }

    /// <summary>Removes a user with everything they set up (mail accounts, rules, lists, runs).</summary>
    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> RemoveUser(
        Guid id, ClaimsPrincipal user, MailnixDbContext db, UserManager<AppUser> users, IWorkerSignals signals, CancellationToken ct)
    {
        var tenantId = user.GetTenantId(db);
        if (id == user.GetUserId())
        {
            return Problem("CannotRemoveSelf", "You cannot remove yourself.");
        }

        if (await db.Users.SingleOrDefaultAsync(u => u.Id == id && u.TenantId == tenantId, ct) is not { } member)
        {
            return TypedResults.NotFound();
        }

        if (await users.IsInRoleAsync(member, Roles.Admin))
        {
            return Problem("CannotRemoveAdmin", "The administrator of the installation cannot be removed here.");
        }

        // Lists are not tied to the user by a foreign key; accounts take their rules, runs and stats along.
        await db.NamedLists.Where(l => l.UserId == id).ExecuteDeleteAsync(ct);
        await users.DeleteAsync(member);
        signals.AccountsChanged();
        return TypedResults.NoContent();
    }

    private static async Task<bool> IsLastTenantAdminAsync(MailnixDbContext db, UserManager<AppUser> users, Guid tenantId, AppUser member)
    {
        if (!await users.IsInRoleAsync(member, Roles.TenantAdmin))
        {
            return false;
        }

        var admins = await users.GetUsersInRoleAsync(Roles.TenantAdmin);
        return admins.Count(a => a.TenantId == tenantId) <= 1;
    }

    private static ProblemHttpResult Problem(string errorCode, string title) =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: title, extensions: new Dictionary<string, object?> { ["errorCode"] = errorCode });
}
