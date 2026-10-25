using Mailnix.Infrastructure.Data;
using Mailnix.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Mailnix.Api.Hosting;
using Microsoft.AspNetCore.Identity;

namespace Mailnix.Api.Auth;

public sealed class AuthOptions
{
    public const string Section = "Mailnix:Auth";

    /// <summary>Allow self-service signup. The very first user can always register (and becomes admin).</summary>
    public bool AllowRegistration { get; set; } = true;
}

public static class Policies
{
    /// <summary>Runs the installation.</summary>
    public const string Admin = "Admin";

    /// <summary>Manages their own tenant's users (installation admins may too).</summary>
    public const string TenantAdmin = "TenantAdmin";
}

internal static class AuthServiceCollectionExtensions
{
    public static IServiceCollection AddMailnixAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AuthOptions>().Bind(configuration.GetSection(AuthOptions.Section));

        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.ConfigureApplicationCookie(cookie =>
        {
            cookie.Cookie.Name = "mailnix.auth";
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SameSite = SameSiteMode.Strict;
            cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            cookie.ExpireTimeSpan = TimeSpan.FromDays(14);
            cookie.SlidingExpiration = true;

            // The SPA handles navigation; the API answers with status codes instead of redirects.
            cookie.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            cookie.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });

        // Every minute the cookie is checked against the database and its roles reloaded, so removed users and
        // changed admin rights take effect within a minute (default: 30 minutes).
        services.Configure<SecurityStampValidatorOptions>(validator => validator.ValidationInterval = TimeSpan.FromMinutes(1));

        services.AddIdentityCore<AppUser>(identity =>
            {
                identity.User.RequireUniqueEmail = true;

                // Length over composition rules (NIST SP 800-63B).
                identity.Password.RequiredLength = 10;
                identity.Password.RequireDigit = false;
                identity.Password.RequireLowercase = false;
                identity.Password.RequireUppercase = false;
                identity.Password.RequireNonAlphanumeric = false;

                identity.Lockout.MaxFailedAccessAttempts = 10;
                identity.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<AppRole>()
            .AddEntityFrameworkStores<MailnixDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders()
            .AddClaimsPrincipalFactory<MailnixClaimsPrincipalFactory>();

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(Policies.Admin, policy => policy.RequireRole(Roles.Admin))
            .AddPolicy(Policies.TenantAdmin, policy => policy.RequireRole(Roles.TenantAdmin, Roles.Admin));

        return services;
    }
}
