using System.Security.Claims;
using Mailnix.Api.Hosting;
using Mailnix.Api.Team;
using Mailnix.Infrastructure.Data;
using Mailnix.Infrastructure.Identity;
using Mailnix.Infrastructure.Options;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mailnix.Api.Auth;

internal static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/auth").WithTags("Auth");

        group.MapGet("/config", GetConfig).AllowAnonymous();
        group.MapGet("/invitations/{token}", GetInvitation).AllowAnonymous().RequireRateLimiting(RateLimiting.Auth);
        group.MapPost("/register", Register).AllowAnonymous().RequireRateLimiting(RateLimiting.Auth);
        group.MapPost("/login", Login).AllowAnonymous().RequireRateLimiting(RateLimiting.Auth);
        group.MapPost("/logout", Logout);
        group.MapGet("/me", Me);

        return group;
    }

    private static async Task<Ok<AuthConfigResponse>> GetConfig(UserManager<AppUser> userManager, IOptions<AuthOptions> options, IOptions<TenancyOptions> tenancy) =>
        TypedResults.Ok(new AuthConfigResponse(
            options.Value.AllowRegistration || !await userManager.Users.AnyAsync(),
            tenancy.Value.Mode == TenancyMode.MultiTenant));

    private static async Task<Results<Ok<InvitationInfo>, NotFound>> GetInvitation(string token, MailnixDbContext db, TimeProvider time) =>
        await FindInvitationAsync(db, token, time) is { } found
            ? TypedResults.Ok(new InvitationInfo(found.TenantName, found.Invitation.Email))
            : TypedResults.NotFound();

    private static async Task<(Invitation Invitation, string TenantName)?> FindInvitationAsync(MailnixDbContext db, string token, TimeProvider time)
    {
        var hash = InvitationTokens.Hash(token);
        var found = await db.Invitations
            .Where(i => i.TokenHash == hash && i.AcceptedAt == null)
            .Join(db.Tenants, i => i.TenantId, t => t.Id, (i, t) => new { Invitation = i, t.Name })
            .SingleOrDefaultAsync();
        return found is null || found.Invitation.ExpiresAt < time.GetUtcNow() ? null : (found.Invitation, found.Name);
    }

    private static async Task<Results<Ok<MeResponse>, ValidationProblem, ProblemHttpResult>> Register(
        RegisterRequest request,
        UserManager<AppUser> userManager,
        SignInManager<AppUser> signInManager,
        MailnixDbContext db,
        IOptions<AuthOptions> options,
        IOptions<TenancyOptions> tenancy,
        TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow();
        var isFirstUser = !await userManager.Users.AnyAsync();
        Invitation? invitation = null;
        if (!string.IsNullOrWhiteSpace(request.InviteToken))
        {
            if (await FindInvitationAsync(db, request.InviteToken, timeProvider) is not { } found)
            {
                return Problem(StatusCodes.Status400BadRequest, "InvitationInvalid", "This invitation is no longer valid.");
            }

            if (found.Invitation.Email is { } invited && !invited.Equals(request.Email.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return Problem(StatusCodes.Status400BadRequest, "InvitationForOtherEmail", $"This invitation is for {invited}.");
            }

            invitation = found.Invitation;
        }
        else if (!options.Value.AllowRegistration && !isFirstUser)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Registration is disabled.");
        }

        // Self-hosted: one tenant for everybody. SaaS: each signup gets its own tenant, unless invited into one.
        var multiTenant = tenancy.Value.Mode == TenancyMode.MultiTenant;
        var tenantId = invitation?.TenantId ?? Tenant.DefaultId;
        Tenant? newTenant = null;
        if (invitation is null && multiTenant && !isFirstUser)
        {
            newTenant = new Tenant
            {
                Id = Guid.CreateVersion7(),
                Name = string.IsNullOrWhiteSpace(request.OrganizationName) ? request.Email.Trim() : request.OrganizationName.Trim(),
                MaxAccounts = tenancy.Value.DefaultMaxAccounts,
                CreatedAt = now,
            };
            db.Tenants.Add(newTenant);
            tenantId = newTenant.Id;
        }

        var user = new AppUser
        {
            UserName = request.Email,
            Email = request.Email,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim(),
            TenantId = tenantId,
            CreatedAt = now,
        };

        // The tenant is saved together with the user (UserManager saves the context).
        var created = await userManager.CreateAsync(user, request.Password);
        if (!created.Succeeded)
        {
            if (newTenant is not null)
            {
                db.Tenants.Remove(newTenant);
            }

            return TypedResults.ValidationProblem(ToValidationErrors(created));
        }

        if (invitation is not null)
        {
            invitation.AcceptedAt = now;
            invitation.AcceptedByUserId = user.Id;
            await db.SaveChangesAsync();
        }

        // The first account administers the installation (and the default tenant); the founder of a SaaS tenant
        // administers that tenant; invited users and later self-hosted signups are plain users.
        string[] roles = isFirstUser ? [Roles.Admin, Roles.TenantAdmin, Roles.User]
            : invitation is null && multiTenant ? [Roles.TenantAdmin, Roles.User]
            : [Roles.User];
        var assigned = await userManager.AddToRolesAsync(user, roles);
        if (!assigned.Succeeded)
        {
            return TypedResults.ValidationProblem(ToValidationErrors(assigned));
        }

        await signInManager.SignInAsync(user, isPersistent: false);
        return TypedResults.Ok(await ToMeResponseAsync(user, userManager));
    }

    private static async Task<Results<Ok<MeResponse>, ProblemHttpResult>> Login(
        LoginRequest request,
        UserManager<AppUser> userManager,
        SignInManager<AppUser> signInManager)
    {
        var result = await signInManager.PasswordSignInAsync(request.Email, request.Password, request.RememberMe, lockoutOnFailure: true);
        if (result.Succeeded && await userManager.FindByEmailAsync(request.Email) is { } user)
        {
            return TypedResults.Ok(await ToMeResponseAsync(user, userManager));
        }

        var title = result switch
        {
            { IsLockedOut: true } => "Account is temporarily locked. Try again later.",
            { RequiresTwoFactor: true } => "Two-factor authentication required.",
            _ => "Invalid email or password.",
        };
        return TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: title);
    }

    private static async Task<NoContent> Logout(SignInManager<AppUser> signInManager)
    {
        await signInManager.SignOutAsync();
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<MeResponse>, UnauthorizedHttpResult>> Me(ClaimsPrincipal principal, UserManager<AppUser> userManager)
    {
        // The cookie can outlive the account (e.g. user deleted by an admin).
        var user = await userManager.GetUserAsync(principal);
        return user is null ? TypedResults.Unauthorized() : TypedResults.Ok(await ToMeResponseAsync(user, userManager));
    }

    private static async Task<MeResponse> ToMeResponseAsync(AppUser user, UserManager<AppUser> userManager) =>
        new(user.Id, user.Email!, user.DisplayName, [.. await userManager.GetRolesAsync(user)], user.TimeZone, user.Language);

    private static ProblemHttpResult Problem(int status, string errorCode, string title) =>
        TypedResults.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["errorCode"] = errorCode });

    private static Dictionary<string, string[]> ToValidationErrors(IdentityResult result) =>
        result.Errors
            .GroupBy(e => e.Code)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray());
}
