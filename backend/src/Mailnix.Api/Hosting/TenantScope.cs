using System.Security.Claims;
using Mailnix.Infrastructure.Data;
using Mailnix.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mailnix.Api.Hosting;

/// <summary>Puts the user's tenant into the sign-in cookie, so requests know it without a database lookup.</summary>
internal sealed class MailnixClaimsPrincipalFactory(UserManager<AppUser> users, RoleManager<AppRole> roles, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<AppUser, AppRole>(users, roles, options)
{
    public const string TenantClaim = "mailnix:tenant";

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(TenantClaim, user.TenantId.ToString()));
        return identity;
    }
}

/// <summary>Applies the signed-in user's tenant as the database filter for the rest of the request.</summary>
internal sealed class TenantScopeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, MailnixDbContext db)
    {
        if (context.User.Identity?.IsAuthenticated == true && Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            // Cookies issued before the tenant claim existed: look it up once per request.
            db.TenantFilter = Guid.TryParse(context.User.FindFirstValue(MailnixClaimsPrincipalFactory.TenantClaim), out var tenantId)
                ? tenantId
                : await db.Users.Where(u => u.Id == userId).Select(u => (Guid?)u.TenantId).SingleOrDefaultAsync(context.RequestAborted);
        }

        await next(context);
    }
}

internal static class TenantClaimsExtensions
{
    public static Guid GetTenantId(this ClaimsPrincipal principal, MailnixDbContext db) =>
        db.TenantFilter ?? (Guid.TryParse(principal.FindFirstValue(MailnixClaimsPrincipalFactory.TenantClaim), out var id)
            ? id
            : throw new InvalidOperationException("The request has no tenant."));
}
