using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Mailnix.Api.Auth;
using Mailnix.Api.Email;
using Mailnix.Api.Hosting;
using Mailnix.Infrastructure.Data;
using Mailnix.Infrastructure.Identity;
using Mailnix.Worker;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Mailnix.Api.Team;

public sealed record TenantInfo(Guid Id, string Name, int? MaxAccounts, int Accounts);

public sealed record TeamMember(Guid Id, string Email, string? DisplayName, bool IsTenantAdmin, bool IsAdmin, int Accounts, DateTimeOffset CreatedAt);

public sealed record InvitationItem(Guid Id, string? Email, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <param name="EmailAvailable">Invitations can be sent by email.</param>
public sealed record TeamResponse(TenantInfo Tenant, IReadOnlyList<TeamMember> Users, IReadOnlyList<InvitationItem> Invitations, bool EmailAvailable);

public sealed record RenameTenantRequest([Required, MaxLength(200)] string Name);

/// <param name="SendEmail">Also send the link to <paramref name="Email"/> (when email is set up).</param>
public sealed record CreateInvitationRequest([EmailAddress, MaxLength(320)] string? Email = null, bool SendEmail = false);

/// <param name="Url">The signup link with the token; shown only now.</param>
/// <param name="EmailSent">The link was also sent by email.</param>
public sealed record CreatedInvitation(InvitationItem Invitation, string Url, bool EmailSent);

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

    private static async Task<Ok<TeamResponse>> Get(
        ClaimsPrincipal user, MailnixDbContext db, UserManager<AppUser> users, AccountEmails emails, TimeProvider time, CancellationToken ct)
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
        return TypedResults.Ok(new TeamResponse(new TenantInfo(tenant.Id, tenant.Name, tenant.MaxAccounts, accounts.Values.Sum()), members, invitations, emails.CanSendLinks));
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
        UserManager<AppUser> users,
        PublicLinks links,
        AccountEmails emails,
        TimeProvider time,
        CancellationToken ct)
    {
        var token = SecretTokens.Create();
        var now = time.GetUtcNow();
        var invitation = new Invitation
        {
            Id = Guid.CreateVersion7(),
            TenantId = user.GetTenantId(db),
            Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
            TokenHash = SecretTokens.Hash(token),
            CreatedByUserId = user.GetUserId(),
            CreatedAt = now,
            ExpiresAt = now + InvitationLifetime,
        };
        db.Invitations.Add(invitation);
        await db.SaveChangesAsync(ct);

        // The link shown to the admin may use the address they are on; an emailed link only the configured one.
        var url = $"{links.BaseUrl ?? $"{http.Scheme}://{http.Host}"}/register?invite={Uri.EscapeDataString(token)}";
        var emailSent = false;
        if (request.SendEmail && invitation.Email is { } to && await users.GetUserAsync(user) is { } inviter)
        {
            var organization = await db.Tenants.Where(t => t.Id == invitation.TenantId).Select(t => t.Name).SingleAsync(ct);
            emailSent = links.IsConfigured
                && emails.Invitation(to, inviter, organization, links.For("/register", new Dictionary<string, string> { ["invite"] = token }), invitation.ExpiresAt);
        }

        return TypedResults.Ok(new CreatedInvitation(
            new InvitationItem(invitation.Id, invitation.Email, invitation.CreatedAt, invitation.ExpiresAt), url, emailSent));
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
            if (await UserRemoval.IsLastTenantAdminAsync(member, db, users, othersMustRemain: false))
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

        await UserRemoval.DeleteAsync(member, db, users, signals, ct);
        return TypedResults.NoContent();
    }

    private static ProblemHttpResult Problem(string errorCode, string title) =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: title, extensions: new Dictionary<string, object?> { ["errorCode"] = errorCode });
}
